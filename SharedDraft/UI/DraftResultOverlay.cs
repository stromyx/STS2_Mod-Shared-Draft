using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;

namespace SharedDraft.UI;

/// <summary>
/// Premium overlay UI for displaying rock/paper/scissors conflict resolution results.
///
/// IMPORTANT: Does NOT inherit from any Godot node class.
/// Uses composition to avoid Godot source generator StringName crashes.
///
/// Features:
///   - Large panel (700x500) with 3D beveled styling matching SharedDraftScreen theme
///   - Big emoji moves (font_size 48) for clear visibility
///   - Steam nicknames prominently displayed for each competitor
///   - Golden winner announcement with large text
///   - Chinese localization throughout
///   - Animated auto-dismiss with countdown
///
/// Layout:
///   CanvasLayer (Layer=98, "DraftResultOverlay")
///   └── ColorRect (semi-transparent dim)
///       └── CenterContainer
///           └── PanelContainer (3D beveled main panel)
///               └── VBoxContainer
///                   ├── Label "⚔️ 卡牌冲突 — 猜拳决胜！" (title)
///                   ├── GradientSeparator
///                   ├── ScrollContainer
///                   │   └── VBoxContainer
///                   │       └── [per conflict]
///                   │           ├── PanelContainer (3D card-name header)
///                   │           ├── HBoxContainer (competitor cards)
///                   │           │   └── [per player] PanelContainer (3D player card)
///                   │           │       ├── Label (Steam nickname)
///                   │           │       ├── Label (move emoji, font_size 48)
///                   │           │       ├── Label (move name in Chinese)
///                   │           │       └── Label (win/lose badge)
///                   │           ├── PanelContainer (golden winner announcement)
///                   │           └── Label (losers must re-pick)
///                   ├── GradientSeparator
///                   └── Label "⏳ N 秒后自动继续..." (countdown)
/// </summary>
public static class DraftResultOverlay
{
    // ── References ──
    private static CanvasLayer? _canvasLayer;
    private static VBoxContainer? _contentContainer;
    private static Label? _countdownLabel;
    private static TaskCompletionSource<bool>? _dismissTcs;

    // ═══════════════════════════════════════════════
    //  COLOR PALETTE — matches SharedDraftScreen theme
    // ═══════════════════════════════════════════════

    private static readonly Color PanelBg = new(0.06f, 0.05f, 0.11f, 0.97f);
    private static readonly Color PanelBorder = new(0.5f, 0.3f, 0.2f, 0.8f);
    private static readonly Color AccentGold = new(1.0f, 0.85f, 0.3f, 1.0f);
    private static readonly Color AccentPurple = new(0.6f, 0.4f, 0.9f, 1.0f);
    private static readonly Color TextPrimary = new(0.95f, 0.92f, 0.98f);
    private static readonly Color TextSecondary = new(0.7f, 0.68f, 0.78f);
    private static readonly Color TextDim = new(0.5f, 0.48f, 0.58f);
    private static readonly Color WinnerGold = new(1.0f, 0.9f, 0.3f, 1.0f);
    private static readonly Color WinnerGlow = new(1.0f, 0.85f, 0.2f, 0.5f);
    private static readonly Color LoserRed = new(1.0f, 0.35f, 0.3f, 1.0f);
    private static readonly Color CardHeaderBg = new(0.12f, 0.1f, 0.2f, 0.9f);

    // Player colors (same as SharedDraftScreen)
    private static readonly Color[] PlayerColors =
    [
        new Color(0.25f, 0.55f, 1.0f, 1.0f),   // Blue — player 0
        new Color(1.0f, 0.35f, 0.3f, 1.0f),     // Red — player 1
        new Color(0.2f, 0.85f, 0.35f, 1.0f),    // Green — player 2
        new Color(1.0f, 0.75f, 0.15f, 1.0f),    // Gold — player 3
    ];

    // ── Move emojis (larger, more expressive) ──
    private static string GetMoveEmoji(RelicPickingFightMove move) => move switch
    {
        RelicPickingFightMove.Rock => "🪨",
        RelicPickingFightMove.Paper => "📄",
        RelicPickingFightMove.Scissors => "✂️",
        _ => "❓"
    };

    private static string GetMoveName(RelicPickingFightMove move) => move switch
    {
        RelicPickingFightMove.Rock => "石头",
        RelicPickingFightMove.Paper => "布",
        RelicPickingFightMove.Scissors => "剪刀",
        _ => "???"
    };

