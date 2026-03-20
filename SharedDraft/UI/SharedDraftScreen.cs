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
///   CanvasLayer (Layer=1, very low so native UI overlays on top)
///   └── ColorRect (dim background, top 80px uncovered for HUD bar, MouseFilter=Stop to block clicks in dim area)
///       └── CenterContainer (MouseFilter=Ignore)
///           └── PanelContainer (transparent, MouseFilter=Ignore)
///               └── VBoxContainer
///                   ├── HBoxContainer (title bar)
///                   ├── HBoxContainer (main content)
///                   │   ├── ScrollContainer (card grid)
///                   │   │   └── CenterContainer (centers the grid)
///                   │   │       └── GridContainer (NCard cells, h_sep=32, v_sep=28)
///                   │   └── VBoxContainer (sidebar, no background)
///                   │       ├── ScrollContainer (player status)
///                   │       │   └── VBoxContainer (_sidebarContainer)
///                   │       └── VBoxContainer (buttonSection — fixed)
///                   │           ├── Label (selected card info)
///                   │           ├── Button (confirm)
///                   │           ├── Button (skip)
///                   │           └── Button (end draft)
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

    // ── Toggle visibility button (hide/show the draft overlay without changing draft state) ──
    // Lives on a separate CanvasLayer so it stays visible when main overlay is hidden
    private static CanvasLayer? _toggleCanvasLayer;
    private static Button? _toggleVisibilityButton;
    private static bool _isManuallyHidden = false;  // True when user clicked the toggle button to hide

    // ── Input handler for Escape key (to open game pause menu) ──
    private static DraftInputHandler? _inputHandler;
    private static bool _isPauseMenuOpen = false;
    private static bool _isHiddenForNativeScreen = false;  // True when auto-hidden for native screen (settings/map/deck)

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

    // NCard.defaultSize = 300x422. Scale to ~58% for clearer card display with cost icons visible.
    private const float CardScale = 0.58f;
    private static readonly Vector2 NCardDefaultSize = new(300f, 422f);
    private static readonly Vector2 ScaledCardSize = new(
        NCardDefaultSize.X * CardScale,   // ~174
        NCardDefaultSize.Y * CardScale    // ~245
    );

    // Cell size = scaled card + generous margin for breathing room
    private static readonly Vector2 CellSize = new(
        ScaledCardSize.X + 24,  // ~198
        ScaledCardSize.Y + 24   // ~269
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
        _isManuallyHidden = false;
        UpdateSelectedInfo();
        UpdateConfirmButton();
        UpdateToggleButton();

        if (_canvasLayer != null)
            _canvasLayer.Visible = true;
        if (_dimBackground != null)
            _dimBackground.Visible = true;
        if (_toggleCanvasLayer != null)
            _toggleCanvasLayer.Visible = true;
    }

    public static void Hide()
    {
        if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
            _canvasLayer.Visible = false;
        if (_toggleCanvasLayer != null && GodotObject.IsInstanceValid(_toggleCanvasLayer))
            _toggleCanvasLayer.Visible = false;
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
                else if (ps.IsOptedOut)
                {
                    // Opted out but not completed — can re-enter
                    statusIcon = "⏸";
                    statusColor = new Color(0.6f, 0.5f, 0.3f); // Dim yellow — paused
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

                string suffix = isLocal ? " (You)" : "";
                string completedSuffix = ps.IsCompleted && ps.IsOptedOut ? " [Skipped]"
                    : ps.IsCompleted ? " [Awarded]"
                    : ps.IsOptedOut ? " [Paused]"
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

        SetStatus("You lost the tiebreaker. Please select another card.", new Color(1.0f, 0.6f, 0.3f));

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
        _canvasLayer.Layer = 1;  // Very low layer so native UI (deck viewer, settings, map) can overlay on top
        _canvasLayer.Visible = false;

        // Dim background — leave top ~80px uncovered for the native HUD bar
        // (HP, gold, map, deck, settings buttons). MouseFilter=Stop so clicks within
        // the dimmed area are consumed (preventing accidental clicks on game buttons
        // that are hidden behind the dim overlay). The uncovered top 80px allows the
        // native HUD buttons to remain clickable; DraftInputHandler._Process() watches
        // for native screens (settings, map, deck viewer) opening and auto-hides the
        // draft overlay so those screens display without interference.
        _dimBackground = new ColorRect();
        _dimBackground.Name = "DraftDimBackground";
        _dimBackground.Color = new Color(0.0f, 0.0f, 0.02f, 0.75f);
        _dimBackground.AnchorLeft = 0;
        _dimBackground.AnchorTop = 0;
        _dimBackground.AnchorRight = 1;
        _dimBackground.AnchorBottom = 1;
        _dimBackground.OffsetTop = 80;  // Leave top 80px uncovered for native HUD bar
        _dimBackground.OffsetLeft = 0;
        _dimBackground.OffsetRight = 0;
        _dimBackground.OffsetBottom = 0;
        _dimBackground.MouseFilter = Control.MouseFilterEnum.Stop;  // Block clicks in dim area from reaching hidden game buttons
        _canvasLayer.AddChild(_dimBackground);

        // CenterContainer — MouseFilter=Ignore so clicks outside the panel pass through
        // to native UI buttons (deck viewer, settings, etc.) beneath the overlay
        var centerContainer = new CenterContainer();
        centerContainer.Name = "DraftCenterContainer";
        centerContainer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        centerContainer.MouseFilter = Control.MouseFilterEnum.Ignore;
        _dimBackground.AddChild(centerContainer);

        // ── Main panel — transparent, no border (just dim background is enough) ──
        var panelContainer = new PanelContainer();
        panelContainer.Name = "SharedDraftPanel";
        // Use anchors for responsive sizing instead of fixed size
        panelContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        panelContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        panelContainer.CustomMinimumSize = new Vector2(1300, 800);

        // Transparent panel style — no visible border, just content padding
        var panelStyle = new StyleBoxFlat();
        panelStyle.BgColor = new Color(0, 0, 0, 0);  // Fully transparent
        panelStyle.SetBorderWidthAll(0);
        panelStyle.SetCornerRadiusAll(0);
        panelStyle.SetContentMarginAll(12);
        panelContainer.AddThemeStyleboxOverride("panel", panelStyle);
        panelContainer.MouseFilter = Control.MouseFilterEnum.Ignore;  // Let clicks pass through transparent areas
        centerContainer.AddChild(panelContainer);

        // Main vertical layout
        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 8);
        mainVBox.MouseFilter = Control.MouseFilterEnum.Ignore;
        panelContainer.AddChild(mainVBox);

        // ── Title Bar ──
        BuildTitleBar(mainVBox);

        // ── Main content area (cards + sidebar) ──
        var contentHBox = new HBoxContainer();
        contentHBox.AddThemeConstantOverride("separation", 12);
        contentHBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        mainVBox.AddChild(contentHBox);

        // Card grid area (scrollable, centered)
        var cardScroll = new ScrollContainer();
        cardScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        cardScroll.SizeFlagsStretchRatio = 4;
        cardScroll.CustomMinimumSize = new Vector2(900, 0);
        contentHBox.AddChild(cardScroll);

        // Center the grid within the scroll area
        var gridCenterContainer = new CenterContainer();
        gridCenterContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        gridCenterContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        gridCenterContainer.MouseFilter = Control.MouseFilterEnum.Ignore;
        cardScroll.AddChild(gridCenterContainer);

        _cardGrid = new GridContainer();
        _cardGrid.Name = "CardGrid";
        _cardGrid.Columns = 3;
        _cardGrid.AddThemeConstantOverride("h_separation", 32);
        _cardGrid.AddThemeConstantOverride("v_separation", 28);
        _cardGrid.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        _cardGrid.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        gridCenterContainer.AddChild(_cardGrid);

        // ── Sidebar (player status + buttons) — transparent, no background ──
        BuildSidebar(contentHBox);

        // ── Bottom section (status label only) ──
        BuildBottomSection(mainVBox);

        // ── Input handler for Escape key → pause menu ──
        _inputHandler = new DraftInputHandler();
        _inputHandler.Name = "DraftInputHandler";
        _canvasLayer.AddChild(_inputHandler);

        // ── Toggle visibility button on separate CanvasLayer ──
        BuildToggleButton();
    }

    private static void BuildTitleBar(VBoxContainer parent)
    {
        var titleBar = new HBoxContainer();
        titleBar.AddThemeConstantOverride("separation", 12);
        titleBar.Alignment = BoxContainer.AlignmentMode.Center;
        parent.AddChild(titleBar);

        _titleLabel = new Label();
        _titleLabel.Text = SharedDraftConfig.DebugMode
            ? "🃏  S H A R E D   D R A F T  [DEBUG]"
            : "🃏  S H A R E D   D R A F T";
        _titleLabel.AddThemeFontSizeOverride("font_size", 24);
        _titleLabel.AddThemeColorOverride("font_color", AccentGold);
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        titleBar.AddChild(_titleLabel);
    }

    private static void BuildSidebar(HBoxContainer parent)
    {
        // Outer VBox to split sidebar into scrollable player area + fixed button area
        var sidebarOuterVBox = new VBoxContainer();
        sidebarOuterVBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        sidebarOuterVBox.SizeFlagsStretchRatio = 1;
        sidebarOuterVBox.CustomMinimumSize = new Vector2(220, 0);
        sidebarOuterVBox.AddThemeConstantOverride("separation", 8);
        parent.AddChild(sidebarOuterVBox);

        // ── Top: Scrollable player status area ──
        var sidebarScroll = new ScrollContainer();
        sidebarScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        sidebarScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        sidebarOuterVBox.AddChild(sidebarScroll);

        _sidebarContainer = new VBoxContainer();
        _sidebarContainer.AddThemeConstantOverride("separation", 6);
        _sidebarContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        sidebarScroll.AddChild(_sidebarContainer);

        // Sidebar title
        var sidebarTitle = new Label();
        sidebarTitle.Text = "👥 Players";
        sidebarTitle.AddThemeFontSizeOverride("font_size", 16);
        sidebarTitle.AddThemeColorOverride("font_color", AccentPurple);
        sidebarTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _sidebarContainer.AddChild(sidebarTitle);

        _sidebarContainer.AddChild(CreateGradientSeparator(AccentPurple, 1));

        // ── Bottom: Fixed action buttons (NOT inside _sidebarContainer, so PopulatePlayerStatus won't touch them) ──
        var buttonSection = new VBoxContainer();
        buttonSection.AddThemeConstantOverride("separation", 6);
        buttonSection.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        sidebarOuterVBox.AddChild(buttonSection);

        // Selected card info label
        _selectedInfoLabel = new Label();
        _selectedInfoLabel.Text = "No card selected";
        _selectedInfoLabel.AddThemeFontSizeOverride("font_size", 13);
        _selectedInfoLabel.AddThemeColorOverride("font_color", TextDim);
        _selectedInfoLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _selectedInfoLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        buttonSection.AddChild(_selectedInfoLabel);

        _confirmButton = Create3DButton(
            "✓  Confirm",
            new Color(0.15f, 0.5f, 0.2f, 0.9f),
            new Color(0.2f, 0.65f, 0.28f, 1.0f),
            new Color(0.08f, 0.25f, 0.1f, 0.5f),
            new Color(0.25f, 0.7f, 0.35f, 0.6f));
        _confirmButton.CustomMinimumSize = new Vector2(0, 40);
        _confirmButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _confirmButton.Disabled = true;
        _confirmButton.Pressed += OnConfirmPressed;
        buttonSection.AddChild(_confirmButton);

        _skipButton = Create3DButton(
            "Skip Draft",
            new Color(0.45f, 0.3f, 0.15f, 0.85f),
            new Color(0.6f, 0.4f, 0.2f, 0.95f),
            new Color(0.2f, 0.15f, 0.08f, 0.5f),
            new Color(0.7f, 0.5f, 0.25f, 0.5f));
        _skipButton.CustomMinimumSize = new Vector2(0, 40);
        _skipButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _skipButton.Pressed += OnSkipPressed;
        buttonSection.AddChild(_skipButton);

        _endDraftButton = Create3DButton(
            "⚡ Skip Waiting",
            new Color(0.55f, 0.15f, 0.15f, 0.85f),
            new Color(0.7f, 0.2f, 0.2f, 0.95f),
            new Color(0.25f, 0.08f, 0.08f, 0.5f),
            new Color(0.8f, 0.3f, 0.3f, 0.5f));
        _endDraftButton.CustomMinimumSize = new Vector2(0, 40);
        _endDraftButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _endDraftButton.Pressed += OnEndDraftPressed;
        _endDraftButton.TooltipText = "Start settlement immediately (only confirmed players count)";
        buttonSection.AddChild(_endDraftButton);
    }

    private static void BuildBottomSection(VBoxContainer parent)
    {
        // Status label (centered at bottom)
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

    /// <summary>
    /// Build the toggle visibility button on a SEPARATE CanvasLayer (Layer=2)
    /// so it remains visible even when the main draft overlay (_dimBackground) is hidden.
    /// Simple transparent button with "Hide" / "Show" text, positioned at bottom center.
    /// </summary>
    private static void BuildToggleButton()
    {
        _toggleCanvasLayer = new CanvasLayer();
        _toggleCanvasLayer.Name = "DraftToggleLayer";
        _toggleCanvasLayer.Layer = 2;  // Above the main draft overlay (Layer=1)
        _toggleCanvasLayer.Visible = false;  // Controlled by Show()/Hide()

        _toggleVisibilityButton = new Button();
        _toggleVisibilityButton.Text = "Hide";
        _toggleVisibilityButton.AddThemeFontSizeOverride("font_size", 14);

        // Simple fully-transparent style — no background, no border
        var transparentStyle = new StyleBoxFlat();
        transparentStyle.BgColor = new Color(0, 0, 0, 0);  // Fully transparent
        transparentStyle.BorderColor = new Color(0, 0, 0, 0);
        transparentStyle.SetBorderWidthAll(0);
        transparentStyle.SetCornerRadiusAll(0);
        transparentStyle.SetContentMarginAll(8);
        _toggleVisibilityButton.AddThemeStyleboxOverride("normal", transparentStyle);

        var hoverStyle = new StyleBoxFlat();
        hoverStyle.BgColor = new Color(1, 1, 1, 0.08f);  // Very subtle hover
        hoverStyle.BorderColor = new Color(0, 0, 0, 0);
        hoverStyle.SetBorderWidthAll(0);
        hoverStyle.SetCornerRadiusAll(4);
        hoverStyle.SetContentMarginAll(8);
        _toggleVisibilityButton.AddThemeStyleboxOverride("hover", hoverStyle);

        var pressedStyle = new StyleBoxFlat();
        pressedStyle.BgColor = new Color(1, 1, 1, 0.12f);
        pressedStyle.BorderColor = new Color(0, 0, 0, 0);
        pressedStyle.SetBorderWidthAll(0);
        pressedStyle.SetCornerRadiusAll(4);
        pressedStyle.SetContentMarginAll(8);
        _toggleVisibilityButton.AddThemeStyleboxOverride("pressed", pressedStyle);

        _toggleVisibilityButton.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f, 0.7f));
        _toggleVisibilityButton.AddThemeColorOverride("font_hover_color", new Color(0.9f, 0.9f, 0.9f, 0.9f));
        _toggleVisibilityButton.AddThemeColorOverride("font_pressed_color", new Color(1f, 1f, 1f, 1f));

        // Position at bottom center of screen
        _toggleVisibilityButton.AnchorLeft = 0.5f;
        _toggleVisibilityButton.AnchorRight = 0.5f;
        _toggleVisibilityButton.AnchorTop = 1.0f;
        _toggleVisibilityButton.AnchorBottom = 1.0f;
        _toggleVisibilityButton.OffsetLeft = -40;
        _toggleVisibilityButton.OffsetRight = 40;
        _toggleVisibilityButton.OffsetTop = -42;
        _toggleVisibilityButton.OffsetBottom = -10;
        _toggleVisibilityButton.GrowHorizontal = Control.GrowDirection.Both;
        _toggleVisibilityButton.GrowVertical = Control.GrowDirection.Begin;

        // MouseFilter=Stop so the button consumes clicks
        _toggleVisibilityButton.MouseFilter = Control.MouseFilterEnum.Stop;

        _toggleVisibilityButton.Pressed += OnToggleVisibilityPressed;
        _toggleCanvasLayer.AddChild(_toggleVisibilityButton);
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
        normalStyle.ContentMarginLeft = 12;
        normalStyle.ContentMarginTop = 10;
        normalStyle.ContentMarginRight = 4;
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
                hoverStyle.ContentMarginLeft = 12;
                hoverStyle.ContentMarginTop = 10;
                hoverStyle.ContentMarginRight = 4;
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
        descLabel.Text = "(Other player's card)";
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
            selectedStyle.ContentMarginLeft = 12;
            selectedStyle.ContentMarginTop = 10;
            selectedStyle.ContentMarginRight = 4;
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
    /// When multiple players pick the same card, a conflict indicator with bounce animation is shown.
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

            bool isConflict = players.Count > 1;
            var overlays = new List<(int slot, Control overlay)>();

            // Use a dedicated overlay container so avatars are not affected by
            // PanelContainer's child layout (which stretches children to fill).
            // The overlay container sits on top of the cell content with absolute positioning.
            var overlayContainer = new Control();
            overlayContainer.Name = "SelectionOverlay";
            overlayContainer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            overlayContainer.MouseFilter = Control.MouseFilterEnum.Ignore;
            cell.AddChild(overlayContainer);
            overlays.Add((-99, overlayContainer)); // Track for cleanup

            for (int i = 0; i < players.Count; i++)
            {
                var ps = players[i];
                bool isLocal = manager.IsLocalPlayerState(ps);

                // Create a character icon avatar badge
                var avatarBadge = CreatePlayerAvatarBadge(ps.PlayerSlot, ps.DisplayName, isLocal);

                // Position at bottom-right of the cell, stacking horizontally.
                // Use CellSize constants instead of cell.Size which may not be computed yet.
                avatarBadge.Position = new Vector2(
                    CellSize.X - 42 - (i * 34),
                    CellSize.Y - 42);

                overlayContainer.AddChild(avatarBadge);
                overlays.Add((ps.PlayerSlot, avatarBadge));
            }

            // If multiple players selected the same card, add conflict indicator + bounce animation
            if (isConflict)
            {
                // Add ⚔️ conflict indicator at top-right
                var conflictIcon = new Label();
                conflictIcon.MouseFilter = Control.MouseFilterEnum.Ignore;
                conflictIcon.Text = "⚔️";
                conflictIcon.AddThemeFontSizeOverride("font_size", 18);
                conflictIcon.Position = new Vector2(CellSize.X - 30, 4);
                conflictIcon.TooltipText = $"Conflict! {players.Count} players chose the same card";
                overlayContainer.AddChild(conflictIcon);
                overlays.Add((-1, conflictIcon)); // -1 = conflict indicator (not a player)

                // Bounce animation on all avatar badges (like NMultiplayerVoteContainer.BouncePlayers)
                Callable.From(() =>
                {
                    foreach (var (slot, overlay) in overlays)
                    {
                        if (slot < 0 || !GodotObject.IsInstanceValid(overlay) || !overlay.IsInsideTree())
                            continue;
                        var bounceTween = overlay.CreateTween();
                        bounceTween.TweenProperty(overlay, "scale", new Vector2(1.3f, 1.3f), 0.15f)
                            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                        bounceTween.TweenProperty(overlay, "scale", new Vector2(1.0f, 1.0f), 0.15f)
                            .SetTrans(Tween.TransitionType.Bounce).SetEase(Tween.EaseType.Out);
                    }
                }).CallDeferred();
            }

            _cardSelectionOverlays[draftId] = overlays;
        }
    }

    /// <summary>
    /// Create a small player avatar badge using the game's native multiplayer_vote_icon scene.
    /// This is the same approach used by NMultiplayerVoteContainer in treasure room relic picking.
    /// Falls back to manual character icon if scene instantiation fails,
    /// and to colored circle + initial if character icon is unavailable.
    /// </summary>
    private static Control CreatePlayerAvatarBadge(int playerSlot, string displayName, bool isLocal)
    {
        Color playerColor = playerSlot < PoolColors.Length
            ? PoolColors[playerSlot]
            : new Color(0.5f, 0.5f, 0.5f);

        // Try to get the player's character icon texture
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

        // ── Strategy 1: Try native multiplayer_vote_icon scene (authentic game look) ──
        try
        {
            var voteIcon = MegaCrit.Sts2.Core.Helpers.SceneHelper.Instantiate<TextureRect>("ui/multiplayer_vote_icon");
            if (voteIcon != null && iconTexture != null)
            {
                voteIcon.MouseFilter = Control.MouseFilterEnum.Ignore;
                voteIcon.Texture = iconTexture;
                voteIcon.CustomMinimumSize = new Vector2(32, 32);
                voteIcon.Size = new Vector2(32, 32);

                // Set outline texture (child node "Outline")
                try
                {
                    var outlineNode = voteIcon.GetNode<TextureRect>("Outline");
                    if (outlineNode != null && outlineTexture != null)
                    {
                        outlineNode.Texture = outlineTexture;
                        outlineNode.Modulate = isLocal ? AccentGold : Colors.White;
                    }
                }
                catch { /* Outline node may not exist */ }

                // Tooltip
                voteIcon.TooltipText = isLocal ? $"{displayName} (You)" : displayName;

                // Animate entry: fade in + slide up (like NMultiplayerVoteContainer.AnimVoteIn)
                // NOTE: Cannot call CreateTween() here — the node hasn't been added to the
                // scene tree yet (AddChild happens in UpdateCardSelectionOverlays after return).
                // Use CallDeferred to ensure the node is inside the tree before tweening.
                voteIcon.Modulate = new Color(1, 1, 1, 0);
                voteIcon.Position += new Vector2(0, 15);
                Callable.From(() =>
                {
                    if (GodotObject.IsInstanceValid(voteIcon) && voteIcon.IsInsideTree())
                    {
                        var tween = voteIcon.CreateTween();
                        tween.SetParallel(true);
                        tween.TweenProperty(voteIcon, "modulate:a", 1.0f, 0.2f);
                        tween.TweenProperty(voteIcon, "position:y", voteIcon.Position.Y - 15, 0.3f)
                            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                    }
                }).CallDeferred();

                ModEntry.Logger.Info($"[Avatar] Created native vote icon for slot {playerSlot}");
                return voteIcon;
            }
        }
        catch (System.Exception ex)
        {
            ModEntry.Logger.Info($"[Avatar] Native vote icon failed for slot {playerSlot}: {ex.Message}");
        }

        // ── Strategy 2: Manual character icon with Tween animation ──
        if (iconTexture != null)
        {
            var container = new Control();
            container.MouseFilter = Control.MouseFilterEnum.Ignore;
            container.CustomMinimumSize = new Vector2(32, 32);
            container.Size = new Vector2(32, 32);

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
                container.MoveChild(glowPanel, 0);
            }

            iconRect.TooltipText = isLocal ? $"{displayName} (You)" : displayName;

            // Animate entry: fade in + slide up
            container.Modulate = new Color(1, 1, 1, 0);
            container.Position += new Vector2(0, 15);
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(container) && container.IsInsideTree())
                {
                    var tween = container.CreateTween();
                    tween.SetParallel(true);
                    tween.TweenProperty(container, "modulate:a", 1.0f, 0.2f);
                    tween.TweenProperty(container, "position:y", container.Position.Y - 15, 0.3f)
                        .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                }
            }).CallDeferred();

            return container;
        }

        // ── Strategy 3: Fallback colored circle with initial ──
        {
            var container = new PanelContainer();
            container.MouseFilter = Control.MouseFilterEnum.Ignore;
            container.CustomMinimumSize = new Vector2(28, 28);
            container.Size = new Vector2(28, 28);

            var badgeStyle = new StyleBoxFlat();
            badgeStyle.BgColor = playerColor;
            badgeStyle.BorderColor = isLocal ? AccentGold : new Color(1, 1, 1, 0.8f);
            badgeStyle.SetBorderWidthAll(isLocal ? 2 : 1);
            badgeStyle.SetCornerRadiusAll(14);
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

            container.TooltipText = isLocal ? $"{displayName} (You)" : displayName;

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
            nameLabel.Text = ps.DisplayName + (isLocal ? " (You)" : "");
            nameLabel.AddThemeFontSizeOverride("font_size", 13);
            nameLabel.AddThemeColorOverride("font_color",
                isLocal ? AccentGold : TextPrimary);
            nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            nameLabel.ClipText = true;
            topRow.AddChild(nameLabel);

            // Status label
            var statusLabel = new Label();
            statusLabel.Text = "⏳ Waiting...";
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
            {
                SetCellSelected(cell, false);
                // Subtle shrink-back animation for deselected cells
                Callable.From(() =>
                {
                    if (GodotObject.IsInstanceValid(cell) && cell.IsInsideTree())
                    {
                        var shrinkTween = cell.CreateTween();
                        shrinkTween.TweenProperty(cell, "scale", Vector2.One, 0.15f)
                            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
                    }
                }).CallDeferred();
            }
        }

        SetCellSelected(clickedCell, true);

        // Bounce animation on clicked card (NMultiplayerVoteContainer-inspired)
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(clickedCell) && clickedCell.IsInsideTree())
            {
                var bounceTween = clickedCell.CreateTween();
                // Quick scale up → settle back — gives satisfying tactile feedback
                bounceTween.TweenProperty(clickedCell, "scale", new Vector2(1.05f, 1.05f), 0.1f)
                    .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                bounceTween.TweenProperty(clickedCell, "scale", Vector2.One, 0.15f)
                    .SetTrans(Tween.TransitionType.Bounce).SetEase(Tween.EaseType.Out);
            }
        }).CallDeferred();

        UpdateSelectedInfo();
        UpdateConfirmButton();
    }

    private static void OnConfirmPressed()
    {
        if (_selectedDraftId < 0)
        {
            SetStatus("⚠ Please select a card first!", new Color(1, 0.4f, 0.4f));
            return;
        }

        SetStatus("Selection confirmed. Waiting for other players...", StatusReady);

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
        ModEntry.Logger.Info("Skip pressed → player opted out (can re-enter via CardReward).");
        SharedDraftManager.Instance.OnLocalPlayerOptOut();
        // Note: OnLocalPlayerOptOut → SubmitLocalOptOut → Hide() is called from Manager
    }

    private static void OnEndDraftPressed()
    {
        SetStatus("⚡ Skip requested. Waiting for settlement...", new Color(1.0f, 0.6f, 0.3f));

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

        SetStatus("⏳ Settlement in progress, please wait...", new Color(1.0f, 0.7f, 0.3f));

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

        SetStatus("Select a card from the shared pool.", new Color(0.7f, 0.8f, 0.9f));

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
            _countdownLabel.Text = $"⏱ Timeout: {secondsLeft}s";

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
            _selectedInfoLabel.Text = "No card selected";
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
                    _selectedInfoLabel.Text = $"Selected: {title}";
                }
                else
                {
                    _selectedInfoLabel.Text = $"Selected: {FormatEntryName(draftCard.CardEntry)} (other player's card)";
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
            contestedStyle.ContentMarginLeft = 12;
            contestedStyle.ContentMarginTop = 10;
            contestedStyle.ContentMarginRight = 4;
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
            if (_toggleCanvasLayer != null)
                sceneTree.Root.CallDeferred("add_child", _toggleCanvasLayer);
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
        if (_toggleCanvasLayer != null && GodotObject.IsInstanceValid(_toggleCanvasLayer))
            _toggleCanvasLayer.QueueFree();

        _canvasLayer = null;
        _toggleCanvasLayer = null;
        _dimBackground = null;
        _cardGrid = null;
        _sidebarContainer = null;
        _titleLabel = null;
        _selectedInfoLabel = null;
        _statusLabel = null;
        _confirmButton = null;
        _skipButton = null;
        _endDraftButton = null;
        _toggleVisibilityButton = null;
        _countdownLabel = null;
        _inputHandler = null;
        _isPauseMenuOpen = false;
        _isHiddenForNativeScreen = false;
        _isManuallyHidden = false;
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
    //  TOGGLE VISIBILITY (manual hide/show)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Toggle button pressed — hide or show the draft overlay without changing player state.
    /// When hidden, the player can access the game's native map, deck viewer, settings, etc.
    /// When shown again, the player returns to the draft screen.
    ///
    /// The toggle button itself is moved to a separate small CanvasLayer that stays visible
    /// even when the main overlay is hidden, so the player can click it to come back.
    /// </summary>
    private static void OnToggleVisibilityPressed()
    {
        if (_isManuallyHidden)
        {
            ShowFromManualHide();
        }
        else
        {
            HideManually();
        }
    }

    /// <summary>
    /// Manually hide the draft overlay (user clicked the toggle button).
    /// The draft state is preserved — the player is still in the drafting process.
    /// A floating "Show Draft" button remains visible at the bottom of the screen.
    /// </summary>
    private static void HideManually()
    {
        _isManuallyHidden = true;

        // Hide the main canvas content but keep the toggle button accessible
        if (_dimBackground != null && GodotObject.IsInstanceValid(_dimBackground))
            _dimBackground.Visible = false;

        UpdateToggleButton();
        ModEntry.Logger.Info("[SharedDraft] Manually hidden by user (toggle button).");
    }

    /// <summary>
    /// Restore the draft overlay from manual hide.
    /// </summary>
    private static void ShowFromManualHide()
    {
        _isManuallyHidden = false;

        if (_dimBackground != null && GodotObject.IsInstanceValid(_dimBackground))
            _dimBackground.Visible = true;

        UpdateToggleButton();
        ModEntry.Logger.Info("[SharedDraft] Restored from manual hide (toggle button).");
    }

    /// <summary>
    /// Update the toggle button text based on current visibility state.
    /// Simple "Hide" / "Show" text.
    /// </summary>
    private static void UpdateToggleButton()
    {
        if (_toggleVisibilityButton == null || !GodotObject.IsInstanceValid(_toggleVisibilityButton))
            return;

        if (_isManuallyHidden)
        {
            _toggleVisibilityButton.Text = "Show";
            _toggleVisibilityButton.AddThemeColorOverride("font_color", new Color(0.9f, 0.85f, 0.5f, 0.9f));
            _toggleVisibilityButton.AddThemeColorOverride("font_hover_color", new Color(1.0f, 0.95f, 0.7f));
        }
        else
        {
            _toggleVisibilityButton.Text = "Hide";
            _toggleVisibilityButton.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f, 0.7f));
            _toggleVisibilityButton.AddThemeColorOverride("font_hover_color", new Color(0.9f, 0.9f, 0.9f, 0.9f));
        }
    }

    /// <summary>Whether the draft overlay is currently manually hidden by the user.</summary>
    public static bool IsManuallyHidden => _isManuallyHidden;

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
        if (_toggleCanvasLayer != null && GodotObject.IsInstanceValid(_toggleCanvasLayer))
            _toggleCanvasLayer.Visible = false;

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
        if (_toggleCanvasLayer != null && GodotObject.IsInstanceValid(_toggleCanvasLayer))
            _toggleCanvasLayer.Visible = true;

        // If was manually hidden before pause menu, keep dimBackground hidden
        if (_isManuallyHidden && _dimBackground != null && GodotObject.IsInstanceValid(_dimBackground))
            _dimBackground.Visible = false;

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

    // ═══════════════════════════════════════════════
    //  NATIVE SCREEN WATCHER (settings, map, deck viewer)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Called by DraftInputHandler._Process() when a native game screen (settings, map,
    /// deck viewer, etc.) is detected as visible. Hides the draft overlay so the native
    /// screen can be seen and interacted with without interference.
    /// </summary>
    public static void HideForNativeScreen()
    {
        if (_isHiddenForNativeScreen || _isPauseMenuOpen) return;

        _isHiddenForNativeScreen = true;

        if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
            _canvasLayer.Visible = false;
        if (_toggleCanvasLayer != null && GodotObject.IsInstanceValid(_toggleCanvasLayer))
            _toggleCanvasLayer.Visible = false;

        ModEntry.Logger.Info("[SharedDraft] Auto-hidden: native game screen detected.");
    }

    /// <summary>
    /// Called by DraftInputHandler._Process() when all native screens have closed.
    /// Restores the draft overlay visibility.
    /// </summary>
    public static void RestoreFromNativeScreen()
    {
        if (!_isHiddenForNativeScreen) return;

        _isHiddenForNativeScreen = false;

        // Only restore if not also hidden for pause menu or card inspector
        if (!_isPauseMenuOpen)
        {
            if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
                _canvasLayer.Visible = true;
            if (_toggleCanvasLayer != null && GodotObject.IsInstanceValid(_toggleCanvasLayer))
                _toggleCanvasLayer.Visible = true;

            // If was manually hidden before native screen opened, keep dimBackground hidden
            if (_isManuallyHidden && _dimBackground != null && GodotObject.IsInstanceValid(_dimBackground))
                _dimBackground.Visible = false;
        }

        ModEntry.Logger.Info("[SharedDraft] Restored: native game screen closed.");
    }

    /// <summary>Whether the draft overlay is currently auto-hidden for a native screen.</summary>
    public static bool IsHiddenForNativeScreen => _isHiddenForNativeScreen;
}

