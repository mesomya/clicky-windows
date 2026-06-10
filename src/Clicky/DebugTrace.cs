//
//  DebugTrace.cs
//  Clicky for Windows
//
//  Opt-in diagnostic trace, active only while a `debug-capture.flag` file
//  sits next to the exe (the same flag that disables capture exclusion).
//  Used by automated verification to observe the app's behavior — state
//  transitions, pipeline stages, errors — via %TEMP%\clicky-debug.log
//  without needing to see the screen. Zero overhead when the flag is absent.
//

using System.IO;

namespace Clicky;

public static class DebugTrace
{
    private static readonly bool isEnabled =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "debug-capture.flag"));

    // Next to the exe (not %TEMP%) so it's readable from any context — the
    // verification harness lives outside the app's process sandbox.
    private static readonly string logFilePath =
        Path.Combine(AppContext.BaseDirectory, "clicky-debug.log");

    private static readonly object writeLock = new();

    public static bool IsEnabled => isEnabled;

    public static void Log(string message)
    {
        if (!isEnabled)
        {
            return;
        }
        try
        {
            lock (writeLock)
            {
                File.AppendAllText(logFilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
            }
        }
        catch
        {
            // Tracing must never break the app.
        }
    }
}