    // ═══════════════════════════════════════════════
    //  SHOW RESULTS
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Show conflict resolution results with animated countdown auto-dismiss.
    /// </summary>
    public static async Task ShowResults(IReadOnlyList<ConflictResult> conflicts)
    {
        if (conflicts.Count == 0)
            return;

        Build(conflicts);
        InjectIntoSceneTree();

        if (_canvasLayer != null)
            _canvasLayer.Visible = true;

        // Auto-dismiss with visible countdown (5 seconds)
        int totalSeconds = 5;
        for (int i = totalSeconds; i > 0; i--)
        {
            if (_countdownLabel != null && GodotObject.IsInstanceValid(_countdownLabel))
            {
                _countdownLabel.Text = $"⏳ {i} 秒后自动继续...";
                if (i <= 2)
                    _countdownLabel.AddThemeColorOverride("font_color", LoserRed);
            }
            await Task.Delay(1000);
        }

        Hide();
    }

    /// <summary>
    /// Hide and cleanup the overlay.
    /// </summary>
    public static void Hide()
    {
        if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
        {
            _canvasLayer.QueueFree();
        }
        _canvasLayer = null;
        _contentContainer = null;
        _countdownLabel = null;
    }

    // ═══════════════════════════════════════════════
    //  BUILD UI — Premium 3D Theme
    // ═══════════════════════════════════════════════

    private static void Build(IReadOnlyList<ConflictResult> conflicts)
    {
        // Clean up previous overlay if any
        Hide();

        _canvasLayer = new CanvasLayer();
        _canvasLayer.Name = "DraftResultOverlay";
        _canvasLayer.Layer = 98; // Above SharedDraftScreen (95)
        _canvasLayer.Visible = false;

        // Dim background
        var dimRect = new ColorRect();
        dimRect.Color = new Color(0.0f, 0.0f, 0.0f, 0.85f);
        dimRect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        dimRect.MouseFilter = Control.MouseFilterEnum.Stop;
        _canvasLayer.AddChild(dimRect);

        // CenterContainer for centering the panel
        var centerContainer = new CenterContainer();
        centerContainer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        dimRect.AddChild(centerContainer);

        // ── Main panel with 3D beveled style ──
        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(700, 480);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        panel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;

        var panelStyle = Create3DPanelStyle(
            PanelBg,
            PanelBorder,
            new Color(0.5f, 0.3f, 0.15f, 0.3f),    // Top highlight (warm)
            new Color(0.02f, 0.01f, 0.05f, 0.6f),   // Bottom shadow
            borderWidth: 2, cornerRadius: 16);
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        centerContainer.AddChild(panel);

        // Main VBox inside panel
        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 10);
        panel.AddChild(mainVBox);

        // ── Title ──
        var titleHBox = new HBoxContainer();
        titleHBox.Alignment = BoxContainer.AlignmentMode.Center;
        titleHBox.AddThemeConstantOverride("separation", 10);
        mainVBox.AddChild(titleHBox);

        var swordIcon = new Label();
        swordIcon.Text = "⚔️";
        swordIcon.AddThemeFontSizeOverride("font_size", 28);
        titleHBox.AddChild(swordIcon);

        var title = new Label();
        title.Text = "卡牌冲突 — 猜拳决胜！";
        title.AddThemeFontSizeOverride("font_size", 24);
        title.AddThemeColorOverride("font_color", AccentGold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        titleHBox.AddChild(title);

        var swordIcon2 = new Label();
        swordIcon2.Text = "⚔️";
        swordIcon2.AddThemeFontSizeOverride("font_size", 28);
        titleHBox.AddChild(swordIcon2);

        // Gradient separator
        mainVBox.AddChild(CreateGradientSeparator(AccentGold));

        // ── Scrollable conflict entries ──
        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        mainVBox.AddChild(scroll);

        _contentContainer = new VBoxContainer();
        _contentContainer.AddThemeConstantOverride("separation", 16);
        _contentContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_contentContainer);

        // Build each conflict result entry
        foreach (var conflict in conflicts)
        {
            BuildConflictEntry(conflict);
        }

        // Bottom separator
        mainVBox.AddChild(CreateGradientSeparator(AccentPurple));

