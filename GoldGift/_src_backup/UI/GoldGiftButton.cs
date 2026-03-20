using Godot;

namespace GoldGift.UI;

/// <summary>
/// Factory for creating the Gift Gold button and panel.
/// Uses composition instead of inheritance to avoid Godot source generator crashes.
/// </summary>
public static class GoldGiftButton
{
    private static Button? _button;
    private static CanvasLayer? _panelLayer;

    /// <summary>
    /// Create and return a configured Gift Gold button.
    /// Also sets up the gift panel in a CanvasLayer.
    /// </summary>
    public static Button Create(Node parentForPanel)
    {
        _button = new Button();
        BuildButton(_button);

        // Create the gift panel overlay
        CreateGiftPanel(parentForPanel);

        return _button;
    }

    /// <summary>
    /// Get the existing button if created.
    /// </summary>
    public static Button? GetButton() => _button;

    private static void BuildButton(Button btn)
    {
        btn.Name = "GoldGiftButton";
        btn.Text = GoldGiftConfig.DebugMode ? "🎁 Gift Gold [DEBUG]" : "🎁 Gift Gold";
        btn.CustomMinimumSize = new Vector2(120, 36);
        btn.TooltipText = "Send gold to another player";

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

        btn.Pressed += OnGiftButtonPressed;
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

    private static void OnGiftButtonPressed()
    {
        GoldGiftPanel.Toggle();
    }

    /// <summary>
    /// Clean up all references.
    /// </summary>
    public static void Cleanup()
    {
        _button = null;
        _panelLayer = null;
        GoldGiftPanel.Cleanup();
    }
}
