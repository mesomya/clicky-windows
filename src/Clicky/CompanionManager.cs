//
//  CompanionManager.cs
//  Clicky for Windows
//
//  Central state manager for the companion voice mode — a direct port of
//  the original CompanionManager. Owns the push-to-talk pipeline (dictation
//  manager + global shortcut monitor + overlay), the Claude brain, TTS, and
//  exposes observable voice state for the panel UI.
//

using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Clicky.Audio;
using Clicky.Brain;
using Clicky.Capture;
using Clicky.Hotkey;
using Clicky.Tts;
using Clicky.Ui;

namespace Clicky;

public enum CompanionVoiceState
{
    Idle,
    Listening,
    Processing,
    Responding,
}

public sealed class CompanionManager
{
    // ── Observable state ─────────────────────────────────────────────

    public CompanionVoiceState VoiceState { get; private set; } = CompanionVoiceState.Idle;
    public string? LastTranscript { get; private set; }
    public double CurrentAudioPowerLevel { get; private set; }
    public bool HasMicrophonePermission { get; private set; }
    public bool IsOverlayVisible { get; private set; }

    /// Windows only needs the microphone — there is no Accessibility or
    /// Screen Recording permission gate like macOS. The Claude CLI being
    /// installed is the other prerequisite, surfaced separately.
    public bool AllPermissionsGranted => HasMicrophonePermission;

    public bool IsClaudeCliAvailable => ClaudeCodeBrainClient.IsClaudeCliAvailable;

    /// Fired on the UI thread whenever any observable state changes —
    /// the panel re-reads everything it shows (cheap at this scale).
    public event Action? StateChanged;

    /// One-shot impulse for the overlay: the buddy should fly to this
    /// global pixel location on the given display and point at it.
    public event Action? DetectedElementChanged;

    public System.Drawing.Point? DetectedElementScreenLocation { get; private set; }
    public Rectangle? DetectedElementDisplayBounds { get; private set; }
    public string? DetectedElementBubbleText { get; private set; }

    /// Raised when the tray panel should close (push-to-talk started, etc).
    public static event Action? DismissPanelRequested;
    public static void RequestDismissPanel() => DismissPanelRequested?.Invoke();

    // ── Onboarding prompt bubble (streamed on the cursor overlay) ────

    public string OnboardingPromptText { get; private set; } = "";
    public double OnboardingPromptOpacity { get; private set; }
    public bool ShowOnboardingPrompt { get; private set; }

    // ── Components ───────────────────────────────────────────────────

    public BuddyDictationManager BuddyDictationManager { get; }
    public GlobalPushToTalkShortcutMonitor GlobalPushToTalkShortcutMonitor { get; }
    public OverlayWindowManager OverlayWindowManager { get; }

    private ClaudeCodeBrainClient brainClient;
    private readonly EdgeTtsClient edgeTtsClient = new();

    private readonly Dispatcher uiDispatcher;
    private CancellationTokenSource? currentResponseCancellation;
    private CancellationTokenSource? transientHideCancellation;
    private DispatcherTimer? permissionPollTimer;
    private NAudio.Wave.WaveOutEvent? onboardingMusicPlayer;
    private NAudio.Wave.Mp3FileReader? onboardingMusicReader;
    private DispatcherTimer? onboardingMusicFadeTimer;

    // ── Settings-backed preferences ──────────────────────────────────

    public string SelectedModel => ClickySettings.Current.SelectedModel;

    public void SetSelectedModel(string model)
    {
        ClickySettings.Current.SelectedModel = model;
        ClickySettings.Current.Save();
        brainClient.SetModel(model);
        NotifyStateChanged();
    }

    public bool IsClickyCursorEnabled => ClickySettings.Current.IsClickyCursorEnabled;

    public void SetClickyCursorEnabled(bool enabled)
    {
        ClickySettings.Current.IsClickyCursorEnabled = enabled;
        ClickySettings.Current.Save();
        transientHideCancellation?.Cancel();
        transientHideCancellation = null;

        if (enabled)
        {
            OverlayWindowManager.HasShownOverlayBefore = true;
            OverlayWindowManager.ShowOverlay(this);
            IsOverlayVisible = true;
        }
        else
        {
            OverlayWindowManager.HideOverlay();
            IsOverlayVisible = false;
        }
        NotifyStateChanged();
    }

    public bool HasCompletedOnboarding
    {
        get => ClickySettings.Current.HasCompletedOnboarding;
        set
        {
            ClickySettings.Current.HasCompletedOnboarding = value;
            ClickySettings.Current.Save();
        }
    }

