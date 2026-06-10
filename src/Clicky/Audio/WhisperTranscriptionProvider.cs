//
//  WhisperTranscriptionProvider.cs
//  Clicky for Windows
//
//  Local speech-to-text using whisper.cpp (via Whisper.net). This is the
//  free, offline replacement for the original's paid AssemblyAI streaming —
//  structurally it mirrors the original's OpenAI provider, which also
//  buffered push-to-talk audio locally and transcribed on key-release.
//
//  The model file (~75–488MB depending on size) downloads once on first run
//  to %APPDATA%\Clicky\models. Transcription only runs for the few seconds
//  after key-release; nothing heavy runs while idle.
//

using System.IO;
using System.Net.Http;
using Whisper.net;

namespace Clicky.Audio;

public class WhisperTranscriptionProvider : IBuddyTranscriptionProvider
{
    public string DisplayName => "local whisper";
    public bool IsConfigured => true;
    public string? UnavailableExplanation => null;

    /// Whisper expects 16kHz mono float PCM.
    public const int WhisperSampleRate = 16000;

    // The factory memory-maps the model file; building it is expensive, so
    // one factory is shared across all sessions for the app's lifetime.
    private static WhisperFactory? sharedWhisperFactory;
    private static readonly SemaphoreSlim FactoryInitializationLock = new(1, 1);

    // ── Model download state (observed by the panel UI) ─────────────

    public static event Action<string>? ModelDownloadStatusChanged;
    public static string ModelDownloadStatus { get; private set; } = "";
    public static bool IsModelReady => File.Exists(ResolveModelFilePath());

    private static string ResolveModelFilePath()
    {
        string modelSize = ClickySettings.Current.WhisperModel.ToLowerInvariant();
        if (modelSize != "tiny" && modelSize != "base" && modelSize != "small")
        {
            modelSize = "base";
        }
        return Path.Combine(ClickySettings.AppDataDirectory, "models", $"ggml-{modelSize}.bin");
    }

    private static string ResolveModelDownloadUrl()
    {
        string fileName = Path.GetFileName(ResolveModelFilePath());
        return $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{fileName}";
    }

    /// Downloads the model if missing and builds the shared factory.
    /// Safe to call repeatedly; only the first call does work.
    public static async Task<WhisperFactory> EnsureFactoryReadyAsync()
    {
        if (sharedWhisperFactory != null)
        {
            return sharedWhisperFactory;
        }

        await FactoryInitializationLock.WaitAsync();
        try
        {
            if (sharedWhisperFactory != null)
            {
                return sharedWhisperFactory;
            }

            string modelFilePath = ResolveModelFilePath();

            if (!File.Exists(modelFilePath))
            {
                await DownloadModelAsync(modelFilePath);
            }

            UpdateDownloadStatus("");
            sharedWhisperFactory = WhisperFactory.FromPath(modelFilePath);
            return sharedWhisperFactory;
        }
        finally
        {
            FactoryInitializationLock.Release();
        }
    }

    private static async Task DownloadModelAsync(string modelFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(modelFilePath)!);
        string downloadUrl = ResolveModelDownloadUrl();
        string temporaryFilePath = modelFilePath + ".downloading";

        UpdateDownloadStatus("downloading speech model… 0%");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1;
        long downloadedBytes = 0;
        int lastReportedPercent = -1;