/// <summary>
/// Lightweight Node that handles _UnhandledInput for the SharedDraft overlay
/// AND polls for native game screens in _Process.
///
/// _UnhandledInput: Listens for Escape key press to trigger the game's native pause/settings menu.
/// _Process: Every 0.25s, checks the game's NRunSubmenuStack.IsScreenOpen property to detect
/// when a native screen (settings, map, deck viewer, etc.) is open. When detected, auto-hides
/// the SharedDraft overlay. When all native screens close, restores the overlay.
///
/// APPROACH HISTORY:
/// - v8: Scanned scene tree for NScreen-derived nodes by name/class matching → UNRELIABLE,
///   because node names and class names don't always match our patterns.
/// - v9 (current): Uses the game's native API: NRunSubmenuStack.IsScreenOpen property
///   (from NRun.SubmenuStack). This is the same mechanism the game itself uses to track
///   whether settings/deck/map/pause screens are open. Much more reliable than heuristic
///   scene tree scanning.
///
/// API chain: RunManager.Instance → .Run (NRun node) → .SubmenuStack (NRunSubmenuStack)
///   → .IsScreenOpen (bool) / .ScreenCount (int)
/// </summary>
internal partial class DraftInputHandler : Node
{
    // Throttle _Process scanning to avoid checking every frame
    private double _scanTimer = 0;
    private const double ScanInterval = 0.25; // Check 4 times per second