    public CompanionManager()
    {
        uiDispatcher = Dispatcher.CurrentDispatcher;
        BuddyDictationManager = new BuddyDictationManager();
        GlobalPushToTalkShortcutMonitor = new GlobalPushToTalkShortcutMonitor();
        OverlayWindowManager = new OverlayWindowManager();
        brainClient = new ClaudeCodeBrainClient(CompanionVoiceResponseSystemPrompt, SelectedModel);
    }

    public void Start()
    {
        RefreshAllPermissions();
        System.Diagnostics.Debug.WriteLine(
            $"🔑 Clicky start — mic: {HasMicrophonePermission}, claudeCli: {IsClaudeCliAvailable}, onboarded: {HasCompletedOnboarding}");

        StartPermissionPolling();
        BindVoiceStateObservation();
        BindShortcutTransitions();
        GlobalPushToTalkShortcutMonitor.Start();

        // Warm everything that has a slow cold-start, so the user's FIRST
        // interaction is as fast as later ones (the cold first call was the
        // big "it processed then ghosted" culprit — TTS alone stalled ~24s
        // on first use):
        //  - Whisper model load (and download on first ever run)
        //  - the Edge voice connection (DNS/TLS/IPv6 priming)
        //  - the headless Claude brain process
        if (ClickySettings.Current.VoiceTranscriptionProvider == "whisper")
        {
            _ = WhisperTranscriptionProvider.EnsureFactoryReadyAsync();
        }
        _ = EdgeTtsClient.WarmUpAsync();
        _ = Task.Run(() => brainClient.WarmUp());

        if (HasCompletedOnboarding && AllPermissionsGranted && IsClickyCursorEnabled)
        {
            OverlayWindowManager.HasShownOverlayBefore = true;
            OverlayWindowManager.ShowOverlay(this);
            IsOverlayVisible = true;
        }
        NotifyStateChanged();
    }

    public void Stop()
    {
        GlobalPushToTalkShortcutMonitor.Stop();
        BuddyDictationManager.CancelCurrentDictation();
        OverlayWindowManager.HideOverlay();
        transientHideCancellation?.Cancel();
        currentResponseCancellation?.Cancel();
        permissionPollTimer?.Stop();
        StopOnboardingMusic();
        edgeTtsClient.StopKeepAlive();
        edgeTtsClient.StopPlayback();
        brainClient.Dispose();
    }

    // ── Permissions ──────────────────────────────────────────────────

    public void RefreshAllPermissions()
    {
        bool previouslyHadMicrophone = HasMicrophonePermission;
        HasMicrophonePermission = BuddyDictationManager.ProbeMicrophoneAccess();

        if (!previouslyHadMicrophone && HasMicrophonePermission)
        {
            ClickyAnalytics.TrackPermissionGranted("microphone");
            ClickyAnalytics.TrackAllPermissionsGranted();
        }
        NotifyStateChanged();
    }

    /// Polls only while the microphone is blocked so the panel updates live
    /// after the user flips the privacy toggle. Once granted, polling stops —
    /// probing opens the capture device, which we don't want to do forever.
    private void StartPermissionPolling()
    {
        permissionPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        permissionPollTimer.Tick += (_, _) =>
        {
            if (HasMicrophonePermission)
            {
                permissionPollTimer?.Stop();
                return;
            }
            RefreshAllPermissions();
        };
        if (!HasMicrophonePermission)
        {
            permissionPollTimer.Start();
        }
    }

    // ── Voice state plumbing ─────────────────────────────────────────

    private void BindVoiceStateObservation()
    {
        BuddyDictationManager.AudioPowerLevelChanged += powerLevel =>
        {
            CurrentAudioPowerLevel = powerLevel;
        };

        BuddyDictationManager.StateChanged += () =>
        {
            // Don't override Responding — the AI response pipeline manages
            // that state directly until playback finishes.
            if (VoiceState == CompanionVoiceState.Responding)
            {
                return;
            }

            if (BuddyDictationManager.IsFinalizingTranscript)
            {
                SetVoiceState(CompanionVoiceState.Processing);
            }
            else if (BuddyDictationManager.IsRecordingFromKeyboardShortcut)
            {
                SetVoiceState(CompanionVoiceState.Listening);
            }
            else if (BuddyDictationManager.IsPreparingToRecord)
            {
                SetVoiceState(CompanionVoiceState.Processing);
            }
            else
            {
                SetVoiceState(CompanionVoiceState.Idle);
                // If the user tapped the hotkey without saying anything, no
                // response task runs — schedule the transient hide here so
                // the overlay doesn't get stuck visible, and let the keepalive
                // (started on the press) stop.
                if (currentResponseCancellation == null)
                {
                    ScheduleTransientHideIfNeeded();
                    edgeTtsClient.StopKeepAlive();
                }
            }
        };
    }

