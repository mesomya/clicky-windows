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

    /// Named event a second launch signals so the running instance opens its
    /// panel — without this, double-clicking the exe while Clicky is already
    /// in the tray would appear to do nothing.
    public const string ShowPanelSignalName = "ClickyForWindowsShowPanelSignal";

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
        // The second launch pokes the running instance to show its panel, so
        // the user always gets visible feedback from a double-click.
        singleInstanceMutex = new Mutex(initiallyOwned: true, "ClickyForWindowsSingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                using var showPanelSignal = EventWaitHandle.OpenExisting(ShowPanelSignalName);
                showPanelSignal.Set();
            }
            catch
            {
                // Running instance is mid-startup or mid-exit — nothing to signal.
            }
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
    private EventWaitHandle? showPanelSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Crash armor first: a tray app has no window, so an unhandled
        // exception would otherwise kill it silently and look like
        // "I double-clicked and nothing happened". Log + tell the user.
        InstallCrashReporting();

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
        StartShowPanelSignalListener();

        DebugTrace.Log($"app started — onboarded: {companionManager.HasCompletedOnboarding}, mic: {companionManager.HasMicrophonePermission}, claudeCli: {companionManager.IsClaudeCliAvailable}");
        StartDebugCommandPoller();
    }

    /// Verification-only command channel, active alongside DebugTrace: an
    /// automated test drops `debug-start-onboarding.flag` next to the exe
    /// to trigger the onboarding flow without clicking the panel.
    private void StartDebugCommandPoller()
    {
        if (!DebugTrace.IsEnabled)
        {
            return;
        }
        var pollTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        pollTimer.Tick += (_, _) =>
        {
            string onboardingPath = System.IO.Path.Combine(AppContext.BaseDirectory, "debug-start-onboarding.flag");
            if (System.IO.File.Exists(onboardingPath))
            {
                try { System.IO.File.Delete(onboardingPath); } catch { }
                DebugTrace.Log("debug command: trigger onboarding");
                companionManager?.TriggerOnboarding();
            }

            // debug-say.txt: run the real screenshot -> Claude -> TTS pipeline
            // on the file's text, exactly as if it were a finalized transcript.
            string sayPath = System.IO.Path.Combine(AppContext.BaseDirectory, "debug-say.txt");
            if (System.IO.File.Exists(sayPath))
            {
                string transcript = "";
                try { transcript = System.IO.File.ReadAllText(sayPath).Trim(); System.IO.File.Delete(sayPath); } catch { }
                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    companionManager?.DebugInjectTranscript(transcript);
                }
            }
        };
        pollTimer.Start();
    }

    /// Waits (on a background thread) for a second launch to signal, then
    /// opens the panel — so double-clicking the exe always shows something.
    private void StartShowPanelSignalListener()
    {
        showPanelSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowPanelSignalName);
        var listenerThread = new Thread(() =>
        {
            while (true)
            {
                showPanelSignal.WaitOne();
                Dispatcher.BeginInvoke(() => trayIconManager?.ShowPanelFromExternalSignal());
            }
        })
        {
            IsBackground = true,
            Name = "ClickyShowPanelSignalListener",
        };
        listenerThread.Start();
    }

    private void InstallCrashReporting()
    {
        DispatcherUnhandledException += (_, exceptionArgs) =>
        {
            ReportFatalCrash(exceptionArgs.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, exceptionArgs) =>
        {
            if (exceptionArgs.ExceptionObject is Exception exception)
            {
                WriteCrashLog(exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, exceptionArgs) =>
        {
            // Background task faulted without anyone awaiting it — log it
            // but don't kill the app over it.
            WriteCrashLog(exceptionArgs.Exception);
            exceptionArgs.SetObserved();
        };
    }

    private void ReportFatalCrash(Exception exception)
    {
        string logPath = WriteCrashLog(exception);
        try
        {
            System.Windows.MessageBox.Show(
                $"Clicky hit an unexpected error and has to close.\n\n{exception.Message}\n\nDetails were saved to:\n{logPath}",
                "Clicky crashed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Even the message box failed — the log file still has the story.
        }
        Shutdown(1);
    }

    private static string WriteCrashLog(Exception exception)
    {
        string logPath = System.IO.Path.Combine(ClickySettings.AppDataDirectory, "crash.log");
        try
        {
            System.IO.Directory.CreateDirectory(ClickySettings.AppDataDirectory);
            System.IO.File.AppendAllText(
                logPath,
                $"\n==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n{exception}\n");
        }
        catch
        {
            // Disk write failed — nothing more we can do.
        }
        return logPath;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        companionManager?.Stop();
        trayIconManager?.Dispose();
        showPanelSignal?.Dispose();
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
