//
//  TrayIconManager.cs
//  Clicky for Windows
//
//  Manages the system tray icon (the Windows analog of the original's
//  NSStatusItem) and the floating control panel that opens near it.
//  The tray icon is the same rotated clicky triangle as the in-app cursor,
//  drawn at runtime so it always matches.
//

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace Clicky.Ui;

public sealed class TrayIconManager : IDisposable
{
    private readonly WinForms.NotifyIcon notifyIcon;
    private readonly CompanionManager companionManager;
    private CompanionPanelWindow? panelWindow;

    public TrayIconManager(CompanionManager companionManager)
    {
        this.companionManager = companionManager;

        notifyIcon = new WinForms.NotifyIcon
        {
            Icon = DrawClickyTrayIcon(),
            Text = $"Clicky — hold {Hotkey.PushToTalkShortcut.DisplayText} to talk",
            Visible = true,
        };

        notifyIcon.MouseClick += (_, mouseEvent) =>
        {
            if (mouseEvent.Button == WinForms.MouseButtons.Left || mouseEvent.Button == WinForms.MouseButtons.Right)
            {
                TogglePanel();
            }
        };

        CompanionManager.DismissPanelRequested += HidePanel;
    }

    /// Opens the panel automatically on app launch so the user sees the
    /// setup state right away. Small delay so the tray icon settles first.
    public void ShowPanelOnLaunch()
    {
        var launchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        launchTimer.Tick += (_, _) =>
        {
            launchTimer.Stop();
            ShowPanel();
        };
        launchTimer.Start();
    }

    private void TogglePanel()
    {
        if (panelWindow is { IsVisible: true })
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    private void ShowPanel()
    {
        panelWindow ??= new CompanionPanelWindow(companionManager);
        panelWindow.ShowNearTray();
    }

    private void HidePanel()
    {
        panelWindow?.Hide();
    }

    /// Draws the clicky triangle as a tray icon — same shape and 35°
    /// rotation as the original's menu bar icon, white so it reads on the
    /// (typically dark) Windows taskbar.
    private static Icon DrawClickyTrayIcon()
    {
        const int iconSize = 32;
        using var bitmap = new Bitmap(iconSize, iconSize);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            float triangleSize = iconSize * 0.7f;
            float centerX = iconSize * 0.5f;
            float centerY = iconSize * 0.5f;
            float height = triangleSize * (float)Math.Sqrt(3.0) / 2.0f;

            // Note: GDI's Y axis points down, so "top" subtracts from Y.
            var top = new PointF(centerX, centerY - height / 1.5f);
            var bottomLeft = new PointF(centerX - triangleSize / 2, centerY + height / 3);
            var bottomRight = new PointF(centerX + triangleSize / 2, centerY + height / 3);

            const double angleRadians = 35.0 * Math.PI / 180.0;
            PointF Rotate(PointF point)
            {
                float dx = point.X - centerX, dy = point.Y - centerY;
                float cosA = (float)Math.Cos(angleRadians), sinA = (float)Math.Sin(angleRadians);
                return new PointF(centerX + cosA * dx - sinA * dy, centerY + sinA * dx + cosA * dy);
            }

            using var brush = new SolidBrush(Color.White);
            graphics.FillPolygon(brush, new[] { Rotate(top), Rotate(bottomLeft), Rotate(bottomRight) });
        }

        IntPtr iconHandle = bitmap.GetHicon();
        return Icon.FromHandle(iconHandle);
    }

    public void Dispose()
    {
        CompanionManager.DismissPanelRequested -= HidePanel;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        panelWindow?.Close();
    }
}
