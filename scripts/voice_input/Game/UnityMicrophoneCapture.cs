using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VoiceInput.Core;

namespace VoiceInput;

internal interface IAudioCapture : IDisposable
{
    bool IsRecording { get; }
    float Elapsed { get; }
    float Level { get; }
    void Start(string device, int maxSeconds);
    void Tick();
    AudioSamples Stop();
    void Cancel();
}

internal sealed class AudioSamples
{
    internal readonly float[] Samples;
    internal readonly int Channels, SampleRate;
    internal AudioSamples(float[] samples, int channels, int sampleRate)
    { Samples = samples; Channels = channels; SampleRate = sampleRate; }
}

internal sealed class UnityMicrophoneCapture : IAudioCapture
{
    // Unity 6 also exposes Span overloads against its newer mscorlib; select the
    // array overload explicitly while keeping this plugin's net472 ABI.
    static readonly Func<AudioClip, float[], int, bool> Read = (Func<AudioClip, float[], int, bool>)Delegate.CreateDelegate(
        typeof(Func<AudioClip, float[], int, bool>), typeof(AudioClip).GetMethod("GetData", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(float[]), typeof(int) }, null));
    AudioClip clip;
    string device;
    float started;
    float[] meter;
    bool receivedSamples;
    public bool IsRecording => clip != null;
    public float Elapsed => clip == null ? 0 : Mathf.Max(0, Time.realtimeSinceStartup - started);
    public float Level { get; private set; }

    public void Start(string requestedDevice, int maxSeconds)
    {
        Cancel();
        var devices = Microphone.devices;
        if (devices.Length == 0) throw Fault("microphone_unavailable", "未找到麦克风，请检查设备和系统权限");
        device = string.IsNullOrWhiteSpace(requestedDevice) ? null : requestedDevice;
        if (device != null && !devices.Contains(device)) throw Fault("microphone_unavailable", "配置的麦克风不可用，请检查设备名称");
        try
        {
            Microphone.GetDeviceCaps(device, out int min, out int max);
            int rate = min == 0 && max == 0 ? 16000 : Math.Max(min, Math.Min(max > 0 ? max : 16000, 16000));
            started = Time.realtimeSinceStartup;
            clip = Microphone.Start(device, false, maxSeconds + 1, rate);
            if (clip == null) throw Fault("microphone_unavailable", "无法打开麦克风，请检查系统权限");
            meter = new float[1024 * clip.channels];
            receivedSamples = false;
            Level = 0;
        }
        catch (SpeechFault) { Cancel(); throw; }
        catch (Exception) { Cancel(); throw Fault("microphone_unavailable", "无法打开麦克风，请检查系统权限"); }
    }

    public void Tick()
    {
        if (clip == null) return;
        int position;
        try { position = Microphone.GetPosition(device); }
        catch (Exception) { throw Fault("microphone_disconnected", "麦克风已断开，请重新连接"); }
        if (position > 0)
        {
            receivedSamples = true;
            if (!Read(clip, meter, Math.Max(0, position - 1024)))
                throw Fault("microphone_read", "无法读取麦克风声音，请重新录音");
            double energy = 0;
            foreach (float sample in meter) energy += sample * sample;
            Level = Mathf.Clamp01((float)Math.Sqrt(energy / meter.Length) * 7);
        }
        if ((receivedSamples && !Microphone.IsRecording(device)) || (!receivedSamples && Elapsed > 3))
            throw Fault("microphone_disconnected", "麦克风没有返回声音，请检查设备和权限");
    }

    public AudioSamples Stop()
    {
        if (clip == null) throw Fault("no_speech", "没有录到声音，请重新录音");
        try
        {
            int count = Math.Min(clip.samples, Microphone.GetPosition(device));
            if (count <= 0) throw Fault("no_speech", "没有录到声音，请重新录音");
            var samples = new float[count * clip.channels];
            if (!Read(clip, samples, 0)) throw Fault("microphone_read", "无法读取录音，请重新录音");
            return new AudioSamples(samples, clip.channels, clip.frequency);
        }
        finally { Cancel(); }
    }

    public void Cancel()
    {
        if (clip == null) return;
        try { Microphone.End(device); }
        finally { UnityEngine.Object.Destroy(clip); clip = null; meter = null; Level = 0; }
    }
    public void Dispose() => Cancel();
    static SpeechFault Fault(string category, string text) => new SpeechFault(category, "", text, false);
}
