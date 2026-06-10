//
//  EdgeTtsClient.cs
//  Clicky for Windows
//
//  Text-to-speech via Microsoft Edge's read-aloud neural voices — the free
//  replacement for the original's paid ElevenLabs. Same client surface as
//  ElevenLabsTTSClient (SpeakTextAsync / IsPlaying / StopPlayback) so the
//  companion pipeline is unchanged: SpeakTextAsync returns the moment audio
//  starts playing, and the manager polls IsPlaying for transient-hide logic.
//
//  Speaks over the same websocket endpoint Edge's "Read aloud" feature uses,
//  which requires a clock-derived Sec-MS-GEC token (ported from the
//  community edge-tts implementation).
//

using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;

namespace Clicky.Tts;

public sealed class EdgeTtsClient
{
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    // The read-aloud service rejects stale client versions with a 403 at the
    // websocket handshake, so this must track the current edge-tts value.
    private const string EdgeVersion = "1-143.0.3650.75";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";

    private IWavePlayer? audioPlayer;
    private Mp3FileReader? audioReader;

    /// Sends text to Edge TTS and starts playing the resulting audio.
    /// Returns once playback has begun (matching the original client's
    /// contract). Throws on network or decoding errors.
    public async Task SpeakTextAsync(string text, CancellationToken cancellationToken)
    {
        byte[] mp3Audio = await SynthesizeToMp3Async(text, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        StopPlayback();

        audioReader = new Mp3FileReader(new MemoryStream(mp3Audio));
        var player = new WaveOutEvent();
        player.Init(audioReader);
        player.Play();
        audioPlayer = player;

        System.Diagnostics.Debug.WriteLine($"🔊 Edge TTS: playing {mp3Audio.Length / 1024}KB audio");
    }

    public bool IsPlaying => audioPlayer?.PlaybackState == PlaybackState.Playing;

    public void StopPlayback()
    {
        try
        {
            audioPlayer?.Stop();
            audioPlayer?.Dispose();
            audioReader?.Dispose();
        }
        catch
        {
            // Player teardown races with the playback thread; safe to ignore.
        }
        audioPlayer = null;
        audioReader = null;
    }

    // ── Synthesis ────────────────────────────────────────────────────

    private static async Task<byte[]> SynthesizeToMp3Async(string text, CancellationToken cancellationToken)
    {
        string connectionId = Guid.NewGuid().ToString("N");
        string requestUrl =
            "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1" +
            $"?TrustedClientToken={TrustedClientToken}" +
            $"&Sec-MS-GEC={ComputeSecMsGecToken()}" +
            $"&Sec-MS-GEC-Version={EdgeVersion}" +
            $"&ConnectionId={connectionId}";

        using var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("Pragma", "no-cache");
        webSocket.Options.SetRequestHeader("Cache-Control", "no-cache");
        webSocket.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        webSocket.Options.SetRequestHeader("User-Agent", UserAgent);
        webSocket.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");

        await webSocket.ConnectAsync(new Uri(requestUrl), cancellationToken);

        string timestamp = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'");

        string speechConfigMessage =
            $"X-Timestamp:{timestamp}\r\n" +
            "Content-Type:application/json; charset=utf-8\r\n" +
            "Path:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{" +
            "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
            "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";

        await SendTextMessageAsync(webSocket, speechConfigMessage, cancellationToken);

        string voiceName = ClickySettings.Current.TtsVoice;
        string ssml =
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
            $"<voice name='{voiceName}'>" +
            "<prosody pitch='+0Hz' rate='+8%' volume='+0%'>" +
            EscapeForXml(text) +
            "</prosody></voice></speak>";

        string ssmlMessage =
            $"X-RequestId:{Guid.NewGuid():N}\r\n" +
            "Content-Type:application/ssml+xml\r\n" +
            $"X-Timestamp:{timestamp}Z\r\n" +
            "Path:ssml\r\n\r\n" +
            ssml;

        await SendTextMessageAsync(webSocket, ssmlMessage, cancellationToken);

        // Collect binary audio frames until the service signals turn.end.
        using var audioStream = new MemoryStream();
        var receiveBuffer = new byte[65536];
        var frameAssembly = new MemoryStream();

        while (true)
        {
            frameAssembly.SetLength(0);
            WebSocketReceiveResult receiveResult;
            do
            {
                receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cancellationToken);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("edge tts connection closed before synthesis finished");
                }
                frameAssembly.Write(receiveBuffer, 0, receiveResult.Count);
            }
            while (!receiveResult.EndOfMessage);

            byte[] frame = frameAssembly.ToArray();

            if (receiveResult.MessageType == WebSocketMessageType.Text)
            {
                string textFrame = Encoding.UTF8.GetString(frame);
                if (textFrame.Contains("Path:turn.end"))
                {
                    break;
                }
                continue;
            }

            // Binary frame layout: 2-byte big-endian header length, the
            // ASCII header block, then the raw MP3 payload.
            if (frame.Length < 2)
            {
                continue;
            }
            int headerLength = (frame[0] << 8) | frame[1];
            if (frame.Length < 2 + headerLength)
            {
                continue;
            }
            string header = Encoding.ASCII.GetString(frame, 2, headerLength);
            if (header.Contains("Path:audio"))
            {
                audioStream.Write(frame, 2 + headerLength, frame.Length - 2 - headerLength);
            }
        }

        try
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch
        {
            // Server often closes first — irrelevant, we have the audio.
        }

        if (audioStream.Length == 0)
        {
            throw new InvalidOperationException("edge tts returned no audio");
        }

        return audioStream.ToArray();
    }

    private static async Task SendTextMessageAsync(ClientWebSocket webSocket, string message, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message);
        await webSocket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// Clock-derived access token: the Windows file time rounded down to the
    /// nearest 5 minutes, concatenated with the trusted client token, SHA-256
    /// hashed, uppercase hex. Mirrors the community edge-tts implementation.
    private static string ComputeSecMsGecToken()
    {
        long windowsFileTimeTicks = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600L) * 10_000_000L;
        windowsFileTimeTicks -= windowsFileTimeTicks % 3_000_000_000L;

        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes($"{windowsFileTimeTicks}{TrustedClientToken}"));
        return Convert.ToHexString(hash);
    }

    private static string EscapeForXml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("'", "&apos;")
            .Replace("\"", "&quot;");
    }
}