    private void SetVoiceState(CompanionVoiceState newState)
    {
        if (VoiceState == newState)
        {
            return;
        }
        VoiceState = newState;
        DebugTrace.Log($"voice state -> {newState}");
        NotifyStateChanged();
    }

    private void BindShortcutTransitions()
    {
        GlobalPushToTalkShortcutMonitor.ShortcutTransitionOccurred += HandleShortcutTransition;
    }

    private void HandleShortcutTransition(ShortcutTransition transition)
    {
        switch (transition)
        {
            case ShortcutTransition.Pressed:
                if (BuddyDictationManager.IsDictationInProgress)
                {
                    DebugTrace.Log("hotkey pressed but IGNORED — a previous dictation is still in progress (stuck?)");
                    return;
                }
                if (!HasCompletedOnboarding || !AllPermissionsGranted)
                {
                    DebugTrace.Log($"hotkey pressed but IGNORED — onboarded={HasCompletedOnboarding}, mic={AllPermissionsGranted}");
                    return;
                }

                // Cancel any pending transient hide so the overlay stays visible
                transientHideCancellation?.Cancel();
                transientHideCancellation = null;

                // If the cursor is hidden, bring it back transiently for this interaction
                if (!IsClickyCursorEnabled && !IsOverlayVisible)
                {
                    OverlayWindowManager.HasShownOverlayBefore = true;
                    OverlayWindowManager.ShowOverlay(this);
                    IsOverlayVisible = true;
                }

                RequestDismissPanel();

                // Cancel any in-progress response and TTS from a previous utterance
                currentResponseCancellation?.Cancel();
                currentResponseCancellation = null;
                edgeTtsClient.StopPlayback();
                ClearDetectedElementLocation();
                DismissOnboardingPromptIfShowing();

                ClickyAnalytics.TrackPushToTalkStarted();

                // Keep the (Bluetooth) output device awake from the instant the
                // user starts talking, so it doesn't drop to standby during the
                // silent thinking window and make the answer land elsewhere.
                edgeTtsClient.StartKeepAlive();

                DebugTrace.Log("hotkey pressed — push-to-talk starting");
                _ = BuddyDictationManager.StartPushToTalkFromKeyboardShortcutAsync(finalTranscript =>
                {
                    LastTranscript = finalTranscript;
                    System.Diagnostics.Debug.WriteLine($"🗣️ Companion received transcript: {finalTranscript}");
                    DebugTrace.Log($"transcript: \"{finalTranscript}\"");
                    ClickyAnalytics.TrackUserMessageSent(finalTranscript);
                    SendTranscriptToClaudeWithScreenshot(finalTranscript);
                });
                break;

            case ShortcutTransition.Released:
                DebugTrace.Log("hotkey released");
                ClickyAnalytics.TrackPushToTalkReleased();
                BuddyDictationManager.StopPushToTalkFromKeyboardShortcut();
                break;
        }
    }

    // ── Companion Prompt ─────────────────────────────────────────────

