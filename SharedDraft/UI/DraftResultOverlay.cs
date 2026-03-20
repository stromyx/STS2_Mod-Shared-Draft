using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;

namespace SharedDraft.UI;

/// <summary>
/// Inline overlay for displaying rock/paper/scissors conflict resolution results.
///
/// V3 — EMBEDDED ANIMATION: No longer uses a separate CanvasLayer popup.
/// Instead, plays the fight animation directly over the SharedDraftScreen,
/// with arms sliding in from screen edges (inspired by NHandImage).
///
/// Animation flow (per conflict):
///   1. Dim the SharedDraftScreen card grid
///   2. Show contested card name banner at top center
///   3. Arms slide in from screen edges (left/right/top/bottom depending on player index)
///   4. Arms pump/swing 3 times (showing Rock fist), getting faster each round
///   5. On final pump, arms reveal actual move (Rock/Paper/Scissors texture)
///   6. Winner: golden glow + scale up
///   7. Loser: shake + desaturate
///   8. Winner's arm reaches toward the contested card and "grabs" it
///   9. Brief winner announcement, then auto-dismiss
///
/// Falls back to emoji display if arm textures are unavailable.
///
/// IMPORTANT: Does NOT inherit from any Godot node class.
/// Uses composition to avoid Godot source generator StringName crashes.
///
/// Layout:
///   [Injected into SharedDraftScreen's CanvasLayer or SceneTree root]
///   CanvasLayer (Layer=98, "DraftResultOverlay")
///   └── Control (full-rect fight arena)
///       ├── ColorRect (semi-transparent dim backdrop)
///       ├── PanelContainer (card name banner, top-center)
///       ├── [per player] Control (arm container — positioned at screen edge)
///       │   ├── TextureRect (arm image, large ~256px)
///       │   └── VBoxContainer (player name + move label below arm)
///       ├── Label (winner announcement, center)
///       └── Label (countdown, bottom-center)
/// </summary>
public static class DraftResultOverlay
{
    // ── References ──
    private static CanvasLayer? _canvasLayer;
    private static Control? _fightArena;
    private static ColorRect? _dimBackdrop;
    private static Label? _bannerLabel;

    // ── Fight animation data ──
    private static readonly List<ArmNode> _armNodes = new();

    private class ArmNode
    {
        public int PlayerIndex;
        public int PlayerSlot;
        public string PlayerName = "";
        public bool IsWinner;
        public Player? Player;

        // Scene nodes
        public Control Container = null!;      // Outer container (position = screen-edge start)
        public TextureRect ArmRect = null!;     // The arm image
        public Label? NameLabel;                // Player name below arm
        public Label? MoveLabel;                // Move name (hidden until reveal)
        public Label? EmojiLabel;               // Fallback emoji

        // Textures
        public Texture2D? RockTexture;
        public Texture2D? ActualMoveTexture;

        // Layout
        public Vector2 OffscreenPos;           // Starting position (off-screen)
        public Vector2 FightPos;               // Fighting position (on-screen, near center)
        public Vector2 GrabTargetPos;          // Where to reach for "grab" animation
        public bool FlipH;                     // Mirror for right-side players
        public float EntryRotation;            // Initial rotation when sliding in
    }

    // ═══════════════════════════════════════════════
    //  COLOR PALETTE — matches SharedDraftScreen theme
    // ═══════════════════════════════════════════════

    private static readonly Color AccentGold = new(1.0f, 0.85f, 0.3f, 1.0f);
    private static readonly Color AccentPurple = new(0.6f, 0.4f, 0.9f, 1.0f);
    private static readonly Color TextPrimary = new(0.95f, 0.92f, 0.98f);
    private static readonly Color TextSecondary = new(0.7f, 0.68f, 0.78f);
    private static readonly Color WinnerGold = new(1.0f, 0.9f, 0.3f, 1.0f);
    private static readonly Color WinnerGlow = new(1.0f, 0.85f, 0.2f, 0.5f);
    private static readonly Color LoserRed = new(1.0f, 0.35f, 0.3f, 1.0f);
    private static readonly Color CardHeaderBg = new(0.12f, 0.1f, 0.2f, 0.9f);
    private static readonly Color DimColor = new(0.0f, 0.0f, 0.0f, 0.75f);

