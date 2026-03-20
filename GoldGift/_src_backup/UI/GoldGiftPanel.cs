using Godot;

namespace GoldGift.UI;

/// <summary>
/// Manages the Gold Gift panel UI without inheriting from Godot node classes.
/// This avoids the Godot source generator creating StringName registrations
/// that crash when loaded as a mod DLL.
/// </summary>
public static class GoldGiftPanel
{
    private static PanelContainer? _panel;
    private static VBoxContainer? _mainContainer;
    private static Label? _titleLabel;
    private static Label? _goldLabel;
    private static VBoxContainer? _playerListContainer;
    private static HBoxContainer? _amountContainer;
    private static SpinBox? _amountInput;
    private static Label? _statusLabel;
    private static int _selectedSlotId = -1;
    private static Button? _sendButton;

    /// <summary>
    /// Whether the panel is currently visible.
    /// </summary>
    public static bool IsVisible => _panel?.Visible ?? false;

    /// <summary>
    /// Get the panel node (creates it if needed).
    /// </summary>
    public static PanelContainer GetOrCreatePanel()
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel))
            return _panel;

        _panel = new PanelContainer();
        BuildUI(_panel);
        return _panel;
    }

    private static void BuildUI(PanelContainer panel)
    {
        // Panel styling
        panel.Name = "GoldGiftPanel";
        panel.CustomMinimumSize = new Vector2(320, 400);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        panel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;

        // Add a dark semi-transparent background
        var styleBox = new StyleBoxFlat();
        styleBox.BgColor = new Color(0.08f, 0.06f, 0.12f, 0.92f);
        styleBox.BorderColor = new Color(0.85f, 0.65f, 0.2f, 0.9f);
        styleBox.SetBorderWidthAll(2);
        styleBox.SetCornerRadiusAll(8);
        styleBox.SetContentMarginAll(16);
        panel.AddThemeStyleboxOverride("panel", styleBox);

        // Main vertical layout
        _mainContainer = new VBoxContainer();
        _mainContainer.AddThemeConstantOverride("separation", 10);
        panel.AddChild(_mainContainer);

        // --- Title bar ---
        var titleBar = new HBoxContainer();
        _mainContainer.AddChild(titleBar);

        _titleLabel = new Label();
        _titleLabel.Text = GoldGiftConfig.DebugMode ? "🎁 Gift Gold [DEBUG]" : "🎁 Gift Gold";
        _titleLabel.AddThemeFontSizeOverride("font_size", 20);
        _titleLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.8f, 0.2f));
        _titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        titleBar.AddChild(_titleLabel);

        var closeBtn = new Button();
        closeBtn.Text = "✕";
        closeBtn.CustomMinimumSize = new Vector2(30, 30);
        closeBtn.Pressed += OnClosePressed;
        titleBar.AddChild(closeBtn);

        // --- Separator ---
        var sep = new HSeparator();
        _mainContainer.AddChild(sep);

        // --- Your gold display ---
        _goldLabel = new Label();
        _goldLabel.Text = "Your Gold: ---";
        _goldLabel.AddThemeFontSizeOverride("font_size", 16);
        _goldLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.85f, 0.3f));
        _mainContainer.AddChild(_goldLabel);

        // --- Player selection label ---
        var selectLabel = new Label();
        selectLabel.Text = "Select a player:";
        selectLabel.AddThemeFontSizeOverride("font_size", 14);
        _mainContainer.AddChild(selectLabel);

        // --- Player list ---
        var scrollContainer = new ScrollContainer();
        scrollContainer.CustomMinimumSize = new Vector2(0, 120);
        scrollContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _mainContainer.AddChild(scrollContainer);

        _playerListContainer = new VBoxContainer();
        _playerListContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _playerListContainer.AddThemeConstantOverride("separation", 4);
        scrollContainer.AddChild(_playerListContainer);

        // --- Amount section ---
        var amountLabel = new Label();
        amountLabel.Text = "Amount:";
        amountLabel.AddThemeFontSizeOverride("font_size", 14);
        _mainContainer.AddChild(amountLabel);

        // Quick amount buttons
        var quickContainer = new HBoxContainer();
        quickContainer.AddThemeConstantOverride("separation", 6);
        _mainContainer.AddChild(quickContainer);

        foreach (int amount in GoldGiftConfig.QuickAmounts)
        {
            var qBtn = new Button();
            qBtn.Text = $"{amount}g";
            qBtn.CustomMinimumSize = new Vector2(55, 32);
            qBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

            int capturedAmount = amount;
            qBtn.Pressed += () => OnQuickAmountPressed(capturedAmount);

            quickContainer.AddChild(qBtn);
        }

        // Custom amount input
        _amountContainer = new HBoxContainer();
        _amountContainer.AddThemeConstantOverride("separation", 8);
        _mainContainer.AddChild(_amountContainer);

        var customLabel = new Label();
        customLabel.Text = "Custom:";
        _amountContainer.AddChild(customLabel);

        _amountInput = new SpinBox();
        _amountInput.MinValue = GoldGiftConfig.MinGiftAmount;
        _amountInput.MaxValue = GoldGiftConfig.MaxGiftAmount;
        _amountInput.Value = GoldGiftConfig.DefaultGiftAmount;
        _amountInput.Step = 1;
        _amountInput.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _amountContainer.AddChild(_amountInput);

        // --- Send button ---
        _sendButton = new Button();
        _sendButton.Text = "💰 Send Gold";
        _sendButton.CustomMinimumSize = new Vector2(0, 40);
        _sendButton.Disabled = true;

        var sendStyle = new StyleBoxFlat();
        sendStyle.BgColor = new Color(0.2f, 0.6f, 0.2f, 0.8f);
        sendStyle.SetCornerRadiusAll(6);
        sendStyle.SetContentMarginAll(8);
        _sendButton.AddThemeStyleboxOverride("normal", sendStyle);

        var sendHoverStyle = new StyleBoxFlat();
        sendHoverStyle.BgColor = new Color(0.3f, 0.7f, 0.3f, 0.9f);
        sendHoverStyle.SetCornerRadiusAll(6);
        sendHoverStyle.SetContentMarginAll(8);
        _sendButton.AddThemeStyleboxOverride("hover", sendHoverStyle);

        var sendDisabledStyle = new StyleBoxFlat();
        sendDisabledStyle.BgColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
        sendDisabledStyle.SetCornerRadiusAll(6);
        sendDisabledStyle.SetContentMarginAll(8);
        _sendButton.AddThemeStyleboxOverride("disabled", sendDisabledStyle);

        _sendButton.Pressed += OnSendPressed;
        _mainContainer.AddChild(_sendButton);

        // --- Status label ---
        _statusLabel = new Label();
        _statusLabel.Text = "";
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _mainContainer.AddChild(_statusLabel);

        // Start hidden
        panel.Visible = false;
    }

    /// <summary>
    /// Toggle panel visibility and refresh content.
    /// </summary>
    public static void Toggle()
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel)) return;

        _panel.Visible = !_panel.Visible;
        if (_panel.Visible)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Refresh the panel content (player list, gold display).
    /// </summary>
    public static void Refresh()
    {
        RefreshGoldDisplay();
        RefreshPlayerList();
        _selectedSlotId = -1;
        UpdateSendButton();
        SetStatus("");
    }

    private static void RefreshGoldDisplay()
    {
        int gold = GoldGiftNetworkHandler.GetLocalGold();
        if (_goldLabel != null)
        {
            _goldLabel.Text = $"Your Gold: {gold}";
        }
    }

    private static void RefreshPlayerList()
    {
        if (_playerListContainer == null) return;

        // Clear existing entries
        foreach (var child in _playerListContainer.GetChildren())
        {
            child.QueueFree();
        }

        var players = GoldGiftNetworkHandler.GetOtherPlayers();

        if (players.Count == 0)
        {
            var noPlayersLabel = new Label();
            noPlayersLabel.Text = "No other players found.";
            noPlayersLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
            _playerListContainer.AddChild(noPlayersLabel);
            return;
        }

        foreach (var player in players)
        {
            var playerBtn = new Button();
            // In debug mode, show gold amount for each player
            string displayText = GoldGiftConfig.DebugMode
                ? $"  {player.Name} ({player.CharacterClass}) — {player.Gold}g"
                : $"  {player.Name} ({player.CharacterClass})";
            playerBtn.Text = displayText;
            playerBtn.Alignment = HorizontalAlignment.Left;
            playerBtn.CustomMinimumSize = new Vector2(0, 36);
            playerBtn.ToggleMode = true;

            var normalStyle = new StyleBoxFlat();
            normalStyle.BgColor = new Color(0.15f, 0.12f, 0.2f, 0.6f);
            normalStyle.SetCornerRadiusAll(4);
            normalStyle.SetContentMarginAll(6);
            playerBtn.AddThemeStyleboxOverride("normal", normalStyle);

            var pressedStyle = new StyleBoxFlat();
            pressedStyle.BgColor = new Color(0.3f, 0.2f, 0.5f, 0.8f);
            pressedStyle.BorderColor = new Color(0.85f, 0.65f, 0.2f, 0.9f);
            pressedStyle.SetBorderWidthAll(1);
            pressedStyle.SetCornerRadiusAll(4);
            pressedStyle.SetContentMarginAll(6);
            playerBtn.AddThemeStyleboxOverride("pressed", pressedStyle);

            int slotId = player.SlotId;
            playerBtn.Pressed += () => OnPlayerSelected(slotId, playerBtn);

            _playerListContainer.AddChild(playerBtn);
        }
    }

    private static void OnPlayerSelected(int slotId, Button selectedBtn)
    {
        _selectedSlotId = slotId;

        // Deselect all other buttons
        if (_playerListContainer != null)
        {
            foreach (var child in _playerListContainer.GetChildren())
            {
                if (child is Button btn && btn != selectedBtn)
                {
                    btn.ButtonPressed = false;
                }
            }
        }

        selectedBtn.ButtonPressed = true;
        UpdateSendButton();
    }

    private static void OnQuickAmountPressed(int amount)
    {
        if (_amountInput != null)
        {
            _amountInput.Value = amount;
        }
    }

    private static void UpdateSendButton()
    {
        if (_sendButton != null)
        {
            _sendButton.Disabled = _selectedSlotId < 0;
        }
    }

    private static void OnSendPressed()
    {
        if (_selectedSlotId < 0 || _amountInput == null || _panel == null) return;

        int amount = (int)_amountInput.Value;

        if (amount <= 0)
        {
            SetStatus("⚠ Please enter a valid amount.", new Color(1, 0.4f, 0.4f));
            return;
        }

        var localGold = GoldGiftNetworkHandler.GetLocalGold();
        if (localGold <= 0)
        {
            SetStatus("⚠ Cannot find your character.", new Color(1, 0.4f, 0.4f));
            return;
        }

        if (localGold < amount)
        {
            SetStatus($"⚠ Not enough gold! You have {localGold}g.",
                new Color(1, 0.4f, 0.4f));
            return;
        }

        bool success = GoldGiftNetworkHandler.SendGoldGift(_selectedSlotId, amount);

        if (success)
        {
            SetStatus($"✓ Sent {amount}g successfully!", new Color(0.4f, 1, 0.4f));
            RefreshGoldDisplay();
            RefreshPlayerList();

            // Auto-close after a short delay
            var tree = _panel.GetTree();
            if (tree != null)
            {
                var timer = tree.CreateTimer(1.5);
                timer.Timeout += () =>
                {
                    if (_panel != null && GodotObject.IsInstanceValid(_panel))
                        _panel.Visible = false;
                };
            }
        }
        else
        {
            SetStatus("⚠ Failed to send gold.", new Color(1, 0.4f, 0.4f));
        }
    }

    private static void OnClosePressed()
    {
        if (_panel != null)
            _panel.Visible = false;
    }

    private static void SetStatus(string text, Color? color = null)
    {
        if (_statusLabel == null) return;

        _statusLabel.Text = text;
        if (color.HasValue)
        {
            _statusLabel.AddThemeColorOverride("font_color", color.Value);
        }
    }

    /// <summary>
    /// Clean up references when the panel is freed.
    /// </summary>
    public static void Cleanup()
    {
        _panel = null;
        _mainContainer = null;
        _titleLabel = null;
        _goldLabel = null;
        _playerListContainer = null;
        _amountContainer = null;
        _amountInput = null;
        _statusLabel = null;
        _sendButton = null;
        _selectedSlotId = -1;
    }
}
