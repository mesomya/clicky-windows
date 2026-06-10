//
//  CompanionPanelWindow.cs
//  Clicky for Windows
//
//  The floating control panel that opens from the tray icon — a port of
//  CompanionPanelView. Dark, rounded, minimal. Shows companion status, the
//  push-to-talk shortcut, model picker, speech-to-text picker, setup rows
//  (microphone + Claude CLI), and the start/quit/replay actions.
//

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Clicky.Audio;
using Clicky.Native;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace Clicky.Ui;

public class CompanionPanelWindow : Window
{
    private readonly CompanionManager companionManager;

    private readonly Ellipse statusDot = new() { Width = 8, Height = 8 };
    private readonly TextBlock statusText = MakeText("", 12, FontWeights.Medium, DS.Brushes.TextTertiary);
    private readonly StackPanel contentStack = new();

    public CompanionPanelWindow(CompanionManager companionManager)
    {
        this.companionManager = companionManager;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 336; // 320 content + 16 shadow margin
        WindowStartupLocation = WindowStartupLocation.Manual;

        var panelBorder = new Border
        {
            Background = DS.Brushes.Background,
            CornerRadius = new CornerRadius(DS.CornerRadius.ExtraLarge),
            BorderBrush = DS.Brushes.BorderSubtle,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 8, 8, 8),
            Child = contentStack,
            Effect = new DropShadowEffect
            {
                Color = System.Windows.Media.Color.FromRgb(0, 0, 0),
                Opacity = 0.5,
                BlurRadius = 20,
                ShadowDepth = 6,
                Direction = 270,
            },
        };
        Content = panelBorder;

        RebuildContent();
        companionManager.StateChanged += HandleManagerStateChanged;
        WhisperTranscriptionProvider.ModelDownloadStatusChanged += HandleWhisperStatusChanged;

