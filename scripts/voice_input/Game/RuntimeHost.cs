using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using VoiceInput.Contracts;
using VoiceInput.Core;

namespace VoiceInput;

public sealed class RuntimeHost : MonoBehaviour
{
    internal static RuntimeHost Current { get; private set; }
    internal SessionState State { get; private set; } = SessionState.Idle;
    internal VoicePanel ActivePanel { get; private set; }
    internal Configuration Configuration { get; private set; }
    internal Func<IAudioCapture> CaptureFactory { get; set; }
    internal Func<ISpeechRecognizer> RecognizerFactory { get; set; }
    internal Task ShutdownCompletion { get; private set; } = Task.CompletedTask;
    internal event Action SettingsChanged;
    internal bool SettingsBusy => testActive || State == SessionState.Starting || State == SessionState.Recording || State == SessionState.Recognizing;
    internal bool TestActive => testActive;
    internal float TestElapsed { get; private set; }
    internal float TestLevel { get; private set; }
    internal string TestStatus { get; private set; }
    internal VoiceProtocol Protocol => protocol;
    readonly Dictionary<int, VoicePanel> panels = new Dictionary<int, VoicePanel>();
    readonly List<PendingRun> pending = new List<PendingRun>();
    readonly HashSet<ISpeechRecognizer> recognizers = new HashSet<ISpeechRecognizer>();
    readonly HashSet<ISpeechRecognizer> retiredRecognizers = new HashSet<ISpeechRecognizer>();
    VoiceProtocol protocol;
    SpeechFault configurationFailure;
    ISpeechRecognizer defaultRecognizer;
    IAudioCapture capture;
    PendingRun activeRun;
    byte[] retryPcm;
    string expectedText;
    int generation;
    bool stopped;
    bool testActive;
    float nextMeterUpdate;
    string gameRoot;

    internal void Initialize(string gameRoot)
    {
        Current = this;
        this.gameRoot = gameRoot;
        CaptureFactory = () => new UnityMicrophoneCapture();
        try { protocol = new VoiceProtocol(); Configuration = protocol.LoadConfiguration(gameRoot); }
        catch (SpeechFault fault) { configurationFailure = fault; LogFault(fault); }
        catch (Exception ex)
        {
            configurationFailure = new SpeechFault("configuration", "", "VoiceInput.json 配置无法读取，请修正后重新加载配置");
            Plugin.Diagnostic("configuration", ex);
        }
        RecognizerFactory = () =>
        {
            if (configurationFailure != null) throw configurationFailure;
            return defaultRecognizer ?? (defaultRecognizer = protocol.CreateRecognizer(Configuration));
        };
        SceneManager.sceneLoaded += SceneLoaded;
        SceneManager.sceneUnloaded += SceneUnloaded;
    }

    internal bool ReloadConfiguration(out string error)
    {
        if (stopped || SettingsBusy) { error = "语音输入正在使用中"; return false; }
        try
        {
            Configuration candidate = protocol.LoadExistingConfiguration(gameRoot);
            InstallConfiguration(candidate);
            error = null;
            return true;
        }
        catch (SpeechFault fault) { LogFault(fault); error = fault.UserMessage; return false; }
        catch (Exception ex) { Plugin.Diagnostic("configuration_reload", ex); error = "无法读取语音输入配置"; return false; }
    }

    internal bool SaveSettings(string microphone, int maxRecordingSeconds, int requestTimeoutSeconds, out string error)
    {
        if (stopped || SettingsBusy) { error = "语音输入正在使用中"; return false; }
        try
        {
            Configuration candidate = protocol.SaveSettings(gameRoot, microphone, maxRecordingSeconds, requestTimeoutSeconds);
            InstallConfiguration(candidate);
            error = null;
            return true;
        }
        catch (SpeechFault fault) { LogFault(fault); error = fault.UserMessage; return false; }
        catch (Exception ex) { Plugin.Diagnostic("configuration_save", ex); error = "无法保存语音输入设置"; return false; }
    }

