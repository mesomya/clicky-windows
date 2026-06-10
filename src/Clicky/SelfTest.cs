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
