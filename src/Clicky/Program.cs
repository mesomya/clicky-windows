//
//  Program.cs
//  Clicky for Windows
//
//  Tray-only companion app. No main window, no taskbar entry — just an
//  always-available icon in the system tray (the Windows equivalent of the
//  original's macOS menu bar status item). Clicking the icon opens a
//  floating panel with companion voice controls.
//

using System.Windows;
using Clicky.Ui;

namespace Clicky;

public static class Program
{
    // Held for the process lifetime so a second launch can detect us and exit.
    private static Mutex? singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Headless smoke tests: Clicky.exe --selftest <point|tts|brain|stt>
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            Environment.Exit(SelfTest.Run(args[1]));
            return;
        }

        // Renders the overlay's visuals to a PNG for inspection.
        if (args.Length >= 1 && args[0] == "--rendertest")
        {
            Environment.Exit(OverlayRenderTest.Run());
            return;
        }

        // macOS apps are single-instance by default; replicate that here so a
        // double-launch doesn't create two overlays fighting over one cursor.
        singleInstanceMutex = new Mutex(initiallyOwned: true, "ClickyForWindowsSingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        var application = new ClickyApplication();
        application.Run();

        GC.KeepAlive(singleInstanceMutex);
    }
}

/// <summary>
/// Manages the companion lifecycle: creates the tray icon + panel and starts
/// the companion voice pipeline on launch. Mirrors CompanionAppDelegate in
/// the original Swift app.
/// </summary>
public class ClickyApplication : Application
{
    private CompanionManager? companionManager;
    private TrayIconManager? trayIconManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The app lives entirely in the tray — closing the panel window must
        // not exit the app. Only the explicit Quit button shuts us down.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        ClickyAnalytics.Configure();
        ClickyAnalytics.TrackAppOpened();

        companionManager = new CompanionManager();
        trayIconManager = new TrayIconManager(companionManager);
        companionManager.Start();

        // Auto-open the panel if the user still needs to do something:
        // either they haven't onboarded yet, or the microphone is blocked.
        if (!companionManager.HasCompletedOnboarding || !companionManager.AllPermissionsGranted)
        {
            trayIconManager.ShowPanelOnLaunch();
        }

        StartupRegistration.RegisterAsStartupAppIfNeeded();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        companionManager?.Stop();
        trayIconManager?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>
/// Registers the app to launch automatically when the user signs in, the
/// Windows equivalent of the original's SMAppService login item. Uses the
/// per-user Run registry key so it shows up in Task Manager > Startup apps,
/// letting the user toggle it off if they want.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "Clicky";

    public static void RegisterAsStartupAppIfNeeded()
    {
        if (!ClickySettings.Current.LaunchAtStartup)
        {
            return;
        }

        try
        {
            string? executablePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executablePath))
            {
                return;
            }

            using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            var existingValue = runKey?.GetValue(StartupValueName) as string;
            if (existingValue != $"\"{executablePath}\"")
            {
                runKey?.SetValue(StartupValueName, $"\"{executablePath}\"");
                System.Diagnostics.Debug.WriteLine("🎯 Clicky: Registered as startup app");
            }
        }
        catch (Exception registrationError)
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ Clicky: Failed to register startup app: {registrationError.Message}");
        }
    }

    public static void Unregister()
    {
        try
        {
            using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            runKey?.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Best effort — nothing useful to do if the registry write fails.
        }
    }
}