    void InstallConfiguration(Configuration candidate)
    {
        CancelCurrent();
        Configuration = candidate;
        configurationFailure = null;
        if (defaultRecognizer != null) retiredRecognizers.Add(defaultRecognizer);
        defaultRecognizer = null;
        RetireUnusedRecognizers();
        NotifySettings();
    }

    internal void StartMicrophoneTest(string device)
    {
        if (stopped || SettingsBusy) return;
        CancelCurrent();
        TestElapsed = TestLevel = 0;
        TestStatus = null;
        try
        {
            capture = CaptureFactory();
            if (capture == null) throw new InvalidOperationException("Capture factory returned null.");
            capture.Start(device, 5);
            testActive = true;
            nextMeterUpdate = 0;
        }
        catch (SpeechFault fault) { ReleaseCapture(); TestStatus = fault.UserMessage; LogFault(fault); }
        catch (Exception ex)
        {
            ReleaseCapture(); Plugin.Diagnostic("microphone_test_start", ex);
            TestStatus = "无法打开麦克风，请检查设备和系统权限";
        }
        NotifySettings();
    }

    internal void CancelMicrophoneTest()
    {
        if (!testActive) return;
        testActive = false;
        ReleaseCapture();
        TestLevel = 0;
        TestStatus = null;
        NotifySettings();
    }

    void UpdateMicrophoneTest()
    {
        try
        {
            capture.Tick();
            TestElapsed = capture.Elapsed;
            TestLevel = capture.Level;
            if (TestElapsed >= 5)
            {
                testActive = false;
                ReleaseCapture();
                TestStatus = "麦克风测试完成";
                NotifySettings();
            }
            else if (Time.unscaledTime >= nextMeterUpdate)
            {
                nextMeterUpdate = Time.unscaledTime + .1f;
                NotifySettings();
            }
        }
        catch (SpeechFault fault) { testActive = false; ReleaseCapture(); TestStatus = fault.UserMessage; LogFault(fault); NotifySettings(); }
        catch (Exception ex)
        {
            testActive = false; ReleaseCapture(); Plugin.Diagnostic("microphone_test_read", ex);
            TestStatus = "麦克风已断开，请重新连接"; NotifySettings();
        }
    }

