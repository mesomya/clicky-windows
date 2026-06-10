//
//  BuddyAudioConversionSupport.cs
//  Clicky for Windows
//
//  Shared audio conversion helpers for voice transcription providers —
//  ports the role of the original's BuddyPCM16AudioConverter. Converts
//  whatever format the microphone delivers (typically 48kHz stereo float
//  via WASAPI shared mode) into mono float chunks, plus a linear
//  resampler for Whisper's required 16kHz input.
//

using NAudio.Wave;

namespace Clicky.Audio;

public static class BuddyAudioConversionSupport
{
    /// Converts a raw WASAPI capture buffer into mono float samples,
    /// averaging channels. Supports the two formats Windows mics deliver:
    /// 32-bit IEEE float and 16-bit PCM.
    public static float[] ConvertCaptureBufferToMonoFloat(byte[] buffer, int bytesRecorded, WaveFormat captureFormat)
    {
        int channelCount = captureFormat.Channels;

        if (captureFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
            (captureFormat.Encoding == WaveFormatEncoding.Extensible && captureFormat.BitsPerSample == 32))
        {
            int totalSamples = bytesRecorded / 4;
            int frameCount = totalSamples / channelCount;
            var monoSamples = new float[frameCount];

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                float channelSum = 0;
                for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
                {
                    channelSum += BitConverter.ToSingle(buffer, (frameIndex * channelCount + channelIndex) * 4);
                }
                monoSamples[frameIndex] = channelSum / channelCount;
            }
            return monoSamples;
        }

        if (captureFormat.BitsPerSample == 16)
        {
            int totalSamples = bytesRecorded / 2;
            int frameCount = totalSamples / channelCount;
            var monoSamples = new float[frameCount];

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                float channelSum = 0;
                for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
                {
                    channelSum += BitConverter.ToInt16(buffer, (frameIndex * channelCount + channelIndex) * 2) / 32768f;
                }
                monoSamples[frameIndex] = channelSum / channelCount;
            }
            return monoSamples;
        }

        return Array.Empty<float>();
    }

    /// Linear-interpolation resampler for mono float audio. Quality is more
    /// than enough for speech recognition input (Whisper internally operates
    /// on 16kHz mel spectrograms anyway).
    public static float[] ResampleMono(float[] sourceSamples, int sourceSampleRate, int targetSampleRate)
    {
        if (sourceSampleRate == targetSampleRate || sourceSamples.Length == 0)
        {
            return sourceSamples;
        }

        double resampleRatio = (double)targetSampleRate / sourceSampleRate;
        int targetLength = (int)(sourceSamples.Length * resampleRatio);
        var targetSamples = new float[targetLength];

        for (int targetIndex = 0; targetIndex < targetLength; targetIndex++)
        {
            double sourcePosition = targetIndex / resampleRatio;
            int sourceIndex = (int)sourcePosition;
            double fraction = sourcePosition - sourceIndex;

            float currentSample = sourceSamples[Math.Min(sourceIndex, sourceSamples.Length - 1)];
            float nextSample = sourceSamples[Math.Min(sourceIndex + 1, sourceSamples.Length - 1)];
            targetSamples[targetIndex] = (float)(currentSample * (1 - fraction) + nextSample * fraction);
        }

        return targetSamples;
    }

    /// Computes the root-mean-square power of a buffer of float samples —
    /// the same RMS the original computed from AVAudioPCMBuffer for the
    /// waveform's audio-reactive height.
    public static float ComputeRootMeanSquare(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        float summedSquares = 0;
        foreach (float sample in samples)
        {
            summedSquares += sample * sample;
        }
        return (float)Math.Sqrt(summedSquares / samples.Length);
    }
}