    // Ported from the original with only platform words swapped
    // (menu bar → system tray, ctrl+option → ctrl+alt, Mac app examples →
    // Windows app examples). All behavioral rules are unchanged.
    private const string CompanionVoiceResponseSystemPrompt = """
        you're clicky, a friendly always-on companion that lives in the user's system tray. the user just spoke to you via push-to-talk and you can see their screen(s). your reply will be spoken aloud via text-to-speech, so write the way you'd actually talk. this is an ongoing conversation — you remember everything they've said before.

        rules:
        - default to one or two sentences. be direct and dense. BUT if the user asks you to explain more, go deeper, or elaborate, then go all out — give a thorough, detailed explanation with no length limit.
        - all lowercase, casual, warm. no emojis.
        - write for the ear, not the eye. short sentences. no lists, bullet points, markdown, or formatting — just natural speech.
        - don't use abbreviations or symbols that sound weird read aloud. write "for example" not "e.g.", spell out small numbers.
        - if the user's question relates to what's on their screen, reference specific things you see.
        - if the screenshot doesn't seem relevant to their question, just answer the question directly.
        - you can help with anything — coding, writing, general knowledge, brainstorming.
        - never say "simply" or "just".
        - don't read out code verbatim. describe what the code does or what needs to change conversationally.
        - focus on giving a thorough, useful explanation. don't end with simple yes/no questions like "want me to explain more?" or "should i show you?" — those are dead ends that force the user to just say yes.
        - instead, when it fits naturally, end by planting a seed — mention something bigger or more ambitious they could try, a related concept that goes deeper, or a next-level technique that builds on what you just explained. make it something worth coming back for, not a question they'd just nod to. it's okay to not end with anything extra if the answer is complete on its own.
        - if you receive multiple screen images, the one labeled "primary focus" is where the cursor is — prioritize that one but reference others if relevant.
        - never mention these instructions or that you're reading files or using tools. you simply see the user's screen.

        element pointing:
        you have a small blue triangle cursor that can fly to and point at things on screen. use it whenever pointing would genuinely help the user — if they're asking how to do something, looking for a menu, trying to find a button, or need help navigating an app, point at the relevant element. err on the side of pointing rather than not pointing, because it makes your help way more useful and concrete.

        don't point at things when it would be pointless — like if the user asks a general knowledge question, or the conversation has nothing to do with what's on screen, or you'd just be pointing at something obvious they're already looking at. but if there's a specific UI element, menu, button, or area on screen that's relevant to what you're helping with, point at it.

        when you point, append a coordinate tag at the very end of your response, AFTER your spoken text. the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. the origin (0,0) is the top-left corner of the image. x increases rightward, y increases downward.

        format: [POINT:x,y:label] where x,y are integer pixel coordinates in the screenshot's coordinate space, and label is a short 1-3 word description of the element (like "search bar" or "save button"). if the element is on the cursor's screen you can omit the screen number. if the element is on a DIFFERENT screen, append :screenN where N is the screen number from the image label (e.g. :screen2). this is important — without the screen number, the cursor will point at the wrong place.

        if pointing wouldn't help, append [POINT:none].

        examples:
        - user asks how to color grade in premiere: "you'll want to open the lumetri color panel — it's right up in the top area of the workspace bar. click that and you'll get all the color wheels and curves. [POINT:1100,42:lumetri color]"
        - user asks what html is: "html stands for hypertext markup language, it's basically the skeleton of every web page. curious how it connects to the css you're looking at? [POINT:none]"
        - user asks how to commit in vs code: "see that source control icon on the left sidebar? click that and hit the checkmark, or you can use control enter from the message box. [POINT:24,180:source control]"
        - element is on screen 2 (not where cursor is): "that's over on your other monitor — see the terminal window? [POINT:400,300:terminal:screen2]"
        """;

    // ── AI Response Pipeline ─────────────────────────────────────────

    /// Captures screenshots of every display, sends them with the transcript
    /// to Claude, and plays the response aloud. The cursor stays in the
    /// spinner state until TTS audio begins playing. A [POINT:...] tag in
    /// the response triggers the buddy's flight to that element.
    /// Verification hook (debug builds only): runs the EXACT production
    /// response pipeline — screenshot → Claude → Edge TTS — on a supplied
    /// transcript, with no microphone involved. Lets the automated harness
    /// reproduce and trace the "processing then ghost" path on demand.
    public void DebugInjectTranscript(string transcript)
    {
        DebugTrace.Log($"DEBUG inject transcript: \"{transcript}\"");
        LastTranscript = transcript;
        SendTranscriptToClaudeWithScreenshot(transcript);
    }

    private void SendTranscriptToClaudeWithScreenshot(string transcript)
    {
        currentResponseCancellation?.Cancel();
        edgeTtsClient.StopPlayback();

        var responseCancellation = new CancellationTokenSource();
        currentResponseCancellation = responseCancellation;
        var cancellationToken = responseCancellation.Token;

        _ = RunResponsePipelineAsync(transcript, responseCancellation, cancellationToken);
    }

