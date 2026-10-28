using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Settings;
using HMUI;
using LocalModUI;
using UnityEngine;
using VoiceInput.Contracts;
using Zenject;

namespace VoiceInput;

internal sealed class VoiceSettingsHost : INotifyPropertyChanged, IDisposable
{
    [UIComponent("voice-settings-content")] RectTransform content;
    RuntimeHost runtime;
    string selectedMicrophone = string.Empty;
    int maxRecordingSeconds, requestTimeoutSeconds;
    List<object> microphones = new List<object> { string.Empty };
    List<object> recordingChoices = new List<object> { 0 };
    List<object> timeoutChoices = new List<object> { 0 };
    string status;

    public event PropertyChangedEventHandler PropertyChanged;
    public List<object> Microphones => microphones;
    public List<object> RecordingChoices => recordingChoices;
    public List<object> TimeoutChoices => timeoutChoices;
    public bool CanEdit => runtime != null && runtime.Configuration != null && !runtime.SettingsBusy;
    public bool CanReload => runtime != null && !runtime.SettingsBusy;
    public bool CanTest => runtime != null && !runtime.SettingsBusy;
    public bool Testing => runtime?.TestActive == true;
    public bool HasStatus => !string.IsNullOrEmpty(Status);
    public string Status => status ?? runtime?.TestStatus ?? string.Empty;
    public string Meter
    {
        get
        {
            if (runtime == null) return string.Empty;
            float level = Math.Max(0, Math.Min(1, runtime.TestLevel));
            int bars = (int)Math.Round(level * 20);
            return new string('|', bars).PadRight(20, '·') + string.Format("  {0:0}%   {1:0.0} / 5 s", level * 100, runtime.TestElapsed);
        }
    }

    [UIAction("#post-parse")]
    void Parsed()
    {
        if (content != null) content.gameObject.AddComponent<VoiceSettingsView>().Initialize(this);
    }

    public string SelectedMicrophone
    {
        get => selectedMicrophone;
        set
        {
            string next = value ?? string.Empty;
            if (CanEdit && selectedMicrophone != next) { selectedMicrophone = next; Notify(nameof(SelectedMicrophone)); }
        }
    }
    public int MaxRecordingSeconds
    {
        get => maxRecordingSeconds;
        set { if (CanEdit && maxRecordingSeconds != value && recordingChoices.Contains(value)) { maxRecordingSeconds = value; Notify(nameof(MaxRecordingSeconds)); } }
    }
    public int RequestTimeoutSeconds
    {
        get => requestTimeoutSeconds;
        set { if (CanEdit && requestTimeoutSeconds != value && timeoutChoices.Contains(value)) { requestTimeoutSeconds = value; Notify(nameof(RequestTimeoutSeconds)); } }
    }

    public string FormatMicrophone(object value) => string.IsNullOrEmpty(value as string) ? "System default" : (string)value;
    public string FormatSeconds(object value) => value + " s";

    public void Refresh()
    {
        BindRuntime();
        if (runtime == null) { status = "语音输入尚未就绪"; NotifyAll(); return; }
        if (runtime.SettingsBusy) { NotifyAll(); return; }
        bool loaded = runtime.ReloadConfiguration(out string error);
        Configuration configuration = runtime.Configuration;
        if (configuration != null)
        {
            selectedMicrophone = configuration.Microphone ?? string.Empty;
            maxRecordingSeconds = configuration.MaxRecordingSeconds;
            requestTimeoutSeconds = configuration.RequestTimeoutSeconds;
        }
        try
        {
            recordingChoices = Choices("maxRecordingSeconds");
            timeoutChoices = Choices("requestTimeoutSeconds");
            if (configuration == null)
            {
                maxRecordingSeconds = (int)recordingChoices[0];
                requestTimeoutSeconds = (int)timeoutChoices[0];
            }
            microphones = new List<object> { string.Empty };
            foreach (string device in Microphone.devices ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(device) && !microphones.Contains(device)) microphones.Add(device);
            if (!string.IsNullOrEmpty(selectedMicrophone) && !microphones.Contains(selectedMicrophone)) microphones.Add(selectedMicrophone);
            status = loaded ? null : error;
        }
        catch (Exception ex)
        {
            Plugin.Diagnostic("settings_devices", ex);
            if (recordingChoices.Count == 0) recordingChoices.Add(maxRecordingSeconds);
            if (timeoutChoices.Count == 0) timeoutChoices.Add(requestTimeoutSeconds);
            status = "无法读取麦克风设备或设置范围";
        }
        NotifyAll();
    }

    List<object> Choices(string property)
    {
        int min = runtime.Protocol.ConfigurationLimit(property, "minimum");
        int max = runtime.Protocol.ConfigurationLimit(property, "maximum");
        if (max < min || (long)max - min > 1000) throw new InvalidOperationException("Invalid settings range.");
        return Enumerable.Range(min, max - min + 1).Select(value => (object)value).ToList();
    }

