using Godot;

namespace GoldGift.UI;

/// <summary>
/// Factory for creating the Gift Gold button and panel.
/// Uses composition instead of inheritance to avoid Godot source generator crashes.
/// 
/// The button is placed inside a draggable container so users can reposition it
/// freely to avoid blocking other UI elements.
/// </summary>
public static class GoldGiftButton
{
    private static Button? _button;
    private static CanvasLayer? _panelLayer;
    private static Control? _dragHandle;
    private static Label? _notificationLabel;

    // Drag state
    private static bool _isDragging;
    private static bool _isPotentialDrag;
    private static Vector2 _dragStartPos;
    private const float DragThreshold = 5.0f; // pixels before drag starts

    /// <summary>
    /// Create and return the button overlay CanvasLayer (with draggable button inside).
    /// </summary>
    public static CanvasLayer CreateOverlay(Node parentForPanel)
    {
        var canvasLayer = new CanvasLayer();
        canvasLayer.Name = "GoldGiftButtonOverlay";
        canvasLayer.Layer = 90;

        // ── Draggable container ──
        // The drag handle is a plain Control used purely as a position container.
        // It does NOT capture input — all input is handled by the button's GuiInput.
        _dragHandle = new Control();
        _dragHandle.Name = "GoldGiftDragHandle";
        _dragHandle.Position = new Vector2(-200, 8); // will be adjusted after added to tree
        _dragHandle.Size = new Vector2(150, 36);
        // IMPORTANT: Ignore input on dragHandle — the button handles everything
        _dragHandle.MouseFilter = Control.MouseFilterEnum.Ignore;

        // Use a PanelContainer for visual feedback (subtle background when hovering)
        var dragPanel = new PanelContainer();
        dragPanel.Name = "DragPanel";
        dragPanel.SetAnchorsAndOffsetsPreset(Godot.Control.LayoutPreset.FullRect);
        var dragStyle = new StyleBoxFlat();
        dragStyle.BgColor = new Color(0, 0, 0, 0); // Transparent by default
        dragStyle.SetCornerRadiusAll(6);
        dragPanel.AddThemeStyleboxOverride("panel", dragStyle);
        dragPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _dragHandle.AddChild(dragPanel);

        // ── Create button ──
        _button = new Button();
        BuildButton(_button);
        _button.Position = new Vector2(0, 0);
        _dragHandle.AddChild(_button);

        // ── Drag grip indicator ──
        var gripLabel = new Label();
        gripLabel.Text = "⠿";
        gripLabel.AddThemeFontSizeOverride("font_size", 10);
        gripLabel.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.3f));
        gripLabel.Position = new Vector2(2, 2);
        gripLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _dragHandle.AddChild(gripLabel);

        // ── Notification label (for "Received Xg from Player") ──
        _notificationLabel = new Label();
        _notificationLabel.Name = "GoldGiftNotification";
        _notificationLabel.Text = "";
        _notificationLabel.Visible = false;
        _notificationLabel.AddThemeFontSizeOverride("font_size", 16);
        _notificationLabel.AddThemeColorOverride("font_color", new Color(0.3f, 1.0f, 0.3f));
        _notificationLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _notificationLabel.Position = new Vector2(0, 40);
        _notificationLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _dragHandle.AddChild(_notificationLabel);

        canvasLayer.AddChild(_dragHandle);

        // Position the drag handle at top-right after being added to tree
        canvasLayer.Ready += () =>
        {
            try
            {
                var viewport = canvasLayer.GetViewport();
                if (viewport != null)
                {
                    var viewSize = viewport.GetVisibleRect().Size;
                    // Default position: shifted left by ~3 button widths from right edge
                    _dragHandle!.Position = new Vector2(viewSize.X - 640, 8);
                }
            }
            catch { /* ignore */ }
        };

        // Create the gift panel overlay
        CreateGiftPanel(canvasLayer);

        // Subscribe to gift received events for notification
        GoldGiftNetworkHandler.GoldGiftReceived += OnGoldGiftReceived;

        ModEntry.Logger.Info("Gold Gift button overlay created (draggable).");

        return canvasLayer;
    }

    /// <summary>
    /// Get the existing button if created.
    /// </summary>
    public static Button? GetButton() => _button;

    private static void BuildButton(Button btn)
    {
        btn.Name = "GoldGiftButton";
        btn.Text = GoldGiftConfig.DebugMode
            ? GoldGiftLocalization.Get("button_debug")
            : GoldGiftLocalization.Get("button");
        btn.CustomMinimumSize = new Vector2(150, 36);
        btn.TooltipText = GoldGiftLocalization.Get("tooltip");

        // Button styling - golden themed
        var normalStyle = new StyleBoxFlat();
        normalStyle.BgColor = new Color(0.55f, 0.4f, 0.1f, 0.85f);
        normalStyle.BorderColor = new Color(0.9f, 0.7f, 0.2f, 0.8f);
        normalStyle.SetBorderWidthAll(1);
        normalStyle.SetCornerRadiusAll(6);
        normalStyle.SetContentMarginAll(6);
        btn.AddThemeStyleboxOverride("normal", normalStyle);

        var hoverStyle = new StyleBoxFlat();
        hoverStyle.BgColor = new Color(0.65f, 0.5f, 0.15f, 0.95f);
        hoverStyle.BorderColor = new Color(1.0f, 0.8f, 0.3f, 1.0f);
        hoverStyle.SetBorderWidthAll(2);
        hoverStyle.SetCornerRadiusAll(6);
        hoverStyle.SetContentMarginAll(6);
        btn.AddThemeStyleboxOverride("hover", hoverStyle);

        var pressedStyle = new StyleBoxFlat();
        pressedStyle.BgColor = new Color(0.45f, 0.35f, 0.08f, 0.9f);
        pressedStyle.BorderColor = new Color(1.0f, 0.85f, 0.3f, 1.0f);
        pressedStyle.SetBorderWidthAll(2);
        pressedStyle.SetCornerRadiusAll(6);
        pressedStyle.SetContentMarginAll(6);
        btn.AddThemeStyleboxOverride("pressed", pressedStyle);

        // Text color
        btn.AddThemeColorOverride("font_color", new Color(1.0f, 0.9f, 0.6f));
        btn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 0.95f, 0.8f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(0.9f, 0.8f, 0.5f));
        btn.AddThemeFontSizeOverride("font_size", 14);

        // MouseFilter = Stop so the button captures all mouse input in its area.
        // We use GuiInput to handle click vs drag ourselves.
        btn.MouseFilter = Control.MouseFilterEnum.Stop;

        // Connect GuiInput for our custom click/drag handling.
        // Do NOT connect btn.Pressed — we handle clicks manually via mouse up/down.
        btn.GuiInput += OnButtonGuiInput;
    }

    private static void CreateGiftPanel(Node parent)
    {
        var panel = GoldGiftPanel.GetOrCreatePanel();

        // We need to add the panel to a CanvasLayer so it renders on top of everything
        _panelLayer = new CanvasLayer();
        _panelLayer.Name = "GoldGiftCanvasLayer";
        _panelLayer.Layer = 100; // High layer to render on top

        // Center the panel on screen
        var centerContainer = new CenterContainer();
        centerContainer.Name = "GoldGiftPanelCenter";
        centerContainer.AnchorsPreset = (int)Control.LayoutPreset.FullRect;
        centerContainer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        centerContainer.MouseFilter = Control.MouseFilterEnum.Ignore;

        _panelLayer.AddChild(centerContainer);
        centerContainer.AddChild(panel);

        // Add to the scene tree
        parent.AddChild(_panelLayer);
    }

    // ═══════════════════════════════════════════════
    //  INPUT HANDLING (Click + Drag)
    // ═══════════════════════════════════════════════

    /// <summary>
    /// All mouse input is handled here via the button's GuiInput signal.
    /// The button has MouseFilter = Stop, so it receives all input.
    /// The drag handle has MouseFilter = Ignore, so it doesn't interfere.
    /// 
    /// Logic:
    ///   Mouse down → record start position, begin potential drag
    ///   Mouse move → if moved > threshold, switch to drag mode
    ///   Mouse up   → if was dragging: stop drag; else: toggle panel (click)
    /// </summary>
    private static void OnButtonGuiInput(InputEvent @event)
    {
        if (_dragHandle == null || _button == null) return;

        if (@event is InputEventMouseButton mouseBtn)
        {
            if (mouseBtn.ButtonIndex == MouseButton.Left)
            {
                if (mouseBtn.Pressed)
                {
                    // Mouse pressed — start tracking for potential drag or click
                    _isPotentialDrag = true;
                    _isDragging = false;
                    _dragStartPos = mouseBtn.GlobalPosition;
                    ModEntry.Logger.Info("[GoldGift] Mouse pressed on button.");
                    // Accept to prevent the event from reaching other UI behind us
                    _button.AcceptEvent();
                }
                else
                {
                    // Mouse released
                    if (_isDragging)
                    {
                        // Was dragging — just stop
                        ModEntry.Logger.Info("[GoldGift] Drag ended.");
                        _isDragging = false;
                        _isPotentialDrag = false;
                    }
                    else if (_isPotentialDrag)
                    {
                        // Was a click (no significant movement) — toggle panel
                        _isPotentialDrag = false;
                        ModEntry.Logger.Info("[GoldGift] Click detected — toggling panel.");
                        GoldGiftPanel.Toggle();
                    }
                    _button.AcceptEvent();
                }
            }
        }
        else if (@event is InputEventMouseMotion mouseMotion && _isPotentialDrag)
        {
            float distance = mouseMotion.GlobalPosition.DistanceTo(_dragStartPos);
            if (!_isDragging && distance > DragThreshold)
            {
                // Movement exceeded threshold — this is a drag, not a click
                _isDragging = true;
                ModEntry.Logger.Info("[GoldGift] Drag started.");
            }

            if (_isDragging)
            {
                // Move the entire drag handle (which contains the button)
                _dragHandle.Position += mouseMotion.Relative;
                _button.AcceptEvent();
            }
        }
    }

    // ═══════════════════════════════════════════════
    //  NOTIFICATION
    // ═══════════════════════════════════════════════

    private static void OnGoldGiftReceived(string senderName, int amount)
    {
        if (_notificationLabel == null || !GodotObject.IsInstanceValid(_notificationLabel))
            return;

        _notificationLabel.Text = GoldGiftLocalization.Get("received", amount, senderName);
        _notificationLabel.Visible = true;
        _notificationLabel.Modulate = new Color(1, 1, 1, 1);

        // Auto-hide after 3 seconds with fade
        try
        {
            var tree = _notificationLabel.GetTree();
            if (tree != null)
            {
                var timer = tree.CreateTimer(3.0);
                timer.Timeout += () =>
                {
                    if (_notificationLabel != null && GodotObject.IsInstanceValid(_notificationLabel))
                    {
                        _notificationLabel.Visible = false;
                    }
                };
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Clean up all references.
    /// </summary>
    public static void Cleanup()
    {
        GoldGiftNetworkHandler.GoldGiftReceived -= OnGoldGiftReceived;
        _isDragging = false;
        _isPotentialDrag = false;
        _button = null;
        _panelLayer = null;
        _dragHandle = null;
        _notificationLabel = null;
        GoldGiftPanel.Cleanup();
    }
}