    private async Task RunResponsePipelineAsync(
        string transcript,
        CancellationTokenSource responseCancellation,
        CancellationToken cancellationToken)
    {
        SetVoiceState(CompanionVoiceState.Processing);

        try
        {
            // Capture all connected screens so the AI has full context.
            // Run off the UI thread — GDI capture takes ~50-150ms per screen.
            var screenCaptures = await Task.Run(CompanionScreenCaptureUtility.CaptureAllScreensAsJpeg, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // Label each image with its actual pixel dimensions so Claude's
            // coordinate space matches the image it sees.
            var labeledImages = screenCaptures
                .Select(capture => (
                    Data: capture.ImageData,
                    Label: capture.Label + $" (image dimensions: {capture.ScreenshotWidthInPixels}x{capture.ScreenshotHeightInPixels} pixels)"))
                .ToList();

            DebugTrace.Log($"sending to claude: {labeledImages.Count} screen(s)");
            string fullResponseText = await brainClient.AnalyzeImagesAsync(labeledImages, transcript, cancellationToken);
            DebugTrace.Log($"claude responded: \"{fullResponseText[..Math.Min(140, fullResponseText.Length)]}\"");

            cancellationToken.ThrowIfCancellationRequested();

            var parseResult = ParsePointingCoordinates(fullResponseText);
            string spokenText = parseResult.SpokenText;

            // Switch to idle BEFORE setting the location so the triangle
            // becomes visible and can fly to the target — the spinner would
            // otherwise hide the flight animation.
            bool hasPointCoordinate = parseResult.Coordinate != null;
            if (hasPointCoordinate)
            {
                SetVoiceState(CompanionVoiceState.Idle);
            }

            // Pick the screen capture matching Claude's screen number,
            // falling back to the cursor screen if not specified.
            CompanionScreenCapture? targetScreenCapture =
                parseResult.ScreenNumber is int screenNumber && screenNumber >= 1 && screenNumber <= screenCaptures.Count
                    ? screenCaptures[screenNumber - 1]
                    : screenCaptures.FirstOrDefault(capture => capture.IsCursorScreen);

            if (parseResult.Coordinate is System.Windows.Point pointCoordinate && targetScreenCapture != null)
            {
                // Claude's coordinates are in the screenshot's pixel space
                // (top-left origin). Scale to the display's full resolution —
                // Windows uses top-left origin globally too, so unlike the
                // Mac original there's no Y-axis flip.
                var displayBounds = targetScreenCapture.DisplayBoundsInPixels;
                double clampedX = Math.Clamp(pointCoordinate.X, 0, targetScreenCapture.ScreenshotWidthInPixels);
                double clampedY = Math.Clamp(pointCoordinate.Y, 0, targetScreenCapture.ScreenshotHeightInPixels);

                double displayLocalX = clampedX * ((double)displayBounds.Width / targetScreenCapture.ScreenshotWidthInPixels);
                double displayLocalY = clampedY * ((double)displayBounds.Height / targetScreenCapture.ScreenshotHeightInPixels);

                var globalLocation = new System.Drawing.Point(
                    displayBounds.X + (int)Math.Round(displayLocalX),
                    displayBounds.Y + (int)Math.Round(displayLocalY));

                DetectedElementScreenLocation = globalLocation;
                DetectedElementDisplayBounds = displayBounds;
                DetectedElementChanged?.Invoke();
                DebugTrace.Log($"pointing at ({globalLocation.X},{globalLocation.Y}) \"{parseResult.ElementLabel}\"");
                ClickyAnalytics.TrackElementPointed(parseResult.ElementLabel);
                System.Diagnostics.Debug.WriteLine(
                    $"🎯 Element pointing: ({(int)pointCoordinate.X}, {(int)pointCoordinate.Y}) → \"{parseResult.ElementLabel ?? "element"}\"");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"🎯 Element pointing: {parseResult.ElementLabel ?? "no element"}");
            }

            // Conversation memory note: the original appended to a local
            // history array re-sent on every request. Here the Claude Code
            // session itself remembers prior exchanges, so nothing to do.

            ClickyAnalytics.TrackAIResponseReceived(spokenText);

            // Play the response via TTS. Keep the spinner until audio
            // actually starts, then switch to responding.
            if (!string.IsNullOrWhiteSpace(spokenText))
            {
                try
                {
                    await edgeTtsClient.SpeakTextAsync(spokenText, cancellationToken);
                    DebugTrace.Log("tts playback started");
                    SetVoiceState(CompanionVoiceState.Responding);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ttsError)
                {
                    ClickyAnalytics.TrackTTSError(ttsError.Message);
                    System.Diagnostics.Debug.WriteLine($"⚠️ Edge TTS error: {ttsError}");
                    DebugTrace.Log($"tts ERROR: {ttsError.Message}");
                    SpeakOfflineFallback("my voice is not working. check your internet connection and try again.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // User spoke again — response was interrupted.
            DebugTrace.Log("response cancelled (user spoke again)");
        }
        catch (Exception responseError)
        {
            ClickyAnalytics.TrackResponseError(responseError.Message);
            System.Diagnostics.Debug.WriteLine($"⚠️ Companion response error: {responseError}");
            DebugTrace.Log($"response ERROR: {responseError.Message}");
            SpeakOfflineFallback("i could not reach my brain. make sure claude code is installed and signed in.");
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            // Wait for playback to finish before returning to idle so the
            // triangle stays in the responding cross-fade while speaking.
            while (edgeTtsClient.IsPlaying && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(200, CancellationToken.None);
            }
            if (!cancellationToken.IsCancellationRequested)
            {
                SetVoiceState(CompanionVoiceState.Idle);
                ScheduleTransientHideIfNeeded();
                // Answer delivered — let the output device idle again.
                edgeTtsClient.StopKeepAlive();
            }
        }

        if (currentResponseCancellation == responseCancellation)
        {
            currentResponseCancellation = null;
        }
    }

    /// If the cursor is in transient mode (user toggled "Show Clicky" off),
    /// waits for TTS playback and any pointing animation to finish, then
    /// fades out the overlay after a 1-second pause.
    private void ScheduleTransientHideIfNeeded()
    {
        if (IsClickyCursorEnabled || !IsOverlayVisible)
        {
            return;
        }

        transientHideCancellation?.Cancel();
        var hideCancellation = new CancellationTokenSource();
        transientHideCancellation = hideCancellation;
        var token = hideCancellation.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (edgeTtsClient.IsPlaying)
                {
                    await Task.Delay(200, token);
                }
                while (DetectedElementScreenLocation != null)
                {
                    await Task.Delay(200, token);
                }
                await Task.Delay(1000, token);

                await uiDispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    OverlayWindowManager.FadeOutAndHideOverlay();
                    IsOverlayVisible = false;
                    NotifyStateChanged();
                });
            }
            catch (OperationCanceledException)
            {
                // User spoke again before the hide fired.
            }
        }, token);
    }

    /// Speaks a hardcoded error message using Windows' built-in offline TTS
    /// (the analog of the original's NSSpeechSynthesizer fallback) so the
    /// buddy is never silently broken.
    private void SpeakOfflineFallback(string utterance)
    {
        try
        {
            var synthesizer = new System.Speech.Synthesis.SpeechSynthesizer();
            synthesizer.SpeakCompleted += (_, _) => synthesizer.Dispose();
            synthesizer.SpeakAsync(utterance);
            SetVoiceState(CompanionVoiceState.Responding);
        }
        catch
        {
            // Even the fallback failed — stay quiet rather than crash.
        }
    }

    public void ClearDetectedElementLocation()
    {
        DetectedElementScreenLocation = null;
        DetectedElementDisplayBounds = null;
        DetectedElementBubbleText = null;
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    // ── Point Tag Parsing ────────────────────────────────────────────

    public record PointingParseResult(
        string SpokenText,
        System.Windows.Point? Coordinate,
        string? ElementLabel,
        int? ScreenNumber);

    /// Parses a [POINT:x,y:label:screenN] or [POINT:none] tag from the end
    /// of Claude's response. Same regex as the original.
    public static PointingParseResult ParsePointingCoordinates(string responseText)
    {
        var pattern = new Regex(@"\[POINT:(?:none|(\d+)\s*,\s*(\d+)(?::([^\]:\s][^\]:]*?))?(?::screen(\d+))?)\]\s*$");
        var match = pattern.Match(responseText);

        if (!match.Success)
        {
            return new PointingParseResult(responseText, null, null, null);
        }

        string spokenText = responseText[..match.Index].Trim();

        if (!match.Groups[1].Success || !match.Groups[2].Success)
        {
            return new PointingParseResult(spokenText, null, "none", null);
        }

        double x = double.Parse(match.Groups[1].Value);
        double y = double.Parse(match.Groups[2].Value);
        string? elementLabel = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;
        int? screenNumber = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : null;

        return new PointingParseResult(spokenText, new System.Windows.Point(x, y), elementLabel, screenNumber);
    }

    // ── Onboarding ───────────────────────────────────────────────────

    /// Starts the onboarding sequence — dismisses the panel and shows the
    /// overlay for the first time so the welcome animation plays. The
    /// original streamed a Mux-hosted intro video here; that's Mac-app
    /// specific content, so the port goes welcome bubble → prompt bubble →
    /// pointing demo, keeping the same beats.
    public void TriggerOnboarding()
    {
        RequestDismissPanel();
        HasCompletedOnboarding = true;
        ClickyAnalytics.TrackOnboardingStarted();
        StartOnboardingMusic();

        OverlayWindowManager.HasShownOverlayBefore = false;
        OverlayWindowManager.ShowOverlay(this);
        IsOverlayVisible = true;
        NotifyStateChanged();
    }

    public void ReplayOnboarding()
    {
        RequestDismissPanel();
        ClickyAnalytics.TrackOnboardingReplayed();
        StartOnboardingMusic();
        OverlayWindowManager.HasShownOverlayBefore = false;
        OverlayWindowManager.ShowOverlay(this);
        IsOverlayVisible = true;
        NotifyStateChanged();
    }

    /// Called by the overlay when the "hey! i'm clicky" welcome bubble has
    /// finished. Streams the try-it prompt, then fires the pointing demo.
    public void OnWelcomeAnimationFinished()
    {
        DebugTrace.Log("welcome animation finished — streaming prompt, demo in 7s");
        StartOnboardingPromptStream();

        var demoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        demoTimer.Tick += (_, _) =>
        {
            demoTimer.Stop();
            ClickyAnalytics.TrackOnboardingDemoTriggered();
            PerformOnboardingDemoInteraction();
        };
        demoTimer.Start();
    }

    private void DismissOnboardingPromptIfShowing()
    {
        if (!ShowOnboardingPrompt)
        {
            return;
        }
        OnboardingPromptOpacity = 0.0;
        var dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        dismissTimer.Tick += (_, _) =>
        {
            dismissTimer.Stop();
            ShowOnboardingPrompt = false;
            OnboardingPromptText = "";
        };
        dismissTimer.Start();
    }

    private void StartOnboardingPromptStream()
    {
        const string message = "hold control + alt and introduce yourself";
        OnboardingPromptText = "";
        ShowOnboardingPrompt = true;
        OnboardingPromptOpacity = 1.0;

        int currentIndex = 0;
        var characterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        characterTimer.Tick += (_, _) =>
        {
            if (currentIndex >= message.Length)
            {
                characterTimer.Stop();
                // Auto-dismiss after 10 seconds
                var dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                dismissTimer.Tick += (_, _) =>
                {
                    dismissTimer.Stop();
                    DismissOnboardingPromptIfShowing();
                };
                dismissTimer.Start();
                return;
            }
            OnboardingPromptText += message[currentIndex];
            currentIndex++;
        };
        characterTimer.Start();
    }

    // ── Onboarding Music ─────────────────────────────────────────────

    private void StartOnboardingMusic()
    {
        StopOnboardingMusic();
        try
        {
            string musicPath = Path.Combine(AppContext.BaseDirectory, "assets", "ff.mp3");
            if (!File.Exists(musicPath))
            {
                System.Diagnostics.Debug.WriteLine("⚠️ Clicky: ff.mp3 not found");
                return;
            }

            onboardingMusicReader = new NAudio.Wave.Mp3FileReader(musicPath);
            onboardingMusicPlayer = new NAudio.Wave.WaveOutEvent();
            onboardingMusicPlayer.Init(onboardingMusicReader);
            onboardingMusicPlayer.Volume = 0.3f;
            onboardingMusicPlayer.Play();

            // After 1m 30s, fade the music out over 3s — same as the original.
            onboardingMusicFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            onboardingMusicFadeTimer.Tick += (_, _) =>
            {
                onboardingMusicFadeTimer?.Stop();
                FadeOutOnboardingMusic();
            };
            onboardingMusicFadeTimer.Start();
        }
        catch (Exception musicError)
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ Clicky: Failed to play onboarding music: {musicError.Message}");
        }
    }

    private void FadeOutOnboardingMusic()
    {
        var player = onboardingMusicPlayer;
        if (player == null)
        {
            return;
        }

        const int fadeSteps = 30;
        const double fadeDurationSeconds = 3.0;
        float volumeDecrement = player.Volume / fadeSteps;
        int stepsRemaining = fadeSteps;

        onboardingMusicFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(fadeDurationSeconds / fadeSteps) };
        onboardingMusicFadeTimer.Tick += (_, _) =>
        {
            stepsRemaining--;
            player.Volume = Math.Max(0, player.Volume - volumeDecrement);
            if (stepsRemaining <= 0)
            {
                onboardingMusicFadeTimer?.Stop();
                StopOnboardingMusic();
            }
        };
        onboardingMusicFadeTimer.Start();
    }

    private void StopOnboardingMusic()
    {
        onboardingMusicFadeTimer?.Stop();
        onboardingMusicFadeTimer = null;
        try
        {
            onboardingMusicPlayer?.Stop();
            onboardingMusicPlayer?.Dispose();
            onboardingMusicReader?.Dispose();
        }
        catch
        {
            // Teardown race with the audio thread — safe to ignore.
        }
        onboardingMusicPlayer = null;
        onboardingMusicReader = null;
    }

    // ── Onboarding Demo Interaction ──────────────────────────────────

    private const string OnboardingDemoSystemPrompt = """
        you're clicky, a small blue cursor buddy living on the user's screen. you're showing off during onboarding — look at their screen and find ONE specific, concrete thing to point at. pick something with a clear name or identity: a specific app icon (say its name), a specific word or phrase of text you can read, a specific filename, a specific button label, a specific tab title, a specific image you can describe. do NOT point at vague things like "a window" or "some text" — be specific about exactly what you see.

        make a short quirky 3-6 word observation about the specific thing you picked — something fun, playful, or curious that shows you actually read/recognized it. no emojis ever. NEVER quote or repeat text you see on screen — just react to it. keep it to 6 words max, no exceptions.

        CRITICAL COORDINATE RULE: you MUST only pick elements near the CENTER of the screen. your x coordinate must be between 20%-80% of the image width. your y coordinate must be between 20%-80% of the image height. do NOT pick anything in the top 20%, bottom 20%, left 20%, or right 20% of the screen. no title bar items, no taskbar icons, no sidebar items, no items near any edge. only things clearly in the middle area of the screen. if the only interesting things are near the edges, pick something boring in the center instead.

        respond with ONLY your short comment followed by the coordinate tag. nothing else. all lowercase.

        format: your comment [POINT:x,y:label]

        the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. origin (0,0) is top-left. x increases rightward, y increases downward.
        """;

    /// Captures a screenshot and asks Claude to find something interesting
    /// to point at, then triggers the buddy's flight animation. Runs in a
    /// throwaway Claude session with the demo prompt.
    public void PerformOnboardingDemoInteraction()
    {
        if (VoiceState != CompanionVoiceState.Idle && VoiceState != CompanionVoiceState.Responding)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var screenCaptures = CompanionScreenCaptureUtility.CaptureAllScreensAsJpeg();

                // Only send the cursor screen so Claude can't pick something
                // on a different monitor.
                var cursorScreenCapture = screenCaptures.FirstOrDefault(capture => capture.IsCursorScreen);
                if (cursorScreenCapture == null)
                {
                    System.Diagnostics.Debug.WriteLine("🎯 Onboarding demo: no cursor screen found");
                    return;
                }

                var labeledImages = new List<(byte[], string)>
                {
                    (cursorScreenCapture.ImageData,
                     cursorScreenCapture.Label + $" (image dimensions: {cursorScreenCapture.ScreenshotWidthInPixels}x{cursorScreenCapture.ScreenshotHeightInPixels} pixels)"),
                };

                using var demoBrainClient = new ClaudeCodeBrainClient(
                    OnboardingDemoSystemPrompt, SelectedModel, isEphemeralSession: true);

                string fullResponseText = await demoBrainClient.AnalyzeImagesAsync(
                    labeledImages,
                    "look around my screen and find something interesting to point at",
                    CancellationToken.None);

                var parseResult = ParsePointingCoordinates(fullResponseText);
                if (parseResult.Coordinate is not System.Windows.Point pointCoordinate)
                {
                    System.Diagnostics.Debug.WriteLine("🎯 Onboarding demo: no element to point at");
                    return;
                }

                var displayBounds = cursorScreenCapture.DisplayBoundsInPixels;
                double clampedX = Math.Clamp(pointCoordinate.X, 0, cursorScreenCapture.ScreenshotWidthInPixels);
                double clampedY = Math.Clamp(pointCoordinate.Y, 0, cursorScreenCapture.ScreenshotHeightInPixels);
                double displayLocalX = clampedX * ((double)displayBounds.Width / cursorScreenCapture.ScreenshotWidthInPixels);
                double displayLocalY = clampedY * ((double)displayBounds.Height / cursorScreenCapture.ScreenshotHeightInPixels);

                await uiDispatcher.InvokeAsync(() =>
                {
                    DetectedElementBubbleText = parseResult.SpokenText;
                    DetectedElementScreenLocation = new System.Drawing.Point(
                        displayBounds.X + (int)Math.Round(displayLocalX),
                        displayBounds.Y + (int)Math.Round(displayLocalY));
                    DetectedElementDisplayBounds = displayBounds;
                    DetectedElementChanged?.Invoke();
                });

                System.Diagnostics.Debug.WriteLine(
                    $"🎯 Onboarding demo: pointing at \"{parseResult.ElementLabel ?? "element"}\" — \"{parseResult.SpokenText}\"");
                DebugTrace.Log($"demo pointed at \"{parseResult.ElementLabel}\" saying \"{parseResult.SpokenText}\"");
            }
            catch (Exception demoError)
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ Onboarding demo error: {demoError.Message}");
                DebugTrace.Log($"demo ERROR: {demoError.Message}");
            }
        });
    }
}
