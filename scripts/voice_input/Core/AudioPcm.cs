using System;

namespace VoiceInput.Core;

/// <summary>Audio conversion helpers kept independent from Unity.</summary>
public static class AudioPcm
{
    public const int TargetSampleRate = 16000;
    public const int TargetChannels = 1;
    public const int BytesPerSample = 2;

    /// <summary>
    /// Downmixes interleaved floating-point samples and linearly resamples them to
    /// mono 16 kHz signed little-endian PCM16.
    /// </summary>
    public static byte[] Encode(float[] interleaved, int channels, int sampleRate)
    {
        if (interleaved == null) throw new ArgumentNullException(nameof(interleaved));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (interleaved.Length == 0) return Array.Empty<byte>();

        int frameCount = interleaved.Length / channels;
        if (frameCount == 0) return Array.Empty<byte>();
        int outputFrames = Math.Max(1, (int)Math.Round(frameCount * (double)TargetSampleRate / sampleRate, MidpointRounding.AwayFromZero));
        var mono = new float[frameCount];
        for (int frame = 0; frame < frameCount; frame++)
        {
            double sum = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                float value = interleaved[frame * channels + channel];
                if (float.IsNaN(value) || float.IsInfinity(value)) value = 0;
                sum += Clamp(value, -1f, 1f);
            }
            mono[frame] = (float)(sum / channels);
        }

        var pcm = new byte[outputFrames * BytesPerSample];
        for (int output = 0; output < outputFrames; output++)
        {
            double sourcePosition = output * (double)sampleRate / TargetSampleRate;
            int left = (int)Math.Floor(sourcePosition);
            if (left >= frameCount) left = frameCount - 1;
            int right = Math.Min(left + 1, frameCount - 1);
            double fraction = sourcePosition - left;
            double value = mono[left] + (mono[right] - mono[left]) * fraction;
            value = Math.Max(-1d, Math.Min(1d, value));
            int sample = value <= -1d
                ? short.MinValue
                : (int)Math.Round(value * short.MaxValue, MidpointRounding.AwayFromZero);
            if (sample > short.MaxValue) sample = short.MaxValue;
            if (sample < short.MinValue) sample = short.MinValue;
            pcm[output * 2] = (byte)(sample & 0xff);
            pcm[output * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }
        return pcm;
    }

    /// <summary>Returns the RMS amplitude of signed little-endian PCM16 samples.</summary>
    public static double MeasureRms(byte[] pcm16)
    {
        if (pcm16 == null) throw new ArgumentNullException(nameof(pcm16));
        if (pcm16.Length < 2) return 0;
        long sumSquares = 0;
        int count = pcm16.Length / 2;
        for (int i = 0; i < count; i++)
        {
            short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            sumSquares += (long)sample * sample;
        }
        return Math.Sqrt(sumSquares / (double)count) / short.MaxValue;
    }

    public static bool IsSilent(byte[] pcm16, double rmsThreshold = 0.005)
    {
        if (rmsThreshold < 0) throw new ArgumentOutOfRangeException(nameof(rmsThreshold));
        return pcm16 == null || pcm16.Length < 2 || MeasureRms(pcm16) <= rmsThreshold;
    }

    public static bool IsUsable(byte[] pcm16, double rmsThreshold = 0.005) =>
        pcm16 != null && pcm16.Length >= BytesPerSample && !IsSilent(pcm16, rmsThreshold);

    static float Clamp(float value, float minimum, float maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;
}
