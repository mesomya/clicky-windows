//
//  BuddyDictationManager.cs
//  Clicky for Windows
//
//  Push-to-talk dictation manager — a direct port of the original's
//  BuddyDictationManager. Captures microphone audio with NAudio's WASAPI
//  capture (the AVAudioEngine analog), routes mono float chunks into the
//  active transcription provider, reports live audio power for the
//  waveform, and hands the final transcript back to the companion manager.
//

using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clicky.Audio;

public sealed class BuddyDictationManager
{
    private const double DefaultFinalTranscriptFallbackDelaySeconds = 2.4;

    // ── Observable state (read by CompanionManager + panel) ─────────

    public bool IsRecordingFromKeyboardShortcut { get; private set; }
    public bool IsFinalizingTranscript { get; private set; }
    public bool IsPreparingToRecord { get; private set; }
    public double CurrentAudioPowerLevel { get; private set; }
    public string TranscriptionProviderDisplayName { get; private set; } = "";
    public string? LastErrorMessage { get; private set; }

    /// Fired on the UI dispatcher whenever any recording/finalizing state flips.
    public event Action? StateChanged;

    /// Fired on the UI dispatcher with the smoothed audio power level (0–1).
    public event Action<double>? AudioPowerLevelChanged;

    public bool IsDictationInProgress =>
        IsPreparingToRecord || IsRecordingFromKeyboardShortcut || IsFinalizingTranscript;

    // ── Internals ────────────────────────────────────────────────────

    private readonly Dispatcher uiDispatcher;
    private IBuddyTranscriptionProvider transcriptionProvider;
    private IBuddyStreamingTranscriptionSession? activeTranscriptionSession;
    private WasapiCapture? microphoneCapture;

    private Action<string>? submitDraftTextCallback;
    private string latestRecognizedText = "";
    private bool hasFinishedCurrentDictationSession;
    private DispatcherTimer? finalizeFallbackTimer;
    private Guid pendingStartRequestIdentifier = Guid.NewGuid();

    public BuddyDictationManager()
    {
        uiDispatcher = Dispatcher.CurrentDispatcher;
        transcriptionProvider = BuddyTranscriptionProviderFactory.MakeDefaultProvider();
        TranscriptionProviderDisplayName = transcriptionProvider.DisplayName;
    }

    /// Rebuilds the provider after the user changes the speech-to-text
    /// setting in the panel. Whisper fully unloads when switched away.
    public void ReloadTranscriptionProviderFromSettings()
    {
        if (IsDictationInProgress)
        {
            CancelCurrentDictation();
        }
        transcriptionProvider = BuddyTranscriptionProviderFactory.MakeDefaultProvider();
        TranscriptionProviderDisplayName = transcriptionProvider.DisplayName;
        NotifyStateChanged();
    }

    // ── Start / stop ─────────────────────────────────────────────────

    public async Task StartPushToTalkFromKeyboardShortcutAsync(Action<string> submitDraftText)
    {
        if (IsDictationInProgress)
        {
            return;
        }

        System.Diagnostics.Debug.WriteLine("🎙️ BuddyDictationManager: start requested (keyboardShortcut)");

        var startRequestIdentifier = Guid.NewGuid();
        pendingStartRequestIdentifier = startRequestIdentifier;

        LastErrorMessage = null;
        IsPreparingToRecord = true;
        NotifyStateChanged();

        submitDraftTextCallback = submitDraftText;
        latestRecognizedText = "";
        hasFinishedCurrentDictationSession = false;
        IsFinalizingTranscript = false;
        CurrentAudioPowerLevel = 0;

        try
        {
            await StartRecognitionSessionAsync();

            // The user may have released the shortcut while the provider was
            // starting — if a newer request superseded us, unwind quietly.
            if (pendingStartRequestIdentifier != startRequestIdentifier)
            {
                System.Diagnostics.Debug.WriteLine("🎙️ BuddyDictationManager: start request superseded");
                StopMicrophoneCapture();
                activeTranscriptionSession?.Cancel();
                ResetSessionState();
                return;
            }

            IsPreparingToRecord = false;
            IsRecordingFromKeyboardShortcut = true;
            NotifyStateChanged();
            System.Diagnostics.Debug.WriteLine("🎙️ BuddyDictationManager: recognition session started");
        }
        catch (Exception startError)
        {
            IsPreparingToRecord = false;
            LastErrorMessage = UserFacingErrorMessage(startError, "couldn't start voice input. try again.");
            System.Diagnostics.Debug.WriteLine($"❌ BuddyDictationManager: failed to start ({transcriptionProvider.DisplayName}): {startError}");
            ResetSessionState();
            NotifyStateChanged();
        }
    }