    void NotifySettings()
    {
        var handlers = SettingsChanged;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception ex) { Plugin.Diagnostic("settings_refresh", ex); }
    }

    internal VoicePanel Attach(ITextTarget target)
    {
        if (stopped || target?.Root == null) return null;
        int id = target.Root.GetInstanceID();
        if (!panels.TryGetValue(id, out var panel) || panel == null)
        {
            panel = VoicePanel.Create(target);
            panels[id] = panel;
        }
        else if (!ReferenceEquals(panel.Target.Identity, target.Identity))
        {
            if (ActivePanel == panel) CancelCurrent();
            panel.Target = target;
            panel.SetState(SessionState.Idle, false, null);
        }
        if (target.IsAvailable && ActivePanel != null && ActivePanel != panel) CancelCurrent();
        if (SceneManager.GetSceneByName("GameCore").isLoaded) panel.gameObject.SetActive(false);
        return panel;
    }

    internal void Detach(object identity)
    {
        if (ActivePanel != null && ReferenceEquals(ActivePanel.Target.Identity, identity)) CancelCurrent();
    }
    internal void PanelShown(VoicePanel panel) { if (panel.Target?.IsAvailable == true && ActivePanel != null && ActivePanel != panel) CancelCurrent(); }
    internal void PanelHidden(VoicePanel panel) { if (ActivePanel == panel) CancelCurrent(false); }
    internal void PanelDestroyed(VoicePanel panel)
    {
        PanelHidden(panel);
        foreach (int id in panels.Where(p => ReferenceEquals(p.Value, panel)).Select(p => p.Key).ToArray()) panels.Remove(id);
    }

    internal void Toggle(VoicePanel panel)
    {
        if (stopped) return;
        if (ActivePanel == panel && State == SessionState.Recording) { EndRecording(); return; }
        if (ActivePanel == panel && (State == SessionState.Starting || State == SessionState.Recognizing)) return;
        if (ActivePanel == panel && State == SessionState.Failed && retryPcm != null)
        {
            if (!TargetUnchanged()) { CancelCurrent(); panel.SetState(SessionState.Failed, false, "文字已改变，请重新录音"); return; }
            StartRecognition(panel, null, retryPcm);
            return;
        }
        Begin(panel);
    }

    internal void Begin(VoicePanel panel)
    {
        if (testActive)
        {
            panel?.SetState(SessionState.Failed, false, "麦克风测试正在进行，请稍后再试");
            return;
        }
        CancelCurrent();
        if (stopped || panel?.Target == null || !panel.Target.IsAvailable || SceneManager.GetSceneByName("GameCore").isLoaded) return;
        ActivePanel = panel;
        expectedText = panel.Target.Text;
        try
        {
            if (configurationFailure != null) throw configurationFailure;
            var recognizer = RecognizerFactory();
            if (recognizer == null) throw new InvalidOperationException("Recognizer factory returned null.");
            recognizers.Add(recognizer);
            SetState(SessionState.Starting);
            capture = CaptureFactory();
            capture.Start(Configuration.Microphone, Configuration.MaxRecordingSeconds);
            SetState(SessionState.Recording);
            panel.RenderAudio(0, 0, Configuration.MaxRecordingSeconds);
        }
        catch (SpeechFault fault) { ReleaseCapture(); Fail(fault); }
        catch (Exception ex)
        {
            ReleaseCapture(); Plugin.Diagnostic("microphone_start", ex);
            Fail(new SpeechFault("microphone_unavailable", "", "无法打开麦克风，请检查设备和系统权限"));
        }
    }

    internal void Cancel(VoicePanel panel) { if (ActivePanel == panel) CancelCurrent(); else panel.SetState(SessionState.Idle, false, null); }

    void EndRecording()
    {
        if (capture == null || ActivePanel == null) return;
        var panel = ActivePanel;
        try
        {
            var audio = capture.Stop();
            ReleaseCapture();
            StartRecognition(panel, audio, null);
        }
        catch (SpeechFault fault) { ReleaseCapture(); Fail(fault); }
        catch (Exception ex)
        {
            ReleaseCapture(); Plugin.Diagnostic("microphone_read", ex);
            Fail(new SpeechFault("microphone_read", "", "读取录音失败，请重新录音"));
        }
    }

    void StartRecognition(VoicePanel panel, AudioSamples audio, byte[] pcm)
    {
        try
        {
            var recognizer = RecognizerFactory();
            if (recognizer == null) throw new InvalidOperationException("Recognizer factory returned null.");
            recognizers.Add(recognizer);
            var run = new PendingRun(++generation, panel, expectedText, Configuration.RequestTimeoutSeconds, recognizer);
            activeRun = run; retryPcm = null;
            SetState(SessionState.Recognizing);
            run.Task = RecognizeAsync(run, recognizer, audio, pcm);
            pending.Add(run);
        }
        catch (SpeechFault fault) { Fail(fault); }
        catch (Exception ex) { Plugin.Diagnostic("recognizer_start", ex); Fail(new SpeechFault("provider", "", "无法启动语音识别，请重新录音")); }
    }

    static async Task<Outcome> RecognizeAsync(PendingRun run, ISpeechRecognizer recognizer, AudioSamples audio, byte[] pcm)
    {
        try
        {
            if (pcm == null) pcm = await Task.Run(() => AudioPcm.Encode(audio.Samples, audio.Channels, audio.SampleRate), run.Cancellation.Token).ConfigureAwait(false);
            if (!AudioPcm.IsUsable(pcm)) return new Outcome(null, null, new SpeechFault("no_speech", "", "没有录到可识别的声音，请重新录音"));
            string text = await recognizer.RecognizeAsync(pcm, run.Cancellation.Token).ConfigureAwait(false);
            run.Cancellation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) return new Outcome(null, pcm, new SpeechFault("empty_result", "", "未识别到文字，请重新录音"));
            return new Outcome(text.Trim(), pcm, null);
        }
        catch (OperationCanceledException)
        {
            return new Outcome(null, pcm, run.UserCancelled ? null : new SpeechFault("timeout", "", "语音识别超时，可以重试", true));
        }
        catch (SpeechFault fault) { return new Outcome(null, pcm, fault); }
        catch (Exception) { return new Outcome(null, pcm, new SpeechFault("provider", "", "语音识别失败，可以重试", true)); }
    }

    void Update()
    {
        for (int n = pending.Count - 1; n >= 0; n--)
        {
            var run = pending[n];
            if (!run.Task.IsCompleted) continue;
            pending.RemoveAt(n);
            var result = run.Task.GetAwaiter().GetResult();
            run.Cancellation.Dispose();
            RetireUnusedRecognizers();
            if (stopped || activeRun != run || run.Generation != generation || run.UserCancelled) continue;
            activeRun = null;
            if (!TargetUnchanged()) { CancelCurrent(); run.Panel.SetState(SessionState.Failed, false, "文字已改变，请重新录音"); continue; }
            if (result.Fault != null)
            {
                retryPcm = result.Fault.Retryable ? result.Pcm : null;
                Fail(result.Fault);
            }
            else if (result.Text != null)
            {
                try
                {
                    bool applied = run.Panel.Target.TryAppend(run.Expected, result.Text);
                    CancelCurrent();
                    if (applied && run.Panel != null && run.Panel.gameObject.activeInHierarchy) run.Panel.ShowSuccess();
                    else if (!applied && run.Panel != null) run.Panel.SetState(SessionState.Failed, false, "无法追加文字，请检查输入长度");
                }
                catch (Exception ex)
                {
                    CancelCurrent(); Plugin.Diagnostic("text_apply", ex);
                    if (run.Panel != null) run.Panel.SetState(SessionState.Failed, false, "文字更新未完成，请检查输入框");
                }
            }
        }
        if (testActive) { UpdateMicrophoneTest(); return; }
        if (ActivePanel == null) return;
        if (!ActivePanel.Target.IsAvailable) { CancelCurrent(); return; }
        if (!String.Equals(ActivePanel.Target.Text, expectedText, StringComparison.Ordinal))
        {
            var panel = ActivePanel; CancelCurrent(); panel.SetState(SessionState.Failed, false, "文字已改变，请重新录音"); return;
        }
        if (capture == null) return;
        try
        {
            capture.Tick();
            ActivePanel.RenderAudio(capture.Elapsed, capture.Level, Configuration.MaxRecordingSeconds);
            if (capture.Elapsed >= Configuration.MaxRecordingSeconds) EndRecording();
        }
        catch (SpeechFault fault) { ReleaseCapture(); Fail(fault); }
        catch (Exception ex) { ReleaseCapture(); Plugin.Diagnostic("microphone_read", ex); Fail(new SpeechFault("microphone_disconnected", "", "麦克风已断开，请重新录音")); }
    }

    bool TargetUnchanged() => ActivePanel != null && ActivePanel.Target.IsAvailable && String.Equals(ActivePanel.Target.Text, expectedText, StringComparison.Ordinal);
    void SetState(SessionState state) { State = state; ActivePanel?.SetState(state, retryPcm != null, null); NotifySettings(); }
    void Fail(SpeechFault fault) { State = SessionState.Failed; ActivePanel?.SetState(State, retryPcm != null, fault.UserMessage); LogFault(fault); NotifySettings(); }
    static void LogFault(SpeechFault fault)
    {
        string id = new string((fault.RequestId ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-').Take(96).ToArray());
        Plugin.Log?.Warn("VoiceInput [" + fault.Category + "] request=" + id);
    }
    void ReleaseCapture()
    {
        var owned = capture; capture = null;
        if (owned == null) return;
        try { owned.Dispose(); } catch (Exception ex) { Plugin.Diagnostic("microphone_release", ex); }
    }
    void CancelCurrent(bool render = true)
    {
        generation++;
        if (activeRun != null) { activeRun.UserCancelled = true; activeRun.Cancellation.Cancel(); activeRun = null; }
        testActive = false; TestLevel = 0; TestStatus = null;
        ReleaseCapture(); retryPcm = null; expectedText = null;
        var panel = ActivePanel; ActivePanel = null; State = SessionState.Idle;
        if (render && panel != null) panel.SetState(SessionState.Idle, false, null);
        NotifySettings();
    }

    void RetireUnusedRecognizers()
    {
        foreach (var client in retiredRecognizers.Where(client => !pending.Any(run => run.Recognizer == client)).ToArray())
        {
            retiredRecognizers.Remove(client);
            recognizers.Remove(client);
            try { client.Dispose(); } catch (Exception ex) { Plugin.Diagnostic("recognizer_release", ex); }
        }
    }
    void SceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "GameCore") return;
        CancelCurrent();
        foreach (var panel in panels.Values.ToArray()) if (panel != null) panel.gameObject.SetActive(false);
    }
    void SceneUnloaded(Scene scene)
    {
        if (scene.name != "GameCore") return;
        foreach (var panel in panels.Values.ToArray()) if (panel != null) panel.gameObject.SetActive(true);
    }
    internal void Shutdown()
    {
        if (stopped) return;
        stopped = true;
        SceneManager.sceneLoaded -= SceneLoaded; SceneManager.sceneUnloaded -= SceneUnloaded;
        CancelCurrent(false);
        var closingRuns = pending.ToArray();
        foreach (var run in closingRuns) { run.UserCancelled = true; run.Cancellation.Cancel(); }
        var closingRecognizers = recognizers.ToArray();
        recognizers.Clear(); retiredRecognizers.Clear(); pending.Clear();
        ShutdownCompletion = DrainShutdownAsync(closingRuns, closingRecognizers);
        foreach (var panel in panels.Values.ToArray()) if (panel != null) Destroy(panel.gameObject);
        panels.Clear();
        if (Current == this) Current = null;
    }
    static async Task DrainShutdownAsync(PendingRun[] runs, ISpeechRecognizer[] clients)
    {
        // The host owns this completion until all cancelled operations terminate;
        // no Unity API or Unity synchronization context is needed while draining.
        try { await Task.WhenAll(runs.Select(r => r.Task)).ConfigureAwait(false); }
        catch (Exception ex) { Plugin.Diagnostic("shutdown", ex); }
        finally
        {
            foreach (var run in runs) run.Cancellation.Dispose();
            foreach (var client in clients)
                try { client.Dispose(); } catch (Exception ex) { Plugin.Diagnostic("recognizer_release", ex); }
        }
    }
    void OnDestroy() => Shutdown();

    sealed class PendingRun
    {
        internal readonly int Generation;
        internal readonly VoicePanel Panel;
        internal readonly string Expected;
        internal readonly CancellationTokenSource Cancellation;
        internal readonly ISpeechRecognizer Recognizer;
        internal volatile bool UserCancelled;
        internal Task<Outcome> Task;
        internal PendingRun(int generation, VoicePanel panel, string expected, int timeout, ISpeechRecognizer recognizer)
        { Generation = generation; Panel = panel; Expected = expected; Recognizer = recognizer; Cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeout)); }
    }
    sealed class Outcome
    {
        internal readonly string Text;
        internal readonly byte[] Pcm;
        internal readonly SpeechFault Fault;
        internal Outcome(string text, byte[] pcm, SpeechFault fault) { Text = text; Pcm = pcm; Fault = fault; }
    }
}
