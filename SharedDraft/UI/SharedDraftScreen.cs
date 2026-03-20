using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace SharedDraft.UI;

/// <summary>
/// Full-screen shared card draft UI using native NCard rendering for authentic card display.
///
/// IMPORTANT: Does NOT inherit from any Godot node class.
/// Uses composition to avoid Godot source generator StringName crashes.
///
/// Cards are rendered using NCard.Create(CardModel) — the game's native card UI node
/// which handles portrait, energy cost, title, description, rarity glow, etc. automatically.
/// Each NCard (300x422 default) is scaled down to fit a grid layout, wrapped in a clickable
/// container with owner info label and player selection avatar overlays.
///
/// Layout:
///   CanvasLayer (Layer=95)
///   └── ColorRect (full-screen dim with subtle gradient)
///       └── CenterContainer
///           └── PanelContainer (main panel, 3D beveled style)
///               └── VBoxContainer
///                   ├── HBoxContainer (title bar with icon + title + player count)
///                   ├── Panel (gradient separator)
///                   ├── HBoxContainer (main content)
///                   │   ├── ScrollContainer (card grid)
///                   │   │   └── GridContainer (NCard cells + owner label)
///                   │   └── PanelContainer (sidebar with 3D style)
///                   │       └── VBoxContainer (player status cards)
///                   ├── Panel (gradient separator)
///                   ├── PanelContainer (selected card detail bar)
///                   ├── HBoxContainer (action buttons with 3D style)
///                   └── Label (status message)
/// </summary>
public static class SharedDraftScreen
{
    // ── References ──
    private static CanvasLayer? _canvasLayer;
    private static ColorRect? _dimBackground;
    private static GridContainer? _cardGrid;
    private static VBoxContainer? _sidebarContainer;
    private static Label? _titleLabel;
    private static Label? _selectedInfoLabel;
    private static Label? _statusLabel;
    private static Button? _confirmButton;
    private static Button? _skipButton;
    private static Button? _endDraftButton;

    // ── State ──
    private static int _selectedDraftId = -1;
    private static bool _isInWaitingState = false;
    private static readonly Dictionary<int, Control> _cardCells = new();
    private static readonly Dictionary<int, Label> _playerStatusLabels = new();
    private static readonly Dictionary<int, Control> _playerAvatarContainers = new();
    private static Label? _countdownLabel;

    // ── Input handler for Escape key (to open game pause menu) ──
    private static DraftInputHandler? _inputHandler;
    private static bool _isPauseMenuOpen = false;

    // ── Native NCard instances we created (track for cleanup) ──
    private static readonly List<NCard> _nativeCards = new();

    // ── Player selection overlays on cards ──
    // Maps DraftId → list of (playerSlot, avatar overlay node)
    private static readonly Dictionary<int, List<(int slot, Control overlay)>> _cardSelectionOverlays = new();

    /// <summary>Whether the draft screen is currently visible.</summary>
    public static bool IsVisible => _canvasLayer?.Visible ?? false;

    // ═══════════════════════════════════════════════
    //  NATIVE CARD SCALING
    // ═══════════════════════════════════════════════

    // NCard.defaultSize = 300x422. Scale to ~65% for clearer card display.
    private const float CardScale = 0.65f;
    private static readonly Vector2 NCardDefaultSize = new(300f, 422f);
    private static readonly Vector2 ScaledCardSize = new(
        NCardDefaultSize.X * CardScale,   // ~195
        NCardDefaultSize.Y * CardScale    // ~274
    );

    // Cell size = scaled card + small bottom margin (no thick border)
    private static readonly Vector2 CellSize = new(
        ScaledCardSize.X + 10,  // ~205
        ScaledCardSize.Y + 10   // ~284
    );

    // ═══════════════════════════════════════════════
    //  COLOR PALETTE — Premium 3D Theme
    // ═══════════════════════════════════════════════

    // Player colors (vivid, high-saturation for avatars)
    private static readonly Color[] PoolColors =
    [
        new Color(0.25f, 0.55f, 1.0f, 1.0f),   // Vivid Blue — player 0
        new Color(1.0f, 0.35f, 0.3f, 1.0f),     // Vivid Red — player 1
        new Color(0.2f, 0.85f, 0.35f, 1.0f),    // Vivid Green — player 2
        new Color(1.0f, 0.75f, 0.15f, 1.0f),     // Vivid Gold — player 3
    ];

    // Player avatar lighter tints (for text/labels)
    private static readonly Color[] PoolLightColors =
    [
        new Color(0.55f, 0.75f, 1.0f, 1.0f),
        new Color(1.0f, 0.65f, 0.6f, 1.0f),
        new Color(0.5f, 1.0f, 0.6f, 1.0f),
        new Color(1.0f, 0.9f, 0.5f, 1.0f),
    ];

    // Panel & card colors
    private static readonly Color SelectedBorderColor = new(1.0f, 0.85f, 0.2f, 1.0f);
    private static readonly Color PanelBgTop = new(0.08f, 0.07f, 0.14f, 0.97f);
    private static readonly Color CardNormalBg = new(0.10f, 0.09f, 0.16f, 0.90f);
    private static readonly Color CardHoverBg = new(0.16f, 0.14f, 0.26f, 0.95f);
    private static readonly Color CardSelectedBg = new(0.20f, 0.17f, 0.35f, 1.0f);
    private static readonly Color SidebarBg = new(0.07f, 0.06f, 0.12f, 0.90f);
    private static readonly Color StatusWaiting = new(0.6f, 0.6f, 0.6f);
    private static readonly Color StatusReady = new(0.4f, 1.0f, 0.4f);
    private static readonly Color StatusConflict = new(1.0f, 0.5f, 0.2f);

    // Accent colors
    private static readonly Color AccentGold = new(1.0f, 0.85f, 0.3f, 1.0f);
    private static readonly Color AccentPurple = new(0.6f, 0.4f, 0.9f, 1.0f);
    private static readonly Color TextPrimary = new(0.95f, 0.92f, 0.98f);
    private static readonly Color TextSecondary = new(0.7f, 0.68f, 0.78f);
    private static readonly Color TextDim = new(0.5f, 0.48f, 0.58f);

    // ═══════════════════════════════════════════════
    //  CREATE / SHOW / HIDE
    // ═══════════════════════════════════════════════

    public static void Show()
    {
        if (_canvasLayer == null || !GodotObject.IsInstanceValid(_canvasLayer))
        {
            Build();
            InjectIntoSceneTree();
        }

        PopulateCards();
        PopulatePlayerStatus();
        _selectedDraftId = -1;
        _isInWaitingState = false;
        UpdateSelectedInfo();
        UpdateConfirmButton();

        if (_canvasLayer != null)
            _canvasLayer.Visible = true;
    }

    public static void Hide()
    {
        if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
            _canvasLayer.Visible = false;
    }

    public static void RefreshPlayerStatus()
    {
        var manager = SharedDraftManager.Instance;
        foreach (var ps in manager.PlayerStates)
        {
            if (_playerStatusLabels.TryGetValue(ps.PlayerSlot, out var label) &&
                GodotObject.IsInstanceValid(label))
            {
                bool isLocal = manager.IsLocalPlayerState(ps);
                string statusIcon;
                Color statusColor;

                if (ps.IsCompleted)
                {
                    if (ps.IsOptedOut)
                    {
                        statusIcon = "✗";
                        statusColor = new Color(0.5f, 0.5f, 0.5f);
                    }
                    else
                    {
                        statusIcon = "🏆";
                        statusColor = new Color(1.0f, 0.85f, 0.3f); // Gold — awarded
                    }
                }
                else if (ps.IsNotEntered)
                {
                    statusIcon = "⏳";
                    statusColor = StatusWaiting;
                }
                else if (ps.IsSelecting)
                {
                    statusIcon = "🤔";
                    statusColor = new Color(0.5f, 0.7f, 1.0f); // Blue — thinking
                }
                else if (ps.IsSelectedState)
                {
                    statusIcon = "✓";
                    statusColor = StatusReady;
                }
                else
                {
                    statusIcon = "?";
                    statusColor = StatusWaiting;
                }

                string suffix = isLocal ? " (你)" : "";
                string completedSuffix = ps.IsCompleted && ps.IsOptedOut ? " [已跳过]"
                    : ps.IsCompleted ? " [已获卡]"
                    : "";
                label.Text = $"{statusIcon} {ps.DisplayName}{suffix}{completedSuffix}";
                label.AddThemeColorOverride("font_color", statusColor);
            }
        }

        // Update card selection overlays
        UpdateCardSelectionOverlays();
    }