    // Cache reflection-based screen detection
    private object? _cachedSubmenuStack;
    private System.Reflection.PropertyInfo? _isScreenOpenProp;
    private System.Reflection.PropertyInfo? _screenCountProp;
    private bool _reflectionInitialized = false;
    private bool _reflectionFailed = false;

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

    public override void _Process(double delta)
    {
        // Only scan when the draft is supposed to be visible (not permanently hidden by manager)
        // We need to check even when auto-hidden, to detect when the native screen closes.
        _scanTimer += delta;
        if (_scanTimer < ScanInterval) return;
        _scanTimer = 0;

        bool nativeScreenOpen = IsAnyNativeScreenOpen();

        if (nativeScreenOpen && !SharedDraftScreen.IsHiddenForNativeScreen)
        {
            SharedDraftScreen.HideForNativeScreen();
        }
        else if (!nativeScreenOpen && SharedDraftScreen.IsHiddenForNativeScreen)
        {
            SharedDraftScreen.RestoreFromNativeScreen();
        }
    }

    /// <summary>
    /// Check if any game native screen (settings, map, deck viewer, pause, etc.) is open.
    ///
    /// Uses reflection to access the game's native IsScreenOpen property, which is the
    /// authoritative source for whether any in-run submenu screen is currently displayed.
    /// Falls back to SceneTree.Paused check (for pause menu) and a lightweight scene tree
    /// scan if the API is unavailable.
    /// </summary>
    private bool IsAnyNativeScreenOpen()
    {
        try
        {
            // === Strategy 1: Use IsScreenOpen via reflection (authoritative game API) ===
            if (CheckIsScreenOpenViaReflection())
                return true;

            // === Strategy 2: Check SceneTree.Paused (for pause menu, which may bypass SubmenuStack) ===
            var tree = GetTree();
            if (tree != null && tree.Paused)
                return true;

            // === Strategy 3: Lightweight fallback — check for NSettingsScreenPopup ===
            // The settings screen sometimes appears as a popup outside the submenu stack.
            if (HasSettingsPopupVisible())
                return true;

            return false;
        }
        catch (System.Exception ex)
        {
            ModEntry.Logger.Info($"[DraftInputHandler] IsAnyNativeScreenOpen error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Use reflection to check the game's screen state via NRunSubmenuStack or RunManager.
    ///
    /// The game has several relevant properties (discovered via DLL analysis):
    /// - NRunSubmenuStack.IsScreenOpen (bool) — authoritative screen open state
    /// - NRunSubmenuStack.ScreenCount (int) — number of open screens
    /// - RunManager.Run (NRun) → .SubmenuStack (NRunSubmenuStack)
    ///
    /// We use reflection because the publicized DLL may not expose all properties to the compiler,
    /// but they exist in the runtime type system and can be accessed via reflection.
    /// </summary>
    private bool CheckIsScreenOpenViaReflection()
    {
        if (_reflectionFailed) return false;

        try
        {
            if (!_reflectionInitialized)
            {
                InitializeReflection();
                _reflectionInitialized = true;
            }

            if (_cachedSubmenuStack == null || (_cachedSubmenuStack is GodotObject go && !GodotObject.IsInstanceValid(go)))
            {
                // Try to re-obtain the submenu stack
                _cachedSubmenuStack = ObtainSubmenuStackViaReflection();
                if (_cachedSubmenuStack == null)
                    return false;
            }

            // Check IsScreenOpen property
            if (_isScreenOpenProp != null)
            {
                var val = _isScreenOpenProp.GetValue(_cachedSubmenuStack);
                if (val is bool isOpen && isOpen)
                    return true;
            }

            // Check ScreenCount property (belt-and-suspenders)
            if (_screenCountProp != null)
            {
                var val = _screenCountProp.GetValue(_cachedSubmenuStack);
                if (val is int count && count > 0)
                    return true;
            }

            return false;
        }
        catch (System.Exception ex)
        {
            ModEntry.Logger.Info($"[DraftInputHandler] Reflection check error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Initialize reflection — find the IsScreenOpen and ScreenCount properties on NRunSubmenuStack.
    /// </summary>
    private void InitializeReflection()
    {
        try
        {
            // Find NRunSubmenuStack type
            var submenuStackType = System.Type.GetType(
                "MegaCrit.Sts2.Core.Nodes.Screens.NRunSubmenuStack, sts2");

            if (submenuStackType == null)
            {
                // Try to find it in loaded assemblies
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    submenuStackType = asm.GetType("MegaCrit.Sts2.Core.Nodes.Screens.NRunSubmenuStack");
                    if (submenuStackType != null) break;
                }
            }

            if (submenuStackType != null)
            {
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;

                _isScreenOpenProp = submenuStackType.GetProperty("IsScreenOpen", flags);
                _screenCountProp = submenuStackType.GetProperty("ScreenCount", flags);

                // If ScreenCount is not a property, it might be a method
                if (_screenCountProp == null)
                {
                    var screenCountMethod = submenuStackType.GetMethod("GetScreenCount", flags);
                    if (screenCountMethod != null)
                    {
                        // Wrap the method as a pseudo-property for uniform access
                        ModEntry.Logger.Info("[DraftInputHandler] Found GetScreenCount() method instead of property.");
                    }
                }

                ModEntry.Logger.Info(
                    $"[DraftInputHandler] Reflection init: SubmenuStackType={submenuStackType.Name}, " +
                    $"IsScreenOpen={_isScreenOpenProp != null}, ScreenCount={_screenCountProp != null}");
            }
            else
            {
                ModEntry.Logger.Info("[DraftInputHandler] Could not find NRunSubmenuStack type via reflection.");

                // Fallback: try to find IsScreenOpen on RunManager itself
                var runManagerType = typeof(MegaCrit.Sts2.Core.Runs.RunManager);
                var flags2 = System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.NonPublic;
                _isScreenOpenProp = runManagerType.GetProperty("IsScreenOpen", flags2);
                _screenCountProp = runManagerType.GetProperty("ScreenCount", flags2);

                if (_isScreenOpenProp != null)
                {
                    ModEntry.Logger.Info("[DraftInputHandler] Found IsScreenOpen on RunManager directly.");
                }
            }
        }
        catch (System.Exception ex)
        {
            ModEntry.Logger.Error($"[DraftInputHandler] Reflection init failed: {ex.Message}");
            _reflectionFailed = true;
        }
    }

    /// <summary>
    /// Obtain the NRunSubmenuStack instance via reflection.
    /// Path: RunManager.Instance → .Run (NRun node) → .SubmenuStack (NRunSubmenuStack)
    /// Falls back to searching RunManager for direct IsScreenOpen property.
    /// </summary>
    private object? ObtainSubmenuStackViaReflection()
    {
        try
        {
            var runManager = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
            if (runManager == null) return null;

            var rmType = runManager.GetType();
            var flags = System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic;

            // If IsScreenOpen is directly on RunManager, use RunManager as the target
            if (_isScreenOpenProp != null && _isScreenOpenProp.DeclaringType == rmType)
            {
                return runManager;
            }

            // Try to get RunManager.Run → NRun
            var runProp = rmType.GetProperty("Run", flags);
            if (runProp == null)
            {
                // Try CurrentRunNode as alternative
                runProp = rmType.GetProperty("CurrentRunNode", flags);
            }

            if (runProp != null)
            {
                var nRun = runProp.GetValue(runManager);
                if (nRun == null) return null;

                if (nRun is GodotObject goRun && !GodotObject.IsInstanceValid(goRun))
                    return null;

                // Get SubmenuStack from NRun
                var nRunType = nRun.GetType();
                var submenuStackProp = nRunType.GetProperty("SubmenuStack", flags);
                if (submenuStackProp != null)
                {
                    var stack = submenuStackProp.GetValue(nRun);
                    if (stack != null)
                    {
                        // Re-discover IsScreenOpen and ScreenCount on the actual type
                        if (_isScreenOpenProp == null)
                        {
                            var stackType = stack.GetType();
                            _isScreenOpenProp = stackType.GetProperty("IsScreenOpen", flags);
                            _screenCountProp = stackType.GetProperty("ScreenCount", flags);
                            ModEntry.Logger.Info(
                                $"[DraftInputHandler] Discovered props on {stackType.Name}: " +
                                $"IsScreenOpen={_isScreenOpenProp != null}, ScreenCount={_screenCountProp != null}");
                        }
                        return stack;
                    }
                }

                // Fallback: check if NRun itself has IsScreenOpen
                var nRunIsScreenOpen = nRunType.GetProperty("IsScreenOpen", flags);
                if (nRunIsScreenOpen != null)
                {
                    _isScreenOpenProp = nRunIsScreenOpen;
                    ModEntry.Logger.Info("[DraftInputHandler] Found IsScreenOpen directly on NRun.");
                    return nRun;
                }
            }

            // Last resort: use RunManager itself if it has ScreenCount/IsScreenOpen
            var rmScreenCount = rmType.GetProperty("ScreenCount", flags);
            if (rmScreenCount != null)
            {
                _screenCountProp = rmScreenCount;
                ModEntry.Logger.Info("[DraftInputHandler] Using RunManager.ScreenCount as fallback.");
                return runManager;
            }

            ModEntry.Logger.Info("[DraftInputHandler] Could not find any screen state API via reflection.");
            return null;
        }
        catch (System.Exception ex)
        {
            ModEntry.Logger.Info($"[DraftInputHandler] ObtainSubmenuStack error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Lightweight check for settings popup window.
    /// NSettingsScreenPopup is sometimes shown independently of the submenu stack.
    /// Only checks direct children of root (depth 1) for performance.
    /// </summary>
    private bool HasSettingsPopupVisible()
    {
        try
        {
            var root = GetTree()?.Root;
            if (root == null) return false;

            foreach (var child in root.GetChildren())
            {
                if (!GodotObject.IsInstanceValid(child)) continue;

                // Check for settings popup by node name
                string nodeName = child.Name;
                if (nodeName.Contains("SettingsScreenPopup") ||
                    nodeName.Contains("NSettingsScreenPopup"))
                {
                    if (child is Control ctrl && ctrl.Visible)
                        return true;
                    if (child is CanvasLayer cl && cl.Visible)
                        return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reset the cached submenu stack reference. Call when the run ends or resets.
    /// </summary>
    public void ResetSubmenuStackCache()
    {
        _cachedSubmenuStack = null;
        _isScreenOpenProp = null;
        _screenCountProp = null;
        _reflectionInitialized = false;
        _reflectionFailed = false;
    }
}