        await using (var downloadStream = await response.Content.ReadAsStreamAsync())
        await using (var fileStream = File.Create(temporaryFilePath))
        {
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await downloadStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                downloadedBytes += bytesRead;

                if (totalBytes > 0)
                {
                    int percent = (int)(downloadedBytes * 100 / totalBytes);
                    if (percent != lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        UpdateDownloadStatus($"downloading speech model… {percent}%");
                    }
                }
            }
        }

        File.Move(temporaryFilePath, modelFilePath, overwrite: true);
        UpdateDownloadStatus("");
        System.Diagnostics.Debug.WriteLine($"🎙️ Whisper model ready at {modelFilePath}");
    }

    private static void UpdateDownloadStatus(string status)
    {
        ModelDownloadStatus = status;
        ModelDownloadStatusChanged?.Invoke(status);
    }

    // ── Provider protocol ────────────────────────────────────────────

    public async Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError)
    {
        // Kick off model download/load in the background if it isn't ready
        // yet — the session can begin buffering audio immediately, and the
        // factory will be awaited again at transcription time.
        _ = EnsureFactoryReadyAsync();
        await Task.CompletedTask;
        return new WhisperStreamingSession(keyterms, onFinalTranscriptReady, onError);
    }

    private sealed class WhisperStreamingSession : IBuddyStreamingTranscriptionSession
    {
        // Whisper produces no live partials in this buffer-then-transcribe
        // design, so the fallback timer is generous — it only exists to
        // unstick the UI if transcription hangs entirely.
        public double FinalTranscriptFallbackDelaySeconds => 30.0;

        private readonly List<float> bufferedSamples = new(capacity: 16000 * 30);
        private int bufferedSampleRate = WhisperSampleRate;
        private readonly IReadOnlyList<string> keyterms;
        private readonly Action<string> onFinalTranscriptReady;
        private readonly Action<Exception> onError;
        private volatile bool isCancelled;

        /// Safety cap so a stuck key can't buffer unbounded audio (~2 min).
        private const int MaxBufferedSeconds = 120;

        public WhisperStreamingSession(
            IReadOnlyList<string> keyterms,
            Action<string> onFinalTranscriptReady,
            Action<Exception> onError)
        {
            this.keyterms = keyterms;
            this.onFinalTranscriptReady = onFinalTranscriptReady;
            this.onError = onError;
        }

        public void AppendAudioSamples(float[] monoSamples, int sampleRate)
        {
            if (isCancelled)
            {
                return;
            }

            bufferedSampleRate = sampleRate;
            if (bufferedSamples.Count < sampleRate * MaxBufferedSeconds)
            {
                bufferedSamples.AddRange(monoSamples);
            }
        }

        public void RequestFinalTranscript()
        {
            float[] utteranceSamples = bufferedSamples.ToArray();
            int utteranceSampleRate = bufferedSampleRate;

            Task.Run(async () =>
            {
                try
                {
                    if (isCancelled)
                    {
                        return;
                    }

                    var whisperFactory = await EnsureFactoryReadyAsync();

                    float[] whisperReadySamples = BuddyAudioConversionSupport.ResampleMono(
                        utteranceSamples, utteranceSampleRate, WhisperSampleRate);

                    // Skip near-silent utterances (quick tap of the hotkey) —
                    // whisper hallucinates text on silence.
                    if (whisperReadySamples.Length < WhisperSampleRate / 4)
                    {
                        if (!isCancelled) onFinalTranscriptReady("");
                        return;
                    }

                    var processorBuilder = whisperFactory.CreateBuilder()
                        .WithLanguage("auto")
                        // Keyterms act like the original's AssemblyAI keyterms —
                        // they bias recognition toward app/tech vocabulary.
                        .WithPrompt(string.Join(", ", keyterms))
                        .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

                    var transcriptParts = new List<string>();
                    await using (var processor = processorBuilder.Build())
                    {
                        await foreach (var segment in processor.ProcessAsync(whisperReadySamples))
                        {
                            transcriptParts.Add(segment.Text);
                        }
                    }

                    if (isCancelled)
                    {
                        return;
                    }

                    string transcript = string.Join(" ", transcriptParts)
                        .Replace("  ", " ")
                        .Trim();
                    onFinalTranscriptReady(transcript);
                }
                catch (Exception transcriptionError)
                {
                    if (!isCancelled)
                    {
                        onError(transcriptionError);
                    }
                }
            });
        }

        public void Cancel()
        {
            isCancelled = true;
        }
    }
}
