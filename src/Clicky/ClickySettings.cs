//
//  ClickySettings.cs
//  Clicky for Windows
//
//  Persisted user preferences — the Windows equivalent of the original's
//  UserDefaults usage. Stored as JSON in %APPDATA%\Clicky\settings.json
//  so the choices survive app restarts.
//

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clicky;

public class ClickySettings
{
    /// The Claude model alias used for voice responses ("sonnet" or "opus"),
    /// passed straight to the Claude Code CLI's --model flag.
    public string SelectedModel { get; set; } = "sonnet";

    /// Whether the buddy cursor overlay is shown persistently. When off,
    /// the overlay only appears transiently during push-to-talk interactions.
    public bool IsClickyCursorEnabled { get; set; } = true;

    /// Whether the user has completed onboarding at least once.
    public bool HasCompletedOnboarding { get; set; }

    /// Voice transcription backend: "whisper" (local, default) or
    /// "windows" (built-in Windows speech recognition).
    public string VoiceTranscriptionProvider { get; set; } = "whisper";

    /// Whisper model size: "tiny", "base", or "small". Bigger is more
    /// accurate but slower on CPU.
    public string WhisperModel { get; set; } = "base";

    /// Edge neural voice used to speak responses.
    public string TtsVoice { get; set; } = "en-US-AvaMultilingualNeural";

    /// Whether Clicky registers itself to launch on sign-in.
    public bool LaunchAtStartup { get; set; } = true;

    // ── Persistence ──────────────────────────────────────────────────

    [JsonIgnore]
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clicky");

    private static string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");

    private static readonly object SaveLock = new();
    private static ClickySettings? current;

    public static ClickySettings Current
    {
        get
        {
            if (current == null)
            {
                current = Load();
            }
            return current;
        }
    }

    private static ClickySettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loadedSettings = JsonSerializer.Deserialize<ClickySettings>(json);
                if (loadedSettings != null)
                {
                    return loadedSettings;
                }
            }
        }
        catch (Exception loadError)
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ Settings load failed, using defaults: {loadError.Message}");
        }
        return new ClickySettings();
    }

    public void Save()
    {
        lock (SaveLock)
        {
            try
            {
                Directory.CreateDirectory(AppDataDirectory);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception saveError)
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ Settings save failed: {saveError.Message}");
            }
        }
    }
}