    /// <summary>
    /// Refresh the card display (e.g., when remote player's cards are added to pool).
    /// </summary>
    public static void RefreshCards()
    {
        if (_cardGrid == null || !GodotObject.IsInstanceValid(_cardGrid))
            return;

        PopulateCards();
        PopulatePlayerStatus();

        // Grey out awarded (already taken) cards
        var manager = SharedDraftManager.Instance;
        var awardedIds = manager.AwardedDraftIds;

        foreach (var (draftId, cell) in _cardCells)
        {
            if (GodotObject.IsInstanceValid(cell))
            {
                if (awardedIds.Contains(draftId))
                {
                    // This card was already awarded — disable and grey out
                    SetCellInteractable(cell, false);
                    cell.Modulate = new Color(0.3f, 0.3f, 0.3f, 0.5f);
                }
                else if (_isInWaitingState)
                {
                    SetCellInteractable(cell, false);
                }
            }
        }
    }

    /// <summary>
    /// Re-enable card selection UI after losing RPS conflict.
    /// </summary>
    public static void EnableReSelection()
    {
        _selectedDraftId = -1;
        _isInWaitingState = false;

        SetStatus("你在猜拳中输了，请重新选择一张卡牌。", new Color(1.0f, 0.6f, 0.3f));

        // Enable non-awarded cards, grey out awarded ones
        var manager = SharedDraftManager.Instance;
        var awardedIds = manager.AwardedDraftIds;

        foreach (var (draftId, cell) in _cardCells)
        {
            if (GodotObject.IsInstanceValid(cell))
            {
                if (awardedIds.Contains(draftId))
                {
                    SetCellInteractable(cell, false);
                    cell.Modulate = new Color(0.3f, 0.3f, 0.3f, 0.5f);
                }
                else
                {
                    SetCellInteractable(cell, true);
                    SetCellSelected(cell, false);
                }
            }
        }

        if (_confirmButton != null && GodotObject.IsInstanceValid(_confirmButton))
            _confirmButton.Disabled = true;
        if (_skipButton != null && GodotObject.IsInstanceValid(_skipButton))
            _skipButton.Disabled = false;
        if (_endDraftButton != null && GodotObject.IsInstanceValid(_endDraftButton))
            _endDraftButton.Disabled = false;

        UpdateSelectedInfo();
        RefreshPlayerStatus();

        ModEntry.Logger.Info("Draft screen re-enabled for re-selection after RPS loss.");
    }

    // ═══════════════════════════════════════════════
    //  BUILD UI — Premium 3D Theme
    // ═══════════════════════════════════════════════

    private static void Build()
    {
        _canvasLayer = new CanvasLayer();
        _canvasLayer.Name = "SharedDraftOverlay";
        _canvasLayer.Layer = 95;
        _canvasLayer.Visible = false;

        // Full-screen dim background
        _dimBackground = new ColorRect();
        _dimBackground.Name = "DraftDimBackground";
        _dimBackground.Color = new Color(0.0f, 0.0f, 0.02f, 0.75f);
        _dimBackground.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _dimBackground.MouseFilter = Control.MouseFilterEnum.Stop;
        _canvasLayer.AddChild(_dimBackground);

        // CenterContainer
        var centerContainer = new CenterContainer();
        centerContainer.Name = "DraftCenterContainer";
        centerContainer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _dimBackground.AddChild(centerContainer);

        // ── Main panel with 3D beveled style — use most of screen space ──
        var panelContainer = new PanelContainer();
        panelContainer.Name = "SharedDraftPanel";
        // Use anchors for responsive sizing instead of fixed size
        panelContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        panelContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        panelContainer.CustomMinimumSize = new Vector2(1400, 850);

        var panelStyle = Create3DPanelStyle(
            PanelBgTop,
            new Color(0.35f, 0.25f, 0.6f, 0.7f),  // Purple border
            new Color(0.5f, 0.4f, 0.8f, 0.4f),     // Top highlight
            new Color(0.02f, 0.01f, 0.05f, 0.6f),   // Bottom shadow
            borderWidth: 2, cornerRadius: 16);
        panelContainer.AddThemeStyleboxOverride("panel", panelStyle);
        centerContainer.AddChild(panelContainer);

        // Main vertical layout
        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 8);
        panelContainer.AddChild(mainVBox);

        // ── Title Bar ──
        BuildTitleBar(mainVBox);

        // Gradient separator
        mainVBox.AddChild(CreateGradientSeparator(AccentPurple));

        // ── Main content area (cards + sidebar) ──
        var contentHBox = new HBoxContainer();
        contentHBox.AddThemeConstantOverride("separation", 12);
        contentHBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        mainVBox.AddChild(contentHBox);

        // Card grid area (scrollable)
        var cardScroll = new ScrollContainer();
        cardScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        cardScroll.SizeFlagsStretchRatio = 4;
        cardScroll.CustomMinimumSize = new Vector2(1000, 0);
        contentHBox.AddChild(cardScroll);

        _cardGrid = new GridContainer();
        _cardGrid.Name = "CardGrid";
        _cardGrid.Columns = 3;
        _cardGrid.AddThemeConstantOverride("h_separation", 16);
        _cardGrid.AddThemeConstantOverride("v_separation", 16);
        _cardGrid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        cardScroll.AddChild(_cardGrid);

        // ── Sidebar (player status) with 3D panel ──
        BuildSidebar(contentHBox);

        // Gradient separator
        mainVBox.AddChild(CreateGradientSeparator(AccentGold));

        // ── Bottom section ──
        BuildBottomSection(mainVBox);