        // Countdown label
        _countdownLabel = new Label();
        _countdownLabel.Text = "⏳ 5 秒后自动继续...";
        _countdownLabel.AddThemeFontSizeOverride("font_size", 14);
        _countdownLabel.AddThemeColorOverride("font_color", TextSecondary);
        _countdownLabel.HorizontalAlignment = HorizontalAlignment.Center;
        mainVBox.AddChild(_countdownLabel);
    }

    /// <summary>
    /// Build a single conflict entry with large moves and player names.
    /// </summary>
    private static void BuildConflictEntry(ConflictResult conflict)
    {
        if (_contentContainer == null) return;

        var entryBox = new VBoxContainer();
        entryBox.AddThemeConstantOverride("separation", 10);
        entryBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // ── Contested card name header (3D styled panel) ──
        string cardName = conflict.ContestedCard.HasCardModel
            ? GetCardDisplayName(conflict.ContestedCard)
            : FormatEntryName(conflict.ContestedCard.CardEntry);

        var cardHeaderPanel = new PanelContainer();
        var headerStyle = Create3DPanelStyle(
            CardHeaderBg,
            new Color(0.4f, 0.3f, 0.6f, 0.5f),
            new Color(0.35f, 0.25f, 0.5f, 0.2f),
            new Color(0.01f, 0.01f, 0.03f, 0.3f),
            borderWidth: 1, cornerRadius: 8);
        headerStyle.SetContentMarginAll(8);
        cardHeaderPanel.AddThemeStyleboxOverride("panel", headerStyle);

        var cardLabel = new Label();
        cardLabel.Text = $"🃏  争夺卡牌：{cardName}";
        cardLabel.AddThemeFontSizeOverride("font_size", 17);
        cardLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.6f));
        cardLabel.HorizontalAlignment = HorizontalAlignment.Center;
        cardHeaderPanel.AddChild(cardLabel);
        entryBox.AddChild(cardHeaderPanel);

        // ── Competitors with big moves ──
        var competitorsHBox = new HBoxContainer();
        competitorsHBox.AddThemeConstantOverride("separation", 24);
        competitorsHBox.Alignment = BoxContainer.AlignmentMode.Center;
        competitorsHBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        entryBox.AddChild(competitorsHBox);

        // If we have fight details, show individual moves
        if (conflict.Fight != null && conflict.Fight.rounds.Count > 0)
        {
            BuildFightRounds(competitorsHBox, conflict);
        }
        else
        {
            // Fallback: show competitors with simple win/lose
            BuildFallbackCompetitors(competitorsHBox, conflict);
        }

        // ── Golden winner announcement ──
        var winnerPanel = new PanelContainer();
        winnerPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var winnerPanelStyle = new StyleBoxFlat();
        winnerPanelStyle.BgColor = new Color(0.15f, 0.12f, 0.05f, 0.8f);
        winnerPanelStyle.BorderColor = WinnerGold;
        winnerPanelStyle.SetBorderWidthAll(2);
        winnerPanelStyle.SetCornerRadiusAll(10);
        winnerPanelStyle.SetContentMarginAll(10);
        winnerPanelStyle.ShadowColor = WinnerGlow;
        winnerPanelStyle.ShadowSize = 8;
        winnerPanel.AddThemeStyleboxOverride("panel", winnerPanelStyle);

        var winnerVBox = new VBoxContainer();
        winnerVBox.AddThemeConstantOverride("separation", 4);
        winnerPanel.AddChild(winnerVBox);

        var winnerTitle = new Label();
        winnerTitle.Text = "🏆  胜 者  🏆";
        winnerTitle.AddThemeFontSizeOverride("font_size", 20);
        winnerTitle.AddThemeColorOverride("font_color", WinnerGold);
        winnerTitle.HorizontalAlignment = HorizontalAlignment.Center;
        winnerVBox.AddChild(winnerTitle);

        var winnerName = new Label();
        winnerName.Text = conflict.Winner.DisplayName;
        winnerName.AddThemeFontSizeOverride("font_size", 22);
        winnerName.AddThemeColorOverride("font_color", new Color(1.0f, 1.0f, 1.0f));
        winnerName.HorizontalAlignment = HorizontalAlignment.Center;
        winnerVBox.AddChild(winnerName);

        entryBox.AddChild(winnerPanel);

        // ── Losers must re-pick ──
        if (conflict.Losers.Count > 0)
        {
            string loserNames = string.Join("、", conflict.Losers.Select(l => l.DisplayName));
            var loserLabel = new Label();
            loserLabel.Text = $"↩ 需要重新选卡：{loserNames}";
            loserLabel.AddThemeFontSizeOverride("font_size", 13);
            loserLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.6f, 0.4f));
            loserLabel.HorizontalAlignment = HorizontalAlignment.Center;
            entryBox.AddChild(loserLabel);
        }

        _contentContainer.AddChild(entryBox);
    }

    /// <summary>
    /// Build detailed fight round display with large emojis and Steam nicknames.
    /// Shows the last (decisive) round.
    /// </summary>
    private static void BuildFightRounds(HBoxContainer container, ConflictResult conflict)
    {
        if (conflict.Fight == null) return;

        var lastRound = conflict.Fight.rounds.LastOrDefault();
        if (lastRound == null) return;

        var playersInvolved = conflict.Fight.playersInvolved;
        bool needsVsLabel = true;

        for (int i = 0; i < lastRound.moves.Count && i < playersInvolved.Count; i++)
        {
            var moveValue = lastRound.moves[i];

            // null = eliminated in a previous round — skip
            if (!moveValue.HasValue)
                continue;

            var player = playersInvolved[i];

            // Find competitor state
            var compState = conflict.Competitors
                .FirstOrDefault(c => c.Player == player);
            string playerName = compState?.DisplayName ?? $"玩家 {player.NetId}";
            bool isWinner = compState == conflict.Winner;
            int playerSlot = compState?.PlayerSlot ?? 0;

            // Insert VS separator between players
            if (!needsVsLabel)
            {
                var vsLabel = new Label();
                vsLabel.Text = "VS";
                vsLabel.AddThemeFontSizeOverride("font_size", 24);
                vsLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.5f, 0.2f));
                vsLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                container.AddChild(vsLabel);
            }
            needsVsLabel = false;

            // ── Player card panel with 3D style ──
            var playerPanel = new PanelContainer();
            playerPanel.CustomMinimumSize = new Vector2(200, 220);

            Color playerColor = playerSlot < PlayerColors.Length
                ? PlayerColors[playerSlot]
                : new Color(0.5f, 0.5f, 0.5f);

            var playerPanelStyle = new StyleBoxFlat();
            if (isWinner)
            {
                playerPanelStyle.BgColor = new Color(0.12f, 0.1f, 0.04f, 0.9f);
                playerPanelStyle.BorderColor = WinnerGold;
                playerPanelStyle.SetBorderWidthAll(2);
                playerPanelStyle.ShadowColor = WinnerGlow;
                playerPanelStyle.ShadowSize = 6;
            }
            else
            {
                playerPanelStyle.BgColor = new Color(0.1f, 0.06f, 0.06f, 0.85f);
                playerPanelStyle.BorderColor = new Color(LoserRed.R, LoserRed.G, LoserRed.B, 0.5f);
                playerPanelStyle.SetBorderWidthAll(1);
                playerPanelStyle.ShadowColor = new Color(0.0f, 0.0f, 0.0f, 0.3f);
                playerPanelStyle.ShadowSize = 3;
            }
            playerPanelStyle.SetCornerRadiusAll(12);
            playerPanelStyle.SetContentMarginAll(12);
            playerPanelStyle.ShadowOffset = new Vector2(2, 3);
            playerPanel.AddThemeStyleboxOverride("panel", playerPanelStyle);

            var playerVBox = new VBoxContainer();
            playerVBox.AddThemeConstantOverride("separation", 6);
            playerVBox.Alignment = BoxContainer.AlignmentMode.Center;
            playerPanel.AddChild(playerVBox);

            // ── Player avatar + name row ──
            var nameRow = new HBoxContainer();
            nameRow.Alignment = BoxContainer.AlignmentMode.Center;
            nameRow.AddThemeConstantOverride("separation", 8);
            playerVBox.AddChild(nameRow);

            // Small avatar circle
            var avatar = new PanelContainer();
            avatar.CustomMinimumSize = new Vector2(24, 24);
            var avatarStyle = new StyleBoxFlat();
            avatarStyle.BgColor = playerColor;
            avatarStyle.SetCornerRadiusAll(12);
            avatarStyle.SetContentMarginAll(0);
            avatar.AddThemeStyleboxOverride("panel", avatarStyle);

            var avatarInitial = new Label();
            avatarInitial.Text = !string.IsNullOrEmpty(playerName) ? playerName[..1].ToUpper() : "?";
            avatarInitial.AddThemeFontSizeOverride("font_size", 12);
            avatarInitial.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.95f));
            avatarInitial.HorizontalAlignment = HorizontalAlignment.Center;
            avatarInitial.VerticalAlignment = VerticalAlignment.Center;
            avatar.AddChild(avatarInitial);
            nameRow.AddChild(avatar);

            // Steam nickname (large, colored)
            var nameLabel = new Label();
            nameLabel.Text = playerName;
            nameLabel.AddThemeFontSizeOverride("font_size", 16);
            nameLabel.AddThemeColorOverride("font_color",
                isWinner ? WinnerGold : TextPrimary);
            nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
            nameLabel.ClipText = true;
            nameLabel.CustomMinimumSize = new Vector2(140, 0);
            nameRow.AddChild(nameLabel);

            // ── Move emoji (LARGE) ──
            var moveLabel = new Label();
            moveLabel.Text = GetMoveEmoji(moveValue.Value);
            moveLabel.AddThemeFontSizeOverride("font_size", 48);
            moveLabel.HorizontalAlignment = HorizontalAlignment.Center;
            playerVBox.AddChild(moveLabel);

            // Move name in Chinese
            var moveNameLabel = new Label();
            moveNameLabel.Text = GetMoveName(moveValue.Value);
            moveNameLabel.AddThemeFontSizeOverride("font_size", 15);
            moveNameLabel.AddThemeColorOverride("font_color", TextSecondary);
            moveNameLabel.HorizontalAlignment = HorizontalAlignment.Center;
            playerVBox.AddChild(moveNameLabel);

            // Win/lose badge
            var resultLabel = new Label();
            if (isWinner)
            {
                resultLabel.Text = "🏆 胜出";
                resultLabel.AddThemeFontSizeOverride("font_size", 18);
                resultLabel.AddThemeColorOverride("font_color", WinnerGold);
            }
            else
            {
                resultLabel.Text = "❌ 落败";
                resultLabel.AddThemeFontSizeOverride("font_size", 18);
                resultLabel.AddThemeColorOverride("font_color", LoserRed);
            }
            resultLabel.HorizontalAlignment = HorizontalAlignment.Center;
            playerVBox.AddChild(resultLabel);

            container.AddChild(playerPanel);
        }
    }

    /// <summary>
    /// Fallback display when game API fight data is not available.
    /// Shows player cards with simple win/lose indicators.
    /// </summary>
    private static void BuildFallbackCompetitors(HBoxContainer container, ConflictResult conflict)
    {
        bool needsVs = true;

        foreach (var comp in conflict.Competitors)
        {
            if (!needsVs)
            {
                var vsLabel = new Label();
                vsLabel.Text = "VS";
                vsLabel.AddThemeFontSizeOverride("font_size", 24);
                vsLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.5f, 0.2f));
                vsLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                container.AddChild(vsLabel);
            }
            needsVs = false;

            bool isWinner = comp == conflict.Winner;
            int playerSlot = comp.PlayerSlot;
            Color playerColor = playerSlot < PlayerColors.Length
                ? PlayerColors[playerSlot]
                : new Color(0.5f, 0.5f, 0.5f);

            var playerPanel = new PanelContainer();
            playerPanel.CustomMinimumSize = new Vector2(200, 160);

            var pStyle = new StyleBoxFlat();
            if (isWinner)
            {
                pStyle.BgColor = new Color(0.12f, 0.1f, 0.04f, 0.9f);
                pStyle.BorderColor = WinnerGold;
                pStyle.SetBorderWidthAll(2);
                pStyle.ShadowColor = WinnerGlow;
                pStyle.ShadowSize = 6;
            }
            else
            {
                pStyle.BgColor = new Color(0.1f, 0.06f, 0.06f, 0.85f);
                pStyle.BorderColor = new Color(LoserRed.R, LoserRed.G, LoserRed.B, 0.5f);
                pStyle.SetBorderWidthAll(1);
                pStyle.ShadowColor = new Color(0, 0, 0, 0.3f);
                pStyle.ShadowSize = 3;
            }
            pStyle.SetCornerRadiusAll(12);
            pStyle.SetContentMarginAll(12);
            pStyle.ShadowOffset = new Vector2(2, 3);
            playerPanel.AddThemeStyleboxOverride("panel", pStyle);

            var pVBox = new VBoxContainer();
            pVBox.AddThemeConstantOverride("separation", 8);
            pVBox.Alignment = BoxContainer.AlignmentMode.Center;
            playerPanel.AddChild(pVBox);

            // Avatar + name
            var nameRow = new HBoxContainer();
            nameRow.Alignment = BoxContainer.AlignmentMode.Center;
            nameRow.AddThemeConstantOverride("separation", 8);
            pVBox.AddChild(nameRow);

            var avatar = new PanelContainer();
            avatar.CustomMinimumSize = new Vector2(24, 24);
            var avStyle = new StyleBoxFlat();
            avStyle.BgColor = playerColor;
            avStyle.SetCornerRadiusAll(12);
            avStyle.SetContentMarginAll(0);
            avatar.AddThemeStyleboxOverride("panel", avStyle);

            var avatarInit = new Label();
            avatarInit.Text = !string.IsNullOrEmpty(comp.DisplayName) ? comp.DisplayName[..1].ToUpper() : "?";
            avatarInit.AddThemeFontSizeOverride("font_size", 12);
            avatarInit.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.95f));
            avatarInit.HorizontalAlignment = HorizontalAlignment.Center;
            avatarInit.VerticalAlignment = VerticalAlignment.Center;
            avatar.AddChild(avatarInit);
            nameRow.AddChild(avatar);

            var nameLabel = new Label();
            nameLabel.Text = comp.DisplayName;
            nameLabel.AddThemeFontSizeOverride("font_size", 16);
            nameLabel.AddThemeColorOverride("font_color", isWinner ? WinnerGold : TextPrimary);
            nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
            nameRow.AddChild(nameLabel);

            // Big result icon
            var resultIcon = new Label();
            resultIcon.Text = isWinner ? "🏆" : "❌";
            resultIcon.AddThemeFontSizeOverride("font_size", 40);
            resultIcon.HorizontalAlignment = HorizontalAlignment.Center;
            pVBox.AddChild(resultIcon);

            // Result text
            var resultText = new Label();
            resultText.Text = isWinner ? "胜出！" : "落败";
            resultText.AddThemeFontSizeOverride("font_size", 16);
            resultText.AddThemeColorOverride("font_color", isWinner ? WinnerGold : LoserRed);
            resultText.HorizontalAlignment = HorizontalAlignment.Center;
            pVBox.AddChild(resultText);

            container.AddChild(playerPanel);
        }
    }

    // ═══════════════════════════════════════════════
    //  CARD DISPLAY HELPERS
    // ═══════════════════════════════════════════════

    private static string GetCardDisplayName(DraftCard draftCard)
    {
        if (draftCard.Card == null)
            return FormatEntryName(draftCard.CardEntry);

        try
        {
            string title = draftCard.Card.Title;
            if (!string.IsNullOrEmpty(title))
                return title;
        }
        catch { }

        return FormatEntryName(draftCard.CardEntry);
    }

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
    //  3D STYLE HELPERS
    // ═══════════════════════════════════════════════

    private static StyleBoxFlat Create3DPanelStyle(
        Color bgColor, Color borderColor, Color highlightColor, Color shadowColor,
        int borderWidth = 2, int cornerRadius = 12)
    {
        var style = new StyleBoxFlat();
        style.BgColor = bgColor;
        style.BorderColor = borderColor;
        style.SetBorderWidthAll(borderWidth);
        style.SetCornerRadiusAll(cornerRadius);
        style.SetContentMarginAll(16);
        // 3D shadow
        style.ShadowColor = shadowColor;
        style.ShadowSize = 6;
        style.ShadowOffset = new Vector2(3, 5);
        return style;
    }

    private static Control CreateGradientSeparator(Color accentColor, int height = 2)
    {
        var separator = new ColorRect();
        separator.CustomMinimumSize = new Vector2(0, height);
        separator.Color = new Color(accentColor.R, accentColor.G, accentColor.B, 0.3f);
        separator.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return separator;
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
                ModEntry.Logger.Error("Could not get SceneTree for DraftResultOverlay injection.");
                return;
            }

            sceneTree.Root.CallDeferred("add_child", _canvasLayer);
        }
        catch (System.Exception ex)
        {
            ModEntry.Logger.Error($"Failed to inject DraftResultOverlay: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════
    //  CLEANUP
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Clean up all references.
    /// </summary>
    public static void Cleanup()
    {
        _dismissTcs?.TrySetResult(false);
        Hide();
        _dismissTcs = null;
    }
}
