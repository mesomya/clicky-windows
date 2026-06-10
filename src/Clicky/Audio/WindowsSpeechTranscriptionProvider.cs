//
//  WindowsSpeechTranscriptionProvider.cs
//  Clicky for Windows
//
//  Local fallback transcription provider backed by Windows' built-in speech
//  recognition (System.Speech) — the direct analog of the original's
//  AppleSpeechTranscriptionProvider. Lower accuracy than Whisper but
//  instant, fully offline, and zero-setup.
//
//  The recognition engine owns its own microphone stream, so this session
//  ignores the audio chunks the dictation manager pushes (those still drive
//  the waveform). Two shared-mode captures on the same mic are fine.
//

using System.Globalization;
using System.Speech.Recognition;

namespace Clicky.Audio;

public class WindowsSpeechTranscriptionProvider : IBuddyTranscriptionProvider
{
    public string DisplayName => "windows speech";
    public string? UnavailableExplanation { get; private set; }

    public bool IsConfigured
    {
        get
        {
            try
            {
                return SpeechRecognitionEngine.InstalledRecognizers().Count > 0;
            }
            catch
            {
                UnavailableExplanation = "no speech recognizer installed";
                return false;
            }
        }
    }

    public Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError)
    {
        var session = new WindowsSpeechStreamingSession(onTranscriptUpdate, onFinalTranscriptReady, onError);
        session.Start();
        return Task.FromResult<IBuddyStreamingTranscriptionSession>(session);
    }

    private sealed class WindowsSpeechStreamingSession : IBuddyStreamingTranscriptionSession
    {
        // Same fallback window the original used for Apple Speech.
        public double FinalTranscriptFallbackDelaySeconds => 2.4;

        private readonly SpeechRecognitionEngine recognitionEngine;
        private readonly Action<string> onTranscriptUpdate;
        private readonly Action<string> onFinalTranscriptReady;
        private readonly Action<Exception> onError;

        /// Finalized phrases accumulated across the session, joined with the
        /// live hypothesis to form the running transcript.
        private readonly List<string> recognizedPhrases = new();
        private string currentHypothesisText = "";
        private volatile bool isFinalizing;
        private volatile bool isCancelled;

        public WindowsSpeechStreamingSession(
            Action<string> onTranscriptUpdate,
            Action<string> onFinalTranscriptReady,
            Action<Exception> onError)
        {
            this.onTranscriptUpdate = onTranscriptUpdate;
            this.onFinalTranscriptReady = onFinalTranscriptReady;
            this.onError = onError;

            recognitionEngine = CreateRecognitionEngine();
            recognitionEngine.LoadGrammar(new DictationGrammar());
            recognitionEngine.SpeechHypothesized += HandleSpeechHypothesized;
            recognitionEngine.SpeechRecognized += HandleSpeechRecognized;
            recognitionEngine.RecognizeCompleted += HandleRecognizeCompleted;
        }

        private static SpeechRecognitionEngine CreateRecognitionEngine()
        {
            try
            {
                return new SpeechRecognitionEngine(CultureInfo.CurrentCulture);
            }
            catch (ArgumentException)
            {
                // No recognizer for the current culture — fall back to
                // whatever recognizer is installed.
                return new SpeechRecognitionEngine();
            }
        }

        public void Start()
        {
            try
            {
                recognitionEngine.SetInputToDefaultAudioDevice();
                recognitionEngine.RecognizeAsync(RecognizeMode.Multiple);
            }
            catch (Exception startError)
            {
                onError(startError);
            }
        }

        public void AppendAudioSamples(float[] monoSamples, int sampleRate)
        {
            // The engine captures the microphone itself — nothing to do here.
        }

        public void RequestFinalTranscript()
        {
            isFinalizing = true;
            try
            {
                recognitionEngine.RecognizeAsyncStop();
            }
            catch (Exception stopError)
            {
                onError(stopError);
            }
        }

        public void Cancel()
        {
            isCancelled = true;
            try
            {
                recognitionEngine.RecognizeAsyncCancel();
                recognitionEngine.Dispose();
            }
            catch
            {
                // Disposal races with in-flight recognition callbacks; safe to ignore.
            }
        }

        private string ComposeRunningTranscript()
        {
            var parts = new List<string>(recognizedPhrases);
            if (!string.IsNullOrWhiteSpace(currentHypothesisText))
            {
                parts.Add(currentHypothesisText);
            }
            return string.Join(" ", parts).Trim();
        }

        private void HandleSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs e)
        {
            if (isCancelled) return;
            currentHypothesisText = e.Result.Text;
            onTranscriptUpdate(ComposeRunningTranscript());
        }

        private void HandleSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            if (isCancelled) return;
            recognizedPhrases.Add(e.Result.Text);
            currentHypothesisText = "";
            onTranscriptUpdate(ComposeRunningTranscript());
        }

        private void HandleRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
        {
            if (isCancelled || !isFinalizing) return;
            onFinalTranscriptReady(ComposeRunningTranscript());
            try
            {
                recognitionEngine.Dispose();
            }
            catch
            {
                // Safe to ignore — the session is over either way.
            }
        }
    }
}