        // ── Input handler for Escape key → pause menu ──
        _inputHandler = new DraftInputHandler();
        _inputHandler.Name = "DraftInputHandler";
        _canvasLayer.AddChild(_inputHandler);
    }

    private static void BuildTitleBar(VBoxContainer parent)
    {
        var titleBar = new HBoxContainer();
        titleBar.AddThemeConstantOverride("separation", 12);
        titleBar.Alignment = BoxContainer.AlignmentMode.Center;
        parent.AddChild(titleBar);

        _titleLabel = new Label();
        _titleLabel.Text = SharedDraftConfig.DebugMode
            ? "🃏  共  享  选  卡  [DEBUG]"
            : "🃏  共  享  选  卡";
        _titleLabel.AddThemeFontSizeOverride("font_size", 24);
        _titleLabel.AddThemeColorOverride("font_color", AccentGold);
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        titleBar.AddChild(_titleLabel);
    }

    private static void BuildSidebar(HBoxContainer parent)
    {
        var sidebarPanel = new PanelContainer();
        sidebarPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        sidebarPanel.SizeFlagsStretchRatio = 1;
        sidebarPanel.CustomMinimumSize = new Vector2(220, 0);

        var sidebarStyle = Create3DPanelStyle(
            SidebarBg,
            new Color(0.3f, 0.25f, 0.5f, 0.5f),
            new Color(0.4f, 0.35f, 0.6f, 0.2f),
            new Color(0.02f, 0.01f, 0.04f, 0.3f),
            borderWidth: 1, cornerRadius: 10);
        sidebarPanel.AddThemeStyleboxOverride("panel", sidebarStyle);
        parent.AddChild(sidebarPanel);

        var sidebarScroll = new ScrollContainer();
        sidebarScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        sidebarPanel.AddChild(sidebarScroll);

        _sidebarContainer = new VBoxContainer();
        _sidebarContainer.AddThemeConstantOverride("separation", 6);
        _sidebarContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        sidebarScroll.AddChild(_sidebarContainer);

        // Sidebar title
        var sidebarTitle = new Label();
        sidebarTitle.Text = "👥 玩家状态";
        sidebarTitle.AddThemeFontSizeOverride("font_size", 16);
        sidebarTitle.AddThemeColorOverride("font_color", AccentPurple);
        sidebarTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _sidebarContainer.AddChild(sidebarTitle);

        _sidebarContainer.AddChild(CreateGradientSeparator(AccentPurple, 1));
    }

    private static void BuildBottomSection(VBoxContainer parent)
    {
        // Selected card info panel with subtle 3D effect
        var selectedPanel = new PanelContainer();
        var selectedStyle = Create3DPanelStyle(
            new Color(0.08f, 0.07f, 0.13f, 0.8f),
            new Color(0.3f, 0.25f, 0.5f, 0.3f),
            new Color(0.3f, 0.25f, 0.5f, 0.1f),
            new Color(0.01f, 0.01f, 0.03f, 0.2f),
            borderWidth: 1, cornerRadius: 8);
        selectedPanel.AddThemeStyleboxOverride("panel", selectedStyle);
        parent.AddChild(selectedPanel);

        _selectedInfoLabel = new Label();
        _selectedInfoLabel.Text = "未选择卡牌";
        _selectedInfoLabel.AddThemeFontSizeOverride("font_size", 14);
        _selectedInfoLabel.AddThemeColorOverride("font_color", TextDim);
        _selectedInfoLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _selectedInfoLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        selectedPanel.AddChild(_selectedInfoLabel);

        // Action buttons
        var actionHBox = new HBoxContainer();
        actionHBox.AddThemeConstantOverride("separation", 16);
        actionHBox.Alignment = BoxContainer.AlignmentMode.Center;
        parent.AddChild(actionHBox);

        _confirmButton = Create3DButton(
            "✓  确认选择",
            new Color(0.15f, 0.5f, 0.2f, 0.9f),
            new Color(0.2f, 0.65f, 0.28f, 1.0f),
            new Color(0.08f, 0.25f, 0.1f, 0.5f),
            new Color(0.25f, 0.7f, 0.35f, 0.6f));
        _confirmButton.CustomMinimumSize = new Vector2(220, 46);
        _confirmButton.Disabled = true;
        _confirmButton.Pressed += OnConfirmPressed;
        actionHBox.AddChild(_confirmButton);

        _skipButton = Create3DButton(
            "跳过",
            new Color(0.45f, 0.3f, 0.15f, 0.85f),
            new Color(0.6f, 0.4f, 0.2f, 0.95f),
            new Color(0.2f, 0.15f, 0.08f, 0.5f),
            new Color(0.7f, 0.5f, 0.25f, 0.5f));
        _skipButton.CustomMinimumSize = new Vector2(150, 46);
        _skipButton.Pressed += OnSkipPressed;
        actionHBox.AddChild(_skipButton);

        _endDraftButton = Create3DButton(
            "⚡ 结束选牌",
            new Color(0.55f, 0.15f, 0.15f, 0.85f),
            new Color(0.7f, 0.2f, 0.2f, 0.95f),
            new Color(0.25f, 0.08f, 0.08f, 0.5f),
            new Color(0.8f, 0.3f, 0.3f, 0.5f));
        _endDraftButton.CustomMinimumSize = new Vector2(180, 46);
        _endDraftButton.Pressed += OnEndDraftPressed;
        _endDraftButton.TooltipText = "立即开始结算（只结算已选择的玩家）";
        actionHBox.AddChild(_endDraftButton);

        // Status label
        _statusLabel = new Label();
        _statusLabel.Text = "";
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        _statusLabel.AddThemeColorOverride("font_color", StatusWaiting);
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        parent.AddChild(_statusLabel);

        // Countdown label
        _countdownLabel = new Label();
        _countdownLabel.Text = "";
        _countdownLabel.AddThemeFontSizeOverride("font_size", 15);
        _countdownLabel.AddThemeColorOverride("font_color", AccentGold);
        _countdownLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _countdownLabel.Visible = false;
        parent.AddChild(_countdownLabel);
    }

    // ═══════════════════════════════════════════════
    //  POPULATE CONTENT — Native NCard Rendering
    // ═══════════════════════════════════════════════

    private static void PopulateCards()
    {
        if (_cardGrid == null) return;

        // Clear old native cards and cells
        _nativeCards.Clear();
        _cardCells.Clear();
        _cardSelectionOverlays.Clear();
        foreach (var child in _cardGrid.GetChildren())
        {
            child.QueueFree();
        }

        var manager = SharedDraftManager.Instance;
        var pool = manager.DraftPool;

        int cardCount = pool.Count;
        _cardGrid.Columns = cardCount <= 6 ? 3 : (cardCount <= 9 ? 3 : 4);

        // PHASE 1: Create empty cell shells and add them to the grid (scene tree).
        // This is critical — NCard needs to be in the scene tree for _Ready() to fire,
        // which initializes the child node references (TitleLabel, DescriptionLabel, etc.)
        // and calls Reload() so the card text/cost/description are populated.
        var cellsToPopulate = new List<(DraftCard draftCard, Control cell)>();
        foreach (var draftCard in pool)
        {
            var cell = CreateEmptyCardCell(draftCard);
            _cardGrid.AddChild(cell);  // Now cell is in the scene tree
            _cardCells[draftCard.DraftId] = cell;
            cellsToPopulate.Add((draftCard, cell));
        }

        // PHASE 2: Now that cells are in the scene tree, create NCard instances
        // and add them. When NCard is added as a child of an in-tree node,
        // _Ready() fires immediately → Reload() runs → text/cost/portrait all populate.
        foreach (var (draftCard, cell) in cellsToPopulate)
        {
            PopulateCardInCell(draftCard, cell);
        }

        // PHASE 3: After all NCards are in-tree and _Ready() has fired,
        // call UpdateVisuals() to ensure correct localization and display mode.
        // We use CallDeferred to ensure it runs after the current frame completes.
        Callable.From(DeferredUpdateAllCardVisuals).CallDeferred();
    }

    /// <summary>
    /// Deferred callback to call UpdateVisuals on all NCard instances
    /// after they are fully in the scene tree and _Ready() has completed.
    /// </summary>
    private static void DeferredUpdateAllCardVisuals()
    {
        int updated = 0;
        foreach (var nCard in _nativeCards)
        {
            if (GodotObject.IsInstanceValid(nCard) && nCard.IsInsideTree())
            {
                try
                {
                    nCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
                    updated++;
                }
                catch (System.Exception ex)
                {
                    ModEntry.Logger.Info($"[NCard] Deferred UpdateVisuals issue: {ex.Message}");
                }
            }
        }
        ModEntry.Logger.Info($"[NCard] Deferred UpdateVisuals completed for {updated}/{_nativeCards.Count} cards.");
    }

    /// <summary>
    /// PHASE 1: Create an empty card cell shell with styling, click handlers, hover effects.
    /// No NCard is created here — that happens in PopulateCardInCell() after the cell
    /// is added to the scene tree (so NCard._Ready() fires properly).
    /// </summary>
    private static Control CreateEmptyCardCell(DraftCard draftCard)
    {
        // ── Outer container: transparent clickable area (no thick border) ──
        var cell = new PanelContainer();
        cell.Name = $"CardCell_{draftCard.DraftId}";
        cell.CustomMinimumSize = CellSize;

        // Pool border color for left accent
        Color poolBorderColor = draftCard.OwnerPlayerSlot < PoolColors.Length
            ? PoolColors[draftCard.OwnerPlayerSlot]
            : new Color(0.5f, 0.5f, 0.5f);

        // Normal style — very subtle, no thick ContentMargin
        var normalStyle = new StyleBoxFlat();
        normalStyle.BgColor = new Color(0.08f, 0.07f, 0.12f, 0.3f);
        normalStyle.BorderColor = new Color(poolBorderColor.R, poolBorderColor.G, poolBorderColor.B, 0.3f);
        normalStyle.BorderWidthLeft = 3;
        normalStyle.BorderWidthTop = 0;
        normalStyle.BorderWidthRight = 0;
        normalStyle.BorderWidthBottom = 0;
        normalStyle.SetCornerRadiusAll(6);
        normalStyle.ContentMarginLeft = 4;
        normalStyle.ContentMarginTop = 2;
        normalStyle.ContentMarginRight = 2;
        normalStyle.ContentMarginBottom = 2;
        cell.AddThemeStyleboxOverride("panel", normalStyle);

        // Store metadata
        cell.SetMeta("normal_style", normalStyle);
        cell.SetMeta("draft_id", draftCard.DraftId);
        cell.SetMeta("is_selected", false);
        cell.SetMeta("is_disabled", false);

        // Inner VBox placeholder — will receive NCard or fallback text in Phase 2
        var vbox = new VBoxContainer();
        vbox.Name = "CardVBox";
        vbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        vbox.AddThemeConstantOverride("separation", 0);
        cell.AddChild(vbox);

        // ── Click handling via GuiInput (left click = select, right click = preview) ──
        int capturedId = draftCard.DraftId;
        cell.MouseFilter = Control.MouseFilterEnum.Stop;
        cell.GuiInput += (InputEvent @event) =>
        {
            if (@event is InputEventMouseButton mb && mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    bool disabled = (bool)cell.GetMeta("is_disabled", false);
                    if (!disabled)
                        OnCellClicked(capturedId, cell);
                }
                else if (mb.ButtonIndex == MouseButton.Right)
                {
                    OpenCardInspector(capturedId);
                }
            }
        };

        // Hover effect
        cell.MouseEntered += () =>
        {
            bool disabled = (bool)cell.GetMeta("is_disabled", false);
            bool selected = (bool)cell.GetMeta("is_selected", false);
            if (!disabled && !selected)
            {
                var hoverStyle = new StyleBoxFlat();
                hoverStyle.BgColor = new Color(0.12f, 0.10f, 0.20f, 0.5f);
                hoverStyle.BorderColor = new Color(poolBorderColor.R, poolBorderColor.G, poolBorderColor.B, 0.7f);
                hoverStyle.BorderWidthLeft = 3;
                hoverStyle.BorderWidthTop = 1;
                hoverStyle.BorderWidthRight = 1;
                hoverStyle.BorderWidthBottom = 1;
                hoverStyle.SetCornerRadiusAll(6);
                hoverStyle.ContentMarginLeft = 4;
                hoverStyle.ContentMarginTop = 2;
                hoverStyle.ContentMarginRight = 2;
                hoverStyle.ContentMarginBottom = 2;
                hoverStyle.ShadowColor = new Color(poolBorderColor.R * 0.3f, poolBorderColor.G * 0.3f, poolBorderColor.B * 0.3f, 0.4f);
                hoverStyle.ShadowSize = 4;
                hoverStyle.ShadowOffset = new Vector2(2, 3);
                cell.AddThemeStyleboxOverride("panel", hoverStyle);
            }
        };

        cell.MouseExited += () =>
        {
            bool selected = (bool)cell.GetMeta("is_selected", false);
            if (!selected)
            {
                var ns = cell.GetMeta("normal_style", Variant.From<StyleBoxFlat>(normalStyle)).As<StyleBoxFlat>();
                cell.AddThemeStyleboxOverride("panel", ns);
            }
        };

        return cell;
    }

    /// <summary>
    /// PHASE 2: Populate a card cell (already in the scene tree) with an NCard instance.
    /// Because the cell is in the scene tree, when NCard is added as a child,
    /// NCard._Ready() fires immediately — initializing TitleLabel, DescriptionLabel,
    /// EnergyLabel etc. and calling Reload() which populates portrait/frame/text.
    ///
    /// KEY INSIGHT: NCard.Create() gets an NCard from NodePool but does NOT add it
    /// to the scene tree. Setting Model triggers Reload(), but Reload() checks
    /// IsNodeReady() which is false if _Ready() hasn't run. So Reload() is silently
    /// skipped, leaving default placeholder text ("if you can read this, there is a bug").
    /// Only after AddChild → _Ready() → Reload() does the card properly initialize.
    /// </summary>
    private static void PopulateCardInCell(DraftCard draftCard, Control cell)
    {
        var vbox = cell.GetNode<VBoxContainer>("CardVBox");
        if (vbox == null) return;

        if (draftCard.HasCardModel)
        {
            try
            {
                NCard? nCard = NCard.Create(draftCard.Card!, ModelVisibility.Visible);
                if (nCard != null)
                {
                    _nativeCards.Add(nCard);

                    // v0.20 FIX: Revert to SubViewport approach from v0.18, which had
                    // the correct card position (centered in viewport), but card was too large.
                    // Fix: scale NCard down inside the SubViewport so it fits.
                    //
                    // v0.18 had: NCard at Position=(150,211) in 300x422 SubViewport → correct
                    // centering but card overflowed. Now we apply Scale=CardScale to NCard
                    // inside the SubViewport, and shrink the SubViewport to match the scaled
                    // card dimensions. SubViewportContainer displays at ScaledCardSize (195x274).

                    var subViewportContainer = new SubViewportContainer();
                    subViewportContainer.MouseFilter = Control.MouseFilterEnum.Ignore;
                    subViewportContainer.CustomMinimumSize = ScaledCardSize;
                    subViewportContainer.Size = ScaledCardSize;
                    subViewportContainer.Stretch = true; // Scale SubViewport content to fit container

                    var subViewport = new SubViewport();
                    subViewport.Size = new Vector2I(
                        (int)ScaledCardSize.X,
                        (int)ScaledCardSize.Y); // 195x274 — matches container
                    subViewport.TransparentBg = true;
                    subViewport.HandleInputLocally = false;
                    subViewport.GuiDisableInput = true;
                    subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                    subViewportContainer.AddChild(subViewport);

                    // *** CRITICAL: First add subViewportContainer to vbox (in scene tree).
                    // This ensures NCard enters the tree → _Ready() fires → Reload() runs ***
                    vbox.AddChild(subViewportContainer);

                    // NCard's visual content is centered around its local origin.
                    // At Scale=CardScale (0.65), its visual footprint is ~195x274.
                    // Position it at the center of the scaled viewport so content fills it.
                    nCard.Position = ScaledCardSize / 2; // (97.5, 137) — center of viewport
                    nCard.Scale = new Vector2(CardScale, CardScale);

                    // Now add NCard to SubViewport. NCard._Ready() fires immediately.
                    subViewport.AddChild(nCard);

                    ModEntry.Logger.Info(
                        $"[NCard] Added to tree: IsNodeReady={nCard.IsNodeReady()}, InTree={nCard.IsInsideTree()}");

                    // NCard is now in the tree and _Ready()+Reload() have run.
                    // Call UpdateVisuals to ensure correct PileType display mode:
                    try
                    {
                        nCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
                    }
                    catch (System.Exception ex)
                    {
                        ModEntry.Logger.Info($"[NCard] UpdateVisuals issue (card still renders): {ex.Message}");
                    }

                    // Activate reward screen glow for rare/uncommon
                    try
                    {
                        nCard.ActivateRewardScreenGlow();
                    }
                    catch (System.Exception ex)
                    {
                        ModEntry.Logger.Info($"[NCard] ActivateRewardScreenGlow minor issue: {ex.Message}");
                    }
                    ModEntry.Logger.Info(
                        $"[NCard] Created native card for DraftId={draftCard.DraftId}, " +
                        $"Entry={draftCard.CardEntry}, Scale={CardScale}, " +
                        $"IsNodeReady={nCard.IsNodeReady()}, InTree={nCard.IsInsideTree()}");
                }
                else
                {
                    ModEntry.Logger.Info($"[NCard] Create returned null for {draftCard.CardEntry}, using text fallback");
                    AddTextFallbackCard(vbox, draftCard);
                }
            }
            catch (System.Exception ex)
            {
                ModEntry.Logger.Error($"[NCard] Failed to create native card for {draftCard.CardEntry}: {ex.Message}");
                AddTextFallbackCard(vbox, draftCard);
            }
        }
        else
        {
            AddTextFallbackCard(vbox, draftCard);
        }
    }

    /// <summary>
    /// Open the game's native card inspector screen for the given DraftId.
    /// Shows large 2x card with upgrade preview toggle and left/right navigation.
    /// </summary>
    private static void OpenCardInspector(int draftId)
    {
        try
        {
            var manager = SharedDraftManager.Instance;
            var pool = manager.DraftPool;

            // Build a list of CardModels that have real models (for the inspector)
            var cardModels = new List<CardModel>();
            int targetIndex = 0;

            foreach (var dc in pool)
            {
                if (dc.HasCardModel)
                {
                    if (dc.DraftId == draftId)
                        targetIndex = cardModels.Count;
                    cardModels.Add(dc.Card!);
                }
            }

            if (cardModels.Count == 0)
            {
                ModEntry.Logger.Info($"[InspectCard] No card models available for inspection.");
                return;
            }

            // Hide the SharedDraft overlay so InspectCardScreen is not obscured
            if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
            {
                _canvasLayer.Visible = false;
            }

            // Use the game's native inspect card screen
            var inspectScreen = NGame.Instance.GetInspectCardScreen();
            inspectScreen.Open(cardModels, targetIndex, false);

            // Register a one-time VisibilityChanged callback to restore our overlay
            // when the InspectCardScreen is closed (Visible → false after close animation)
            void OnInspectVisibilityChanged()
            {
                if (!GodotObject.IsInstanceValid(inspectScreen))
                {
                    // Node was destroyed — restore anyway
                    if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
                        _canvasLayer.Visible = true;
                    return;
                }

                if (!inspectScreen.Visible)
                {
                    // InspectCardScreen closed — restore SharedDraft overlay
                    if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
                        _canvasLayer.Visible = true;

                    // Disconnect this one-time handler
                    inspectScreen.VisibilityChanged -= OnInspectVisibilityChanged;

                    ModEntry.Logger.Info("[InspectCard] Inspector closed, SharedDraft overlay restored.");
                }
            }

            inspectScreen.VisibilityChanged += OnInspectVisibilityChanged;

            ModEntry.Logger.Info(
                $"[InspectCard] Opened inspector for DraftId={draftId}, " +
                $"index={targetIndex}/{cardModels.Count}, SharedDraft overlay hidden.");
        }
        catch (System.Exception ex)
        {
            // On error, make sure the overlay is visible again
            if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
                _canvasLayer.Visible = true;

            ModEntry.Logger.Error($"[InspectCard] Failed to open: {ex.Message}");
        }
    }

    /// <summary>
    /// Fallback text display for cards without CardModel (remote cards) or when NCard.Create fails.
    /// </summary>
    private static void AddTextFallbackCard(VBoxContainer parent, DraftCard draftCard)
    {
        var fallbackPanel = new PanelContainer();
        fallbackPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
        fallbackPanel.CustomMinimumSize = ScaledCardSize;

        var fbStyle = new StyleBoxFlat();
        fbStyle.BgColor = new Color(0.06f, 0.05f, 0.10f, 0.9f);
        fbStyle.BorderColor = new Color(0.4f, 0.35f, 0.6f, 0.5f);
        fbStyle.SetBorderWidthAll(2);
        fbStyle.SetCornerRadiusAll(8);
        fbStyle.SetContentMarginAll(8);
        fallbackPanel.AddThemeStyleboxOverride("panel", fbStyle);

        var textVBox = new VBoxContainer();
        textVBox.MouseFilter = Control.MouseFilterEnum.Ignore;
        textVBox.AddThemeConstantOverride("separation", 6);
        fallbackPanel.AddChild(textVBox);

        var titleLabel = new Label();
        titleLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        titleLabel.Text = $"🃏 {FormatEntryName(draftCard.CardEntry)}";
        titleLabel.AddThemeFontSizeOverride("font_size", 14);
        titleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        titleLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.75f, 0.9f));
        titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        textVBox.AddChild(titleLabel);

        var entryLabel = new Label();
        entryLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        entryLabel.Text = draftCard.CardEntry ?? "???";
        entryLabel.AddThemeFontSizeOverride("font_size", 10);
        entryLabel.AddThemeColorOverride("font_color", TextDim);
        entryLabel.HorizontalAlignment = HorizontalAlignment.Center;
        textVBox.AddChild(entryLabel);

        var descLabel = new Label();
        descLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        descLabel.Text = "(对方角色的卡牌)";
        descLabel.AddThemeFontSizeOverride("font_size", 11);
        descLabel.AddThemeColorOverride("font_color", TextSecondary);
        descLabel.HorizontalAlignment = HorizontalAlignment.Center;
        descLabel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        textVBox.AddChild(descLabel);

        parent.AddChild(fallbackPanel);
    }

    // ═══════════════════════════════════════════════
    //  CELL STATE HELPERS
    // ═══════════════════════════════════════════════

    private static void SetCellSelected(Control cell, bool selected)
    {
        cell.SetMeta("is_selected", selected);
        if (selected)
        {
            var selectedStyle = new StyleBoxFlat();
            selectedStyle.BgColor = new Color(0.15f, 0.12f, 0.28f, 0.6f);
            selectedStyle.BorderColor = SelectedBorderColor;
            selectedStyle.SetBorderWidthAll(2);
            selectedStyle.SetCornerRadiusAll(6);
            selectedStyle.ContentMarginLeft = 4;
            selectedStyle.ContentMarginTop = 2;
            selectedStyle.ContentMarginRight = 2;
            selectedStyle.ContentMarginBottom = 2;
            selectedStyle.ShadowColor = new Color(1.0f, 0.85f, 0.2f, 0.3f);
            selectedStyle.ShadowSize = 6;
            cell.AddThemeStyleboxOverride("panel", selectedStyle);
        }
        else
        {
            var ns = cell.GetMeta("normal_style").As<StyleBoxFlat>();
            cell.AddThemeStyleboxOverride("panel", ns);
        }
    }

    private static void SetCellInteractable(Control cell, bool interactable)
    {
        cell.SetMeta("is_disabled", !interactable);
        cell.Modulate = interactable ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.7f);
    }

    // ═══════════════════════════════════════════════
    //  PLAYER SELECTION AVATAR OVERLAYS
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Update selection overlays on all cards — show small player avatar badges
    /// on cards that players have selected (like relic picking in treasure rooms).
    /// </summary>
    private static void UpdateCardSelectionOverlays()
    {
        // Clear existing overlays
        foreach (var (_, overlays) in _cardSelectionOverlays)
        {
            foreach (var (_, overlay) in overlays)
            {
                if (GodotObject.IsInstanceValid(overlay))
                    overlay.QueueFree();
            }
        }
        _cardSelectionOverlays.Clear();

        var manager = SharedDraftManager.Instance;

        // Build a map of DraftId → list of selecting players
        var selectionMap = new Dictionary<int, List<PlayerDraftState>>();
        foreach (var ps in manager.PlayerStates)
        {
            if (ps.HasSelected && !ps.IsOptedOut)
            {
                if (!selectionMap.ContainsKey(ps.SelectedDraftId))
                    selectionMap[ps.SelectedDraftId] = new();
                selectionMap[ps.SelectedDraftId].Add(ps);
            }
        }

        // Add avatar overlays on selected cards
        foreach (var (draftId, players) in selectionMap)
        {
            if (!_cardCells.TryGetValue(draftId, out var cell) ||
                !GodotObject.IsInstanceValid(cell))
                continue;

            var overlays = new List<(int slot, Control overlay)>();

            for (int i = 0; i < players.Count; i++)
            {
                var ps = players[i];
                bool isLocal = manager.IsLocalPlayerState(ps);

                // Create a character icon avatar badge
                var avatarBadge = CreatePlayerAvatarBadge(ps.PlayerSlot, ps.DisplayName, isLocal);

                // Position at bottom-right, stacking horizontally
                avatarBadge.Position = new Vector2(
                    cell.Size.X - 38 - (i * 34),
                    cell.Size.Y - 38);

                cell.AddChild(avatarBadge);
                overlays.Add((ps.PlayerSlot, avatarBadge));
            }

            _cardSelectionOverlays[draftId] = overlays;
        }
    }

    /// <summary>
    /// Create a small player avatar badge using the game's native character icon texture.
    /// Falls back to colored circle + initial if character icon is unavailable.
    /// Like the relic picking overlay in treasure rooms (NMultiplayerVoteContainer).
    /// </summary>
    private static Control CreatePlayerAvatarBadge(int playerSlot, string displayName, bool isLocal)
    {
        Color playerColor = playerSlot < PoolColors.Length
            ? PoolColors[playerSlot]
            : new Color(0.5f, 0.5f, 0.5f);

        // Try to get the player's character icon texture (like the vote container does)
        Texture2D? iconTexture = null;
        Texture2D? outlineTexture = null;
        try
        {
            var ps = SharedDraftManager.Instance.PlayerStates
                .FirstOrDefault(p => p.PlayerSlot == playerSlot);
            if (ps?.Player?.Character != null)
            {
                iconTexture = ps.Player.Character.IconTexture;
                outlineTexture = ps.Player.Character.IconOutlineTexture;
            }
        }
        catch (System.Exception ex)
        {
            ModEntry.Logger.Info($"[Avatar] Could not get character icon for slot {playerSlot}: {ex.Message}");
        }

        if (iconTexture != null)
        {
            // ── Use native character icon (like NMultiplayerVoteContainer) ──
            var container = new Control();
            container.MouseFilter = Control.MouseFilterEnum.Ignore;
            container.CustomMinimumSize = new Vector2(32, 32);

            var iconRect = new TextureRect();
            iconRect.MouseFilter = Control.MouseFilterEnum.Ignore;
            iconRect.Texture = iconTexture;
            iconRect.CustomMinimumSize = new Vector2(32, 32);
            iconRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            iconRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            container.AddChild(iconRect);

            // Add outline on top
            if (outlineTexture != null)
            {
                var outlineRect = new TextureRect();
                outlineRect.MouseFilter = Control.MouseFilterEnum.Ignore;
                outlineRect.Texture = outlineTexture;
                outlineRect.CustomMinimumSize = new Vector2(32, 32);
                outlineRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
                outlineRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                // Tint outline with player color if local, white otherwise
                outlineRect.Modulate = isLocal ? AccentGold : Colors.White;
                container.AddChild(outlineRect);
            }

            // Add a subtle glow ring for local player
            if (isLocal)
            {
                var glowPanel = new PanelContainer();
                glowPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
                glowPanel.CustomMinimumSize = new Vector2(36, 36);
                glowPanel.Position = new Vector2(-2, -2);
                var glowStyle = new StyleBoxFlat();
                glowStyle.BgColor = new Color(0, 0, 0, 0);
                glowStyle.BorderColor = AccentGold;
                glowStyle.SetBorderWidthAll(2);
                glowStyle.SetCornerRadiusAll(18);
                glowStyle.SetContentMarginAll(0);
                glowPanel.AddThemeStyleboxOverride("panel", glowStyle);
                container.AddChild(glowPanel);
                // Move glow behind icon
                container.MoveChild(glowPanel, 0);
            }

            // Tooltip with full name
            iconRect.TooltipText = isLocal ? $"{displayName} (你)" : displayName;

            return container;
        }
        else
        {
            // ── Fallback: colored circle with initial ──
            var container = new PanelContainer();
            container.MouseFilter = Control.MouseFilterEnum.Ignore;
            container.CustomMinimumSize = new Vector2(28, 28);

            var badgeStyle = new StyleBoxFlat();
            badgeStyle.BgColor = playerColor;
            badgeStyle.BorderColor = isLocal ? AccentGold : new Color(1, 1, 1, 0.8f);
            badgeStyle.SetBorderWidthAll(isLocal ? 2 : 1);
            badgeStyle.SetCornerRadiusAll(14); // Circular
            badgeStyle.SetContentMarginAll(0);
            badgeStyle.ShadowColor = new Color(0, 0, 0, 0.6f);
            badgeStyle.ShadowSize = 2;
            badgeStyle.ShadowOffset = new Vector2(1, 1);
            container.AddThemeStyleboxOverride("panel", badgeStyle);

            var initial = new Label();
            initial.MouseFilter = Control.MouseFilterEnum.Ignore;
            initial.Text = !string.IsNullOrEmpty(displayName) ? displayName[..1].ToUpper() : "?";
            initial.AddThemeFontSizeOverride("font_size", 13);
            initial.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.95f));
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            initial.VerticalAlignment = VerticalAlignment.Center;
            container.AddChild(initial);

            container.TooltipText = isLocal ? $"{displayName} (你)" : displayName;

            return container;
        }
    }

    // ── Card info helpers ──

    private static string FormatEntryName(string entry)
    {
        if (string.IsNullOrEmpty(entry))
            return "???";
        var words = entry.Split('_');
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
                words[i] = char.ToUpper(words[i][0]) + words[i][1..].ToLower();
        }
        return string.Join(" ", words);
    }

    // ═══════════════════════════════════════════════
    //  POPULATE PLAYER STATUS — Premium cards
    // ═══════════════════════════════════════════════

    private static void PopulatePlayerStatus()
    {
        if (_sidebarContainer == null) return;

        _playerStatusLabels.Clear();
        _playerAvatarContainers.Clear();

        // Remove old player entries (keep title + separator = first 2 children)
        var children = _sidebarContainer.GetChildren();
        for (int i = children.Count - 1; i >= 2; i--)
        {
            children[i].QueueFree();
        }

        var manager = SharedDraftManager.Instance;

        foreach (var ps in manager.PlayerStates)
        {
            Color playerColor = ps.PlayerSlot < PoolColors.Length
                ? PoolColors[ps.PlayerSlot]
                : new Color(0.5f, 0.5f, 0.5f);

            bool isLocal = manager.IsLocalPlayerState(ps);

            // Player card panel
            var playerPanel = new PanelContainer();
            var playerCardStyle = new StyleBoxFlat();
            playerCardStyle.BgColor = new Color(playerColor.R * 0.15f, playerColor.G * 0.15f, playerColor.B * 0.15f, 0.6f);
            playerCardStyle.BorderColor = new Color(playerColor.R, playerColor.G, playerColor.B, 0.4f);
            playerCardStyle.BorderWidthLeft = 3;
            playerCardStyle.BorderWidthTop = 1;
            playerCardStyle.BorderWidthRight = 1;
            playerCardStyle.BorderWidthBottom = 1;
            playerCardStyle.SetCornerRadiusAll(8);
            playerCardStyle.SetContentMarginAll(8);
            playerCardStyle.ShadowColor = new Color(0, 0, 0, 0.3f);
            playerCardStyle.ShadowSize = 2;
            playerCardStyle.ShadowOffset = new Vector2(1, 2);
            playerPanel.AddThemeStyleboxOverride("panel", playerCardStyle);

            var playerVBox = new VBoxContainer();
            playerVBox.AddThemeConstantOverride("separation", 2);
            playerPanel.AddChild(playerVBox);

            // Top row: avatar + name
            var topRow = new HBoxContainer();
            topRow.AddThemeConstantOverride("separation", 8);
            playerVBox.AddChild(topRow);

            // Small avatar — use character icon if available
            Texture2D? charIcon = null;
            try
            {
                if (ps.Player?.Character != null)
                    charIcon = ps.Player.Character.IconTexture;
            }
            catch { /* ignore */ }

            Control avatar;
            if (charIcon != null)
            {
                avatar = new TextureRect();
                ((TextureRect)avatar).Texture = charIcon;
                avatar.CustomMinimumSize = new Vector2(24, 24);
                ((TextureRect)avatar).StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
                ((TextureRect)avatar).ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            }
            else
            {
                // Fallback: colored circle
                avatar = new PanelContainer();
                avatar.CustomMinimumSize = new Vector2(24, 24);
                var avatarStyle = new StyleBoxFlat();
                avatarStyle.BgColor = playerColor;
                avatarStyle.SetCornerRadiusAll(12);
                avatarStyle.SetContentMarginAll(0);
                ((PanelContainer)avatar).AddThemeStyleboxOverride("panel", avatarStyle);

                var avatarInitial = new Label();
                avatarInitial.Text = !string.IsNullOrEmpty(ps.DisplayName) ? ps.DisplayName[..1].ToUpper() : "?";
                avatarInitial.AddThemeFontSizeOverride("font_size", 12);
                avatarInitial.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.95f));
                avatarInitial.HorizontalAlignment = HorizontalAlignment.Center;
                avatarInitial.VerticalAlignment = VerticalAlignment.Center;
                ((PanelContainer)avatar).AddChild(avatarInitial);
            }
            topRow.AddChild(avatar);

            _playerAvatarContainers[ps.PlayerSlot] = avatar;

            // Player name
            var nameLabel = new Label();
            nameLabel.Text = ps.DisplayName + (isLocal ? " (你)" : "");
            nameLabel.AddThemeFontSizeOverride("font_size", 13);
            nameLabel.AddThemeColorOverride("font_color",
                isLocal ? AccentGold : TextPrimary);
            nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            nameLabel.ClipText = true;
            topRow.AddChild(nameLabel);

            // Status label
            var statusLabel = new Label();
            statusLabel.Text = "⏳ 等待中...";
            statusLabel.AddThemeFontSizeOverride("font_size", 11);
            statusLabel.AddThemeColorOverride("font_color", StatusWaiting);
            playerVBox.AddChild(statusLabel);

            _playerStatusLabels[ps.PlayerSlot] = statusLabel;
            _sidebarContainer.AddChild(playerPanel);
        }
    }

    // ═══════════════════════════════════════════════
    //  EVENT HANDLERS
    // ═══════════════════════════════════════════════

    private static void OnCellClicked(int draftId, Control clickedCell)
    {
        _selectedDraftId = draftId;

        foreach (var (id, cell) in _cardCells)
        {
            if (GodotObject.IsInstanceValid(cell) && id != draftId)
                SetCellSelected(cell, false);
        }

        SetCellSelected(clickedCell, true);
        UpdateSelectedInfo();
        UpdateConfirmButton();
    }

    private static void OnConfirmPressed()
    {
        if (_selectedDraftId < 0)
        {
            SetStatus("⚠ 请先选择一张卡牌！", new Color(1, 0.4f, 0.4f));
            return;
        }

        SetStatus("已确认选择，等待其他玩家...", StatusReady);

        if (_confirmButton != null)
            _confirmButton.Disabled = true;
        if (_skipButton != null)
            _skipButton.Disabled = true;
        if (_endDraftButton != null)
            _endDraftButton.Disabled = false; // Keep End Draft enabled after confirming
        foreach (var (_, cell) in _cardCells)
        {
            if (GodotObject.IsInstanceValid(cell))
                SetCellInteractable(cell, false);
        }

        SharedDraftManager.Instance.OnLocalPlayerPickCard(_selectedDraftId);
    }

    private static void OnSkipPressed()
    {
        SetStatus("已跳过选卡 — 本轮不获取任何卡牌。", new Color(0.6f, 0.6f, 0.6f));

        if (_confirmButton != null)
            _confirmButton.Disabled = true;
        if (_skipButton != null)
            _skipButton.Disabled = true;
        if (_endDraftButton != null)
            _endDraftButton.Disabled = true;
        foreach (var (_, cell) in _cardCells)
        {
            if (GodotObject.IsInstanceValid(cell))
                SetCellInteractable(cell, false);
        }

        SharedDraftManager.Instance.OnLocalPlayerOptOut();
        ModEntry.Logger.Info("Skip pressed → player opted out (no card selected).");
    }

    private static void OnEndDraftPressed()
    {
        SetStatus("⚡ 已请求结束选牌，等待结算...", new Color(1.0f, 0.6f, 0.3f));

        if (_endDraftButton != null)
            _endDraftButton.Disabled = true;

        SharedDraftManager.Instance.OnLocalPlayerEndDraft();
        ModEntry.Logger.Info("End Draft pressed → requesting immediate settlement.");
    }

    // ═══════════════════════════════════════════════
    //  WAITING / SELECTING STATE MANAGEMENT
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Legacy method — redirects to ShowWaitingForResolve for backward compatibility.
    /// </summary>
    public static void ShowWaitingState()
    {
        ShowWaitingForResolve();
    }

    /// <summary>
    /// Show "waiting for resolve" state — displayed during Resolving phase.
    /// UI is visible but all interactions are disabled.
    /// Players who enter during this phase can see the cards but cannot interact.
    /// </summary>
    public static void ShowWaitingForResolve()
    {
        _isInWaitingState = true;

        SetStatus("⏳ 结算进行中，请等待...", new Color(1.0f, 0.7f, 0.3f));

        foreach (var (_, cell) in _cardCells)
        {
            if (GodotObject.IsInstanceValid(cell))
                SetCellInteractable(cell, false);
        }

        if (_confirmButton != null)
            _confirmButton.Disabled = true;
        if (_skipButton != null)
            _skipButton.Disabled = true;
        if (_endDraftButton != null)
            _endDraftButton.Disabled = true;

        if (_countdownLabel != null)
            _countdownLabel.Visible = false;

        RefreshPlayerStatus();
    }

    public static void ShowSelectingState()
    {
        _isInWaitingState = false;

        SetStatus("从共享卡池中选择一张卡牌。", new Color(0.7f, 0.8f, 0.9f));

        // Enable cards that are not yet awarded
        var manager = SharedDraftManager.Instance;
        var awardedIds = manager.AwardedDraftIds;

        foreach (var (draftId, cell) in _cardCells)
        {
            if (GodotObject.IsInstanceValid(cell))
            {
                if (awardedIds.Contains(draftId))
                {
                    // Already awarded — keep disabled and greyed out
                    SetCellInteractable(cell, false);
                    cell.Modulate = new Color(0.3f, 0.3f, 0.3f, 0.5f);
                }
                else
                {
                    SetCellInteractable(cell, true);
                }
            }
        }

        if (_confirmButton != null)
            _confirmButton.Disabled = _selectedDraftId < 0;
        if (_skipButton != null)
            _skipButton.Disabled = false;
        if (_endDraftButton != null)
            _endDraftButton.Disabled = false;

        if (_countdownLabel != null)
            _countdownLabel.Visible = false;

        RefreshPlayerStatus();
    }

    public static void UpdateCountdown(int secondsLeft)
    {
        if (_countdownLabel != null && GodotObject.IsInstanceValid(_countdownLabel))
        {
            _countdownLabel.Text = $"⏱ 等待超时: {secondsLeft}s";

            if (secondsLeft <= 10)
                _countdownLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.4f, 0.3f));
            else
                _countdownLabel.AddThemeColorOverride("font_color", AccentGold);
        }
    }

    // ═══════════════════════════════════════════════
    //  UI HELPERS
    // ═══════════════════════════════════════════════

    private static void UpdateSelectedInfo()
    {
        if (_selectedInfoLabel == null) return;

        if (_selectedDraftId < 0)
        {
            _selectedInfoLabel.Text = "未选择卡牌";
            _selectedInfoLabel.AddThemeColorOverride("font_color", TextDim);
        }
        else
        {
            var draftCard = SharedDraftManager.Instance.DraftPool
                .FirstOrDefault(dc => dc.DraftId == _selectedDraftId);
            if (draftCard != null)
            {
                if (draftCard.HasCardModel)
                {
                    string title = draftCard.Card!.Title ?? draftCard.CardEntry;
                    _selectedInfoLabel.Text = $"已选择：{title}";
                }
                else
                {
                    _selectedInfoLabel.Text = $"已选择：{FormatEntryName(draftCard.CardEntry)} (对方角色的卡牌)";
                }
                _selectedInfoLabel.AddThemeColorOverride("font_color", AccentGold);
            }
        }
    }

    private static void UpdateConfirmButton()
    {
        if (_confirmButton != null)
            _confirmButton.Disabled = _selectedDraftId < 0;
    }

    public static void SetStatus(string text, Color? color = null)
    {
        if (_statusLabel == null) return;
        _statusLabel.Text = text;
        if (color.HasValue)
            _statusLabel.AddThemeColorOverride("font_color", color.Value);
    }

    public static void MarkCardContested(int draftId)
    {
        if (_cardCells.TryGetValue(draftId, out var cell) &&
            GodotObject.IsInstanceValid(cell))
        {
            var contestedStyle = new StyleBoxFlat();
            contestedStyle.BgColor = new Color(0.25f, 0.08f, 0.06f, 0.5f);
            contestedStyle.BorderColor = StatusConflict;
            contestedStyle.SetBorderWidthAll(2);
            contestedStyle.SetCornerRadiusAll(6);
            contestedStyle.ContentMarginLeft = 4;
            contestedStyle.ContentMarginTop = 2;
            contestedStyle.ContentMarginRight = 2;
            contestedStyle.ContentMarginBottom = 2;
            contestedStyle.ShadowColor = new Color(1.0f, 0.3f, 0.1f, 0.3f);
            contestedStyle.ShadowSize = 4;
            cell.AddThemeStyleboxOverride("panel", contestedStyle);
            cell.SetMeta("normal_style", contestedStyle);
        }
    }

    // ═══════════════════════════════════════════════
    //  3D STYLE HELPERS
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Create a 3D beveled panel StyleBoxFlat with highlight top edge and shadow bottom edge.
    /// </summary>
    private static StyleBoxFlat Create3DPanelStyle(
        Color bgColor, Color borderColor, Color highlightColor, Color shadowColor,
        int borderWidth = 2, int cornerRadius = 12)
    {
        var style = new StyleBoxFlat();
        style.BgColor = bgColor;
        style.BorderColor = borderColor;
        style.SetBorderWidthAll(borderWidth);
        style.SetCornerRadiusAll(cornerRadius);
        style.SetContentMarginAll(12);
        // 3D shadow
        style.ShadowColor = shadowColor;
        style.ShadowSize = 5;
        style.ShadowOffset = new Vector2(2, 4);
        return style;
    }

    /// <summary>
    /// Create a gradient-like separator using a thin ColorRect.
    /// </summary>
    private static Control CreateGradientSeparator(Color accentColor, int height = 2)
    {
        var separator = new ColorRect();
        separator.CustomMinimumSize = new Vector2(0, height);
        separator.Color = new Color(accentColor.R, accentColor.G, accentColor.B, 0.3f);
        separator.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return separator;
    }

    /// <summary>
    /// Create a 3D styled button with highlight and shadow effects.
    /// </summary>
    private static Button Create3DButton(
        string text, Color normalBg, Color hoverBg, Color disabledBg, Color glowColor)
    {
        var btn = new Button();
        btn.Text = text;

        // Normal — raised 3D
        var normalStyle = new StyleBoxFlat();
        normalStyle.BgColor = normalBg;
        normalStyle.BorderColor = new Color(normalBg.R * 1.3f, normalBg.G * 1.3f, normalBg.B * 1.3f, 0.6f);
        normalStyle.SetBorderWidthAll(1);
        normalStyle.SetCornerRadiusAll(8);
        normalStyle.SetContentMarginAll(10);
        normalStyle.ShadowColor = new Color(0, 0, 0, 0.4f);
        normalStyle.ShadowSize = 3;
        normalStyle.ShadowOffset = new Vector2(1, 2);
        btn.AddThemeStyleboxOverride("normal", normalStyle);

        // Hover — elevated
        var hoverStyle = new StyleBoxFlat();
        hoverStyle.BgColor = hoverBg;
        hoverStyle.BorderColor = glowColor;
        hoverStyle.SetBorderWidthAll(2);
        hoverStyle.SetCornerRadiusAll(8);
        hoverStyle.SetContentMarginAll(10);
        hoverStyle.ShadowColor = new Color(glowColor.R * 0.3f, glowColor.G * 0.3f, glowColor.B * 0.3f, 0.5f);
        hoverStyle.ShadowSize = 5;
        hoverStyle.ShadowOffset = new Vector2(2, 3);
        btn.AddThemeStyleboxOverride("hover", hoverStyle);

        // Pressed — inset
        var pressedStyle = new StyleBoxFlat();
        pressedStyle.BgColor = new Color(normalBg.R * 0.8f, normalBg.G * 0.8f, normalBg.B * 0.8f, normalBg.A);
        pressedStyle.BorderColor = glowColor;
        pressedStyle.SetBorderWidthAll(2);
        pressedStyle.SetCornerRadiusAll(8);
        pressedStyle.SetContentMarginAll(10);
        btn.AddThemeStyleboxOverride("pressed", pressedStyle);

        // Disabled
        var disabledStyle = new StyleBoxFlat();
        disabledStyle.BgColor = disabledBg;
        disabledStyle.SetCornerRadiusAll(8);
        disabledStyle.SetContentMarginAll(10);
        btn.AddThemeStyleboxOverride("disabled", disabledStyle);

        // Text colors
        btn.AddThemeColorOverride("font_color", TextPrimary);
        btn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 1.0f, 1.0f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(0.9f, 0.9f, 0.9f));
        btn.AddThemeColorOverride("font_disabled_color", new Color(0.4f, 0.4f, 0.45f));
        btn.AddThemeFontSizeOverride("font_size", 15);

        return btn;
    }

    // ═══════════════════════════════════════════════
    //  SCENE TREE INJECTION
    // ═══════════════════════════════════════════════

    private static void InjectIntoSceneTree()
    {
        if (_canvasLayer == null) return;

        try
        {
            var sceneTree = Engine.GetMainLoop() as SceneTree;
            if (sceneTree?.Root == null)
            {
                ModEntry.Logger.Error("Could not get SceneTree for SharedDraftScreen injection.");
                return;
            }
            sceneTree.Root.CallDeferred("add_child", _canvasLayer);
            ModEntry.Logger.Info("SharedDraftScreen injected into scene tree (deferred).");
        }
        catch (System.Exception ex)
        {
            ModEntry.Logger.Error($"Failed to inject SharedDraftScreen: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════
    //  CLEANUP
    // ═══════════════════════════════════════════════

    public static void Cleanup()
    {
        // Release native NCard instances
        ReleaseNativeCards();

        if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
            _canvasLayer.QueueFree();

        _canvasLayer = null;
        _dimBackground = null;
        _cardGrid = null;
        _sidebarContainer = null;
        _titleLabel = null;
        _selectedInfoLabel = null;
        _statusLabel = null;
        _confirmButton = null;
        _skipButton = null;
        _endDraftButton = null;
        _countdownLabel = null;
        _inputHandler = null;
        _isPauseMenuOpen = false;
        _selectedDraftId = -1;
        _isInWaitingState = false;
        _cardCells.Clear();
        _playerStatusLabels.Clear();
        _playerAvatarContainers.Clear();
        _cardSelectionOverlays.Clear();
    }

    /// <summary>
    /// Release all native NCard instances. They were obtained from NodePool via
    /// NCard.Create(), so they should be freed when no longer needed.
    /// </summary>
    private static void ReleaseNativeCards()
    {
        foreach (var nCard in _nativeCards)
        {
            try
            {
                if (GodotObject.IsInstanceValid(nCard))
                {
                    // Remove from parent so QueueFree on parent doesn't double-free
                    var parent = nCard.GetParent();
                    if (parent != null)
                        parent.RemoveChild(nCard);

                    // NCard implements IPoolable — ideally we'd return it to pool,
                    // but QueueFree also works since NodePool handles this
                    nCard.QueueFree();
                }
            }
            catch (System.Exception ex)
            {
                ModEntry.Logger.Info($"[NCard] Cleanup issue: {ex.Message}");
            }
        }
        _nativeCards.Clear();
    }

    // ═══════════════════════════════════════════════
    //  PAUSE MENU SUPPORT
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Hide the SharedDraft overlay and let the game's native pause menu appear.
    /// Called when the player presses Escape.
    ///
    /// Flow:
    /// 1. Hide the SharedDraft CanvasLayer so it no longer blocks input/rendering
    /// 2. After a brief delay, send a synthetic Escape key event so the game's
    ///    native pause/settings menu opens (since the original Escape was consumed)
    /// 3. Poll for the pause state to end, then restore the overlay
    /// </summary>
    public static void OpenPauseMenu()
    {
        if (_isPauseMenuOpen || !IsVisible) return;

        _isPauseMenuOpen = true;

        // Step 1: Hide the overlay
        if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
            _canvasLayer.Visible = false;

        ModEntry.Logger.Info("[SharedDraft] Overlay hidden for pause menu (Escape pressed).");

        // Step 2: After a brief delay, re-send an Escape key event so the game
        // processes it and opens the pause menu. We need the delay because the
        // current frame's input has already been consumed.
        var sceneTree = Engine.GetMainLoop() as SceneTree;
        if (sceneTree != null)
        {
            sceneTree.CreateTimer(0.05).Timeout += () =>
            {
                // Synthesize Escape key press
                var keyDown = new InputEventKey();
                keyDown.Keycode = Key.Escape;
                keyDown.Pressed = true;
                keyDown.PhysicalKeycode = Key.Escape;
                Input.ParseInputEvent(keyDown);

                // And release after a tiny delay
                sceneTree.CreateTimer(0.05).Timeout += () =>
                {
                    var keyUp = new InputEventKey();
                    keyUp.Keycode = Key.Escape;
                    keyUp.Pressed = false;
                    keyUp.PhysicalKeycode = Key.Escape;
                    Input.ParseInputEvent(keyUp);

                    // Step 3: Start watching for the pause menu to close
                    StartPauseMenuWatcher(sceneTree);
                };
            };
        }
        else
        {
            // Fallback: just restore after a few seconds
            RestoreFromPauseMenuDelayed(5.0);
        }
    }

    /// <summary>
    /// Poll for the pause menu to close, then restore the SharedDraft overlay.
    /// Checks every 0.5 seconds. Gives up after 120 seconds to prevent infinite polling.
    /// </summary>
    private static void StartPauseMenuWatcher(SceneTree sceneTree)
    {
        int pollCount = 0;
        const int maxPolls = 240; // 120 seconds max

        void CheckPauseState()
        {
            if (!_isPauseMenuOpen) return; // Already restored

            pollCount++;
            if (pollCount > maxPolls)
            {
                ModEntry.Logger.Info("[SharedDraft] Pause menu watcher timeout, restoring overlay.");
                RestoreFromPauseMenu();
                return;
            }

            // Heuristic: the pause menu is considered closed when SceneTree is not paused.
            // If the game doesn't use SceneTree.Paused, we also check after a reasonable delay.
            bool pauseMenuClosed = !sceneTree.Paused;

            if (pauseMenuClosed && pollCount >= 2)
            {
                // Wait at least 1 second (2 polls) before considering the menu closed,
                // to avoid restoring too early before the menu has fully opened.
                RestoreFromPauseMenu();
            }
            else
            {
                sceneTree.CreateTimer(0.5).Timeout += CheckPauseState;
            }
        }

        // Start polling after the pause menu has had time to open
        sceneTree.CreateTimer(0.5).Timeout += CheckPauseState;
    }

    /// <summary>
    /// Restore the SharedDraft overlay after the pause menu closes.
    /// </summary>
    public static void RestoreFromPauseMenu()
    {
        if (!_isPauseMenuOpen) return;

        _isPauseMenuOpen = false;

        if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
            _canvasLayer.Visible = true;

        ModEntry.Logger.Info("[SharedDraft] Overlay restored after pause menu closed.");
    }

    /// <summary>
    /// Fallback: restore after a fixed delay if SceneTree polling is unavailable.
    /// </summary>
    private static void RestoreFromPauseMenuDelayed(double seconds)
    {
        var sceneTree = Engine.GetMainLoop() as SceneTree;
        if (sceneTree != null)
        {
            sceneTree.CreateTimer(seconds).Timeout += RestoreFromPauseMenu;
        }
        else
        {
            RestoreFromPauseMenu();
        }
    }
}

/// <summary>
/// Lightweight Node that handles _UnhandledInput for the SharedDraft overlay.
/// Listens for Escape key press to trigger the game's native pause/settings menu.
/// This is a separate class (not static) because it needs to inherit from Node
/// to receive Godot input callbacks.
///
/// Key flow: When Escape is pressed while SharedDraft overlay is visible,
/// we consume the event (preventing it from reaching the game), hide the overlay,
/// then re-send Escape so the game's pause menu opens without interference.
/// </summary>
internal partial class DraftInputHandler : Node
{
    public override void _UnhandledInput(InputEvent @event)
    {
        // Only handle input when the SharedDraft overlay is visible
        if (!SharedDraftScreen.IsVisible) return;

        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.Escape)
            {
                // Consume the event so it doesn't propagate further in this frame
                GetViewport().SetInputAsHandled();

                // Trigger the pause menu flow
                SharedDraftScreen.OpenPauseMenu();
            }
        }
    }
}