        // Click-outside dismissal: hiding on deactivate gives the same
        // transient feel as the original's global click monitor.
        Deactivated += (_, _) => Hide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Keep the panel out of the AI's screenshots, like every Clicky window.
        var windowHandle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(windowHandle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    private void HandleManagerStateChanged()
    {
        if (IsVisible)
        {
            RebuildContent();
        }
    }

    private void HandleWhisperStatusChanged(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible)
            {
                RebuildContent();
            }
        });
    }

    /// Positions the panel just above the tray clock (bottom-right of the
    /// primary display's working area) — the Windows analog of dropping
    /// down below the menu bar status item.
    public void ShowNearTray()
    {
        companionManager.RefreshAllPermissions();
        RebuildContent();
        Show();
        Activate();

        var workingArea = WinFormsScreen.PrimaryScreen!.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);

        UpdateLayout();
        double panelWidthPx = ActualWidth * dpi.DpiScaleX;
        double panelHeightPx = ActualHeight * dpi.DpiScaleY;

        double leftPx = workingArea.Right - panelWidthPx - 8 * dpi.DpiScaleX;
        double topPx = workingArea.Bottom - panelHeightPx - 8 * dpi.DpiScaleY;

        Left = leftPx / dpi.DpiScaleX;
        Top = topPx / dpi.DpiScaleY;
    }

    // ── Content construction ─────────────────────────────────────────

    private void RebuildContent()
    {
        contentStack.Children.Clear();

        contentStack.Children.Add(BuildHeader());
        contentStack.Children.Add(BuildDivider());

        contentStack.Children.Add(WithMargin(BuildCopySection(), 16, 16, 16, 0));

        if (companionManager.HasCompletedOnboarding && companionManager.AllPermissionsGranted)
        {
            contentStack.Children.Add(WithMargin(BuildModelPickerRow(), 16, 12, 16, 0));
            contentStack.Children.Add(WithMargin(BuildSpeechToTextRow(), 16, 8, 16, 0));
        }

        if (!companionManager.AllPermissionsGranted || !companionManager.IsClaudeCliAvailable)
        {
            contentStack.Children.Add(WithMargin(BuildSetupSection(), 16, 16, 16, 0));
        }

        if (!companionManager.HasCompletedOnboarding && companionManager.AllPermissionsGranted && companionManager.IsClaudeCliAvailable)
        {
            contentStack.Children.Add(WithMargin(BuildStartButton(), 16, 16, 16, 0));
        }

        if (companionManager.HasCompletedOnboarding && companionManager.AllPermissionsGranted)
        {
            contentStack.Children.Add(WithMargin(BuildCreditButton(), 16, 16, 16, 0));
        }

        var footerSpacer = new Border { Height = 12 };
        contentStack.Children.Add(footerSpacer);
        contentStack.Children.Add(BuildDivider());
        contentStack.Children.Add(WithMargin(BuildFooter(), 16, 12, 16, 12));
    }

    private UIElement BuildHeader()
    {
        statusDot.Fill = StatusDotBrush();
        statusDot.Effect = new DropShadowEffect
        {
            Color = ((SolidColorBrush)StatusDotBrush()).Color,
            Opacity = 0.6,
            BlurRadius = 4,
            ShadowDepth = 0,
        };
        statusText.Text = StatusTextValue();

        var titleRow = new DockPanel { Margin = new Thickness(16, 14, 16, 14) };

        var leftGroup = new StackPanel { Orientation = Orientation.Horizontal };
        statusDot.VerticalAlignment = VerticalAlignment.Center;
        leftGroup.Children.Add(statusDot);
        var title = MakeText("Clicky", 14, FontWeights.SemiBold, DS.Brushes.TextPrimary);
        title.Margin = new Thickness(8, 0, 0, 0);
        leftGroup.Children.Add(title);
        DockPanel.SetDock(leftGroup, Dock.Left);
        titleRow.Children.Add(leftGroup);

        var closeButton = MakeTextButton("✕", () => Hide());
        closeButton.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(closeButton, Dock.Right);

        statusText.VerticalAlignment = VerticalAlignment.Center;
        statusText.HorizontalAlignment = HorizontalAlignment.Right;
        statusText.Margin = new Thickness(0, 0, 10, 0);

        titleRow.Children.Add(closeButton);
        titleRow.Children.Add(statusText);

        return titleRow;
    }

    private UIElement BuildCopySection()
    {
        var copyStack = new StackPanel();

        if (companionManager.HasCompletedOnboarding && companionManager.AllPermissionsGranted)
        {
            copyStack.Children.Add(MakeText(
                $"Hold {Hotkey.PushToTalkShortcut.DisplayText} to talk.",
                12, FontWeights.Medium, DS.Brushes.TextSecondary));
        }
        else if (companionManager.AllPermissionsGranted && companionManager.IsClaudeCliAvailable)
        {
            copyStack.Children.Add(MakeText(
                "You're all set. Hit Start to meet Clicky.",
                12, FontWeights.Medium, DS.Brushes.TextSecondary));
        }
        else
        {
            copyStack.Children.Add(MakeText(
                "Hi, this is Clicky for Windows.",
                12, FontWeights.Bold, DS.Brushes.TextSecondary));
            var description = MakeText(
                "An AI buddy that lives next to your cursor — a port of Farza's Clicky for Mac. It uses your Claude Code login as its brain.",
                11, FontWeights.Normal, DS.Brushes.TextTertiary);
            description.TextWrapping = TextWrapping.Wrap;
            description.Margin = new Thickness(0, 6, 0, 0);
            copyStack.Children.Add(description);

            var privacyNote = MakeText(
                "Nothing runs in the background. Clicky only takes a screenshot when you press the hotkey.",
                11, FontWeights.Normal, new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 102, 102)));
            privacyNote.TextWrapping = TextWrapping.Wrap;
            privacyNote.Margin = new Thickness(0, 6, 0, 0);
            copyStack.Children.Add(privacyNote);
        }

        return copyStack;
    }

    private UIElement BuildSetupSection()
    {
        var setupStack = new StackPanel();
        var sectionLabel = MakeText("SETUP", 10, FontWeights.SemiBold, DS.Brushes.TextTertiary);
        sectionLabel.Margin = new Thickness(0, 0, 0, 6);
        setupStack.Children.Add(sectionLabel);

        setupStack.Children.Add(BuildSetupRow(
            label: "Microphone",
            isGranted: companionManager.HasMicrophonePermission,
            grantAction: BuddyDictationManager.OpenMicrophonePrivacySettings));

        setupStack.Children.Add(BuildSetupRow(
            label: "Claude Code",
            isGranted: companionManager.IsClaudeCliAvailable,
            grantAction: () => OpenUrl("https://claude.com/claude-code"),
            grantLabel: "Install"));

        return setupStack;
    }

    private UIElement BuildSetupRow(string label, bool isGranted, Action grantAction, string grantLabel = "Grant")
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };

        var labelText = MakeText(label, 13, FontWeights.Medium, DS.Brushes.TextSecondary);
        DockPanel.SetDock(labelText, Dock.Left);
        row.Children.Add(labelText);

        if (isGranted)
        {
            var grantedGroup = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            grantedGroup.Children.Add(new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = DS.Brushes.Success,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            grantedGroup.Children.Add(MakeText("Ready", 11, FontWeights.Medium, DS.Brushes.Success));
            row.Children.Add(grantedGroup);
        }
        else
        {
            var grantButton = MakePillButton(grantLabel, grantAction);
            grantButton.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(grantButton);
        }

        return row;
    }

    private UIElement BuildModelPickerRow()
    {
        var row = new DockPanel();
        var label = MakeText("Model", 13, FontWeights.Medium, DS.Brushes.TextSecondary);
        label.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);

        var picker = BuildSegmentedPicker(
            options: new[] { ("Sonnet", "sonnet"), ("Opus", "opus") },
            selectedValue: companionManager.SelectedModel,
            onSelect: companionManager.SetSelectedModel);
        picker.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(picker);
        return row;
    }

    private UIElement BuildSpeechToTextRow()
    {
        var container = new StackPanel();

        var row = new DockPanel();
        var label = MakeText("Speech to Text", 13, FontWeights.Medium, DS.Brushes.TextSecondary);
        label.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);

        var picker = BuildSegmentedPicker(
            options: new[] { ("Whisper", "whisper"), ("Windows", "windows") },
            selectedValue: ClickySettings.Current.VoiceTranscriptionProvider,
            onSelect: providerValue =>
            {
                ClickySettings.Current.VoiceTranscriptionProvider = providerValue;
                ClickySettings.Current.Save();
                companionManager.BuddyDictationManager.ReloadTranscriptionProviderFromSettings();
                RebuildContent();
            });
        picker.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(picker);
        container.Children.Add(row);

        string downloadStatus = WhisperTranscriptionProvider.ModelDownloadStatus;
        if (!string.IsNullOrEmpty(downloadStatus))
        {
            var statusLine = MakeText(downloadStatus, 10, FontWeights.Normal, DS.Brushes.TextTertiary);
            statusLine.Margin = new Thickness(0, 4, 0, 0);
            container.Children.Add(statusLine);
        }

        return container;
    }

    private FrameworkElement BuildSegmentedPicker(
        (string Label, string Value)[] options,
        string selectedValue,
        Action<string> onSelect)
    {
        var pickerBorder = new Border
        {
            Background = DS.Brushes.WhiteWithOpacity(0.06),
            BorderBrush = DS.Brushes.BorderSubtle,
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(6),
        };
        var optionsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        pickerBorder.Child = optionsPanel;

        foreach (var (optionLabel, optionValue) in options)
        {
            bool isSelected = selectedValue == optionValue;
            var optionText = MakeText(
                optionLabel, 11, FontWeights.Medium,
                isSelected ? DS.Brushes.TextPrimary : DS.Brushes.TextTertiary);
            var optionBorder = new Border
            {
                Child = optionText,
                Padding = new Thickness(10, 5, 10, 5),
                CornerRadius = new CornerRadius(5),
                Background = isSelected ? DS.Brushes.WhiteWithOpacity(0.1) : System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            string capturedValue = optionValue;
            optionBorder.MouseLeftButtonUp += (_, _) => onSelect(capturedValue);
            optionsPanel.Children.Add(optionBorder);
        }

        return pickerBorder;
    }

    private UIElement BuildStartButton()
    {
        var startText = MakeText("Start", 14, FontWeights.SemiBold, DS.Brushes.TextOnAccent);
        startText.HorizontalAlignment = HorizontalAlignment.Center;
        var startButton = new Border
        {
            Child = startText,
            Background = DS.Brushes.Accent,
            CornerRadius = new CornerRadius(DS.CornerRadius.Large),
            Padding = new Thickness(0, 10, 0, 10),
            Cursor = Cursors.Hand,
        };
        startButton.MouseLeftButtonUp += (_, _) => companionManager.TriggerOnboarding();
        return startButton;
    }

    private UIElement BuildCreditButton()
    {
        var creditStack = new StackPanel { Orientation = Orientation.Vertical };
        creditStack.Children.Add(MakeText("Made by Farza — this is the Windows port", 12, FontWeights.SemiBold, DS.Brushes.TextSecondary));
        var creditSubtitle = MakeText("Tap to see the original open-source Clicky for Mac.", 10, FontWeights.Normal, DS.Brushes.TextTertiary);
        creditSubtitle.Margin = new Thickness(0, 2, 0, 0);
        creditStack.Children.Add(creditSubtitle);

        var creditButton = new Border
        {
            Child = creditStack,
            Background = DS.Brushes.WhiteWithOpacity(0.06),
            BorderBrush = DS.Brushes.BorderSubtle,
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(DS.CornerRadius.Medium),
            Padding = new Thickness(12, 10, 12, 10),
            Cursor = Cursors.Hand,
        };
        creditButton.MouseLeftButtonUp += (_, _) => OpenUrl("https://github.com/farzaa/clicky");
        return creditButton;
    }

    private UIElement BuildFooter()
    {
        var footer = new DockPanel();

        var quitButton = MakeTextButton("⏻  Quit Clicky", () => System.Windows.Application.Current.Shutdown());
        DockPanel.SetDock(quitButton, Dock.Left);
        footer.Children.Add(quitButton);

        if (companionManager.HasCompletedOnboarding)
        {
            var replayButton = MakeTextButton("Replay Onboarding", () => companionManager.ReplayOnboarding());
            replayButton.HorizontalAlignment = HorizontalAlignment.Right;
            footer.Children.Add(replayButton);
        }

        return footer;
    }

    private UIElement BuildDivider()
    {
        return new Border
        {
            Height = 1,
            Background = DS.Brushes.BorderSubtle,
            Margin = new Thickness(16, 0, 16, 0),
            Opacity = 0.6,
        };
    }

    // ── Status helpers ───────────────────────────────────────────────

    private System.Windows.Media.Brush StatusDotBrush()
    {
        if (!companionManager.IsOverlayVisible)
        {
            return DS.Brushes.TextTertiary;
        }
        return companionManager.VoiceState switch
        {
            CompanionVoiceState.Idle => DS.Brushes.Success,
            _ => new SolidColorBrush(DS.Colors.Blue400),
        };
    }

    private string StatusTextValue()
    {
        if (!companionManager.HasCompletedOnboarding || !companionManager.AllPermissionsGranted)
        {
            return "Setup";
        }
        if (!companionManager.IsOverlayVisible)
        {
            return "Ready";
        }
        return companionManager.VoiceState switch
        {
            CompanionVoiceState.Idle => "Active",
            CompanionVoiceState.Listening => "Listening",
            CompanionVoiceState.Processing => "Processing",
            CompanionVoiceState.Responding => "Responding",
            _ => "",
        };
    }

    // ── Small widget factories ───────────────────────────────────────

    private static TextBlock MakeText(string text, double size, FontWeight weight, System.Windows.Media.Brush brush)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush,
            FontFamily = new FontFamily("Segoe UI"),
        };
    }

    private static FrameworkElement MakeTextButton(string label, Action onClick)
    {
        var text = MakeText(label, 12, FontWeights.Medium, DS.Brushes.TextTertiary);
        var button = new Border
        {
            Child = text,
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(4),
            Cursor = Cursors.Hand,
        };
        button.MouseEnter += (_, _) => text.Foreground = DS.Brushes.TextPrimary;
        button.MouseLeave += (_, _) => text.Foreground = DS.Brushes.TextTertiary;
        button.MouseLeftButtonUp += (_, _) => onClick();
        return button;
    }

    private static FrameworkElement MakePillButton(string label, Action onClick)
    {
        var text = MakeText(label, 11, FontWeights.SemiBold, DS.Brushes.TextOnAccent);
        var button = new Border
        {
            Child = text,
            Background = DS.Brushes.Accent,
            CornerRadius = new CornerRadius(99),
            Padding = new Thickness(10, 4, 10, 4),
            Cursor = Cursors.Hand,
        };
        button.MouseLeftButtonUp += (_, _) => onClick();
        return button;
    }

    private static FrameworkElement WithMargin(UIElement element, double left, double top, double right, double bottom)
    {
        var container = new Border { Child = element, Margin = new Thickness(left, top, right, bottom) };
        return container;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Browser launch failed — nothing useful to do.
        }
    }
}