    // Player colors (same as SharedDraftScreen)
    private static readonly Color[] PlayerColors =
    [
        new Color(0.25f, 0.55f, 1.0f, 1.0f),   // Blue
        new Color(1.0f, 0.35f, 0.3f, 1.0f),     // Red
        new Color(0.2f, 0.85f, 0.35f, 1.0f),    // Green
        new Color(1.0f, 0.75f, 0.15f, 1.0f),    // Gold
    ];

    // Arm display size (large, like NHandImage in the game)
    private const float ArmSize = 220f;

    // ── Move helpers ──

    private static string GetMoveEmoji(RelicPickingFightMove move) => move switch
    {
        RelicPickingFightMove.Rock => "🪨",
        RelicPickingFightMove.Paper => "📄",
        RelicPickingFightMove.Scissors => "✂️",
        _ => "❓"
    };

    private static string GetMoveName(RelicPickingFightMove move) => move switch
    {
        RelicPickingFightMove.Rock => "Rock",
        RelicPickingFightMove.Paper => "Paper",
        RelicPickingFightMove.Scissors => "Scissors",
        _ => "???"
    };

    private static Texture2D? GetArmTexture(Player? player, RelicPickingFightMove move)
    {
        // Try the player's own Character textures first
        var character = player?.Character;

        // Fallback: if this player has no Character (remote/virtual), use any player that has one
        if (character == null)
        {
            try
            {
                var manager = SharedDraftManager.Instance;
                foreach (var ps in manager.PlayerStates)
                {
                    if (ps.Player?.Character != null)
                    {
                        character = ps.Player.Character;
                        break;
                    }
                }
            }
            catch { }
        }

        if (character == null) return null;

        try
        {
            return move switch
            {
                RelicPickingFightMove.Rock => character.ArmRockTexture,
                RelicPickingFightMove.Paper => character.ArmPaperTexture,
                RelicPickingFightMove.Scissors => character.ArmScissorsTexture,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════
    //  SHOW RESULTS — Embedded fight animation
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Show conflict resolution results with inline animated RPS fight.
    /// Plays directly over the SharedDraftScreen without a separate popup.
    /// </summary>
    public static async Task ShowResults(IReadOnlyList<ConflictResult> conflicts)
    {
        if (conflicts.Count == 0)
            return;

        // Get screen size for layout calculations
        Vector2 screenSize = new(1920, 1080); // Default fallback
        try
        {
            var vp = (Engine.GetMainLoop() as SceneTree)?.Root;
            if (vp != null)
                screenSize = vp.GetVisibleRect().Size;
        }
        catch { }

        // Process each conflict sequentially with its own animation
        for (int i = 0; i < conflicts.Count; i++)
        {
            var conflict = conflicts[i];

            // Build the fight arena for this conflict
            BuildFightArena(conflict, screenSize);
            InjectIntoSceneTree();

            if (_canvasLayer != null)
                _canvasLayer.Visible = true;

            // Animate
            await AnimateConflict(conflict, screenSize);

            // Brief pause to show result
            await Task.Delay(1200);

            // Clean up this conflict's arena before moving to next
            Hide();
        }

        // Brief transition pause, then return to card selection
        await Task.Delay(300);
    }

    // ═══════════════════════════════════════════════
    //  BUILD FIGHT ARENA — Per conflict
    // ═══════════════════════════════════════════════

    private static void BuildFightArena(ConflictResult conflict, Vector2 screenSize)
    {
        Hide(); // Clean up previous
        _armNodes.Clear();

        _canvasLayer = new CanvasLayer();
        _canvasLayer.Name = "DraftResultOverlay";
        _canvasLayer.Layer = 98;
        _canvasLayer.Visible = false;

        // Full-rect fight arena
        _fightArena = new Control();
        _fightArena.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _fightArena.MouseFilter = Control.MouseFilterEnum.Stop; // Block input during fight
        _canvasLayer.AddChild(_fightArena);

        // Semi-transparent dim backdrop
        _dimBackdrop = new ColorRect();
        _dimBackdrop.Color = DimColor;
        _dimBackdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _dimBackdrop.Modulate = new Color(1, 1, 1, 0); // Start invisible for fade-in
        _fightArena.AddChild(_dimBackdrop);

        // Card name banner at top
        string cardName = conflict.ContestedCard.HasCardModel
            ? GetCardDisplayName(conflict.ContestedCard)
            : FormatEntryName(conflict.ContestedCard.CardEntry);

        var bannerPanel = new PanelContainer();
        bannerPanel.Position = new Vector2(screenSize.X / 2 - 250, 40);
        bannerPanel.CustomMinimumSize = new Vector2(500, 50);
        var bannerStyle = new StyleBoxFlat();
        bannerStyle.BgColor = CardHeaderBg;
        bannerStyle.BorderColor = AccentGold;
        bannerStyle.SetBorderWidthAll(2);
        bannerStyle.SetCornerRadiusAll(10);
        bannerStyle.SetContentMarginAll(10);
        bannerStyle.ShadowColor = new Color(0, 0, 0, 0.5f);
        bannerStyle.ShadowSize = 6;
        bannerStyle.ShadowOffset = new Vector2(2, 4);
        bannerPanel.AddThemeStyleboxOverride("panel", bannerStyle);
        bannerPanel.Modulate = new Color(1, 1, 1, 0); // Start invisible
        _fightArena.AddChild(bannerPanel);

        _bannerLabel = new Label();
        _bannerLabel.Text = $"⚔️  Contested Card: {cardName}  ⚔️";
        _bannerLabel.AddThemeFontSizeOverride("font_size", 22);
        _bannerLabel.AddThemeColorOverride("font_color", AccentGold);
        _bannerLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _bannerLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        bannerPanel.AddChild(_bannerLabel);

        // Build arm nodes for each player
        // V4: Always use arm animation, even in fallback/debug mode.
        // When Fight data is null, we generate synthetic fight data using the
        // local player's arm textures for all participants.
        BuildFightArms(conflict, screenSize);
    }

    /// <summary>
    /// Build arm TextureRects positioned at screen edges, ready to slide in.
    /// Mimics NHandImage positioning: player 0=bottom, 1=left, 2=right, 3=top.
    /// For 2-player fights, use left and right positions for dramatic face-off.
    ///
    /// V4: Also works in fallback mode (Fight==null). When fight data is unavailable,
    /// generates synthetic moves and uses the local player's arm textures for all participants.
    /// </summary>
    private static void BuildFightArms(ConflictResult conflict, Vector2 screenSize)
    {
        if (_fightArena == null) return;

        float centerX = screenSize.X / 2;
        float centerY = screenSize.Y / 2;
        Vector2 cardCenter = new(centerX, centerY + 30);

        // Determine participants and their moves.
        // If Fight data exists, use it. Otherwise, generate synthetic moves.
        int playerCount = conflict.Competitors.Count;
        var participantData = new List<(int index, string playerName, int playerSlot,
            bool isWinner, Player? player, RelicPickingFightMove actualMove)>();

        if (conflict.Fight != null && conflict.Fight.rounds.Count > 0)
        {
            // Real fight data available
            var lastRound = conflict.Fight.rounds.Last();
            var playersInvolved = conflict.Fight.playersInvolved;

            for (int i = 0; i < lastRound.moves.Count && i < playersInvolved.Count; i++)
            {
                var moveValue = lastRound.moves[i];
                if (!moveValue.HasValue) continue;

                var player = playersInvolved[i];
                var compState = conflict.Competitors.FirstOrDefault(c => c.Player == player);
                string playerName = compState?.DisplayName ?? $"Player {player.NetId}";
                bool isWinner = compState == conflict.Winner;
                int playerSlot = compState?.PlayerSlot ?? 0;

                participantData.Add((i, playerName, playerSlot, isWinner, player, moveValue.Value));
            }
        }
        else
        {
            // Fallback: no fight data — generate synthetic moves for animation.
            // Use the local player's arm textures as stand-in for all participants.
            Player? localPlayer = null;
            try
            {
                localPlayer = conflict.Competitors
                    .FirstOrDefault(c => c.Player != null)?.Player;
            }
            catch { /* ignore */ }

            // Generate plausible moves: winner gets a winning move, losers get a losing one.
            // For visual spectacle, pick a random matchup.
            var rng = new System.Random();
            var possibleMoves = System.Enum.GetValues<RelicPickingFightMove>();
            var winnerMove = possibleMoves[rng.Next(possibleMoves.Length)];
            // Loser move: the move that loses to the winner's move
            // Rock(0) beats Scissors(2), Paper(1) beats Rock(0), Scissors(2) beats Paper(1)
            var loserMove = (RelicPickingFightMove)(((int)winnerMove + 2) % 3);

            for (int i = 0; i < conflict.Competitors.Count; i++)
            {
                var comp = conflict.Competitors[i];
                bool isWinner = comp == conflict.Winner;
                var move = isWinner ? winnerMove : loserMove;
                Player? player = comp.Player ?? localPlayer;

                participantData.Add((i, comp.DisplayName, comp.PlayerSlot,
                    isWinner, player, move));
            }
        }

        // Now build arm nodes for each participant
        for (int idx = 0; idx < participantData.Count; idx++)
        {
            var (i, playerName, playerSlot, isWinner, player, actualMove) = participantData[idx];

            // Determine positions based on player count and index
            Vector2 offscreen, fightPos;
            bool flipH;
            float entryRotation;

            if (participantData.Count == 2)
            {
                // 2 players: left vs right (classic face-off)
                if (idx == 0)
                {
                    offscreen = new Vector2(-ArmSize - 50, centerY - ArmSize / 2);
                    fightPos = new Vector2(centerX - 280, centerY - ArmSize / 2);
                    flipH = false;
                    entryRotation = -0.15f;
                }
                else
                {
                    offscreen = new Vector2(screenSize.X + 50, centerY - ArmSize / 2);
                    fightPos = new Vector2(centerX + 80, centerY - ArmSize / 2);
                    flipH = true;
                    entryRotation = 0.15f;
                }
            }
            else
            {
                // 3-4 players: spread around center
                int slot = idx % 4;
                switch (slot)
                {
                    case 0: // Bottom
                        offscreen = new Vector2(centerX - ArmSize / 2, screenSize.Y + 50);
                        fightPos = new Vector2(centerX - ArmSize / 2, centerY + 80);
                        flipH = false;
                        entryRotation = 0;
                        break;
                    case 1: // Left
                        offscreen = new Vector2(-ArmSize - 50, centerY - ArmSize / 2);
                        fightPos = new Vector2(centerX - 300, centerY - ArmSize / 2);
                        flipH = false;
                        entryRotation = -0.2f;
                        break;
                    case 2: // Right
                        offscreen = new Vector2(screenSize.X + 50, centerY - ArmSize / 2);
                        fightPos = new Vector2(centerX + 100, centerY - ArmSize / 2);
                        flipH = true;
                        entryRotation = 0.2f;
                        break;
                    default: // Top
                        offscreen = new Vector2(centerX - ArmSize / 2, -ArmSize - 50);
                        fightPos = new Vector2(centerX - ArmSize / 2, centerY - 280);
                        flipH = false;
                        entryRotation = Mathf.Pi;
                        break;
                }
            }

            // Get textures — use participant's own player if available
            Texture2D? rockTex = GetArmTexture(player, RelicPickingFightMove.Rock);
            Texture2D? actualTex = GetArmTexture(player, actualMove);

            // Create arm container at offscreen position
            var armContainer = new Control();
            armContainer.Position = offscreen;
            armContainer.Size = new Vector2(ArmSize + 60, ArmSize + 80);
            armContainer.MouseFilter = Control.MouseFilterEnum.Ignore;
            _fightArena.AddChild(armContainer);

            var armNode = new ArmNode
            {
                PlayerIndex = i,
                PlayerSlot = playerSlot,
                PlayerName = playerName,
                IsWinner = isWinner,
                Player = player,
                Container = armContainer,
                OffscreenPos = offscreen,
                FightPos = fightPos,
                GrabTargetPos = cardCenter,
                FlipH = flipH,
                EntryRotation = entryRotation,
                RockTexture = rockTex,
                ActualMoveTexture = actualTex
            };

            if (rockTex != null || actualTex != null)
            {
                // Arm texture
                var armRect = new TextureRect();
                armRect.Texture = rockTex ?? actualTex;
                armRect.CustomMinimumSize = new Vector2(ArmSize, ArmSize);
                armRect.Size = new Vector2(ArmSize, ArmSize);
                armRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
                armRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                armRect.MouseFilter = Control.MouseFilterEnum.Ignore;
                armRect.PivotOffset = new Vector2(ArmSize / 2, ArmSize / 2);
                if (flipH)
                    armRect.FlipH = true;
                armContainer.AddChild(armRect);
                armNode.ArmRect = armRect;
            }
            else
            {
                // Emoji fallback (no arm textures at all)
                var emojiRect = new TextureRect();
                emojiRect.CustomMinimumSize = new Vector2(ArmSize, ArmSize);
                emojiRect.Size = new Vector2(ArmSize, ArmSize);
                emojiRect.MouseFilter = Control.MouseFilterEnum.Ignore;
                emojiRect.PivotOffset = new Vector2(ArmSize / 2, ArmSize / 2);
                emojiRect.Visible = false; // Placeholder
                armContainer.AddChild(emojiRect);
                armNode.ArmRect = emojiRect;

                var emojiLabel = new Label();
                emojiLabel.Text = "✊"; // Start with fist
                emojiLabel.AddThemeFontSizeOverride("font_size", 72);
                emojiLabel.HorizontalAlignment = HorizontalAlignment.Center;
                emojiLabel.VerticalAlignment = VerticalAlignment.Center;
                emojiLabel.Position = new Vector2(ArmSize / 2 - 40, ArmSize / 2 - 50);
                emojiLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
                armContainer.AddChild(emojiLabel);
                armNode.EmojiLabel = emojiLabel;
            }

            // Player name label below arm
            Color playerColor = playerSlot < PlayerColors.Length
                ? PlayerColors[playerSlot]
                : TextPrimary;

            var nameLabel = new Label();
            nameLabel.Text = playerName;
            nameLabel.AddThemeFontSizeOverride("font_size", 16);
            nameLabel.AddThemeColorOverride("font_color", playerColor);
            nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
            nameLabel.Position = new Vector2(0, ArmSize + 5);
            nameLabel.Size = new Vector2(ArmSize + 60, 24);
            nameLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
            nameLabel.Modulate = new Color(1, 1, 1, 0); // Hidden until slide-in
            armContainer.AddChild(nameLabel);
            armNode.NameLabel = nameLabel;

            // Move name label (hidden until reveal phase)
            var moveLabel = new Label();
            moveLabel.Text = "";
            moveLabel.AddThemeFontSizeOverride("font_size", 14);
            moveLabel.AddThemeColorOverride("font_color", TextSecondary);
            moveLabel.HorizontalAlignment = HorizontalAlignment.Center;
            moveLabel.Position = new Vector2(0, ArmSize + 28);
            moveLabel.Size = new Vector2(ArmSize + 60, 20);
            moveLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
            armContainer.AddChild(moveLabel);
            armNode.MoveLabel = moveLabel;

            // Store actual move name for later reveal
            moveLabel.SetMeta("actual_move_name", GetMoveName(actualMove));
            if (armNode.EmojiLabel != null)
                armNode.EmojiLabel.SetMeta("actual_emoji", GetMoveEmoji(actualMove));

            _armNodes.Add(armNode);
        }
    }

    // ═══════════════════════════════════════════════
    //  ANIMATE — The full fight sequence per conflict
    // ═══════════════════════════════════════════════

    private static async Task AnimateConflict(ConflictResult conflict, Vector2 screenSize)
    {
        // Phase 0: Fade in dim backdrop + banner
        Callable.From(() =>
        {
            if (_dimBackdrop != null && GodotObject.IsInstanceValid(_dimBackdrop))
            {
                var tween = _dimBackdrop.CreateTween();
                tween.TweenProperty(_dimBackdrop, "modulate:a", 1.0f, 0.3f);
            }

            // Fade in banner
            var bannerParent = _bannerLabel?.GetParent() as Control;
            if (bannerParent != null && GodotObject.IsInstanceValid(bannerParent))
            {
                var tween = bannerParent.CreateTween();
                tween.TweenProperty(bannerParent, "modulate:a", 1.0f, 0.3f)
                    .SetTrans(Tween.TransitionType.Sine);
            }
        }).CallDeferred();

        await Task.Delay(400);

        // If no arm nodes (fallback mode), just wait and return
        if (_armNodes.Count == 0)
        {
            await Task.Delay(1500);
            return;
        }

        // Phase 1: Arms slide in from screen edges
        Callable.From(() =>
        {
            foreach (var arm in _armNodes)
            {
                if (!GodotObject.IsInstanceValid(arm.Container)) continue;

                var tween = arm.Container.CreateTween();
                tween.SetParallel(true);

                // Slide from offscreen to fight position
                tween.TweenProperty(arm.Container, "position", arm.FightPos, 0.5f)
                    .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);

                // Fade in name label
                if (arm.NameLabel != null && GodotObject.IsInstanceValid(arm.NameLabel))
                {
                    tween.TweenProperty(arm.NameLabel, "modulate:a", 1.0f, 0.3f)
                        .SetDelay(0.2f);
                }
            }
        }).CallDeferred();

        await Task.Delay(700);

        // Phase 2: Pump animation (3 pumps with Rock fist, getting faster)
        float[] pumpDurations = [0.32f, 0.24f, 0.16f];
        foreach (float dur in pumpDurations)
        {
            Callable.From(() =>
            {
                foreach (var arm in _armNodes)
                {
                    if (!GodotObject.IsInstanceValid(arm.ArmRect) || !arm.ArmRect.IsInsideTree()) continue;

                    var tween = arm.ArmRect.CreateTween();
                    float swingAngle = (arm.FlipH ? -1f : 1f) * Mathf.Pi / 8f;
                    float jitter = (float)(new System.Random().NextDouble() * 0.06 - 0.03);

                    // Swing down
                    tween.TweenProperty(arm.ArmRect, "rotation", swingAngle + jitter, dur * 0.5f)
                        .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
                    // Swing back up
                    tween.TweenProperty(arm.ArmRect, "rotation", 0f, dur * 0.5f)
                        .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);

                    // Also pump emoji if fallback
                    if (arm.EmojiLabel != null && GodotObject.IsInstanceValid(arm.EmojiLabel))
                    {
                        var eTween = arm.EmojiLabel.CreateTween();
                        eTween.TweenProperty(arm.EmojiLabel, "scale", new Vector2(1.15f, 1.15f), dur * 0.5f)
                            .SetTrans(Tween.TransitionType.Sine);
                        eTween.TweenProperty(arm.EmojiLabel, "scale", Vector2.One, dur * 0.5f)
                            .SetTrans(Tween.TransitionType.Sine);
                    }
                }
            }).CallDeferred();

            await Task.Delay((int)(dur * 1000) + 60);
        }

        // Phase 3: Reveal actual moves
        Callable.From(() =>
        {
            foreach (var arm in _armNodes)
            {
                if (!GodotObject.IsInstanceValid(arm.ArmRect)) continue;

                if (arm.ActualMoveTexture != null && arm.ArmRect.Visible)
                {
                    // Final dramatic swing + texture swap
                    var tween = arm.ArmRect.CreateTween();
                    float revealAngle = (arm.FlipH ? -1f : 1f) * Mathf.Pi / 6f;

                    tween.TweenProperty(arm.ArmRect, "rotation", revealAngle, 0.12f)
                        .SetTrans(Tween.TransitionType.Sine);

                    var capturedArm = arm;
                    tween.TweenCallback(Callable.From(() =>
                    {
                        if (GodotObject.IsInstanceValid(capturedArm.ArmRect))
                            capturedArm.ArmRect.Texture = capturedArm.ActualMoveTexture;
                    }));

                    tween.TweenProperty(arm.ArmRect, "rotation", 0f, 0.3f)
                        .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                }
                else if (arm.EmojiLabel != null && GodotObject.IsInstanceValid(arm.EmojiLabel))
                {
                    // Emoji reveal
                    string actualEmoji = arm.EmojiLabel.HasMeta("actual_emoji")
                        ? arm.EmojiLabel.GetMeta("actual_emoji").AsString()
                        : "❓";

                    var tween = arm.EmojiLabel.CreateTween();
                    tween.TweenProperty(arm.EmojiLabel, "scale", new Vector2(1.3f, 1.3f), 0.1f);
                    tween.TweenCallback(Callable.From(() =>
                    {
                        if (GodotObject.IsInstanceValid(arm.EmojiLabel))
                            arm.EmojiLabel.Text = actualEmoji;
                    }));
                    tween.TweenProperty(arm.EmojiLabel, "scale", Vector2.One, 0.2f)
                        .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                }

                // Reveal move name
                if (arm.MoveLabel != null && GodotObject.IsInstanceValid(arm.MoveLabel)
                    && arm.MoveLabel.HasMeta("actual_move_name"))
                {
                    arm.MoveLabel.Text = arm.MoveLabel.GetMeta("actual_move_name").AsString();
                }
            }
        }).CallDeferred();

        await Task.Delay(600);

        // Phase 4: Winner/Loser effects
        Callable.From(() =>
        {
            foreach (var arm in _armNodes)
            {
                if (!GodotObject.IsInstanceValid(arm.Container)) continue;

                if (arm.IsWinner)
                {
                    // Winner: golden glow + scale up
                    var tween = arm.ArmRect.CreateTween();
                    tween.TweenProperty(arm.ArmRect, "scale", new Vector2(1.25f, 1.25f), 0.35f)
                        .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                    tween.TweenProperty(arm.ArmRect, "modulate", WinnerGold, 0.2f);

                    // Golden name
                    if (arm.NameLabel != null && GodotObject.IsInstanceValid(arm.NameLabel))
                    {
                        arm.NameLabel.AddThemeColorOverride("font_color", WinnerGold);
                        arm.NameLabel.Text = $"🏆 {arm.PlayerName}";
                    }
                }
                else
                {
                    // Loser: shake + desaturate
                    var tween = arm.ArmRect.CreateTween();
                    float baseX = arm.ArmRect.Position.X;
                    tween.TweenProperty(arm.ArmRect, "position:x", baseX + 8, 0.04f);
                    tween.TweenProperty(arm.ArmRect, "position:x", baseX - 8, 0.04f);
                    tween.TweenProperty(arm.ArmRect, "position:x", baseX + 5, 0.04f);
                    tween.TweenProperty(arm.ArmRect, "position:x", baseX - 3, 0.04f);
                    tween.TweenProperty(arm.ArmRect, "position:x", baseX, 0.04f);
                    tween.TweenProperty(arm.ArmRect, "modulate", new Color(0.4f, 0.4f, 0.4f, 0.6f), 0.4f);

                    // Dim name + show "defeated"
                    if (arm.NameLabel != null && GodotObject.IsInstanceValid(arm.NameLabel))
                    {
                        arm.NameLabel.AddThemeColorOverride("font_color", LoserRed);
                        arm.NameLabel.Text = $"❌ {arm.PlayerName}";
                    }
                }
            }
        }).CallDeferred();

        await Task.Delay(800);

        // Phase 5: Winner arm "grabs" the card (reaches toward center)
        var winner = _armNodes.FirstOrDefault(a => a.IsWinner);
        if (winner != null)
        {
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(winner.Container)) return;

                // Switch to Paper (open hand) for grab
                Texture2D? paperTex = GetArmTexture(winner.Player, RelicPickingFightMove.Paper);

                var grabTween = winner.Container.CreateTween();

                // Move toward card center
                Vector2 grabPos = new(
                    winner.GrabTargetPos.X - ArmSize / 2,
                    winner.GrabTargetPos.Y - ArmSize / 2);

                // Open hand (Paper) while reaching
                if (paperTex != null && GodotObject.IsInstanceValid(winner.ArmRect) && winner.ArmRect.Visible)
                {
                    grabTween.TweenCallback(Callable.From(() =>
                    {
                        if (GodotObject.IsInstanceValid(winner.ArmRect))
                            winner.ArmRect.Texture = paperTex;
                    }));
                }
                else if (winner.EmojiLabel != null && GodotObject.IsInstanceValid(winner.EmojiLabel))
                {
                    grabTween.TweenCallback(Callable.From(() =>
                    {
                        if (GodotObject.IsInstanceValid(winner.EmojiLabel))
                            winner.EmojiLabel.Text = "🤚";
                    }));
                }

                grabTween.TweenProperty(winner.Container, "position", grabPos, 0.4f)
                    .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);

                // Close hand (Rock / fist) after reaching
                grabTween.TweenCallback(Callable.From(() =>
                {
                    if (winner.RockTexture != null && GodotObject.IsInstanceValid(winner.ArmRect) && winner.ArmRect.Visible)
                        winner.ArmRect.Texture = winner.RockTexture;
                    else if (winner.EmojiLabel != null && GodotObject.IsInstanceValid(winner.EmojiLabel))
                        winner.EmojiLabel.Text = "✊";
                }));

                // Pull back to fight position (carrying the card)
                grabTween.TweenProperty(winner.Container, "position", winner.FightPos, 0.5f)
                    .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
            }).CallDeferred();

            await Task.Delay(1200);
        }

        // Phase 6: All arms slide out
        Callable.From(() =>
        {
            foreach (var arm in _armNodes)
            {
                if (!GodotObject.IsInstanceValid(arm.Container)) continue;

                var tween = arm.Container.CreateTween();
                tween.SetParallel(true);
                tween.TweenProperty(arm.Container, "position", arm.OffscreenPos, 0.4f)
                    .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.In);
                tween.TweenProperty(arm.Container, "modulate:a", 0.0f, 0.3f);
            }

            // Fade out dim
            if (_dimBackdrop != null && GodotObject.IsInstanceValid(_dimBackdrop))
            {
                var dimTween = _dimBackdrop.CreateTween();
                dimTween.TweenProperty(_dimBackdrop, "modulate:a", 0.0f, 0.4f);
            }

            // Fade out banner
            var bannerParent = _bannerLabel?.GetParent() as Control;
            if (bannerParent != null && GodotObject.IsInstanceValid(bannerParent))
            {
                var tween = bannerParent.CreateTween();
                tween.TweenProperty(bannerParent, "modulate:a", 0.0f, 0.3f);
            }
        }).CallDeferred();

        await Task.Delay(500);
    }

    // ═══════════════════════════════════════════════
    //  HIDE / CLEANUP
    // ═══════════════════════════════════════════════

    public static void Hide()
    {
        if (_canvasLayer != null && GodotObject.IsInstanceValid(_canvasLayer))
        {
            _canvasLayer.QueueFree();
        }
        _canvasLayer = null;
        _fightArena = null;
        _dimBackdrop = null;
        _bannerLabel = null;
        _armNodes.Clear();
    }

    public static void Cleanup()
    {
        Hide();
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
}
