//
//  SelfTest.cs
//  Clicky for Windows
//
//  Headless smoke tests for the three risky integrations — Edge TTS, the
//  Claude Code CLI brain, and local Whisper — runnable without touching
//  the UI:
//
//    Clicky.exe --selftest point          [POINT] tag parsing
//    Clicky.exe --selftest tts            Edge neural voice synthesis + playback
//    Clicky.exe --selftest brain          screenshot → Claude → response
//    Clicky.exe --selftest stt            SAPI-generated speech → Whisper transcript
//
//  Results go to stdout and %TEMP%\clicky-selftest.log (a WinExe app's
//  console output is invisible unless redirected, so the log file is the
//  reliable channel).
//

using System.IO;
using Clicky.Audio;
using Clicky.Brain;
using Clicky.Capture;
using Clicky.Tts;
using NAudio.Wave;

namespace Clicky;

public static class SelfTest
{
    private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "clicky-selftest.log");

    public static int Run(string testName)
    {
        File.WriteAllText(LogFilePath, $"== clicky selftest: {testName} ==\n");
        try
        {
            switch (testName)
            {
                case "point":
                    RunPointParsingTest();
                    break;
                case "tts":
                    RunTtsTest().GetAwaiter().GetResult();
                    break;
                case "brain":
                    RunBrainTest().GetAwaiter().GetResult();
                    break;
                case "stt":
                    RunSttTest().GetAwaiter().GetResult();
                    break;
                case "diagnose":
                    RunLiveDiagnostic().GetAwaiter().GetResult();
                    break;
                case "audioroute":
                    RunAudioRouteTest().GetAwaiter().GetResult();
                    break;
                default:
                    Log($"unknown selftest '{testName}'");
                    return 2;
            }
            Log("RESULT: PASS");
            return 0;
        }
        catch (Exception testError)
        {
            Log($"RESULT: FAIL — {testError}");
            return 1;
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        File.AppendAllText(LogFilePath, message + "\n");
    }

    // ── Audio routing proof (does sound reach the default device?) ───

    /// Plays a spoken phrase while simultaneously recording the default
    /// output device's own loopback. If the loopback captures sound, the
    /// audio physically reached that device — proving (without a human)
    /// whether TTS lands on the speaker the user is actually listening on.
    private static async Task RunAudioRouteTest()
    {
        using var deviceEnumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var renderDevice = deviceEnumerator.GetDefaultAudioEndpoint(
            NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        Log($"default output endpoint: {renderDevice.FriendlyName}");

        float loopbackPeak = 0;
        long loopbackSamples = 0;
        using var loopbackCapture = new NAudio.Wave.WasapiLoopbackCapture(renderDevice);
        var loopbackFormat = loopbackCapture.WaveFormat;
        loopbackCapture.DataAvailable += (_, dataEvent) =>
        {
            float[] mono = Audio.BuddyAudioConversionSupport.ConvertCaptureBufferToMonoFloat(
                dataEvent.Buffer, dataEvent.BytesRecorded, loopbackFormat);
            loopbackSamples += mono.Length;
            foreach (float sample in mono)
            {
                float magnitude = Math.Abs(sample);
                if (magnitude > loopbackPeak) loopbackPeak = magnitude;
            }
        };

        loopbackCapture.StartRecording();
        Log("playing a TTS phrase while recording that device's loopback…");

        var ttsClient = new Tts.EdgeTtsClient();
        await ttsClient.SpeakTextAsync(
            "testing audio output routing. one, two, three.", CancellationToken.None);
        while (ttsClient.IsPlaying)
        {
            await Task.Delay(200);
        }
        await Task.Delay(400);
        loopbackCapture.StopRecording();

        Log($"loopback captured {loopbackSamples} samples, PEAK {loopbackPeak:F4}");
        if (loopbackPeak > 0.002)
        {
            Log($"VERDICT: audio IS reaching '{renderDevice.FriendlyName}' — what you're listening on. ✓");
        }
        else
        {
            Log("VERDICT: NO audio on the default output device — TTS is landing on a DIFFERENT device.");
        }
    }

    // ── Live end-to-end diagnostic (real mic + real speakers) ────────

    /// Captures the actual microphone for a few seconds, measures the audio
    /// level (proves the mic delivers sound), transcribes it with Whisper,
    /// lists the default audio devices, and plays a spoken test phrase
    /// (proves audio output). This is the "is everything actually working
    /// on my hardware" check the whole pipeline depends on.
    private static async Task RunLiveDiagnostic()
    {
        const int captureSeconds = 6;

        // 1) Which devices are we using?
        using (var deviceEnumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
        {
            try
            {
                // Role.Console = the "Default device" Clicky actually captures
                // from (NAudio's WasapiCapture uses this role), NOT the
                // separate "Default communications device".
                var defaultMic = deviceEnumerator.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console);
                Log($"microphone Clicky will use: {defaultMic.FriendlyName}");
            }
            catch (Exception micDeviceError) { Log($"NO default microphone: {micDeviceError.Message}"); }

            try
            {
                var defaultSpeaker = deviceEnumerator.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                Log($"default speaker: {defaultSpeaker.FriendlyName}");
            }
            catch (Exception speakerDeviceError) { Log($"NO default speaker: {speakerDeviceError.Message}"); }
        }

        // 2) Capture the live microphone and measure the level.
        Log($"recording the microphone for {captureSeconds}s — SPEAK NOW (say: testing one two three)…");
        var capturedSamples = new List<float>();
        int capturedSampleRate = 16000;
        float peakLevel = 0;
        var captureFinished = new TaskCompletionSource<bool>();

        using (var microphoneCapture = new NAudio.CoreAudioApi.WasapiCapture())
        {
            var captureFormat = microphoneCapture.WaveFormat;
            capturedSampleRate = captureFormat.SampleRate;
            Log($"capture format: {captureFormat.SampleRate}Hz, {captureFormat.Channels}ch, {captureFormat.BitsPerSample}bit {captureFormat.Encoding}");

            microphoneCapture.DataAvailable += (_, dataEvent) =>
            {
                float[] mono = Audio.BuddyAudioConversionSupport.ConvertCaptureBufferToMonoFloat(
                    dataEvent.Buffer, dataEvent.BytesRecorded, captureFormat);
                capturedSamples.AddRange(mono);
                foreach (float sample in mono)
                {
                    float magnitude = Math.Abs(sample);
                    if (magnitude > peakLevel) peakLevel = magnitude;
                }
            };
            microphoneCapture.RecordingStopped += (_, _) => captureFinished.TrySetResult(true);

            try
            {
                microphoneCapture.StartRecording();
            }
            catch (Exception captureStartError)
            {
                throw new InvalidOperationException(
                    $"microphone could not start — likely blocked in Settings > Privacy > Microphone, or no mic. ({captureStartError.Message})");
            }

            await Task.Delay(captureSeconds * 1000);
            microphoneCapture.StopRecording();
            await captureFinished.Task;
        }

        float rmsLevel = Audio.BuddyAudioConversionSupport.ComputeRootMeanSquare(capturedSamples.ToArray());
        Log($"captured {capturedSamples.Count} samples ({capturedSamples.Count / (double)capturedSampleRate:F1}s)");
        Log($"mic PEAK level: {peakLevel:F4}  RMS level: {rmsLevel:F4}");
        if (peakLevel < 0.0005)
        {
            Log("VERDICT: microphone delivered (near) SILENCE — the mic is muted, blocked, or the wrong device is default.");
        }
        else if (peakLevel < 0.02)
        {
            Log("VERDICT: microphone is alive but very quiet — only faint/ambient sound captured.");
        }
        else
        {
            Log("VERDICT: microphone is capturing real sound. ✓");
        }

        // 3) Transcribe what was captured (proves live STT on real mic audio).
        string liveTranscript = "";
        try
        {
            var whisperFactory = await Audio.WhisperTranscriptionProvider.EnsureFactoryReadyAsync();
            float[] whisperSamples = Audio.BuddyAudioConversionSupport.ResampleMono(
                capturedSamples.ToArray(), capturedSampleRate, Audio.WhisperTranscriptionProvider.WhisperSampleRate);
            var transcriptParts = new List<string>();
            await using (var processor = whisperFactory.CreateBuilder().WithLanguage("auto").Build())
            {
                await foreach (var segment in processor.ProcessAsync(whisperSamples))
                {
                    transcriptParts.Add(segment.Text);
                }
            }
            liveTranscript = string.Join(" ", transcriptParts).Trim();
            Log($"Whisper heard: \"{liveTranscript}\"");
        }
        catch (Exception transcribeError)
        {
            Log($"Whisper FAILED on the captured audio: {transcribeError.Message}");
        }

        // 4) THE FULL LOOP: send what you said (plus a screenshot) to Claude
        //    and speak the real answer — exactly what pressing Ctrl+Alt does.
        var ttsClient = new Tts.EdgeTtsClient();
        try
        {
            string toSpeak;
            if (!string.IsNullOrWhiteSpace(liveTranscript))
            {
                Log("sending what you said + a screenshot to Claude…");
                var captures = Capture.CompanionScreenCaptureUtility.CaptureAllScreensAsJpeg();
                var labeledImages = captures
                    .Select(c => (c.ImageData, c.Label + $" (image dimensions: {c.ScreenshotWidthInPixels}x{c.ScreenshotHeightInPixels} pixels)"))
                    .ToList();
                using var brain = new Brain.ClaudeCodeBrainClient(
                    "you're clicky, a friendly screen buddy. answer in one or two short spoken sentences. no markdown.",
                    ClickySettings.Current.SelectedModel, isEphemeralSession: true);
                var started = DateTime.UtcNow;
                string answer = await brain.AnalyzeImagesAsync(labeledImages, liveTranscript, CancellationToken.None);
                Log($"Claude answered ({(DateTime.UtcNow - started).TotalSeconds:F1}s): \"{answer}\"");
                toSpeak = CompanionManager.ParsePointingCoordinates(answer).SpokenText;
                if (string.IsNullOrWhiteSpace(toSpeak)) toSpeak = "i heard you, but i don't have anything to say about that.";
            }
            else
            {
                toSpeak = "this is clicky. if you can hear this, your speakers are working. i did not catch any speech though.";
            }

            Log("speaking the answer now — LISTEN…");
            await ttsClient.SpeakTextAsync(toSpeak, CancellationToken.None);
            while (ttsClient.IsPlaying)
            {
                await Task.Delay(200);
            }
            Log("finished speaking (did you hear it?).");
        }
        catch (Exception loopError)
        {
            Log($"full-loop step FAILED: {loopError.Message}");
        }
    }

    // ── [POINT] parsing ──────────────────────────────────────────────

    private static void RunPointParsingTest()
    {
        var simpleCase = CompanionManager.ParsePointingCoordinates(
            "click the save button up top. [POINT:1100,42:save button]");
        Assert(simpleCase.Coordinate?.X == 1100 && simpleCase.Coordinate?.Y == 42, "simple coordinate");
        Assert(simpleCase.ElementLabel == "save button", "simple label");
        Assert(simpleCase.SpokenText == "click the save button up top.", "simple spoken text");
        Assert(simpleCase.ScreenNumber == null, "simple screen number");

        var screenCase = CompanionManager.ParsePointingCoordinates(
            "that's on your other monitor. [POINT:400,300:terminal:screen2]");
        Assert(screenCase.ScreenNumber == 2, "screen number parsed");
        Assert(screenCase.ElementLabel == "terminal", "screen case label");

        var noneCase = CompanionManager.ParsePointingCoordinates(
            "html is the skeleton of every web page. [POINT:none]");
        Assert(noneCase.Coordinate == null, "none case has no coordinate");
        Assert(noneCase.SpokenText == "html is the skeleton of every web page.", "none spoken text");

        var missingCase = CompanionManager.ParsePointingCoordinates("no tag here at all");
        Assert(missingCase.Coordinate == null && missingCase.SpokenText == "no tag here at all", "missing tag");

        Log("all point-parsing assertions passed");
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"assertion failed: {label}");
        }
        Log($"  ok: {label}");
    }

    // ── Edge TTS ─────────────────────────────────────────────────────

    private static async Task RunTtsTest()
    {
        var ttsClient = new EdgeTtsClient();
        Log("synthesizing with edge neural voice…");
        await ttsClient.SpeakTextAsync("hey! i'm clicky, now living on windows.", CancellationToken.None);
        Log("playback started; waiting for it to finish…");
        while (ttsClient.IsPlaying)
        {
            await Task.Delay(200);
        }
        Log("playback finished");
    }

    // ── Claude Code brain ────────────────────────────────────────────

    private static async Task RunBrainTest()
    {
        Log($"claude cli: {ClaudeCodeBrainClient.ResolveClaudeCliPath() ?? "NOT FOUND"}");

        Log("capturing screens…");
        var captures = CompanionScreenCaptureUtility.CaptureAllScreensAsJpeg();
        foreach (var capture in captures)
        {
            Log($"  captured: {capture.Label} — {capture.ScreenshotWidthInPixels}x{capture.ScreenshotHeightInPixels}, {capture.ImageData.Length / 1024}KB");
        }

        var labeledImages = captures
            .Select(capture => (
                capture.ImageData,
                capture.Label + $" (image dimensions: {capture.ScreenshotWidthInPixels}x{capture.ScreenshotHeightInPixels} pixels)"))
            .ToList();

        using var brainClient = new ClaudeCodeBrainClient(
            "you're clicky, a tiny cursor buddy. reply in one short lowercase sentence describing the most prominent thing you see, then append [POINT:x,y:label] pointing at it using the screenshot's pixel coordinate space.",
            ClickySettings.Current.SelectedModel,
            isEphemeralSession: true);

        Log("sending to claude…");
        var startedAt = DateTime.UtcNow;
        string response = await brainClient.AnalyzeImagesAsync(
            labeledImages, "what do you see on my screen?", CancellationToken.None);
        double elapsedSeconds = (DateTime.UtcNow - startedAt).TotalSeconds;

        Log($"response ({elapsedSeconds:F1}s): {response}");

        var parsed = CompanionManager.ParsePointingCoordinates(response);
        Log($"spoken: {parsed.SpokenText}");
        Log($"point: {(parsed.Coordinate != null ? $"({parsed.Coordinate.Value.X},{parsed.Coordinate.Value.Y}) '{parsed.ElementLabel}'" : "none")}");

        if (string.IsNullOrWhiteSpace(response))
        {
            throw new InvalidOperationException("empty response from claude");
        }
    }

    // ── Whisper speech-to-text ───────────────────────────────────────

    /// Validates Whisper end-to-end without needing a human at the mic:
    /// Windows' offline SAPI voice speaks a known phrase into a WAV file,
    /// and Whisper must transcribe it back.
    private static async Task RunSttTest()
    {
        const string spokenPhrase = "hello clicky, can you see my screen";
        string wavPath = Path.Combine(Path.GetTempPath(), "clicky-selftest-speech.wav");

        Log("generating reference speech with the offline windows voice…");
        using (var synthesizer = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            synthesizer.SetOutputToWaveFile(wavPath, new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            synthesizer.Speak(spokenPhrase);
        }

        Log("loading wav…");
        float[] samples;
        await using (var reader = new NAudio.Wave.WaveFileReader(wavPath))
        {
            var sampleList = new List<float>();
            var sampleProvider = reader.ToSampleProvider();
            var buffer = new float[16000];
            int read;
            while ((read = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    sampleList.Add(buffer[i]);
                }
            }
            samples = sampleList.ToArray();
        }
        Log($"wav: {samples.Length} samples ({samples.Length / 16000.0:F1}s)");

        Log("ensuring whisper model is ready (downloads ~150MB on first run)…");
        WhisperTranscriptionProvider.ModelDownloadStatusChanged += status =>
        {
            if (!string.IsNullOrEmpty(status) && status.Contains("0%"))
            {
                Log($"  {status}");
            }
        };
        var factory = await WhisperTranscriptionProvider.EnsureFactoryReadyAsync();

        Log("transcribing…");
        var startedAt = DateTime.UtcNow;
        var transcriptParts = new List<string>();
        await using (var processor = factory.CreateBuilder().WithLanguage("auto").Build())
        {
            await foreach (var segment in processor.ProcessAsync(samples))
            {
                transcriptParts.Add(segment.Text);
            }
        }
        double elapsedSeconds = (DateTime.UtcNow - startedAt).TotalSeconds;
        string transcript = string.Join(" ", transcriptParts).Trim();

        Log($"transcript ({elapsedSeconds:F1}s): {transcript}");

        // Loose match — the SAPI voice is robotic, so allow partial hits.
        string normalized = transcript.ToLowerInvariant();
        if (!normalized.Contains("clicky") && !normalized.Contains("screen") && !normalized.Contains("hello"))
        {
            throw new InvalidOperationException($"transcript doesn't resemble the spoken phrase: '{transcript}'");
        }
    }
}
