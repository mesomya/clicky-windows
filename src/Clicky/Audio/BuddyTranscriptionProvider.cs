//
//  BuddyTranscriptionProvider.cs
//  Clicky for Windows
//
//  Shared protocol surface for voice transcription backends — a direct port
//  of the original's pluggable provider layer. The original shipped
//  AssemblyAI (paid streaming), OpenAI (paid upload), and Apple Speech
//  (free local fallback). This port ships local Whisper (free, default)
//  and Windows Speech (free, built-in fallback) behind the same protocol,
//  so swapping or adding providers stays a one-file change.
//

namespace Clicky.Audio;

public interface IBuddyStreamingTranscriptionSession
{
    /// How long the dictation manager waits after key-release before giving
    /// up on the provider and submitting whatever partial transcript exists.
    double FinalTranscriptFallbackDelaySeconds { get; }

    /// Receives mono float audio chunks at the given sample rate while the
    /// user is holding the push-to-talk key. Buffer-based providers (Whisper)
    /// accumulate these; engine-based providers (Windows Speech) that own
    /// their own microphone stream can ignore them.
    void AppendAudioSamples(float[] monoSamples, int sampleRate);

    /// Called on key-release — the provider should finalize and deliver the
    /// transcript via its onFinalTranscriptReady callback.
    void RequestFinalTranscript();

    void Cancel();
}

public interface IBuddyTranscriptionProvider
{
    string DisplayName { get; }
    bool IsConfigured { get; }
    string? UnavailableExplanation { get; }

    Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError);
}

public static class BuddyTranscriptionProviderFactory
{
    /// Resolves the provider from settings: local Whisper by default, with
    /// Windows Speech as the explicit alternative — mirroring the original's
    /// Info.plist-driven provider resolution with Apple Speech fallback.
    public static IBuddyTranscriptionProvider MakeDefaultProvider()
    {
        var provider = ResolveProvider();
        System.Diagnostics.Debug.WriteLine($"🎙️ Transcription: using {provider.DisplayName}");
        return provider;
    }

    private static IBuddyTranscriptionProvider ResolveProvider()
    {
        string preferredProvider = ClickySettings.Current.VoiceTranscriptionProvider.ToLowerInvariant();

        if (preferredProvider == "windows")
        {
            return new WindowsSpeechTranscriptionProvider();
        }

        var whisperProvider = new WhisperTranscriptionProvider();
        if (whisperProvider.IsConfigured)
        {
            return whisperProvider;
        }

        System.Diagnostics.Debug.WriteLine("⚠️ Transcription: Whisper preferred but unavailable, falling back to Windows Speech");
        return new WindowsSpeechTranscriptionProvider();
    }
}