    public void StopPushToTalkFromKeyboardShortcut()
    {
        pendingStartRequestIdentifier = Guid.NewGuid();

        if (!IsRecordingFromKeyboardShortcut && !IsPreparingToRecord)
        {
            return;
        }
        if (IsFinalizingTranscript)
        {
            return;
        }

        System.Diagnostics.Debug.WriteLine("🎙️ BuddyDictationManager: stop requested");

        // If recording never actually began (quick press-and-release while
        // the provider was starting), just unwind without finalizing.
        if (!IsRecordingFromKeyboardShortcut)
        {
            CancelCurrentDictation();
            return;
        }

        IsRecordingFromKeyboardShortcut = false;
        IsFinalizingTranscript = true;
        NotifyStateChanged();

        double fallbackDelaySeconds = activeTranscriptionSession?.FinalTranscriptFallbackDelaySeconds
            ?? DefaultFinalTranscriptFallbackDelaySeconds;

        StopMicrophoneCapture();
        activeTranscriptionSession?.RequestFinalTranscript();

        // Safety net: if the provider never delivers a final transcript,
        // submit whatever partial text exists so the UI doesn't get stuck
        // in the processing state forever.
        finalizeFallbackTimer?.Stop();
        finalizeFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(fallbackDelaySeconds) };
        finalizeFallbackTimer.Tick += (_, _) =>
        {
            finalizeFallbackTimer?.Stop();
            FinishCurrentDictationSessionIfNeeded();
        };
        finalizeFallbackTimer.Start();
    }

    public void CancelCurrentDictation()
    {
        pendingStartRequestIdentifier = Guid.NewGuid();

        if (!IsDictationInProgress)
        {
            return;
        }

        finalizeFallbackTimer?.Stop();
        finalizeFallbackTimer = null;

        StopMicrophoneCapture();
        activeTranscriptionSession?.Cancel();
        ResetSessionState();
        NotifyStateChanged();
    }

    // ── Recognition session ──────────────────────────────────────────

    private async Task StartRecognitionSessionAsync()
    {
        activeTranscriptionSession?.Cancel();
        activeTranscriptionSession = null;

        System.Diagnostics.Debug.WriteLine($"🎙️ BuddyDictationManager: opening provider {transcriptionProvider.DisplayName}");

        activeTranscriptionSession = await transcriptionProvider.StartStreamingSessionAsync(
            keyterms: BuildTranscriptionKeyterms(),
            onTranscriptUpdate: transcriptText =>
            {
                uiDispatcher.BeginInvoke(() => latestRecognizedText = transcriptText);
            },
            onFinalTranscriptReady: transcriptText =>
            {
                uiDispatcher.BeginInvoke(() =>
                {
                    latestRecognizedText = transcriptText;
                    if (IsFinalizingTranscript)
                    {
                        FinishCurrentDictationSessionIfNeeded();
                    }
                });
            },
            onError: recognitionError =>
            {
                uiDispatcher.BeginInvoke(() => HandleRecognitionError(recognitionError));
            });

        StartMicrophoneCapture();
    }

    private void StartMicrophoneCapture()
    {
        // WASAPI shared-mode capture of the default microphone. Throws
        // E_ACCESSDENIED if the Windows privacy setting blocks microphone
        // access for desktop apps — surfaced as a friendly error upstream.
        microphoneCapture = new WasapiCapture();
        var captureFormat = microphoneCapture.WaveFormat;

        microphoneCapture.DataAvailable += (_, dataEvent) =>
        {
            float[] monoSamples = BuddyAudioConversionSupport.ConvertCaptureBufferToMonoFloat(
                dataEvent.Buffer, dataEvent.BytesRecorded, captureFormat);

            if (monoSamples.Length == 0)
            {
                return;
            }

            activeTranscriptionSession?.AppendAudioSamples(monoSamples, captureFormat.SampleRate);
            UpdateAudioPowerLevel(monoSamples);
        };

        microphoneCapture.StartRecording();
    }

    private void StopMicrophoneCapture()
    {
        try
        {
            microphoneCapture?.StopRecording();
            microphoneCapture?.Dispose();
        }
        catch
        {
            // Disposal races with the capture thread; safe to ignore.
        }
        microphoneCapture = null;
    }

    private void HandleRecognitionError(Exception recognitionError)
    {
        if (hasFinishedCurrentDictationSession)
        {
            return;
        }

        if (IsFinalizingTranscript && !string.IsNullOrWhiteSpace(latestRecognizedText))
        {
            FinishCurrentDictationSessionIfNeeded();
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"❌ Buddy dictation error ({transcriptionProvider.DisplayName}): {recognitionError}");
            DebugTrace.Log($"dictation ERROR ({transcriptionProvider.DisplayName}): {recognitionError.Message}");
            LastErrorMessage = UserFacingErrorMessage(recognitionError, "couldn't transcribe that. try again.");
            CancelCurrentDictation();
        }
    }

    private void FinishCurrentDictationSessionIfNeeded()
    {
        if (hasFinishedCurrentDictationSession)
        {
            return;
        }
        hasFinishedCurrentDictationSession = true;

        finalizeFallbackTimer?.Stop();
        finalizeFallbackTimer = null;

        string finalTranscriptText = latestRecognizedText.Trim();
        var submitCallback = submitDraftTextCallback;

        StopMicrophoneCapture();
        activeTranscriptionSession?.Cancel();
        ResetSessionState();
        NotifyStateChanged();

        if (!string.IsNullOrEmpty(finalTranscriptText))
        {
            DebugTrace.Log($"dictation finalized -> submitting \"{finalTranscriptText}\"");
            submitCallback?.Invoke(finalTranscriptText);
        }
        else
        {
            DebugTrace.Log("dictation finalized -> EMPTY transcript, nothing submitted (buddy just returns to idle)");
        }
    }

    private void ResetSessionState()
    {
        pendingStartRequestIdentifier = Guid.NewGuid();
        activeTranscriptionSession = null;
        submitDraftTextCallback = null;
        latestRecognizedText = "";
        hasFinishedCurrentDictationSession = false;
        IsPreparingToRecord = false;
        IsRecordingFromKeyboardShortcut = false;
        IsFinalizingTranscript = false;
        CurrentAudioPowerLevel = 0;
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    // ── Keyterms ─────────────────────────────────────────────────────

    private static IReadOnlyList<string> BuildTranscriptionKeyterms()
    {
        // Vocabulary bias, same idea as the original's AssemblyAI keyterms —
        // adapted for the Windows port's world.
        return new[]
        {
            "Clicky",
            "Claude",
            "Anthropic",
            "OpenAI",
            "Visual Studio",
            "VS Code",
            "Windows",
            "Vercel",
            "Next.js",
            "localhost",
        };
    }

    // ── Audio power ──────────────────────────────────────────────────

    private void UpdateAudioPowerLevel(float[] monoSamples)
    {
        float rootMeanSquare = BuddyAudioConversionSupport.ComputeRootMeanSquare(monoSamples);

        // Same boost + decay-smoothing curve as the original, so the
        // waveform feels identical.
        double boostedLevel = Math.Clamp(rootMeanSquare * 10.2, 0, 1);

        uiDispatcher.BeginInvoke(() =>
        {
            double smoothedAudioPowerLevel = Math.Max(boostedLevel, CurrentAudioPowerLevel * 0.72);
            CurrentAudioPowerLevel = smoothedAudioPowerLevel;
            AudioPowerLevelChanged?.Invoke(smoothedAudioPowerLevel);
        });
    }

    // ── Microphone permission probe ──────────────────────────────────

    /// Windows has no upfront permission prompt for desktop apps — access is
    /// governed by Settings > Privacy > Microphone. We probe by briefly
    /// opening a capture session.
    public static bool ProbeMicrophoneAccess()
    {
        try
        {
            using var deviceEnumerator = new MMDeviceEnumerator();
            using var defaultMicrophone = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            using var probeCapture = new WasapiCapture(defaultMicrophone);
            probeCapture.StartRecording();
            probeCapture.StopRecording();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void OpenMicrophonePrivacySettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:privacy-microphone",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Settings app unavailable — nothing useful to do.
        }
    }

    private static string UserFacingErrorMessage(Exception error, string fallback)
    {
        string description = error.Message.Trim();
        if (error is System.Runtime.InteropServices.COMException comError &&
            (uint)comError.HResult == 0x80070005)
        {
            return "microphone access is blocked. allow it in Settings > Privacy > Microphone.";
        }
        return string.IsNullOrEmpty(description) ? fallback : description;
    }
}