    public void Save()
    {
        if (!CanEdit) return;
        if (runtime.SaveSettings(selectedMicrophone, maxRecordingSeconds, requestTimeoutSeconds, out string error))
            status = "设置已保存";
        else status = error;
        NotifyAll();
    }
    public void StartTest()
    {
        if (!CanTest) return;
        status = null;
        runtime.StartMicrophoneTest(selectedMicrophone);
        NotifyAll();
    }
    public void StopTest() { runtime?.CancelMicrophoneTest(); status = null; NotifyAll(); }
    public void Closed() { runtime?.CancelMicrophoneTest(); }
    public void Attach(ViewController controller) { }
    public void Dispose()
    {
        Closed();
        if (runtime != null) runtime.SettingsChanged -= RuntimeChanged;
        runtime = null;
    }
    void BindRuntime()
    {
        RuntimeHost current = RuntimeHost.Current;
        if (runtime == current) return;
        if (runtime != null) runtime.SettingsChanged -= RuntimeChanged;
        runtime = current;
        if (runtime != null) runtime.SettingsChanged += RuntimeChanged;
    }
    void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    void RuntimeChanged()
    {
        Notify(nameof(CanEdit)); Notify(nameof(CanReload)); Notify(nameof(CanTest)); Notify(nameof(Testing));
        Notify(nameof(Status)); Notify(nameof(HasStatus)); Notify(nameof(Meter));
    }
    void NotifyAll()
    {
        Notify(nameof(Microphones)); Notify(nameof(RecordingChoices)); Notify(nameof(TimeoutChoices));
        Notify(nameof(SelectedMicrophone)); Notify(nameof(MaxRecordingSeconds)); Notify(nameof(RequestTimeoutSeconds));
        Notify(nameof(CanEdit)); Notify(nameof(CanReload)); Notify(nameof(CanTest)); Notify(nameof(Testing));
        Notify(nameof(Status)); Notify(nameof(HasStatus)); Notify(nameof(Meter));
    }
}

public sealed class VoiceMenuInstaller : Installer
{
    public override void InstallBindings() => Container.BindInterfacesTo<VoiceMenu>().AsSingle();
}

internal sealed class VoiceMenu : IInitializable, IDisposable
{
    readonly BSMLSettings settings;
    readonly VoiceSettingsHost host = new VoiceSettingsHost();
    IDisposable registration;
    public VoiceMenu(BSMLSettings settings) { this.settings = settings; }
    public void Initialize()
    {
        bool nativeAttempted = false, flowAttempted = false;
        try
        {
            host.Refresh();
            registration = UiRegistry.Register(new UiEntry
            {
                Id = "voice-input", Title = "Voice Input", Category = UiCategory.Devices, Order = 10,
                SavePolicy = SavePolicy.Explicit,
                Page = new SettingsPage
                {
                    Host = host, Assembly = typeof(VoiceSettingsHost).Assembly, Resource = "VoiceInput.UI.VoiceSettings.bsml",
                    Refresh = host.Refresh, Closed = host.Closed, Attach = host.Attach
                }
            });
            nativeAttempted = true;
            settings.AddSettingsMenu("Voice Input", "VoiceInput.UI.VoiceSettings.bsml", host);
            flowAttempted = true;
            InstantSettingsFlow.Register(host, error => Plugin.Diagnostic("settings_navigation", error));
        }
        catch
        {
            try { if (flowAttempted) InstantSettingsFlow.Unregister(host); }
            catch (Exception error) { Plugin.Diagnostic("settings_navigation_cleanup", error); }
            try { if (nativeAttempted) settings.RemoveSettingsMenu(host); }
            catch (Exception error) { Plugin.Diagnostic("settings_menu_cleanup", error); }
            try { registration?.Dispose(); registration = null; }
            catch (Exception error) { Plugin.Diagnostic("settings_registration_cleanup", error); }
            try { host.Dispose(); }
            catch (Exception error) { Plugin.Diagnostic("settings_host_cleanup", error); }
            throw;
        }
    }
    public void Dispose()
    {
        registration?.Dispose();
        host.Dispose();
        InstantSettingsFlow.Unregister(host);
        settings.RemoveSettingsMenu(host);
    }
}

public sealed class VoiceSettingsView : MonoBehaviour
{
    VoiceSettingsHost host;
    internal void Initialize(VoiceSettingsHost value)
    {
        host = value;
        if (isActiveAndEnabled) host.Refresh();
    }
    void OnEnable() { host?.Refresh(); }
    void OnDisable() { host?.Closed(); }
    void OnDestroy() { host?.Closed(); host = null; }
}
