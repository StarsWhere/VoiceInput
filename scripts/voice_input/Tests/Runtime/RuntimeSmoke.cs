using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HMUI;
using IPA;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VoiceInput.Core;
using Object = UnityEngine.Object;

namespace VoiceInput.RuntimeSmoke
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        GameObject owner;

        [OnStart]
        public void Start()
        {
            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOICE_INPUT_SMOKE_DIR")))
                return;

            owner = new GameObject("VoiceInput.RuntimeSmoke");
            Object.DontDestroyOnLoad(owner);
            owner.AddComponent<Runner>();
        }

        [OnExit]
        public void Exit()
        {
            if (owner != null)
                Object.Destroy(owner);
        }
    }

    /// <summary>
    /// Menu-only integration checks. The runner deliberately does not call a
    /// provider or download a song; recognition is supplied by FakeRecognizer.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public sealed class Runner : MonoBehaviour
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const float RunDeadlineSeconds = 380f;

        readonly List<object> checks = new List<object>();
        readonly List<string> errors = new List<string>();
        readonly List<string> diagnostics = new List<string>();
        readonly Stopwatch timer = Stopwatch.StartNew();
        string output;
        string phase;
        string livePcmPath;
        string liveExpectedText;
        bool liveOnly;
        bool bsmlOnly;
        bool pointerOnly;
        bool bsmlReserveProbe;
        bool finished;
        double lastProgress;
        Task run;
        Task finishTask;
        RuntimeHost host;
        FakeRecognizer recognizer;
        int keyboardChanges;
        UnityEngine.Events.UnityAction<InputFieldView> nativeChangeListener;
        bool gameCoreObserved;
        Component keyboardManagerObserved;
        object pointerPressSpyModule;
        EventInfo pointerPressSpyEvent;
        Delegate pointerPressSpyDelegate;
        int pointerPressSpyCalls;
        string pointerPressSpyLastObject;

        void Start()
        {
            output = Path.GetFullPath(Environment.GetEnvironmentVariable("VOICE_INPUT_SMOKE_DIR"));
            livePcmPath = Environment.GetEnvironmentVariable("VOICE_INPUT_LIVE_PCM");
            liveExpectedText = Environment.GetEnvironmentVariable("VOICE_INPUT_LIVE_EXPECT");
            liveOnly = IsEnvironmentFlagEnabled(Environment.GetEnvironmentVariable("VOICE_INPUT_LIVE_ONLY"));
            bsmlOnly = IsEnvironmentFlagEnabled(Environment.GetEnvironmentVariable("VOICE_INPUT_BSML_ONLY"));
            pointerOnly = IsEnvironmentFlagEnabled(Environment.GetEnvironmentVariable("VOICE_INPUT_POINTER_ONLY"));
            bsmlReserveProbe = IsEnvironmentFlagEnabled(Environment.GetEnvironmentVariable("VOICE_INPUT_BSML_RESERVE_PROBE"));
            Directory.CreateDirectory(output);
            Application.runInBackground = true;
            Application.targetFrameRate = 60;
            Application.logMessageReceived += OnLog;
            run = RunAsync();
        }

        void Update()
        {
            if (finished)
                return;

            if (timer.Elapsed.TotalSeconds - lastProgress >= 5)
            {
                lastProgress = timer.Elapsed.TotalSeconds;
                WriteProgress();
            }

            if (timer.Elapsed.TotalSeconds > RunDeadlineSeconds)
                _ = FinishAsync("finite runtime deadline exceeded during " + phase);
        }

        void OnDestroy()
        {
            RemovePointerPressWiring();
            Application.logMessageReceived -= OnLog;
        }

        void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                return;

            string text = message + "\n" + stack;
            if (text.IndexOf("VoiceInput", StringComparison.OrdinalIgnoreCase) >= 0)
                errors.Add(text);
        }

        async Task RunAsync()
        {
            bool attemptedNative = false;
            bool attemptedBsml = false;
            try
            {
                if (liveOnly || !String.IsNullOrWhiteSpace(livePcmPath))
                {
                    phase = "live recognition";
                    await RunLiveRecognition();
                    if (liveOnly)
                        return;
                }

                phase = "startup";
                await WaitUntil(IsMainMenuActive, 85, "MainMenuViewController.isActivated");
                // MainMenuViewController can be activated while the transition
                // animation and its buttons are still settling.  The native
                // smoke workflow uses the same post-activation grace period.
                await Task.Delay(2000);
                if (bsmlOnly)
                {
                    phase = "BSML keyboard";
                    Capture("startup.png");
                    try
                    {
                        attemptedBsml = await RunBsmlKeyboardChecks();
                    }
                    catch (Exception exception)
                    {
                        errors.Add("BSML keyboard: " + exception);
                        WriteNavigationDiagnostics("bsml-failure-navigation.json");
                    }
                    if (FindActiveComponentBySimpleName("BSSFlowCoordinator", "") != null)
                        await ReturnBssToMainMenu();
                    return;
                }
                Capture("startup.png");
                await ProbePhysicalMicrophone();

                InputFieldView nativeInput = await EnterNativeSearchFromMainMenu();

                attemptedNative = true;
                await RunNativeKeyboardChecks(nativeInput);

                phase = "BSML keyboard";
                await ReturnToMainMenu();
                try
                {
                    attemptedBsml = await RunBsmlKeyboardChecks();
                }
                catch (Exception exception)
                {
                    errors.Add("BSML keyboard: " + exception);
                    WriteNavigationDiagnostics("bsml-failure-navigation.json");
                }

                if (FindActiveComponentBySimpleName("BSSFlowCoordinator", "") != null)
                    await ReturnBssToMainMenu();

                if (pointerOnly)
                    return;

                // Re-enter native Solo after the independent BSML coverage so
                // a later gameplay return failure cannot hide the BSML result.
                phase = "native play and GameCore guard";
                nativeInput = await EnterNativeSearchFromMainMenu();
                await RunNativeGameplayGuard(nativeInput);
            }
            catch (Exception exception)
            {
                WriteNavigationDiagnostics("failure-navigation.json");
                errors.Add(phase + ": " + exception);
                UnityEngine.Debug.LogError("VoiceInput.RuntimeSmoke [" + phase + "]: " + exception);
            }
            finally
            {
                if (!liveOnly && !bsmlOnly)
                {
                    if (!attemptedNative)
                        Check("native.keyboard-flow", false, "native local-search flow was not reached");
                    if (!attemptedBsml)
                        Check("bsml.keyboard-flow", false, "BetterSongSearch BSML playlist keyboard flow was not reached");

                    if (!pointerOnly)
                        CheckGameCoreMicrophoneGuard();
                }
                else if (!liveOnly && bsmlOnly && !attemptedBsml)
                    Check("bsml.keyboard-flow", false, "BetterSongSearch BSML keyboard flow was not reached");
                await FinishAsync(null);
            }
        }

        async Task<InputFieldView> EnterNativeSearchFromMainMenu()
        {
            phase = "native local search";
            await WaitUntil(IsMainMenuActive, 20, "MainMenuViewController before Solo");
            await Task.Delay(1500);
            Component mainMenu = FindActiveComponentBySimpleName("MainMenuViewController", "");
            Button solo = GetMember<Button>(mainMenu, "_soloButton", "soloButton");
            Check("native.solo-control", solo != null, "MainMenuViewController._soloButton");
            if (solo == null)
                throw new InvalidOperationException("MainMenuViewController._soloButton was not available");
            solo.onClick.Invoke();
            await WaitUntil(IsLevelSelectionActive, 45, "LevelCollectionNavigationController.isActivated");
            WriteNavigationDiagnostics("native-navigation-after-solo.json");
            phase = "native search control";
            bool searchControlInvoked = RevealNativeSearchInput();
            WriteNavigationDiagnostics("native-navigation.json");
            Check("native.search-control", searchControlInvoked, "selected the active native level search/category control");
            InputFieldView nativeInput = await WaitForNativeSearchInput(35);
            if (nativeInput == null)
                throw new InvalidOperationException("HMUI native local-search InputFieldView was not found");
            return nativeInput;
        }

        async Task RunLiveRecognition()
        {
            if (String.IsNullOrWhiteSpace(livePcmPath))
            {
                Check("live.recognition", false, "raw16k fixture path was not supplied");
                return;
            }

            string fixturePath;
            try { fixturePath = Path.GetFullPath(livePcmPath); }
            catch
            {
                Check("live.recognition", false, "raw16k fixture path is invalid");
                return;
            }

            if (!File.Exists(fixturePath))
            {
                Check("live.recognition", false, "raw16k fixture was not found");
                return;
            }

            await WaitUntil(() => RuntimeHost.Current != null && RuntimeHost.Current.Configuration != null,
                90, "RuntimeHost configuration");
            host = RuntimeHost.Current;
            await WaitUntil(IsMainMenuActive, 85, "MainMenuViewController.isActivated");

            byte[] pcm;
            try { pcm = await Task.Run(() => File.ReadAllBytes(fixturePath)); }
            catch
            {
                Check("live.recognition", false, "raw16k fixture could not be read");
                return;
            }

            if (!AudioPcm.IsUsable(pcm))
            {
                Check("live.recognition", false, "raw16k fixture contains no usable PCM");
                return;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            string requestId = "unavailable";
            bool expectedChecked = !String.IsNullOrWhiteSpace(liveExpectedText);
            bool expectedMatched = !expectedChecked;
            try
            {
                using (ISpeechRecognizer recognizer = new VoiceProtocol().CreateRecognizer(host.Configuration))
                using (CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    string text = await recognizer.RecognizeAsync(pcm, cancellation.Token);
                    stopwatch.Stop();
                    bool nonEmpty = !String.IsNullOrWhiteSpace(text);
                    expectedMatched = !expectedChecked || (nonEmpty && text.IndexOf(liveExpectedText, StringComparison.OrdinalIgnoreCase) >= 0);
                    Check("live.recognition", nonEmpty && expectedMatched,
                        "elapsedMs=" + stopwatch.ElapsedMilliseconds + "; requestId=" + requestId +
                        "; expectedChecked=" + expectedChecked + "; expectedMatched=" + expectedMatched);
                    WriteJson("live-recognition.json", new
                    {
                        success = nonEmpty && expectedMatched,
                        elapsedMs = stopwatch.ElapsedMilliseconds,
                        requestId,
                        expectedChecked,
                        expectedMatched
                    });
                }
            }
            catch (SpeechFault fault)
            {
                stopwatch.Stop();
                requestId = SafeRequestId(fault.RequestId);
                Check("live.recognition", false,
                    "elapsedMs=" + stopwatch.ElapsedMilliseconds + "; requestId=" + requestId +
                    "; category=" + SafeToken(fault.Category) + "; retryable=" + fault.Retryable);
                WriteJson("live-recognition.json", new
                {
                    success = false,
                    elapsedMs = stopwatch.ElapsedMilliseconds,
                    requestId,
                    category = SafeToken(fault.Category),
                    retryable = fault.Retryable
                });
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                Check("live.recognition", false,
                    "elapsedMs=" + stopwatch.ElapsedMilliseconds + "; requestId=" + requestId + "; category=timeout");
                WriteJson("live-recognition.json", new
                {
                    success = false,
                    elapsedMs = stopwatch.ElapsedMilliseconds,
                    requestId,
                    category = "timeout"
                });
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                Check("live.recognition", false,
                    "elapsedMs=" + stopwatch.ElapsedMilliseconds + "; requestId=" + requestId +
                    "; category=" + SafeToken(exception.GetType().Name));
                WriteJson("live-recognition.json", new
                {
                    success = false,
                    elapsedMs = stopwatch.ElapsedMilliseconds,
                    requestId,
                    category = SafeToken(exception.GetType().Name)
                });
            }
        }

        static bool IsEnvironmentFlagEnabled(string value)
        {
            return String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        static string SafeToken(string value)
        {
            return new string((value ?? String.Empty).Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').Take(96).ToArray());
        }

        static string SafeRequestId(string value)
        {
            string sanitized = SafeToken(value);
            return String.IsNullOrWhiteSpace(sanitized) ? "unavailable" : sanitized;
        }

        async Task RunNativeKeyboardChecks(InputFieldView input)
        {
            ConfigureFactories();
            SetInputText(input, "原始");
            keyboardChanges = 0;
            nativeChangeListener = delegate { keyboardChanges++; };
            input.onValueChanged.AddListener(nativeChangeListener);

            try
            {
                phase = "native keyboard open";
                OpenInput(input);
                await WaitUntil(IsKeyboardVisible, 18, "native UIKeyboardManager");
                VoicePanel panel = await WaitForPanel(18);
                CheckPanelAttachment("native", panel);
                TestConfigurationReload();
                await TestMicrophoneSettings(panel);
                Capture("native-keyboard.png");
                Capture("native-keyboard-focused.png", false, panel == null ? null : panel.transform);

                Button record = GetMember<Button>(panel, "RecordButton", "recordButton");
                Check("native.record-button", record != null, "VoicePanel.RecordButton");
                if (record == null)
                    return;

                panel = await RunNativePointerRegression(input, panel);
                record = GetMember<Button>(panel, "RecordButton", "recordButton");
                if (pointerOnly)
                {
                    await RunNativePointerAppendCheck(input, panel);
                    return;
                }

                phase = "recording state";
                int changesBeforeAppend = keyboardChanges;
                record.onClick.Invoke();
                await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 5, "capture start");
                FakeCapture recording = FakeCapture.Last;
                recording.LevelValue = 0f;
                await Task.Delay(1250);
                Check("native.silence-does-not-auto-stop", recording.IsRecording && recognizer.Requests == 0,
                    "recording=" + recording.IsRecording + "; requests=" + recognizer.Requests + "; elapsed=" + recording.Elapsed.ToString("0.00"));
                CheckRecordingVisuals(panel);
                Capture("native-recording.png");
                Capture("native-recording-focused.png", false, panel.transform);

                phase = "recording stop and append";
                record.onClick.Invoke();
                // The second click is intentionally in the same frame. A
                // disabled recording button must not create a second request.
                record.onClick.Invoke();
                await WaitUntil(() => recognizer.Requests == 1, 8, "recognizer request");
                CheckRecognizingVisuals(panel);
                recognizer.CompleteNext("你好世界");
                await WaitUntil(() => input.text.IndexOf("你好世界", StringComparison.Ordinal) >= 0, 8, "native append");
                Check("native.append-chinese", input.text.IndexOf("你好世界", StringComparison.Ordinal) >= 0, input.text);
                Check("native.onValueChanged-once", keyboardChanges - changesBeforeAppend == 1,
                    "delta=" + (keyboardChanges - changesBeforeAppend));
                Capture("native-appended.png");

                phase = "manual edit stale result";
                SetInputText(input, "手动编辑");
                record.onClick.Invoke();
                await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 4, "second capture start");
                record = GetMember<Button>(panel, "RecordButton", "recordButton");
                record.onClick.Invoke();
                await WaitUntil(() => recognizer.Requests == 2, 7, "stale recognizer request");
                SetInputText(input, "人工修改");
                recognizer.CompleteNext("旧结果");
                await Task.Delay(500);
                Check("native.manual-edit-invalidates-result", input.text == "人工修改" && input.text.IndexOf("旧结果", StringComparison.Ordinal) < 0, input.text);

                phase = "native max length";
                await TestCharacterLimit(input, panel);
                phase = "native failure and retry";
                await TestFailureRetry(input, panel);

                phase = "native keyboard reopen";
                CloseKeyboard();
                await WaitUntil(() => !IsKeyboardVisible(), 8, "native keyboard close");
                OpenInput(input);
                await WaitUntil(IsKeyboardVisible, 12, "native keyboard reopen");
                VoicePanel reopened = await WaitForPanel(10);
                CheckPanelAttachment("native-reopen", reopened);
                Check("native.reopen-no-duplicate-panel", CountPanelsForKeyboard() <= 1,
                    "active VoicePanel count=" + CountPanelsForKeyboard());

                phase = "close cancellation";
                Button reopenRecord = GetMember<Button>(reopened, "RecordButton", "recordButton");
                SetInputText(input, "关闭前");
                reopenRecord.onClick.Invoke();
                await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 4, "close capture start");
                FakeCapture closeCapture = FakeCapture.Last;
                CloseKeyboard();
                await Task.Delay(500);
                Check("native.close-cancels-capture", !closeCapture.IsRecording && (closeCapture.CancelCount > 0 || closeCapture.Disposed),
                    "cancel=" + closeCapture.CancelCount + "; disposed=" + closeCapture.Disposed);

                // Stop a request before closing and complete the provider after
                // closure. The old target must not receive a late write.
                OpenInput(input);
                await WaitUntil(IsKeyboardVisible, 12, "native keyboard reopen for stale close");
                VoicePanel stalePanel = await WaitForPanel(8);
                Button staleRecord = GetMember<Button>(stalePanel, "RecordButton", "recordButton");
                SetInputText(input, "关闭后不应写入");
                staleRecord.onClick.Invoke();
                await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 4, "late capture start");
                staleRecord.onClick.Invoke();
                await WaitUntil(() => recognizer.Requests >= 4, 7, "late recognizer request");
                string beforeClose = input.text;
                CloseKeyboard();
                await Task.Delay(250);
                recognizer.CompleteNext("关闭旧结果");
                await Task.Delay(500);
                Check("native.close-invalidates-result", input.text == beforeClose && input.text.IndexOf("关闭旧结果", StringComparison.Ordinal) < 0, input.text);
                Capture("native-close.png");
            }
            finally
            {
                if (nativeChangeListener != null)
                    input.onValueChanged.RemoveListener(nativeChangeListener);
                nativeChangeListener = null;
            }
        }

        void TestConfigurationReload()
        {
            string root = Path.Combine(output, "settings-config-" + Guid.NewGuid().ToString("N"));
            RuntimeHost original = RuntimeHost.Current;
            GameObject isolatedObject = new GameObject("VoiceInput.SettingsReloadCheck");
            RuntimeHost isolated = isolatedObject.AddComponent<RuntimeHost>();
            try
            {
                isolated.Initialize(root);
                var valid = isolated.Configuration;
                string path = Path.Combine(root, "UserData", "VoiceInput.json");
                string originalText = File.ReadAllText(path);
                File.WriteAllText(path, "{ invalid");
                bool rejected = !isolated.ReloadConfiguration(out string error);
                Check("settings.reload-preserves-last-valid", rejected && !String.IsNullOrEmpty(error) &&
                    Object.ReferenceEquals(valid, isolated.Configuration), "invalid file must retain the running configuration");
                File.Delete(path);
                bool missingRejected = !isolated.ReloadConfiguration(out error);
                Check("settings.missing-file-preserves-last-valid", missingRejected && Object.ReferenceEquals(valid, isolated.Configuration) && !File.Exists(path),
                    "a missing file must not replace the running configuration with defaults");
                var corrected = Newtonsoft.Json.Linq.JObject.Parse(originalText);
                corrected["maxRecordingSeconds"] = 25;
                File.WriteAllText(path, corrected.ToString());
                bool reloaded = isolated.ReloadConfiguration(out error);
                Check("settings.reload-recovers", reloaded && isolated.Configuration.MaxRecordingSeconds == 25,
                    "a corrected file replaces the last valid configuration");
            }
            finally
            {
                isolated.Shutdown();
                typeof(RuntimeHost).GetProperty("Current", Any).SetValue(null, original, null);
                Object.Destroy(isolatedObject);
                string userData = Path.Combine(root, "UserData");
                string configurationPath = Path.Combine(userData, "VoiceInput.json");
                if (File.Exists(configurationPath)) File.Delete(configurationPath);
                if (Directory.Exists(userData)) Directory.Delete(userData);
                if (Directory.Exists(root)) Directory.Delete(root);
            }
        }

        async Task TestMicrophoneSettings(VoicePanel panel)
        {
            var settings = new VoiceSettingsHost();
            try
            {
                settings.Refresh();
                int requestsBefore = recognizer.Requests;
                settings.StartTest();
                FakeCapture test = FakeCapture.Last;
                Check("settings.test-owns-capture", test != null && test.IsRecording && host.TestActive && !settings.CanEdit,
                    "test holds one microphone and locks settings");
                int created = FakeCapture.Created;
                host.Begin(panel);
                string error;
                bool saved = host.SaveSettings("", 1, 1, out error);
                Check("settings.busy-blocks-record-and-save", !saved && !String.IsNullOrEmpty(error) && host.TestActive &&
                    FakeCapture.Created == created && FakeCapture.ActiveCount == 1,
                    "recording and saving cannot replace an active test");
                settings.Closed();
                Check("settings.close-releases-microphone", !host.TestActive && !test.IsRecording && test.Disposed,
                    "closing a settings page releases its test capture");

                settings.StartTest();
                test = FakeCapture.Last;
                await WaitUntil(() => !host.TestActive, 8, "five-second microphone test");
                Check("settings.test-completes-locally", test.Disposed && !test.IsRecording && host.TestElapsed >= 5 &&
                    recognizer.Requests == requestsBefore && host.TestLevel > 0,
                    "five-second level test completes without a recognition request");
                host.Begin(panel);
                FakeCapture recording = FakeCapture.Last;
                created = FakeCapture.Created;
                settings.StartTest();
                Check("settings.recording-blocks-test", recording != null && recording.IsRecording && !host.TestActive &&
                    FakeCapture.Created == created && !settings.CanEdit, "recording retains microphone ownership");
                string targetBefore = panel.Target.Text;
                recognizer.IgnoreCancellation = true;
                host.Toggle(panel);
                await WaitUntil(() => recognizer.Requests == requestsBefore + 1, 8, "settings recognition ownership");
                settings.StartTest();
                saved = host.SaveSettings("", 1, 1, out error);
                Check("settings.recognition-blocks-test-and-save", !host.TestActive && !settings.CanEdit && !saved &&
                    FakeCapture.Created == created, "a recognition request locks settings and test start");
                host.Cancel(panel);
                recognizer.CompleteNext("旧识别结果");
                await Task.Delay(250);
                Check("settings.cancelled-result-isolated", panel.Target.Text == targetBefore,
                    "late result does not update the prior text target");
            }
            finally { host.Cancel(panel); settings.Dispose(); }
            // Later native checks have their own request counters and fixtures.
            ConfigureFactories();
        }

        async Task<VoicePanel> RunNativePointerRegression(InputFieldView input, VoicePanel panel)
        {
            phase = "native pointer/raycast regression";
            Button record = GetMember<Button>(panel, "RecordButton", "recordButton");
            if (record == null)
                throw new InvalidOperationException("native VoicePanel.RecordButton was unavailable for pointer regression");

            Transform microphone = panel.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(x => String.Equals(x.name, "Microphone", StringComparison.Ordinal));
            Check("native.microphone-pointer-icon", microphone != null, "VoicePanel.Microphone aim transform");
            await DispatchPointerClick(record, "native.microphone-pointer", microphone ?? record.transform);
            await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 5, "native pointer capture start");
            FakeCapture capture = FakeCapture.Last;
            Check("native.microphone-pointer-starts-recording", capture != null && capture.IsRecording,
                "capture was not started by a VRUI pointer click");
            Check("native.microphone-pointer-keeps-keyboard", IsKeyboardVisible(),
                "UIKeyboardManager closed after a raycasted VoicePanel microphone click");

            Button cancel = GetMember<Button>(panel, "CancelButton", "cancelButton");
            Check("native.cancel-pointer-control", cancel != null && cancel.gameObject.activeInHierarchy,
                "VoicePanel.CancelButton was not active while recording");
            if (cancel != null)
            {
                await DispatchPointerClick(cancel, "native.cancel-pointer");
                await WaitUntil(() => capture == null || !capture.IsRecording, 5, "native pointer cancel");
                Check("native.cancel-pointer-stops-recording", capture == null || !capture.IsRecording,
                    "CancelButton pointer click did not stop the active capture");
                Check("native.cancel-pointer-keeps-keyboard", IsKeyboardVisible(),
                    "UIKeyboardManager closed after a raycasted VoicePanel cancel click");
            }

            Check("native.blank-pointer-keyboard-before", IsKeyboardVisible(), "native keyboard was not visible before blank click");
            await DispatchBlankOutsideKeyboard(panel, "native.blank-outside-pointer");
            await WaitUntil(() => !IsKeyboardVisible(), 8, "native blank outside keyboard close");
            Check("native.blank-outside-closes-keyboard", !IsKeyboardVisible(),
                "UIKeyboardManager remained visible after a raycasted blank outside click");

            OpenInput(input);
            await WaitUntil(IsKeyboardVisible, 12, "native keyboard reopen after pointer regression");
            VoicePanel reopened = await WaitForPanel(8);
            CheckPanelAttachment("native-pointer-reopen", reopened);
            return reopened;
        }

        async Task RunNativePointerAppendCheck(InputFieldView input, VoicePanel panel)
        {
            SetInputText(input, "指针");
            Button record = GetMember<Button>(panel, "RecordButton", "recordButton");
            if (record == null)
                throw new InvalidOperationException("native VoicePanel.RecordButton was unavailable for pointer append");
            int request = recognizer.Requests + 1;
            await DispatchPointerClick(record, "native.pointer-append-start");
            await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 5, "native pointer append capture start");
            record = GetMember<Button>(panel, "RecordButton", "recordButton");
            await DispatchPointerClick(record, "native.pointer-append-stop");
            await WaitUntil(() => recognizer.Requests >= request, 8, "native pointer append recognizer request");
            recognizer.CompleteNext("结果");
            await WaitUntil(() => input.text.IndexOf("结果", StringComparison.Ordinal) >= 0, 8, "native pointer append result");
            Check("native.pointer-append-result", input.text.IndexOf("结果", StringComparison.Ordinal) >= 0, input.text);
        }

        async Task RunBsmlPointerCancelRegression(VoicePanel panel, object modalKeyboard, Component modalView)
        {
            phase = "BSML pointer/raycast regression";
            Button record = GetMember<Button>(panel, "RecordButton", "recordButton");
            if (record == null)
                throw new InvalidOperationException("BSML VoicePanel.RecordButton was unavailable for pointer regression");

            Transform microphone = panel.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(x => String.Equals(x.name, "Microphone", StringComparison.Ordinal));
            Check("bsml.microphone-pointer-icon", microphone != null, "VoicePanel.Microphone aim transform");
            await DispatchPointerClick(record, "bsml.microphone-pointer-cancel-probe", microphone ?? record.transform);
            await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 5, "BSML pointer capture start");
            FakeCapture capture = FakeCapture.Last;
            Check("bsml.microphone-pointer-starts-recording", capture != null && capture.IsRecording,
                "capture was not started by a VRUI pointer click");
            Check("bsml.microphone-pointer-keeps-keyboard", IsBsmlKeyboardVisible(modalKeyboard, modalView),
                "BSML ModalKeyboard closed after a raycasted VoicePanel microphone click");

            Button cancel = GetMember<Button>(panel, "CancelButton", "cancelButton");
            Check("bsml.cancel-pointer-control", cancel != null && cancel.gameObject.activeInHierarchy,
                "BSML VoicePanel.CancelButton was not active while recording");
            if (cancel != null)
            {
                await DispatchPointerClick(cancel, "bsml.cancel-pointer");
                await WaitUntil(() => capture == null || !capture.IsRecording, 5, "BSML pointer cancel");
                Check("bsml.cancel-pointer-stops-recording", capture == null || !capture.IsRecording,
                    "BSML CancelButton pointer click did not stop the active capture");
                Check("bsml.cancel-pointer-keeps-keyboard", IsBsmlKeyboardVisible(modalKeyboard, modalView),
                    "BSML ModalKeyboard closed after a raycasted VoicePanel cancel click");
            }
        }

        async Task<bool> DispatchBlankOutsideKeyboard(VoicePanel panel, string scope)
        {
            Button blockerButton = FindOutsideKeyboardBlockerButton();
            if (blockerButton != null)
            {
                Check(scope + ".target", ShouldCloseKeyboardFor(blockerButton.gameObject),
                    "target=" + TransformPath(blockerButton.transform));
                return await DispatchPointerClick(blockerButton, scope, blockerButton.transform);
            }

            Transform blockerAim = FindOutsideKeyboardBlockerAim(panel);
            if (blockerAim == null)
            {
                Check(scope + ".target", false, "an existing outside-keyboard Blocker target was unavailable");
                return false;
            }

            PointerDispatch dispatch;
            if (!TryBuildPointerDispatch((GameObject)null, blockerAim, scope, blockerAim, out dispatch))
                return false;

            int spyCallsBefore = pointerPressSpyCalls;
            bool dispatched = false;
            try { dispatched = InvokePointerClick(dispatch); }
            catch (Exception exception) { errors.Add(scope + ".dispatch: " + exception.GetBaseException()); }
            Check(scope + ".dispatch", dispatched,
                "module=" + dispatch.ModuleName + "; hit=" + TransformPath(dispatch.HitObject == null ? null : dispatch.HitObject.transform) +
                "; handler=" + TransformPath(dispatch.EventHandler == null ? null : dispatch.EventHandler.transform) +
                "; inputModule=" + dispatch.InputModuleName + "; path=" + dispatch.DispatchPath);
            Check(scope + ".inputmodule-press-callback", pointerPressSpyCalls > spyCallsBefore,
                "spyDelta=" + (pointerPressSpyCalls - spyCallsBefore) + "; lastObject=" + pointerPressSpyLastObject);
            WriteJson(scope + ".json", new
            {
                target = dispatch.TargetPath,
                aim = dispatch.AimPath,
                hit = TransformPath(dispatch.HitObject == null ? null : dispatch.HitObject.transform),
                handler = TransformPath(dispatch.EventHandler == null ? null : dispatch.EventHandler.transform),
                module = dispatch.ModuleName,
                inputModule = dispatch.InputModuleName,
                dispatchPath = dispatch.DispatchPath,
                rayOrigin = dispatch.RayOrigin.ToString(),
                rayDirection = dispatch.RayDirection.ToString(),
                raycastCount = dispatch.RaycastCount,
                dispatched
            });
            WriteJson(scope + ".wiring.json", DescribePointerPressWiring(dispatch.InputModule));
            await Task.Yield();
            return dispatched;
        }

        Button FindOutsideKeyboardBlockerButton()
        {
            Transform wrapper = FindKeyboardDismissalWrapper();
            return Resources.FindObjectsOfTypeAll<Button>()
                .Where(IsLiveSceneComponent)
                .Where(x => String.Equals(x.name, "Blocker", StringComparison.OrdinalIgnoreCase) ||
                    TransformPath(x.transform).EndsWith("/Blocker", StringComparison.OrdinalIgnoreCase))
                .Where(x => wrapper == null || !x.transform.IsChildOf(wrapper))
                .OrderBy(x => TransformPath(x.transform), StringComparer.Ordinal)
                .FirstOrDefault();
        }

        Transform FindOutsideKeyboardBlockerAim(VoicePanel panel)
        {
            Transform wrapper = FindKeyboardDismissalWrapper();
            Canvas panelCanvas = panel == null ? null : panel.GetComponentInParent<Canvas>(true);
            Canvas rootCanvas = panelCanvas == null ? null : panelCanvas.rootCanvas;
            return Resources.FindObjectsOfTypeAll<Graphic>()
                .Where(IsLiveSceneComponent)
                .Where(x => x.raycastTarget && (String.Equals(x.name, "Blocker", StringComparison.OrdinalIgnoreCase) ||
                    TransformPath(x.transform).EndsWith("/Blocker", StringComparison.OrdinalIgnoreCase)))
                .Where(x => wrapper == null || !x.transform.IsChildOf(wrapper))
                .Where(x => rootCanvas == null || x.canvas == rootCanvas)
                .OrderBy(x => TransformPath(x.transform), StringComparer.Ordinal)
                .Select(x => x.transform)
                .FirstOrDefault();
        }

        Transform FindKeyboardDismissalWrapper()
        {
            Component manager = FindKeyboardManager();
            return ReadMember(manager, "_keyboardContainerTransform", "keyboardContainerTransform") as Transform ??
                FindKeyboardContainer(manager);
        }

        bool ShouldCloseKeyboardFor(GameObject target)
        {
            if (target == null)
                return false;
            Component manager = FindKeyboardManager();
            MethodInfo method = manager == null ? null : FindDeclaredMethodInHierarchy(manager.GetType(), "ShouldCloseKeyboard",
                x => x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(GameObject));
            if (method != null)
            {
                try
                {
                    object value = method.Invoke(manager, new object[] { target });
                    if (value is bool)
                        return (bool)value;
                }
                catch (Exception exception) { diagnostics.Add("ShouldCloseKeyboard: " + exception.GetBaseException().Message); }
            }
            Transform wrapper = FindKeyboardDismissalWrapper();
            return wrapper != null && !target.transform.IsChildOf(wrapper);
        }

        async Task<bool> DispatchPointerClick(Button button, string scope, Transform aimTransform = null)
        {
            PointerDispatch dispatch;
            if (button == null)
            {
                Check(scope + ".control", false, "Button was unavailable");
                return false;
            }

            if (!TryBuildPointerDispatch(button, scope, aimTransform, out dispatch))
                return false;

            int spyCallsBefore = pointerPressSpyCalls;
            bool dispatched = false;
            try
            {
                dispatched = InvokePointerClick(dispatch);
            }
            catch (Exception exception)
            {
                errors.Add(scope + ".dispatch: " + exception.GetBaseException());
            }
            Check(scope + ".dispatch", dispatched,
                "module=" + dispatch.ModuleName + "; hit=" + TransformPath(dispatch.HitObject == null ? null : dispatch.HitObject.transform) +
                "; handler=" + TransformPath(dispatch.EventHandler == null ? null : dispatch.EventHandler.transform) +
                "; inputModule=" + dispatch.InputModuleName + "; path=" + dispatch.DispatchPath);
            Check(scope + ".inputmodule-press-callback", pointerPressSpyCalls > spyCallsBefore,
                "spyDelta=" + (pointerPressSpyCalls - spyCallsBefore) + "; lastObject=" + pointerPressSpyLastObject);
            WriteJson(scope + ".json", new
            {
                target = TransformPath(button.transform),
                aim = dispatch.AimPath,
                hit = TransformPath(dispatch.HitObject == null ? null : dispatch.HitObject.transform),
                handler = TransformPath(dispatch.EventHandler == null ? null : dispatch.EventHandler.transform),
                module = dispatch.ModuleName,
                inputModule = dispatch.InputModuleName,
                dispatchPath = dispatch.DispatchPath,
                rayOrigin = dispatch.RayOrigin.ToString(),
                rayDirection = dispatch.RayDirection.ToString(),
                raycastCount = dispatch.RaycastCount,
                dispatched
            });
            WriteJson(scope + ".wiring.json", DescribePointerPressWiring(dispatch.InputModule));
            await Task.Yield();
            return dispatched;
        }

        bool TryBuildPointerDispatch(Button button, string scope, Transform aimTransform, out PointerDispatch dispatch)
        {
            return TryBuildPointerDispatch(button == null ? null : button.gameObject, button == null ? null : button.transform,
                scope, aimTransform, out dispatch);
        }

        bool TryBuildPointerDispatch(GameObject expectedTarget, Transform targetTransform, string scope, Transform aimTransform, out PointerDispatch dispatch)
        {
            dispatch = new PointerDispatch();
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                Check(scope + ".event-system", false, "EventSystem.current was unavailable");
                return false;
            }

            object inputModule = ResolveKeyboardInputModule();
            dispatch.InputModule = inputModule;
            dispatch.InputModuleName = inputModule == null ? null : inputModule.GetType().FullName;
            EnsurePointerPressWiring(inputModule);
            Comparison<RaycastResult> raycastComparer = ResolveRaycastComparer(inputModule);
            if (raycastComparer == null)
            {
                Check(scope + ".raycast-order", false, "VRInputModule._raycastComparer was unavailable");
                return false;
            }

            PointerEventData pointer = new PointerEventData(eventSystem) { button = PointerEventData.InputButton.Left };
            Camera camera = Resources.FindObjectsOfTypeAll<Camera>().FirstOrDefault(x => IsLiveSceneComponent(x) && x.name == "MenuMainCamera");
            if (camera == null)
            {
                Check(scope + ".camera", false, "MenuMainCamera was unavailable for the representative pointer ray");
                return false;
            }

            Transform aim = aimTransform ?? targetTransform;
            if (aim == null)
            {
                Check(scope + ".aim", false, "aim transform was unavailable");
                return false;
            }
            if (expectedTarget != null && targetTransform != null && aim != targetTransform && !aim.IsChildOf(targetTransform))
            {
                Check(scope + ".aim", false, "aim transform was not a descendant of the target Button");
                return false;
            }
            dispatch.TargetPath = TransformPath(targetTransform);
            dispatch.AimPath = TransformPath(aim);
            Vector3 center = ResolveCurvedCanvasFocus(aim);
            Vector3 ray = center - camera.transform.position;
            if (ray.sqrMagnitude < .0001f)
            {
                Check(scope + ".ray", false, "MenuMainCamera and curved button center were coincident");
                return false;
            }
            Vector3 direction = ray.normalized;
            List<object> observed = new List<object>();
            List<RaycastResult> results = new List<RaycastResult>();
            pointer.pointerCurrentRaycast = new RaycastResult
            {
                worldPosition = camera.transform.position,
                worldNormal = direction,
                distance = ray.magnitude
            };
            pointer.position = (Vector2)camera.WorldToScreenPoint(center);
            eventSystem.RaycastAll(pointer, results);
            results.Sort(raycastComparer);

            RaycastResult first = results.FirstOrDefault(x => x.gameObject != null);
            foreach (RaycastResult result in results)
            {
                GameObject handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(result.gameObject);
                observed.Add(new
                {
                    module = result.module == null ? null : result.module.GetType().FullName,
                    objectPath = TransformPath(result.gameObject == null ? null : result.gameObject.transform),
                    handlerPath = TransformPath(handler == null ? null : handler.transform),
                    distance = result.distance,
                    screenPosition = result.screenPosition.ToString(),
                    isFirstNonNull = result.gameObject != null && first.gameObject == result.gameObject
                });
            }

            GameObject firstHandler = first.gameObject == null ? null : ExecuteEvents.GetEventHandler<IPointerClickHandler>(first.gameObject);
            bool firstIsTarget = expectedTarget != null ? first.gameObject != null && firstHandler == expectedTarget :
                first.gameObject != null && ShouldCloseKeyboardFor(first.gameObject);
            bool firstIsVr = IsVrGraphicRaycaster(first.module);
            if (first.gameObject != null && firstIsVr && firstIsTarget)
            {
                dispatch.Pointer = pointer;
                dispatch.Hit = first;
                dispatch.HitObject = first.gameObject;
                dispatch.EventHandler = firstHandler;
                dispatch.ModuleName = first.module.GetType().FullName;
                dispatch.RayOrigin = camera.transform.position;
                dispatch.RayDirection = direction;
                dispatch.RaycastCount = results.Count;
                pointer.pointerCurrentRaycast = first;
                pointer.position = first.screenPosition;
                WriteJson(scope + ".raycast.json", new
                {
                    target = dispatch.TargetPath,
                    aim = dispatch.AimPath,
                    hit = TransformPath(first.gameObject.transform),
                    handler = TransformPath(firstHandler.transform),
                    module = dispatch.ModuleName,
                    inputModule = dispatch.InputModuleName,
                    rayOrigin = camera.transform.position.ToString(),
                    rayDirection = direction.ToString(),
                    raycastCount = results.Count,
                    observed = observed.Take(24).ToArray()
                });
                return true;
            }

            WriteJson(scope + ".raycast.json", new
            {
                target = dispatch.TargetPath,
                aim = dispatch.AimPath,
                camera = TransformPath(camera.transform),
                curvedCenter = center.ToString(),
                rayDirection = direction.ToString(),
                firstWinner = first.gameObject == null ? null : TransformPath(first.gameObject.transform),
                firstWinnerModule = first.module == null ? null : first.module.GetType().FullName,
                firstWinnerHandler = firstHandler == null ? null : TransformPath(firstHandler.transform),
                vrRaycasters = Resources.FindObjectsOfTypeAll<BaseRaycaster>().Where(IsLiveSceneComponent)
                    .Select(x => new { type = x.GetType().FullName, path = TransformPath(x.transform) }).Take(32).ToArray(),
                observed = observed.Take(48).ToArray()
            });
            Check(scope + ".raycast", false,
                "first sorted raycast hit did not belong to the expected target/outside keyboard surface; winner=" +
                TransformPath(first.gameObject == null ? null : first.gameObject.transform) +
                "; module=" + (first.module == null ? "null" : first.module.GetType().FullName) +
                "; observed=" + observed.Count);
            return false;
        }

        static Comparison<RaycastResult> ResolveRaycastComparer(object inputModule)
        {
            foreach (Type type in new[] { inputModule == null ? null : inputModule.GetType(), FindType("VRUIControls.VRInputModule") })
            {
                Comparison<RaycastResult> comparer = ReadStaticMember(type, "_raycastComparer") as Comparison<RaycastResult>;
                if (comparer != null)
                    return comparer;
            }
            return null;
        }

        bool InvokePointerClick(PointerDispatch dispatch)
        {
            if (dispatch == null || dispatch.Pointer == null || dispatch.HitObject == null)
                return false;

            object module = ResolveKeyboardInputModule();
            if (module == null)
            {
                Check("pointer.input-module", false, "the module wired to keyboard dismissal was unavailable");
                return false;
            }
            dispatch.InputModule = module;
            dispatch.InputModuleName = module.GetType().FullName;
            MethodInfo processMousePress = FindDeclaredMethodInHierarchy(module.GetType(), "ProcessMousePress", method =>
                method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.FullName == "VRUIControls.MouseButtonEventData");
            if (processMousePress != null)
            {
                dispatch.DispatchPath = "VRInputModule.ProcessMousePress";
                InvokeMousePress(processMousePress, module, dispatch.Pointer, "Pressed");
                InvokeMousePress(processMousePress, module, dispatch.Pointer, "Released");
                return true;
            }

            FieldInfo processEvent = module.GetType().GetField("onProcessMousePressEvent", Any);
            Delegate processCallback = processEvent == null ? null : processEvent.GetValue(module) as Delegate;
            if (processCallback == null)
            {
                Check("pointer.input-module-hook", false, "input module has neither VRInputModule.ProcessMousePress nor an active press callback");
                return false;
            }

            dispatch.DispatchPath = "NoVRInputModule.pointer-events+onProcessMousePressEvent";
            GameObject pointerPress = ExecuteEvents.ExecuteHierarchy<IPointerDownHandler>(dispatch.HitObject, dispatch.Pointer, ExecuteEvents.pointerDownHandler);
            if (pointerPress == null)
                pointerPress = ExecuteEvents.GetEventHandler<IPointerClickHandler>(dispatch.HitObject);
            dispatch.Pointer.eligibleForClick = pointerPress != null;
            dispatch.Pointer.pointerPress = pointerPress;
            dispatch.Pointer.rawPointerPress = dispatch.HitObject;
            if (pointerPress != null)
                processCallback.DynamicInvoke(new object[] { dispatch.HitObject });
            if (pointerPress != null)
            {
                ExecuteEvents.Execute(pointerPress, dispatch.Pointer, ExecuteEvents.pointerUpHandler);
                if (dispatch.Pointer.eligibleForClick)
                    ExecuteEvents.Execute(pointerPress, dispatch.Pointer, ExecuteEvents.pointerClickHandler);
            }

            FieldInfo clickEvent = module.GetType().GetField("pointerDidClickEvent", Any);
            Delegate clickCallback = clickEvent == null ? null : clickEvent.GetValue(module) as Delegate;
            if (clickCallback != null && pointerPress != null && dispatch.Pointer.eligibleForClick)
                clickCallback.DynamicInvoke(new object[] { dispatch.Pointer });
            return true;
        }

        static void InvokeMousePress(MethodInfo processMousePress, object module, PointerEventData pointer, string state)
        {
            Type mouseDataType = processMousePress.GetParameters()[0].ParameterType;
            object mouseData = Activator.CreateInstance(mouseDataType);
            Type pressStateType = typeof(PointerEventData).GetNestedType("FramePressState", Any);
            object pressState = Enum.Parse(pressStateType, state);
            mouseDataType.GetField("buttonState", Any).SetValue(mouseData, pressState);
            mouseDataType.GetField("buttonData", Any).SetValue(mouseData, pointer);
            processMousePress.Invoke(module, new[] { mouseData });
        }

        object ResolveKeyboardInputModule()
        {
            Component manager = FindKeyboardManager();
            if (manager != null)
                keyboardManagerObserved = manager;
            object module = ReadMember(manager, "_vrInputModule", "vrInputModule");
            if (module != null)
                return module;
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem != null && eventSystem.currentInputModule != null)
                return eventSystem.currentInputModule;
            Type vrInputModule = FindType("VRUIControls.VRInputModule");
            return vrInputModule == null ? null : Resources.FindObjectsOfTypeAll(vrInputModule).OfType<Component>().FirstOrDefault(IsLiveSceneComponent);
        }

        void EnsurePointerPressWiring(object module)
        {
            if (module == null)
                return;
            Component manager = FindKeyboardManager();
            if (manager != null)
                keyboardManagerObserved = manager;
            EventInfo eventInfo = module.GetType().GetEvent("onProcessMousePressEvent", Any);
            if (eventInfo == null || eventInfo.EventHandlerType == null)
            {
                diagnostics.Add("pointer press event was unavailable on " + module.GetType().FullName);
                return;
            }

            if (!ReferenceEquals(pointerPressSpyModule, module))
            {
                RemovePointerPressSpy();
                try
                {
                    pointerPressSpyDelegate = Delegate.CreateDelegate(eventInfo.EventHandlerType, this,
                        GetType().GetMethod("OnPointerPressSpy", Any));
                    eventInfo.AddEventHandler(module, pointerPressSpyDelegate);
                    pointerPressSpyModule = module;
                    pointerPressSpyEvent = eventInfo;
                }
                catch (Exception exception)
                {
                    pointerPressSpyDelegate = null;
                    diagnostics.Add("attach pointer press spy: " + exception.GetBaseException().Message);
                }
            }

        }

        void OnPointerPressSpy(GameObject target)
        {
            pointerPressSpyCalls++;
            pointerPressSpyLastObject = TransformPath(target == null ? null : target.transform);
        }

        void RemovePointerPressSpy()
        {
            if (pointerPressSpyModule == null || pointerPressSpyEvent == null || pointerPressSpyDelegate == null)
            {
                pointerPressSpyModule = null;
                pointerPressSpyEvent = null;
                pointerPressSpyDelegate = null;
                return;
            }
            try { pointerPressSpyEvent.RemoveEventHandler(pointerPressSpyModule, pointerPressSpyDelegate); }
            catch (Exception exception) { diagnostics.Add("remove pointer press spy: " + exception.GetBaseException().Message); }
            pointerPressSpyModule = null;
            pointerPressSpyEvent = null;
            pointerPressSpyDelegate = null;
        }

        void RemovePointerPressWiring()
        {
            RemovePointerPressSpy();
        }

        object DescribePointerPressWiring(object module)
        {
            Component manager = FindKeyboardManager() ?? keyboardManagerObserved;
            Delegate callbacks = module == null ? null : ReadMember(module, "onProcessMousePressEvent") as Delegate;
            object[] invocation = callbacks == null ? new object[0] : callbacks.GetInvocationList().Select(x => new
            {
                targetType = x.Target == null ? null : x.Target.GetType().FullName,
                targetPath = TransformPath((x.Target as Component)?.transform),
                method = x.Method == null ? null : x.Method.DeclaringType.FullName + "." + x.Method.Name
            }).Cast<object>().ToArray();
            Behaviour managerBehaviour = manager as Behaviour;
            object selected = ReadMember(manager, "_selectedInput", "selectedInput");
            object managerModule = ReadMember(manager, "_vrInputModule", "vrInputModule");
            return new
            {
                module = module == null ? null : module.GetType().FullName,
                modulePath = TransformPath((module as Component)?.transform),
                callbackCount = invocation.Length,
                callbacks = invocation,
                spyAttached = pointerPressSpyModule != null,
                spyCalls = pointerPressSpyCalls,
                spyLastObject = pointerPressSpyLastObject,
                manager = manager == null ? null : manager.GetType().FullName,
                managerPath = TransformPath(manager == null ? null : manager.transform),
                managerEnabled = managerBehaviour == null ? (bool?)null : managerBehaviour.enabled,
                managerActive = manager != null && manager.gameObject.activeInHierarchy,
                selectedInput = TransformPath((selected as Component)?.transform),
                managerModule = managerModule == null ? null : managerModule.GetType().FullName,
                managerModuleMatches = managerModule != null && ReferenceEquals(managerModule, module)
            };
        }

        static bool IsVrGraphicRaycaster(BaseRaycaster raycaster)
        {
            return raycaster != null && String.Equals(raycaster.GetType().FullName, "VRUIControls.VRGraphicRaycaster", StringComparison.Ordinal);
        }

        sealed class PointerDispatch
        {
            public PointerEventData Pointer;
            public RaycastResult Hit;
            public GameObject HitObject;
            public GameObject EventHandler;
            public string ModuleName;
            public object InputModule;
            public string InputModuleName;
            public string DispatchPath;
            public string TargetPath;
            public string AimPath;
            public Vector3 RayOrigin;
            public Vector3 RayDirection;
            public int RaycastCount;
        }

        async Task RunNativeGameplayGuard(InputFieldView input)
        {
            phase = "native play and GameCore guard";
            ConfigureFactories();
            OpenInput(input);
            await WaitUntil(IsKeyboardVisible, 12, "native keyboard for gameplay guard");
            VoicePanel panel = await WaitForPanel(8);
            // The search field still contains one of the text-target fixtures
            // from the preceding checks.  Clear it through the real native
            // event exactly once so the game's table reloads its playable
            // levels before the pending recognition is started.
            SetInputText(input, "");
            input.onValueChanged.Invoke(input);
            Component table = null;
            object[] levels = new object[0];
            await WaitUntil(() =>
            {
                table = FindActiveComponentBySimpleName("LevelCollectionTableView", "");
                object values = ReadMember(table, "_beatmapLevels", "beatmapLevels");
                levels = values is IEnumerable enumerable
                    ? enumerable.Cast<object>().Where(x => !String.IsNullOrWhiteSpace(ReadStringMember(x, "levelID", "levelId"))).ToArray()
                    : new object[0];
                return table != null && levels.Length > 0;
            }, 20, "native song list after clearing search");
            levels = levels.OrderBy(x => ReadStringMember(x, "levelID", "levelId").StartsWith("custom_level_", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToArray();
            Check("native.search-cleared-for-play", String.IsNullOrEmpty(input.text) && levels.Length > 0,
                "textLength=" + (input.text == null ? 0 : input.text.Length) + "; levels=" + levels.Length);
            string before = input.text;
            Button record = GetMember<Button>(panel, "RecordButton", "recordButton");
            Check("native.gameplay-record-button", record != null, "VoicePanel.RecordButton");
            if (record == null)
                return;
            record.onClick.Invoke();
            await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 4, "gameplay guard capture start");
            int request = recognizer.Requests + 1;
            record.onClick.Invoke();
            await WaitUntil(() => recognizer.Requests >= request, 7, "gameplay guard pending recognition");

            MethodInfo selectLevel = FindMethod(table, "SelectLevel");
            Check("native.play-fixture", table != null && levels.Length > 0 && selectLevel != null,
                table == null ? "LevelCollectionTableView unavailable" : "levels=" + levels.Length + "; select=" + (selectLevel != null));
            if (table == null || levels.Length == 0 || selectLevel == null)
                return;

            Component detail = null;
            Button action = null;
            Button practice = null;
            foreach (object level in levels.Take(12))
            {
                try { selectLevel.Invoke(table, new[] { level }); } catch (Exception exception) { diagnostics.Add("select play fixture: " + exception.GetBaseException().Message); }
                await Task.Delay(250);
                detail = FindActiveComponentBySimpleName("StandardLevelDetailView", "");
                action = GetMember<Button>(detail, "actionButton", "ActionButton");
                practice = GetMember<Button>(detail, "_practiceButton", "practiceButton", "PracticeButton");
                if (action != null && action.interactable)
                    break;
            }
            Check("native.play-button", action != null && action.interactable, action == null ? "StandardLevelDetailView action button unavailable" : action.interactable.ToString());
            Check("native.practice-button", practice != null && practice.interactable,
                practice == null ? "StandardLevelDetailView practice button unavailable" : practice.interactable.ToString());
            if (action == null || !action.interactable)
            {
                CloseKeyboard();
                return;
            }

            action.onClick.Invoke();
            await WaitUntil(() => SceneManager.GetSceneByName("GameCore").isLoaded && FindActiveComponentBySimpleName("AudioTimeSyncController", "") != null,
                70, "native GameCore gameplay");
            gameCoreObserved = true;
            Check("gamecore.no-active-capture", FakeCapture.ActiveCount == 0 && (FakeCapture.Last == null || !FakeCapture.Last.IsRecording),
                "active captures=" + FakeCapture.ActiveCount);
            Check("gamecore.host-idle", String.Equals(ReadMember(host, "State", "state")?.ToString(), "Idle", StringComparison.Ordinal),
                ReadMember(host, "State", "state")?.ToString() ?? "null");
            recognizer.CompleteNext("游戏中旧结果");
            await Task.Delay(300);
            string afterGameplay = input == null ? "" : input.text;
            Check("gamecore.no-late-write", afterGameplay == before && afterGameplay.IndexOf("游戏中旧结果", StringComparison.Ordinal) < 0, afterGameplay);

            Component audio = FindActiveComponentBySimpleName("AudioTimeSyncController", "");
            float startTime = ReadMember(audio, "songTime") is float start ? start : 0;
            await Task.Delay(1300);
            float endTime = ReadMember(audio, "songTime") is float end ? end : startTime;
            Check("gamecore.audio-progress", endTime > startTime + .05f, "songTime=" + startTime.ToString("0.00") + "->" + endTime.ToString("0.00"));
            Capture("gameplay.png");

            Component pause = FindActiveComponentBySimpleName("PauseController", "");
            MethodInfo pauseMethod = FindMethod(pause, "Pause");
            Check("gamecore.pause-control", pause != null && pauseMethod != null, "PauseController");
            if (pauseMethod != null)
                pauseMethod.Invoke(pause, null);
            await ReturnFromGameCore("gamecore");

            await RunNativePracticeGuard();
        }

        async Task<bool> ReturnFromGameCore(string scope)
        {
            Component pause = FindActiveComponentBySimpleName("PauseController", "");
            if (pause == null)
            {
                Check(scope + ".return-control", false, "PauseController unavailable");
                return false;
            }

            // PauseController.ShowMenu starts the pause animation and leaves
            // PauseMenuManager interaction disabled for its first frame. The
            // native gameplay helpers wait for the menu animation to settle
            // before dispatching the menu event.
            await Task.Delay(1200);
            Component pauseMenu = null;
            MethodInfo menuButtonPressed = null;
            await WaitUntil(() =>
            {
                pauseMenu = ReadMember(pause, "_pauseMenuManager", "pauseMenuManager") as Component ??
                    FindActiveComponentBySimpleName("PauseMenuManager", "");
                menuButtonPressed = pauseMenu == null ? null : FindDeclaredMethodInHierarchy(pauseMenu.GetType(), "MenuButtonPressed", method => method.GetParameters().Length == 0);
                return IsLiveSceneComponent(pauseMenu) && menuButtonPressed != null;
            }, 15, scope + " pause menu event");

            Check(scope + ".return-control", menuButtonPressed != null,
                "PauseMenuManager.MenuButtonPressed; active=" + IsLiveSceneComponent(pauseMenu));
            if (menuButtonPressed == null)
                return false;
            menuButtonPressed.Invoke(pauseMenu, null);
            bool practiceReturn = scope.IndexOf("practice", StringComparison.OrdinalIgnoreCase) >= 0;
            await WaitUntil(() => !SceneManager.GetSceneByName("GameCore").isLoaded &&
                (IsLevelSelectionActive() || (practiceReturn && FindActiveComponentBySimpleName("PracticeViewController", "") != null)),
                45, scope + " return from GameCore");
            bool returned = !SceneManager.GetSceneByName("GameCore").isLoaded &&
                (IsLevelSelectionActive() || (practiceReturn && FindActiveComponentBySimpleName("PracticeViewController", "") != null));
            Check(scope + ".returned-to-menu", returned,
                practiceReturn ? "PracticeViewController restored after GameCore" : "native level selection restored");
            return returned;
        }

        async Task RunNativePracticeGuard()
        {
            phase = "native Practice lifecycle";
            Component detail = FindActiveComponentBySimpleName("StandardLevelDetailView", "");
            Button practice = GetMember<Button>(detail, "_practiceButton", "practiceButton", "PracticeButton");
            Check("native.practice-button", practice != null && practice.interactable,
                practice == null ? "StandardLevelDetailView practice button unavailable" : practice.interactable.ToString());
            if (practice == null || !practice.interactable)
                return;

            practice.onClick.Invoke();
            Component practiceView = null;
            Button practicePlay = null;
            await WaitUntil(() =>
            {
                practiceView = FindActiveComponentBySimpleName("PracticeViewController", "");
                practicePlay = GetMember<Button>(practiceView, "_playButton", "playButton", "PlayButton");
                return practiceView != null && practicePlay != null && practicePlay.interactable;
            }, 30, "native PracticeViewController");
            Check("native.practice-menu", practiceView != null && practicePlay != null && practicePlay.interactable,
                "PracticeViewController._playButton");
            if (practiceView == null || practicePlay == null || !practicePlay.interactable)
                return;

            Check("native.practice-settings", ReadMember(practiceView, "_practiceSettings", "practiceSettings") != null,
                "PracticeViewController._practiceSettings");
            practicePlay.onClick.Invoke();
            await WaitUntil(() => SceneManager.GetSceneByName("GameCore").isLoaded && FindActiveComponentBySimpleName("AudioTimeSyncController", "") != null,
                70, "native Practice GameCore gameplay");
            Check("native.practice-entered-gamecore", SceneManager.GetSceneByName("GameCore").isLoaded,
                "Practice play action entered GameCore");
            object setup = ResolveGameCoreType("GameplayCoreSceneSetupData");
            object practiceSettings = ReadMember(setup, "practiceSettings", "PracticeSettings");
            Check("native.practice-effective", setup != null && practiceSettings != null,
                setup == null ? "GameplayCoreSceneSetupData unavailable" : "practiceSettings=" + (practiceSettings != null));
            Check("native.practice-no-active-capture", FakeCapture.ActiveCount == 0,
                "active captures=" + FakeCapture.ActiveCount);
            Component audio = FindActiveComponentBySimpleName("AudioTimeSyncController", "");
            float startTime = ReadMember(audio, "songTime") is float start ? start : 0;
            await Task.Delay(1300);
            float endTime = ReadMember(audio, "songTime") is float end ? end : startTime;
            Check("native.practice-audio-progress", endTime > startTime + .05f,
                "songTime=" + startTime.ToString("0.00") + "->" + endTime.ToString("0.00"));
            Capture("practice-gameplay.png");
            await ReturnFromGameCore("native.practice");
        }

        object ResolveGameCoreType(string typeName)
        {
            try
            {
                Type contextType = FindType("Zenject.SceneContext");
                Type valueType = FindType(typeName);
                Component context = contextType == null ? null : Resources.FindObjectsOfTypeAll(contextType).OfType<Component>()
                    .FirstOrDefault(x => IsLiveSceneComponent(x) && x.gameObject.scene.name.IndexOf("GameCore", StringComparison.OrdinalIgnoreCase) >= 0);
                object container = ReadMember(context, "Container", "container");
                if (container == null || valueType == null)
                    return null;
                MethodInfo resolve = FindDeclaredMethodInHierarchy(container.GetType(), "Resolve", method =>
                    method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(Type));
                return resolve == null ? null : resolve.Invoke(container, new object[] { valueType });
            }
            catch (Exception exception)
            {
                diagnostics.Add("resolve GameCore setup: " + exception.GetBaseException().Message);
                return null;
            }
        }

        async Task ProbePhysicalMicrophone()
        {
            string[] devices = Microphone.devices ?? new string[0];
            WriteJson("microphone.json", new { devices, probed = false, samples = 0, detail = devices.Length == 0 ? "no microphone reported by Unity" : "pending" });
            if (devices.Length == 0)
            {
                Check("microphone.physical-probe", true, "Unity reported no microphone; deterministic fake capture remains local");
                return;
            }

            VoiceInput.UnityMicrophoneCapture capture = new VoiceInput.UnityMicrophoneCapture();
            int samples = 0;
            string detail = "";
            bool passed = false;
            try
            {
                capture.Start(devices[0], 3);
                await Task.Delay(1500);
                capture.Tick();
                AudioSamples result = capture.Stop();
                samples = result == null || result.Samples == null ? 0 : result.Samples.Length;
                passed = samples > 0;
                detail = "device=" + devices[0] + "; samples=" + samples;
            }
            catch (Exception exception)
            {
                detail = exception.GetBaseException().Message;
            }
            finally
            {
                capture.Dispose();
                WriteJson("microphone.json", new { devices, probed = true, samples, passed, detail });
            }
            Check("microphone.physical-probe", passed, detail);
        }

        async Task TestCharacterLimit(InputFieldView input, VoicePanel panel)
        {
            object rawLimit = ReadMember(input, "_textLengthLimit");
            int limit = rawLimit is int ? (int)rawLimit : 0;
            string originalText = input.text ?? "";
            try
            {
                if (limit <= 0)
                {
                    Check("native.max-limit", false, "active native InputFieldView has no configured text-length limit");
                    return;
                }

                SetInputText(input, new string('一', Math.Max(1, limit - 1)));
                Button record = GetMember<Button>(panel, "RecordButton", "recordButton");
                record.onClick.Invoke();
                await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 4, "max-limit capture start");
                record.onClick.Invoke();
                await WaitUntil(() => recognizer.Requests >= 3, 7, "max-limit recognizer request");
                recognizer.CompleteNext("甲乙丙");
                await Task.Delay(450);
                Check("native.max-limit", input.text.Length <= limit, "limit=" + limit + "; length=" + input.text.Length + "; text=" + input.text);
            }
            finally
            {
                SetInputText(input, originalText);
            }
        }

        async Task TestFailureRetry(InputFieldView input, VoicePanel panel)
        {
            SetInputText(input, "失败重试前");
            Button record = GetMember<Button>(panel, "RecordButton", "recordButton");
            int failedRequest = recognizer.Requests + 1;
            record.onClick.Invoke();
            await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 4, "failure capture start");
            record.onClick.Invoke();
            await WaitUntil(() => recognizer.Requests >= failedRequest, 7, "failure recognizer request");
            recognizer.FailNext(new SpeechFault("provider", "runtime-smoke", "模拟可重试识别失败", true));
            await WaitUntil(() => String.Equals(ReadMember(host, "State", "state")?.ToString(), "Failed", StringComparison.Ordinal), 8, "failure state");
            Check("native.failure-state", String.Equals(ReadMember(host, "State", "state")?.ToString(), "Failed", StringComparison.Ordinal), "RuntimeHost.State");

            int retryRequest = recognizer.Requests + 1;
            record = GetMember<Button>(panel, "RecordButton", "recordButton");
            Check("native.failure-retry-control", record != null && record.gameObject.activeInHierarchy && record.interactable, "retry RecordButton");
            record.onClick.Invoke();
            await WaitUntil(() => recognizer.Requests >= retryRequest, 7, "retry recognizer request");
            recognizer.CompleteNext("重试成功");
            await WaitUntil(() => input.text.IndexOf("重试成功", StringComparison.Ordinal) >= 0, 8, "retry append");
            Check("native.failure-retry-appends", input.text.IndexOf("重试成功", StringComparison.Ordinal) >= 0, input.text);
        }

        async Task<bool> RunBsmlKeyboardChecks()
        {
            Type flowType = FindType("BetterSongSearch.UI.BSSFlowCoordinator");
            if (flowType == null)
            {
                Check("bsml.keyboard-flow", false, "BetterSongSearch.UI.BSSFlowCoordinator assembly is absent");
                return false;
            }

            Button entry = FindButton(new[] { "搜歌与下载", "Better Song Search", "BetterSongSearch", "Search and Download" });
            if (entry == null)
            {
                Check("bsml.menu-entry", false, "BetterSongSearch menu entry was not visible");
                return false;
            }

            entry.onClick.Invoke();
            await WaitUntil(() => Resources.FindObjectsOfTypeAll(flowType).OfType<Component>().Any(IsLiveSceneComponent), 30, "BetterSongSearch flow");
            object flow = Resources.FindObjectsOfTypeAll(flowType).OfType<Component>().FirstOrDefault(IsLiveSceneComponent);
            object songList = ReadMember(flow, "songListView", "_songListView");
            if (songList == null)
            {
                Check("bsml.song-list", false, "BetterSongSearch song list view was not discoverable");
                return false;
            }

            ConfigureFactories();
            await RunBssSongSearchKeyboardCheck(songList);

            MethodInfo showPlaylist = FindMethod(songList, "ShowPlaylistCreation");
            if (showPlaylist == null)
            {
                Check("bsml.playlist-name", false, "BetterSongSearch playlist creation action was not discoverable");
                return false;
            }

            showPlaylist.Invoke(songList, null);
            await Task.Delay(600);
            Component setting = FindBssSplitSetting("BetterSongSearch.UI.SplitViews.PlaylistCreation", "playlistName");
            if (setting == null)
            {
                Check("bsml.playlist-name", false, "PlaylistCreation.instance.playlistName was not active");
                return false;
            }

            object modalKeyboard = ReadMember(setting, "ModalKeyboard", "modalKeyboard");
            Component modalView = ReadMember(modalKeyboard, "ModalView", "modalView") as Component;
            if (modalKeyboard == null || modalView == null)
            {
                Check("bsml.modal-keyboard", false, "StringSetting.ModalKeyboard/ModalView unavailable");
                return false;
            }

            string before = ReadStringMember(setting, "Text", "text") ?? "";
            TryOpenSetting(setting, modalKeyboard, modalView);
            await WaitForBsmlModalSettled(modalKeyboard, modalView, 15, "BSML ModalKeyboard");
            Transform initialRoot = FindBsmlTargetRoot(ReadMember(modalKeyboard, "Keyboard", "keyboard"));
            VoicePanel panel = await WaitForPanelRoot(initialRoot, 10);
            CheckBsmlPanelAttachment("bsml", panel, modalKeyboard);
            Capture("bsml-keyboard.png");

            object target = ReadMember(panel, "Target", "target");
            Check("bsml.text-target", target != null && target.GetType().Name.IndexOf("Bsml", StringComparison.OrdinalIgnoreCase) >= 0,
                target == null ? "VoicePanel target unavailable" : target.GetType().FullName);

            Button record = GetMember<Button>(panel, "RecordButton", "recordButton");
            Check("bsml.record-button", record != null, "BSML VoicePanel.RecordButton");
            if (record == null)
                return false;

            await RunBsmlPointerCancelRegression(panel, modalKeyboard, modalView);
            record = GetMember<Button>(panel, "RecordButton", "recordButton");
            await DispatchPointerClick(record, "bsml.microphone-pointer");
            await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 4, "BSML capture start");
            Check("bsml.microphone-pointer-keeps-keyboard", IsBsmlKeyboardVisible(modalKeyboard, modalView),
                "BSML ModalKeyboard remained shown after microphone pointer click");
            FakeCapture bsmlRecording = FakeCapture.Last;
            bsmlRecording.LevelValue = 0.8f;
            await Task.Delay(300);
            Capture("bsml-recording-focused.png", false, panel.transform);
            await DispatchPointerClick(record, "bsml.microphone-stop-pointer");
            await WaitUntil(() => recognizer.Requests >= 1, 7, "BSML recognizer request");
            recognizer.CompleteNext("语音名");
            await Task.Delay(500);
            string pending = ReadStringMember(setting, "Text", "text") ?? "";
            Check("bsml.append-stays-pending", pending == before, "before=" + before + "; setting=" + pending);
            if (pointerOnly)
                return true;

            Button confirm = FindOriginalKeyboardConfirm(modalView);
            bool confirmed = false;
            if (confirm != null)
            {
                confirm.onClick.Invoke();
                confirmed = true;
            }
            else
            {
                confirmed = InvokeOriginalKeyboardConfirm(modalKeyboard);
            }
            Check("bsml.original-confirm", confirmed, "original ModalKeyboard confirmation control");
            if (confirmed)
            {
                await Task.Delay(500);
                string committed = ReadStringMember(setting, "Text", "text") ?? "";
                Check("bsml.original-confirm-commits", committed.IndexOf("语音名", StringComparison.Ordinal) >= 0, committed);
            }

            CloseKeyboard();
            await Task.Delay(400);
            TryOpenSetting(setting, modalKeyboard, modalView);
            await WaitForBsmlModalSettled(modalKeyboard, modalView, 12, "BSML keyboard reopen");
            object reopenedKeyboard = ReadMember(modalKeyboard, "Keyboard", "keyboard");
            Transform reopenedRoot = FindBsmlTargetRoot(reopenedKeyboard);
            await WaitForPanelRoot(reopenedRoot, 8);
            Check("bsml.reopen-no-duplicate-panel", CountPanelsForRoot(reopenedRoot) <= 1, "active VoicePanel count=" + CountPanelsForRoot(reopenedRoot));
            Capture("bsml-reopen.png");
            await TestBsmlDistinctKeyboardSwitch(setting, modalKeyboard, modalView);
            return true;
        }

        async Task RunBssSongSearchKeyboardCheck(object songList)
        {
            InputFieldView input = ReadMember(songList, "songSearchInput") as InputFieldView;
            Check("bsml.song-search-input", input != null, "BetterSongSearch SongListController.songSearchInput");
            if (input == null)
                return;

            string originalText = input.text ?? "";
            int originalCount = ReadBssSearchCount(songList);
            int beforeRevision = ReadIntMember(songList, "revision");
            string query = "zzzzvinputnomapzz";
            int changes = 0;
            UnityEngine.Events.UnityAction<InputFieldView> listener = delegate { changes++; };
            input.onValueChanged.AddListener(listener);
            try
            {
                phase = "BetterSongSearch search keyboard";
                OpenInput(input);
                await WaitUntil(IsKeyboardVisible, 18, "BSS song search UIKeyboardManager");
                VoicePanel panel = await WaitForPanel(18);
                CheckPanelAttachment("bsml-search", panel);

                Button record = GetMember<Button>(panel, "RecordButton", "recordButton");
                Check("bsml.song-search-record-button", record != null, "BSS search VoicePanel.RecordButton");
                if (record == null)
                    return;

                int request = recognizer.Requests + 1;
                record.onClick.Invoke();
                await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 4, "BSS search capture start");
                record.onClick.Invoke();
                await WaitUntil(() => recognizer.Requests >= request, 7, "BSS search recognizer request");
                recognizer.CompleteNext(query);
                try
                {
                    await WaitUntil(() => input.text.IndexOf(query, StringComparison.Ordinal) >= 0, 8, "BSS search append");
                }
                catch (Exception exception)
                {
                    int limit = ReadIntMember(input, "_textLengthLimit", "textLengthLimit", "characterLimit");
                    diagnostics.Add("BSS search append failed: actualText=" + input.text +
                        "; actualLength=" + (input.text == null ? 0 : input.text.Length) +
                        "; limit=" + limit + "; actualUpper=" + (input.text ?? "").ToUpperInvariant() +
                        "; expected=" + query + "; error=" + exception.GetBaseException().Message);
                    throw;
                }
                await WaitUntil(() => ReadIntMember(songList, "revision") > beforeRevision, 8, "BSS search revision");
                await Task.Delay(350);
                await WaitUntil(() => ReadBssSearchCount(songList) == 0 && !IsBssSearchInProgress(songList), 20,
                    "BSS empty search result");

                int afterRevision = ReadIntMember(songList, "revision");
                int afterCount = ReadBssSearchCount(songList);
                Check("bsml.song-search-appends", input.text == originalText + query,
                    "before=" + originalText + "; after=" + input.text);
                Check("bsml.song-search-onValueChanged-once", changes == 1, "delta=" + changes);
                Check("bsml.song-search-revision-increments", afterRevision > beforeRevision,
                    "revision=" + beforeRevision + "->" + afterRevision);
                Check("bsml.song-search-empty-results", afterCount == 0, "searchedSongsList=" + afterCount);
            }
            finally
            {
                try
                {
                    SetInputText(input, originalText);
                    input.onValueChanged.Invoke(input);
                    await Task.Delay(450);
                    Check("bsml.song-search-restored", input.text == originalText &&
                        (originalCount < 0 || ReadBssSearchCount(songList) == originalCount),
                        "text=" + input.text + "; searchedSongsList=" + ReadBssSearchCount(songList));
                }
                catch (Exception exception)
                {
                    Check("bsml.song-search-restored", false, exception.GetBaseException().Message);
                }

                input.onValueChanged.RemoveListener(listener);
                CloseKeyboard();
                try { await WaitUntil(() => !IsKeyboardVisible(), 8, "BSS search keyboard close"); }
                catch (Exception exception) { diagnostics.Add("BSS search keyboard close: " + exception.GetBaseException().Message); }

                // The playlist/preset checks intentionally start with a fresh
                // fake recognizer so their request counters are independent
                // of this search-field exercise.
                ConfigureFactories();
            }
        }

        async Task TestBsmlDistinctKeyboardSwitch(Component oldSetting, object oldModalKeyboard, Component oldModalView)
        {
            string before = ReadStringMember(oldSetting, "Text", "text") ?? "";
            VoicePanel oldPanel = FindActivePanel();
            Button oldRecord = GetMember<Button>(oldPanel, "RecordButton", "recordButton");
            if (oldRecord == null)
            {
                Check("bsml.switch-distinct-keyboard", false, "current BSML VoicePanel record button was unavailable");
                return;
            }
            int oldRequest = recognizer.Requests + 1;
            oldRecord.onClick.Invoke();
            await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 4, "BSML switch capture start");
            oldRecord.onClick.Invoke();
            await WaitUntil(() => recognizer.Requests >= oldRequest, 7, "BSML switch recognizer request");

            oldModalView.GetType().GetMethod("Hide", Any, null, new[] { typeof(bool), typeof(Action) }, null)?.Invoke(oldModalView, new object[] { false, null });
            foreach (Component modal in oldSetting.GetComponentsInParent<ModalView>(true))
            {
                if (modal != oldModalView)
                    modal.GetType().GetMethod("Hide", Any, null, new[] { typeof(bool), typeof(Action) }, null)?.Invoke(modal, new object[] { false, null });
            }
            await Task.Delay(350);
            recognizer.CompleteNext("切换旧结果");
            await Task.Delay(400);
            Check("bsml.switch-cancels-old-result", ReadStringMember(oldSetting, "Text", "text") == before,
                "old setting=" + ReadStringMember(oldSetting, "Text", "text"));

            Component filter = FindActiveComponentBySimpleName("FilterView", "");
            MethodInfo showPresets = filter == null ? null : FindDeclaredMethodInHierarchy(filter.GetType(), "ShowPresets", method => method.GetParameters().Length == 0);
            if (showPresets == null)
            {
                Check("bsml.switch-distinct-keyboard", false, "BetterSongSearch FilterView.ShowPresets was unavailable");
                return;
            }
            showPresets.Invoke(filter, null);
            object presetParams = null;
            await WaitUntil(() => (presetParams = ReadMember(filter, "presetsViewParams", "_presetsViewParams")) != null,
                10, "BetterSongSearch presets parser params");
            MethodInfo emitPresetModal = FindDeclaredMethodInHierarchy(presetParams.GetType(), "EmitEvent", method =>
                method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(string));
            Check("bsml.preset-name-control", emitPresetModal != null, "FilterView.presetsViewParams.EmitEvent");
            if (emitPresetModal == null)
                return;
            emitPresetModal.Invoke(presetParams, new object[] { "OpenAddPresetModal" });

            Component newSetting = null;
            await WaitUntil(() => (newSetting = FindBssSplitSetting("BetterSongSearch.UI.SplitViews.Presets", "newPresetName")) != null,
                12, "Presets.instance.newPresetName");
            if (newSetting == null)
            {
                Check("bsml.switch-distinct-keyboard", false, "Presets.instance.newPresetName was not active");
                return;
            }
            object newModalKeyboard = ReadMember(newSetting, "ModalKeyboard", "modalKeyboard");
            Component newModalView = ReadMember(newModalKeyboard, "ModalView", "modalView") as Component;
            if (newModalKeyboard == null || newModalView == null)
            {
                Check("bsml.switch-distinct-keyboard", false, "preset ModalKeyboard/ModalView was unavailable");
                return;
            }
            TryOpenSetting(newSetting, newModalKeyboard, newModalView);
            await WaitForBsmlModalSettled(newModalKeyboard, newModalView, 12, "BSML preset keyboard");
            object newKeyboard = ReadMember(newModalKeyboard, "Keyboard", "keyboard");
            Transform newRoot = FindBsmlTargetRoot(newKeyboard);
            VoicePanel newPanel = await WaitForPanelRoot(newRoot, 8);
            CheckBsmlPanelAttachment("bsml-switch", newPanel, newModalKeyboard);
            Check("bsml.switch-distinct-target", newPanel != oldPanel && ReadMember(newPanel, "Target", "target") != ReadMember(oldPanel, "Target", "target"),
                "old/new VoicePanel targets");

            string presetBefore = ReadStringMember(newSetting, "Text", "text") ?? "";
            Button presetRecord = GetMember<Button>(newPanel, "RecordButton", "recordButton");
            Check("bsml.preset-record-button", presetRecord != null, "preset VoicePanel.RecordButton");
            if (presetRecord != null)
            {
                int presetRequest = recognizer.Requests + 1;
                presetRecord.onClick.Invoke();
                await WaitUntil(() => FakeCapture.Last != null && FakeCapture.Last.IsRecording, 4, "preset capture start");
                presetRecord.onClick.Invoke();
                await WaitUntil(() => recognizer.Requests >= presetRequest, 7, "preset recognizer request");
                recognizer.CompleteNext("预设名");
                await Task.Delay(500);
                Check("bsml.preset-append-stays-pending", ReadStringMember(newSetting, "Text", "text") == presetBefore,
                    "before=" + presetBefore + "; setting=" + ReadStringMember(newSetting, "Text", "text"));

                Button presetConfirm = FindOriginalKeyboardConfirm(newModalView);
                bool presetConfirmed = presetConfirm != null;
                if (presetConfirm != null)
                    presetConfirm.onClick.Invoke();
                else
                    presetConfirmed = InvokeOriginalKeyboardConfirm(newModalKeyboard);
                Check("bsml.preset-original-confirm", presetConfirmed, "original preset ModalKeyboard confirmation control");
                if (presetConfirmed)
                {
                    await Task.Delay(500);
                    string committedPreset = ReadStringMember(newSetting, "Text", "text") ?? "";
                    Check("bsml.preset-original-confirm-commits", committedPreset.IndexOf("预设名", StringComparison.Ordinal) >= 0,
                        committedPreset);
                }
            }
            HideModalView(newModalView);
            HideModalView(oldModalView as Component);
        }

        void ConfigureFactories()
        {
            host = RuntimeHost.Current;
            Check("runtime-host.current", host != null, "RuntimeHost.Current");
            if (host == null)
                throw new InvalidOperationException("RuntimeHost.Current is null");

            FakeCapture.Reset();
            recognizer = new FakeRecognizer();
            bool captureSet = SetMember(host, "CaptureFactory", (Func<IAudioCapture>)(() => new FakeCapture()));
            bool recognizerSet = SetMember(host, "RecognizerFactory", (Func<ISpeechRecognizer>)(() => recognizer));
            Check("runtime-host.capture-seam", captureSet, "CaptureFactory seam");
            Check("runtime-host.recognizer-seam", recognizerSet, "RecognizerFactory seam");
            if (!captureSet || !recognizerSet)
                throw new InvalidOperationException("RuntimeHost factory seams were not writable");
        }

        void CheckPanelAttachment(string scope, VoicePanel panel)
        {
            Check(scope + ".panel-present", panel != null && panel.gameObject.activeInHierarchy, "VoicePanel");
            if (panel == null)
                return;

            TMP_Text status = panel.Status;
            Button primary = panel.RecordButton;
            TMP_Text label = primary == null ? null : primary.GetComponentInChildren<TMP_Text>(true);
            LayoutElement layout = primary == null ? null : primary.GetComponent<LayoutElement>();
            RectTransform primaryRect = primary == null ? null : primary.transform as RectTransform;
            float primaryHeight = Math.Max(layout == null ? 0 : layout.preferredHeight, primaryRect == null ? 0 : primaryRect.rect.height);
            Check(scope + ".status-wraps", status != null && status.enableWordWrapping && status.overflowMode != TextOverflowModes.Ellipsis,
                "status and errors must wrap without ellipsis");
            Check(scope + ".primary-control-size", label != null && label.fontSize >= 3 && primaryHeight >= 7.95f,
                "primary font=" + (label == null ? 0 : label.fontSize) + "; height=" + primaryHeight);

            Component manager = FindKeyboardManager();
            Transform container = FindKeyboardContainer(manager);
            bool descendant = container != null && panel.transform.IsChildOf(container);
            Check(scope + ".panel-descendant-of-keyboard", descendant,
                "panel=" + TransformPath(panel.transform) + "; container=" + TransformPath(container));
            WriteJson(scope + "-panel.json", DescribePanel(panel, manager, container));
        }

        void CheckBsmlPanelAttachment(string scope, VoicePanel panel, object modalKeyboard)
        {
            Check(scope + ".panel-present", panel != null && panel.gameObject.activeInHierarchy, "VoicePanel");
            if (panel == null)
                return;
            object target = ReadMember(panel, "Target", "target");
            Transform root = ReadMember(target, "Root", "root") as Transform;
            bool descendant = root != null && panel.transform.IsChildOf(root);
            Transform keyboardParent = root == null ? null : root.parent;
            bool parentDescendant = keyboardParent != null && panel.transform.IsChildOf(keyboardParent);
            Check(scope + ".panel-descendant-of-bsml-keyboard", descendant || parentDescendant,
                "panel=" + TransformPath(panel.transform) + "; root=" + TransformPath(root) +
                "; rootDescendant=" + descendant + "; parent=" + TransformPath(keyboardParent) +
                "; parentDescendant=" + parentDescendant);
            Capture(scope + "-focused.png", false, panel.transform);
            RectTransform rootRect = root as RectTransform;
            RectTransform panelRect = panel.transform as RectTransform;
            object keyboard = ReadMember(modalKeyboard, "Keyboard", "keyboard");
            RectTransform keyboardRect = (keyboard as Component)?.transform as RectTransform;
            WriteJson(scope + "-geometry.json", new
            {
                keyboard = DescribeRect(keyboardRect),
                root = DescribeRect(rootRect),
                rootParent = DescribeRect(rootRect == null ? null : rootRect.parent as RectTransform),
                panel = DescribeRect(panelRect),
                panelParent = DescribeRect(panelRect == null ? null : panelRect.parent as RectTransform)
            });
            WriteBsmlVisibilityDiagnostics(scope, panel, root);
            WriteJson(scope + "-panel.json", new
            {
                panel = TransformPath(panel.transform),
                root = TransformPath(root),
                rootParent = TransformPath(keyboardParent),
                target = target == null ? null : target.GetType().FullName,
                active = panel.gameObject.activeInHierarchy,
                enabled = panel.enabled,
                activeAndEnabled = panel.isActiveAndEnabled,
                reserveMethod = target == null ? null : target.GetType().GetMethod("ReservePanelSpace", Any)?.ToString()
            });

            if (bsmlReserveProbe)
                ProbeBsmlReserveSpace(scope, panel, target);
        }

        void ProbeBsmlReserveSpace(string scope, VoicePanel panel, object target)
        {
            try
            {
                WriteJson(scope + "-reserve-before.json", new
                {
                    panel = DescribeRect(panel == null ? null : panel.transform as RectTransform),
                    modal = DescribeRect(panel == null || panel.transform.parent == null ? null : panel.transform.parent.parent as RectTransform),
                    targetType = target == null ? null : target.GetType().FullName,
                    enabled = panel != null && panel.enabled,
                    activeAndEnabled = panel != null && panel.isActiveAndEnabled
                });
                MethodInfo reserve = target == null ? null : target.GetType().GetMethod("ReservePanelSpace", Any);
                if (reserve == null)
                {
                    Check(scope + ".reserve-panel-space", false, "BsmlTextTarget.ReservePanelSpace unavailable");
                    return;
                }
                RectTransform rect = panel.transform as RectTransform;
                reserve.Invoke(target, new object[] { rect == null ? 38f : rect.rect.width, 5f });
                Canvas.ForceUpdateCanvases();
                WriteJson(scope + "-reserve-after.json", new
                {
                    panel = DescribeRect(panel == null ? null : panel.transform as RectTransform),
                    modal = DescribeRect(panel == null || panel.transform.parent == null ? null : panel.transform.parent.parent as RectTransform),
                    targetType = target.GetType().FullName
                });
                Check(scope + ".reserve-panel-space", true, "ReservePanelSpace invoked for diagnostic comparison");
            }
            catch (Exception exception)
            {
                Check(scope + ".reserve-panel-space", false, exception.GetBaseException().Message);
            }
        }

        void WriteBsmlVisibilityDiagnostics(string scope, VoicePanel panel, Transform root)
        {
            try
            {
                Canvas panelCanvas = panel == null ? null : panel.GetComponentInParent<Canvas>(true);
                var ancestors = new List<object>();
                Transform current = panel == null ? null : panel.transform;
                for (int depth = 0; current != null && depth < 16; depth++, current = current.parent)
                {
                    RectTransform rect = current as RectTransform;
                    Canvas canvas = current.GetComponent<Canvas>();
                    CanvasGroup group = current.GetComponent<CanvasGroup>();
                    RectMask2D rectMask = current.GetComponent<RectMask2D>();
                    Mask mask = current.GetComponent<Mask>();
                    ancestors.Add(new
                    {
                        path = TransformPath(current),
                        rect = DescribeRect(rect),
                        world = DescribeWorldRect(rect),
                        components = current.GetComponents<Component>().Where(component => component != null).Select(component => component.GetType().FullName).ToArray(),
                        canvas = canvas == null ? null : new { enabled = canvas.enabled, renderMode = canvas.renderMode.ToString(), overrideSorting = canvas.overrideSorting, sortingOrder = canvas.sortingOrder },
                        canvasGroup = group == null ? null : new { alpha = group.alpha, interactable = group.interactable, blocksRaycasts = group.blocksRaycasts },
                        rectMask = rectMask == null ? null : new { enabled = rectMask.enabled, showMaskGraphic = rectMask.isActiveAndEnabled },
                        mask = mask == null ? null : new { enabled = mask.enabled, showMaskGraphic = mask.showMaskGraphic }
                    });
                }

                var graphics = panel == null ? new object[0] : panel.GetComponentsInChildren<Graphic>(true)
                    .Where(graphic => graphic != null)
                    .Select(graphic =>
                    {
                        Material material = null;
                        try { material = graphic.material; } catch { }
                        return new
                        {
                            path = TransformPath(graphic.transform),
                            type = graphic.GetType().FullName,
                            active = graphic.gameObject.activeInHierarchy,
                            enabled = graphic.enabled,
                            maskable = graphic is MaskableGraphic maskableGraphic && maskableGraphic.maskable,
                            cull = graphic.canvasRenderer.cull,
                            alpha = graphic.canvasRenderer.GetColor().a,
                            rect = DescribeRect(graphic.transform as RectTransform),
                            world = DescribeWorldRect(graphic.transform as RectTransform),
                            curvedCanvas = DescribeCurvedCanvas(graphic.canvas, graphic.transform),
                            material = material == null ? null : material.name,
                            shader = material == null || material.shader == null ? null : material.shader.name
                        };
                    }).Cast<object>().ToArray();

                WriteJson(scope + "-visibility.json", new
                {
                    targetRoot = TransformPath(root),
                    curvedCanvas = DescribeCurvedCanvas(panelCanvas, panel == null ? null : panel.transform),
                    ancestors,
                    graphics,
                    canvasGroups = panel == null ? new object[0] : panel.GetComponentsInParent<CanvasGroup>(true)
                        .Where(group => group != null)
                        .Select(group => new { path = TransformPath(group.transform), alpha = group.alpha, interactable = group.interactable, blocksRaycasts = group.blocksRaycasts }).ToArray(),
                    rectMasks = panel == null ? new object[0] : panel.GetComponentsInParent<RectMask2D>(true)
                        .Where(mask => mask != null)
                        .Select(mask => new { path = TransformPath(mask.transform), enabled = mask.enabled, rect = DescribeRect(mask.transform as RectTransform), world = DescribeWorldRect(mask.transform as RectTransform) }).ToArray(),
                    masks = panel == null ? new object[0] : panel.GetComponentsInParent<Mask>(true)
                        .Where(mask => mask != null)
                        .Select(mask => new { path = TransformPath(mask.transform), enabled = mask.enabled, showMaskGraphic = mask.showMaskGraphic, rect = DescribeRect(mask.transform as RectTransform), world = DescribeWorldRect(mask.transform as RectTransform) }).ToArray()
                });
            }
            catch (Exception exception)
            {
                diagnostics.Add("BSML visibility diagnostics: " + exception.GetBaseException().Message);
            }
        }

        void CheckRecordingVisuals(VoicePanel panel)
        {
            string status = ReadStringMember(panel, "Status", "status") ?? "";
            string timerText = ReadStringMember(panel, "Timer", "timer") ?? "";
            object state = ReadMember(host, "State", "state");
            Button record = GetMember<Button>(panel, "RecordButton", "recordButton");
            Color? color = record == null ? (Color?)null : record.GetComponentsInChildren<Image>(true).Select(x => (Color?)x.color).FirstOrDefault();
            TMP_Text statusText = ReadMember(panel, "Status", "status") as TMP_Text;
            bool statusRed = statusText != null && statusText.color.r > statusText.color.g * 1.25f && statusText.color.r > statusText.color.b * 1.25f;
            bool red = statusRed || (color.HasValue && color.Value.r > color.Value.g * 1.25f && color.Value.r > color.Value.b * 1.25f);
            Component meterComponent = ReadMember(panel, "meter", "Meter") as Component;
            bool meter = meterComponent != null && meterComponent.gameObject.activeInHierarchy ||
                panel.GetComponentsInChildren<HorizontalLayoutGroup>(true).Any(x => x.gameObject.activeInHierarchy &&
                    (x.name.IndexOf("meter", StringComparison.OrdinalIgnoreCase) >= 0 || x.name.IndexOf("volume", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                panel.GetComponentsInChildren<Image>(true).Any(x => x.gameObject.activeInHierarchy &&
                    (x.name.IndexOf("VolumeTrack", StringComparison.OrdinalIgnoreCase) >= 0 || x.name.IndexOf("Volume", StringComparison.OrdinalIgnoreCase) >= 0));
            Check("native.recording-red", red, color.HasValue ? color.Value.ToString() : "button image unavailable");
            Check("native.recording-meter", meter, "active meter layout");
            Check("native.recording-timer", !String.IsNullOrWhiteSpace(timerText), timerText);
            Check("native.recording-status", !String.IsNullOrWhiteSpace(status), status);
            Check("native.state-recording", state == null || String.Equals(state.ToString(), "Recording", StringComparison.Ordinal), state == null ? "RuntimeHost.State unavailable" : state.ToString());
            Check("native.active-panel-recording", ReferenceEquals(ReadMember(host, "ActivePanel", "activePanel"), panel), "RuntimeHost.ActivePanel");
        }

        void CheckRecognizingVisuals(VoicePanel panel)
        {
            string status = ReadStringMember(panel, "Status", "status") ?? "";
            object state = ReadMember(host, "State", "state");
            Button cancel = GetMember<Button>(panel, "CancelButton", "cancelButton");
            bool spinner = panel.GetComponentsInChildren<Animator>(true).Any(x => x.gameObject.activeInHierarchy) ||
                panel.GetComponentsInChildren<ImageView>(true).Any(x => x.gameObject.activeInHierarchy && x.name.IndexOf("busy", StringComparison.OrdinalIgnoreCase) >= 0);
            Check("native.recognizing-cancel", cancel != null && cancel.gameObject.activeInHierarchy, "CancelButton active");
            Check("native.recognizing-spinner", spinner || !String.IsNullOrWhiteSpace(status), status);
            Check("native.state-recognizing", state == null || String.Equals(state.ToString(), "Recognizing", StringComparison.Ordinal), state == null ? "RuntimeHost.State unavailable" : state.ToString());
            Check("native.active-panel-recognizing", ReferenceEquals(ReadMember(host, "ActivePanel", "activePanel"), panel), "RuntimeHost.ActivePanel");
        }

        void CheckGameCoreMicrophoneGuard()
        {
            bool noCapture = FakeCapture.ActiveCount == 0;
            Check("gamecore.no-microphone", gameCoreObserved && noCapture,
                gameCoreObserved ? "observed GameCore; active fake captures=" + FakeCapture.ActiveCount : "GameCore gameplay lifecycle was not reached");
        }

        async Task<VoicePanel> WaitForPanel(int seconds)
        {
            await WaitUntil(() => FindActivePanel() != null, seconds, "VoicePanel");
            return FindActivePanel();
        }

        async Task WaitForBsmlModalSettled(object modalKeyboard, Component modalView, int seconds, string label)
        {
            await WaitUntil(() => IsBsmlKeyboardVisible(modalKeyboard, modalView), seconds, label);

            // ModalView.isShown becomes true at the start of its show
            // transition. Wait for two observed Unity frames and the normal
            // BSML modal animation window before taking geometry or panel
            // screenshots. This also gives VoicePanel.Start time to run.
            int startFrame = Time.frameCount;
            float frameDeadline = Time.realtimeSinceStartup + Math.Min(seconds, 3);
            while (Time.frameCount < startFrame + 2 && Time.realtimeSinceStartup < frameDeadline)
                await Task.Delay(25);
            await Task.Delay(600);
            Check(label + ".settled", IsBsmlKeyboardVisible(modalKeyboard, modalView), "ModalView remained visible after show transition");
        }

        async Task<VoicePanel> WaitForPanelRoot(Transform root, int seconds)
        {
            VoicePanel panel = null;
            await WaitUntil(() => (panel = Resources.FindObjectsOfTypeAll<VoicePanel>().FirstOrDefault(x => IsLiveSceneComponent(x) &&
                root != null && x.transform.IsChildOf(root))) != null, seconds, "VoicePanel root");
            return panel;
        }

        async Task<InputFieldView> WaitForNativeSearchInput(int seconds)
        {
            InputFieldView result = null;
            float deadline = Time.realtimeSinceStartup + seconds;
            while ((result = FindNativeSearchInput()) == null && Time.realtimeSinceStartup < deadline)
            {
                RevealNativeSearchInput();
                await Task.Delay(200);
            }
            if (result == null)
                throw new TimeoutException("native search InputFieldView timed out after " + seconds + " seconds");
            return result;
        }

        async Task WaitUntil(Func<bool> predicate, int seconds, string label)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!predicate() && Time.realtimeSinceStartup < deadline)
                await Task.Delay(100);
            if (!predicate())
                throw new TimeoutException(label + " timed out after " + seconds + " seconds");
        }

        void OpenInput(InputFieldView input)
        {
            Component manager = FindKeyboardManager();
            if (manager != null)
            {
                MethodInfo openKeyboard = manager.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == "OpenKeyboardFor" &&
                    x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType.IsInstanceOfType(input));
                if (openKeyboard != null)
                {
                    try
                    {
                        openKeyboard.Invoke(manager, new object[] { input });
                        return;
                    }
                    catch (Exception exception)
                    {
                        diagnostics.Add("open native keyboard manager: " + exception.GetBaseException().Message);
                    }
                }
                else
                {
                    diagnostics.Add("UIKeyboardManager.OpenKeyboardFor(InputFieldView) was not found");
                }
            }

            EventSystem eventSystem = EventSystem.current;
            PointerEventData pointer = eventSystem == null ? null : new PointerEventData(eventSystem) { button = PointerEventData.InputButton.Left };
            MethodInfo click = input.GetType().GetMethod("OnPointerClick", Any, null, new[] { typeof(PointerEventData) }, null);
            if (click != null && pointer != null)
            {
                click.Invoke(input, new object[] { pointer });
                return;
            }

            MethodInfo activateKeyboard = input.GetType().GetMethod("ActivateKeyboard", Any, null, Type.EmptyTypes, null);
            if (activateKeyboard != null)
            {
                activateKeyboard.Invoke(input, null);
                return;
            }

            MethodInfo select = input.GetType().GetMethod("Select", Any, null, Type.EmptyTypes, null);
            if (select != null)
                select.Invoke(input, null);
            else
            {
                MethodInfo activate = input.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == "ActivateInputField" && x.GetParameters().Length == 0);
                if (activate != null)
                    activate.Invoke(input, null);
            }
        }

        void CloseKeyboard()
        {
            Component manager = FindKeyboardManager();
            if (manager != null)
            {
                foreach (string name in new[] { "CloseKeyboard", "HideKeyboard", "Hide", "Dismiss", "Close" })
                {
                    MethodInfo method = manager.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == 0);
                    if (method == null)
                        continue;
                    try { method.Invoke(manager, null); return; } catch (Exception exception) { diagnostics.Add("close keyboard " + name + ": " + exception.GetBaseException().Message); }
                }
            }

            InputFieldView input = FindActiveInputForKeyboard();
            if (input != null)
            {
                MethodInfo deactivate = input.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == "DeactivateInputField" && x.GetParameters().Length == 0);
                if (deactivate != null)
                    deactivate.Invoke(input, null);
            }
        }

        async Task ReturnToMainMenu()
        {
            CloseKeyboard();
            await Task.Delay(500);
            Component flow = FindActiveComponentBySimpleName("SoloFreePlayFlowCoordinator", "") ??
                FindActiveComponentBySimpleName("SinglePlayerLevelSelectionFlowCoordinator", "");
            Check("native.main-menu-return-control", flow != null, "SinglePlayerLevelSelectionFlowCoordinator");
            if (flow == null)
                throw new InvalidOperationException("active single-player level-selection flow was unavailable");

            MethodInfo backMethod = FindDeclaredMethodInHierarchy(flow.GetType(), "BackButtonWasPressed", method => method.GetParameters().Length == 1);
            Check("native.main-menu-return-method", backMethod != null, "BackButtonWasPressed(HMUI.ViewController)");
            if (backMethod == null)
                throw new MissingMethodException(flow.GetType().FullName, "BackButtonWasPressed");

            ParameterInfo parameter = backMethod.GetParameters()[0];
            object view = ReadMember(flow, "topViewController", "_topViewController");
            if (view == null || !parameter.ParameterType.IsInstanceOfType(view))
                view = ReadMember(flow, "levelSelectionNavigationController", "_levelSelectionNavigationController");
            if (view == null || !parameter.ParameterType.IsInstanceOfType(view))
                view = FindActiveComponentBySimpleName("LevelCollectionNavigationController", "");
            if (view == null || !parameter.ParameterType.IsInstanceOfType(view))
                throw new InvalidOperationException("single-player back callback did not expose a HMUI.ViewController argument");
            backMethod.Invoke(flow, new[] { view });
            await WaitUntil(() => IsMainMenuActive() && !IsLevelSelectionActive(), 30, "return to main menu");
            Check("native.returned-to-main-menu", IsMainMenuActive() && !IsLevelSelectionActive(), "MainMenuViewController active; Solo flow dismissed");
            await Task.Delay(500);
        }

        async Task ReturnBssToMainMenu()
        {
            HideActiveBsmlModals();
            await Task.Delay(350);
            Component flow = FindActiveComponentBySimpleName("BSSFlowCoordinator", "");
            if (flow == null)
                return;
            MethodInfo backMethod = FindDeclaredMethodInHierarchy(flow.GetType(), "BackButtonWasPressed", method => method.GetParameters().Length == 1);
            Check("bsml.main-menu-return-control", backMethod != null, "BSSFlowCoordinator.BackButtonWasPressed(HMUI.ViewController)");
            if (backMethod == null)
                throw new MissingMethodException(flow.GetType().FullName, "BackButtonWasPressed");
            ParameterInfo parameter = backMethod.GetParameters()[0];
            object view = ReadMember(flow, "topViewController", "_topViewController");
            if (view != null && !parameter.ParameterType.IsInstanceOfType(view))
                view = null;
            backMethod.Invoke(flow, new[] { view });
            await WaitUntil(() => IsMainMenuActive() && !IsLevelSelectionActive(), 30, "return from BetterSongSearch");
            await WaitForStableBssMenu(flow.GetType());
            Check("bsml.returned-to-main-menu", IsMainMenuActive() && !IsLevelSelectionActive(), "MainMenuViewController active; BSS flow dismissed");
        }

        async Task WaitForStableBssMenu(Type flowType)
        {
            MethodInfo stableMethod = FindDeclaredMethodInHierarchy(flowType, "WaitForStableMenuAsync", method =>
                method.IsStatic && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(CancellationToken));
            if (stableMethod == null)
            {
                await Task.Delay(1500);
                return;
            }
            object result = stableMethod.Invoke(null, new object[] { CancellationToken.None });
            if (!(result is Task<bool> task))
            {
                await Task.Delay(1500);
                return;
            }
            bool stable = await task;
            Check("bsml.stable-menu", stable, "BSSFlowCoordinator.WaitForStableMenuAsync");
            if (!stable)
                throw new TimeoutException("BetterSongSearch menu did not become stable after dismissal");
        }

        void HideActiveBsmlModals()
        {
            foreach (ModalView modal in Resources.FindObjectsOfTypeAll<ModalView>().Where(IsLiveSceneComponent).ToArray())
                HideModalView(modal);
        }

        void HideModalView(Component modal)
        {
            if (modal == null)
                return;
            MethodInfo hide = FindDeclaredMethodInHierarchy(modal.GetType(), "Hide", method =>
                method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(bool));
            if (hide != null)
            {
                try { hide.Invoke(modal, new object[] { false, null }); } catch (Exception exception) { diagnostics.Add("hide modal: " + exception.GetBaseException().Message); }
            }
        }

        bool IsLevelSelectionActive()
        {
            // LevelSelectionFlowCoordinator and its child views can be loaded
            // in the menu scene before they are actually entered.  The
            // navigation controller's activation flag is the same lifecycle
            // signal used by the game's own flow and by the successful native
            // smoke helpers.
            Type navigation = FindType("LevelCollectionNavigationController");
            return navigation != null && Resources.FindObjectsOfTypeAll(navigation).OfType<Component>().Any(IsActivatedSceneComponent);
        }

        bool IsMainMenuActive()
        {
            Type type = FindType("MainMenuViewController");
            return type != null && Resources.FindObjectsOfTypeAll(type).OfType<Component>().Any(IsActivatedSceneComponent);
        }

        static bool IsActivatedSceneComponent(Component component)
        {
            if (!IsLiveSceneComponent(component))
                return false;
            object activated = ReadMember(component, "isActivated", "IsActivated");
            return activated is bool && (bool)activated;
        }

        static bool IsLiveSceneComponent(Component component)
        {
            if (component == null)
                return false;
            try
            {
                return component.gameObject != null && component.gameObject.scene.IsValid() && component.gameObject.activeInHierarchy;
            }
            catch
            {
                return false;
            }
        }

        void ClickButton(string[] labels)
        {
            Button button = FindButton(labels);
            if (button != null)
                button.onClick.Invoke();
        }

        Button FindButton(string[] labels)
        {
            HashSet<string> wanted = new HashSet<string>(labels ?? new string[0], StringComparer.OrdinalIgnoreCase);
            return Resources.FindObjectsOfTypeAll<Button>()
                .Where(IsLiveSceneComponent)
                .Where(x => x.interactable)
                .OrderBy(x => x.transform.GetSiblingIndex())
                .FirstOrDefault(x => x.GetComponentsInChildren<TMP_Text>(true).Any(t => wanted.Contains(t.text == null ? "" : t.text.Trim())));
        }

        InputFieldView FindNativeSearchInput()
        {
            Type searchType = FindType("LevelSearchViewController");
            if (searchType != null)
            {
                foreach (object controller in Resources.FindObjectsOfTypeAll(searchType))
                {
                    if (!(controller is Component component) || !IsLiveSceneComponent(component))
                        continue;
                    object value = ReadMember(controller, "_searchTextInputFieldView", "searchTextInputFieldView");
                    if (value is InputFieldView field && IsLiveSceneComponent(field))
                        return field;
                }
            }

            return Resources.FindObjectsOfTypeAll<InputFieldView>()
                .Where(IsLiveSceneComponent)
                .FirstOrDefault(x => TransformPath(x.transform).IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.gameObject.name.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        bool RevealNativeSearchInput()
        {
            bool invoked = false;
            Type filteringType = FindType("LevelFilteringNavigationController");
            if (filteringType != null)
            {
                Component filtering = Resources.FindObjectsOfTypeAll(filteringType).OfType<Component>().FirstOrDefault(IsLiveSceneComponent);
                object category = ReadMember(filtering, "_selectLevelCategoryViewController") ?? FindActiveComponentBySimpleName("SelectLevelCategoryViewController", "");
                object segmented = ReadMember(category, "_levelFilterCategoryIconSegmentedControl");
                object categoryInfos = ReadMember(category, "_levelCategoryInfos");
                if (segmented != null && categoryInfos is IEnumerable infoEnumerable)
                {
                    var infos = infoEnumerable.Cast<object>().ToArray();
                    int index = Array.FindIndex(infos, info => String.Equals(ReadStringMember(info, "levelCategory", "LevelCategory"), "All", StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        MethodInfo select = segmented.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == "SelectCellWithNumber" && x.GetParameters().Length == 1);
                        MethodInfo callback = category?.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == "LevelFilterCategoryIconSegmentedControlDidSelectCell" && x.GetParameters().Length == 2);
                        try
                        {
                            if (select != null)
                            {
                                select.Invoke(segmented, new object[] { index });
                                callback?.Invoke(category, new object[] { segmented, index });
                                invoked = true;
                            }
                        }
                        catch (Exception exception) { diagnostics.Add("select native local category: " + exception.GetBaseException().Message); }
                    }
                }
            }

            Type searchType = FindType("LevelSearchViewController");
            if (searchType != null)
            {
                foreach (object controller in Resources.FindObjectsOfTypeAll(searchType))
                {
                    if (controller is Component searchComponent && !IsLiveSceneComponent(searchComponent))
                        continue;
                    object input = ReadMember(controller, "_searchTextInputFieldView", "searchTextInputFieldView");
                    if (input is InputFieldView field && IsLiveSceneComponent(field))
                        return true;
                    Button searchButton = ReadMember(controller, "_searchButton", "searchButton") as Button;
                    if (!IsLiveSceneComponent(searchButton) || !searchButton.interactable)
                        continue;
                    try { searchButton.onClick.Invoke(); invoked = true; }
                    catch (Exception exception) { diagnostics.Add("open native search button: " + exception.GetBaseException().Message); }
                }
            }
            return invoked;
        }

        void WriteNavigationDiagnostics(string name)
        {
            try
            {
                Type[] interesting = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeGetTypes)
                    .Where(x => x.Name.IndexOf("Level", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("Solo", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("Search", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();
                var views = new List<object>();
                foreach (Type type in interesting)
                {
                    UnityEngine.Object[] objects;
                    try { objects = Resources.FindObjectsOfTypeAll(type); }
                    catch (Exception exception)
                    {
                        diagnostics.Add("navigation diagnostics type " + type.FullName + ": " + exception.GetBaseException().Message);
                        continue;
                    }
                    foreach (object value in objects ?? new UnityEngine.Object[0])
                    {
                        Component component = value as Component;
                        if (!IsLiveSceneComponent(component))
                            continue;
                        try
                        {
                            InputFieldView[] inputs = component.GetComponentsInChildren<InputFieldView>(true)
                                .Where(IsLiveSceneComponent)
                                .ToArray();
                            Button[] buttons = component.GetComponentsInChildren<Button>(true)
                                .Where(IsLiveSceneComponent)
                                .ToArray();
                            views.Add(new
                            {
                                type = type.FullName,
                                path = TransformPath(component.transform),
                                activated = ReadMember(value, "isActivated", "IsActivated"),
                                inputs = inputs.Select(x => new { name = x.name, active = x.gameObject.activeInHierarchy, path = TransformPath(x.transform), text = x.text }).ToArray(),
                                buttons = buttons.Select(x => new { name = x.name, active = x.gameObject.activeInHierarchy, interactable = x.interactable, path = TransformPath(x.transform), text = String.Join("/", x.GetComponentsInChildren<TMP_Text>(true).Where(t => t != null).Select(t => t.text)) }).ToArray()
                            });
                        }
                        catch (Exception exception)
                        {
                            diagnostics.Add("navigation diagnostics component " + type.FullName + ": " + exception.GetBaseException().Message);
                        }
                        if (views.Count >= 120)
                            break;
                    }
                    if (views.Count >= 120)
                        break;
                }
                WriteJson(name, new { scene = SceneManager.GetActiveScene().name, levelSelection = IsLevelSelectionActive(), mainMenu = IsMainMenuActive(), views });
            }
            catch (Exception exception) { diagnostics.Add("navigation diagnostics: " + exception.Message); }
        }

        InputFieldView FindActiveInputForKeyboard()
        {
            return Resources.FindObjectsOfTypeAll<InputFieldView>().FirstOrDefault(IsLiveSceneComponent);
        }

        bool IsKeyboardVisible()
        {
            Component manager = FindKeyboardManager();
            if (manager == null)
                return false;

            InputFieldView selectedInput = ReadMember(manager, "_selectedInput", "selectedInput") as InputFieldView;
            if (selectedInput == null)
                return false;
            object assigned = ReadMember(selectedInput, "_hasKeyboardAssigned", "hasKeyboardAssigned");
            if (!(assigned is bool) || !(bool)assigned)
                return false;

            Component modal = ReadMember(manager, "_keyboardModalView", "keyboardModalView") as Component;
            if (modal != null && !IsModalShown(modal))
                return false;

            object keyboard = ReadMember(manager, "keyboard", "_uiKeyboard");
            if (keyboard is Behaviour keyboardBehaviour)
                return keyboardBehaviour.isActiveAndEnabled;
            object visible = ReadMember(manager, "isActive", "IsActive", "isActivated", "IsActivated");
            if (visible is bool)
                return (bool)visible;
            return true;
        }

        bool IsBsmlKeyboardVisible(object modalKeyboard, Component modalView)
        {
            if (!IsModalShown(modalView) || modalKeyboard == null)
                return false;
            object keyboard = ReadMember(modalKeyboard, "Keyboard", "keyboard");
            if (keyboard == null)
                return false;
            if (keyboard is Component keyboardComponent && !IsLiveSceneComponent(keyboardComponent))
                return false;
            object keys = ReadMember(keyboard, "Keys", "keys");
            bool hasKeys = keys is IEnumerable enumerable && enumerable.Cast<object>().Any();
            Transform root = ReadMember(keyboard, "container", "Container") as Transform ?? (keyboard as Component)?.transform;
            bool rootActive = root != null && root.gameObject.scene.IsValid() && root.gameObject.activeInHierarchy;
            return hasKeys && rootActive;
        }

        static Transform FindBsmlTargetRoot(object keyboard)
        {
            Transform container = ReadMember(keyboard, "container", "Container") as Transform ?? (keyboard as Component)?.transform;
            if (container == null)
                return null;
            // BSML's KEYBOARD constructor receives KeyboardParent, then
            // creates its private Keys child. VoiceInput's target root is the
            // constructor parent so the panel shares the modal layout region.
            return container.parent ?? container;
        }

        Component FindKeyboardManager()
        {
            Type type = FindType("HMUI.UIKeyboardManager") ?? FindType("UIKeyboardManager");
            if (type == null)
                return null;
            return Resources.FindObjectsOfTypeAll(type).OfType<Component>().FirstOrDefault(IsLiveSceneComponent);
        }

        Transform FindKeyboardContainer(Component manager)
        {
            if (manager == null)
                return null;
            IEnumerable<MemberInfo> members = manager.GetType().GetMembers(Any)
                .OrderByDescending(x => x.Name.IndexOf("container", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenByDescending(x => x.Name.IndexOf("keyboard", StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (MemberInfo member in members)
            {
                if (member.Name.IndexOf("keyboard", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                object value = ReadMember(manager, member.Name);
                Transform transform = value as Transform ?? (value as Component)?.transform as Transform;
                if (transform != null)
                    return transform;
            }
            return manager.transform;
        }

        int CountPanelsForKeyboard()
        {
            Component manager = FindKeyboardManager();
            Transform container = FindKeyboardContainer(manager);
            return CountPanelsForRoot(container);
        }

        int CountPanelsForRoot(Transform root)
        {
            if (root == null)
                return 0;
            return Resources.FindObjectsOfTypeAll<VoicePanel>().Count(x => IsLiveSceneComponent(x) && x.transform.IsChildOf(root));
        }

        VoicePanel FindActivePanel()
        {
            return Resources.FindObjectsOfTypeAll<VoicePanel>().FirstOrDefault(IsLiveSceneComponent);
        }

        void TryOpenSetting(Component setting, object modalKeyboard, Component modalView)
        {
            if (IsModalShown(modalView))
                return;

            Button button = setting.GetComponentsInChildren<Button>(true).FirstOrDefault(x => IsLiveSceneComponent(x));
            if (button != null)
            {
                button.onClick.Invoke();
                if (IsModalShown(modalView))
                    return;
            }

            MethodInfo edit = setting.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == "EditButtonPressed" && x.GetParameters().Length == 0);
            if (edit != null)
            {
                edit.Invoke(setting, null);
                if (IsModalShown(modalView))
                    return;
            }

            foreach (string name in new[] { "OnButtonPressed", "Open", "Show", "Activate" })
            {
                MethodInfo method = modalKeyboard.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == 0);
                if (method == null)
                    continue;
                try { method.Invoke(modalKeyboard, null); } catch (Exception exception) { diagnostics.Add("open BSML keyboard " + name + ": " + exception.GetBaseException().Message); }
                if (IsModalShown(modalView))
                    return;
            }
        }

        Button FindOriginalKeyboardConfirm(Component modalView)
        {
            return modalView.GetComponentsInChildren<Button>(true)
                .Where(x => IsLiveSceneComponent(x) && x.interactable)
                .FirstOrDefault(x => x.GetComponentsInChildren<TMP_Text>(true).Any(t =>
                    new[] { "Enter", "确定", "Done", "完成", "OK" }.Contains((t.text ?? "").Trim(), StringComparer.OrdinalIgnoreCase)));
        }

        bool InvokeOriginalKeyboardConfirm(object modalKeyboard)
        {
            object keyboard = ReadMember(modalKeyboard, "Keyboard", "keyboard");
            if (keyboard == null)
                return false;
            object enterKey = null;
            foreach (MemberInfo member in keyboard.GetType().GetMembers(Any))
            {
                if (member.MemberType != MemberTypes.Field && member.MemberType != MemberTypes.Property)
                    continue;
                object value = ReadMember(keyboard, member.Name);
                if (!(value is IEnumerable enumerable) || value is string)
                    continue;
                foreach (object candidate in enumerable)
                {
                    string name = ReadStringMember(candidate, "Name", "name", "Value", "value");
                    if (!String.IsNullOrWhiteSpace(name) && (name.IndexOf("enter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("确定", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("done", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        enterKey = candidate;
                        break;
                    }
                }
                if (enterKey != null)
                    break;
            }
            if (enterKey == null)
                return false;
            MethodInfo method = keyboard.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == "Enter" && x.GetParameters().Length == 1 &&
                x.GetParameters()[0].ParameterType.IsInstanceOfType(enterKey));
            if (method == null)
                return false;
            method.Invoke(keyboard, new[] { enterKey });
            return true;
        }

        static bool IsModalShown(Component modalView)
        {
            if (!IsLiveSceneComponent(modalView))
                return false;
            object shown = ReadMember(modalView, "isShown", "IsShown");
            return shown is bool ? (bool)shown : modalView.gameObject.activeInHierarchy;
        }

        static Type FindType(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType(name, false)).FirstOrDefault(x => x != null) ??
                AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => SafeGetTypes(x)).FirstOrDefault(x => x.Name == name || x.FullName == name);
        }

        static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); } catch (ReflectionTypeLoadException exception) { return exception.Types.Where(x => x != null); }
        }

        static MethodInfo FindMethod(object target, string name)
        {
            return target == null ? null : target.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == name);
        }

        static MethodInfo FindDeclaredMethodInHierarchy(Type type, string name, Func<MethodInfo, bool> predicate)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                MethodInfo method = current.GetMethods(Any | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(x => x.Name == name && (predicate == null || predicate(x)));
                if (method != null)
                    return method;
            }
            return null;
        }

        static object ReadMember(object target, params string[] names)
        {
            if (target == null)
                return null;
            foreach (string name in names ?? new string[0])
            {
                for (Type current = target.GetType(); current != null; current = current.BaseType)
                {
                    PropertyInfo property = current.GetProperty(name, Any | BindingFlags.DeclaredOnly);
                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        try { return property.GetValue(target, null); } catch { }
                    }
                    FieldInfo field = current.GetField(name, Any | BindingFlags.DeclaredOnly);
                    if (field != null)
                    {
                        try { return field.GetValue(target); } catch { }
                    }
                }
            }
            return null;
        }

        static object ReadStaticMember(Type type, params string[] names)
        {
            if (type == null)
                return null;
            foreach (string name in names ?? new string[0])
            {
                for (Type current = type; current != null; current = current.BaseType)
                {
                    PropertyInfo property = current.GetProperty(name, Any | BindingFlags.DeclaredOnly);
                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        try { return property.GetValue(null, null); } catch { }
                    }
                    FieldInfo field = current.GetField(name, Any | BindingFlags.DeclaredOnly);
                    if (field != null)
                    {
                        try { return field.GetValue(null); } catch { }
                    }
                }
            }
            return null;
        }

        static bool SetMember(object target, string name, object value)
        {
            if (target == null)
                return false;
            PropertyInfo property = target.GetType().GetProperty(name, Any);
            if (property != null && property.CanWrite && property.PropertyType.IsInstanceOfType(value))
            {
                property.SetValue(target, value, null);
                return true;
            }
            FieldInfo field = target.GetType().GetField(name, Any);
            if (field != null && !field.IsInitOnly && field.FieldType.IsInstanceOfType(value))
            {
                field.SetValue(target, value);
                return true;
            }
            return false;
        }

        static T GetMember<T>(object target, params string[] names) where T : class
        {
            return ReadMember(target, names) as T;
        }

        static void SetInputText(InputFieldView input, string value)
        {
            if (input == null)
                return;
            MethodInfo setText = input.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == "SetText" &&
                x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(string));
            if (setText != null)
            {
                setText.Invoke(input, new object[] { value ?? "" });
                return;
            }
            PropertyInfo property = input.GetType().GetProperty("text", Any);
            MethodInfo setter = property == null ? null : property.GetSetMethod(true);
            if (setter == null)
                throw new MissingMethodException(input.GetType().FullName, "SetText");
            setter.Invoke(input, new object[] { value ?? "" });
        }

        static string ReadStringMember(object target, params string[] names)
        {
            object value = ReadMember(target, names);
            TMP_Text text = value as TMP_Text;
            if (text != null)
                return text.text;
            return value == null ? null : value.ToString();
        }

        static int ReadIntMember(object target, params string[] names)
        {
            object value = ReadMember(target, names);
            if (value == null)
                return -1;
            if (value is int integer)
                return integer;
            try { return Convert.ToInt32(value); }
            catch { return -1; }
        }

        static int ReadBssSearchCount(object songList)
        {
            object values = ReadStaticMember(songList == null ? null : songList.GetType(), "searchedSongsList");
            if (values == null)
                return -1;
            if (values is ICollection collection)
                return collection.Count;
            if (values is IEnumerable enumerable)
            {
                int count = 0;
                foreach (object value in enumerable)
                    count++;
                return count;
            }
            return -1;
        }

        static bool IsBssSearchInProgress(object songList)
        {
            Component progress = ReadMember(songList, "searchInProgress") as Component;
            return progress != null && progress.gameObject.activeInHierarchy;
        }

        static Component FindActiveComponentBySimpleName(string simpleName, string pathFragment)
        {
            Type type = FindType(simpleName);
            if (type == null)
                return null;
            return Resources.FindObjectsOfTypeAll(type).OfType<Component>().FirstOrDefault(x => IsLiveSceneComponent(x) &&
                (String.IsNullOrWhiteSpace(pathFragment) || TransformPath(x.transform).IndexOf(pathFragment, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.gameObject.name.IndexOf(pathFragment, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        static Component FindBssSplitSetting(string ownerTypeName, string fieldName)
        {
            Type ownerType = FindType(ownerTypeName);
            object owner = ReadStaticMember(ownerType, "instance");
            Component setting = ReadMember(owner, fieldName) as Component;
            return IsLiveSceneComponent(setting) ? setting : null;
        }

        static string TransformPath(Transform value)
        {
            if (value == null)
                return "null";
            List<string> names = new List<string>();
            Transform current = value;
            while (current != null && names.Count < 32)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return String.Join("/", names.ToArray());
        }

        static object DescribeRect(RectTransform rect)
        {
            if (rect == null)
                return null;
            return new
            {
                path = TransformPath(rect),
                scene = rect.gameObject.scene.name,
                active = rect.gameObject.activeInHierarchy,
                rect = rect.rect.ToString(),
                anchorMin = rect.anchorMin.ToString(),
                anchorMax = rect.anchorMax.ToString(),
                pivot = rect.pivot.ToString(),
                sizeDelta = rect.sizeDelta.ToString(),
                anchoredPosition = rect.anchoredPosition.ToString(),
                localPosition = rect.localPosition.ToString(),
                localScale = rect.localScale.ToString()
            };
        }

        static object DescribeWorldRect(RectTransform rect)
        {
            if (rect == null)
                return null;
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return new
            {
                bottomLeft = corners[0].ToString(),
                topLeft = corners[1].ToString(),
                topRight = corners[2].ToString(),
                bottomRight = corners[3].ToString(),
                lossyScale = rect.lossyScale.ToString()
            };
        }

        static object DescribeCurvedCanvas(Canvas canvas, Transform target)
        {
            if (canvas == null)
                return null;
            try
            {
                Canvas rootCanvas = canvas.rootCanvas;
                CurvedCanvasSettings settings = rootCanvas == null ? null : rootCanvas.GetComponent<CurvedCanvasSettings>();
                Vector3 rawWorld = GetRawFocusPoint(target);
                Vector3 rootLocal = rootCanvas == null ? Vector3.zero : rootCanvas.transform.InverseTransformPoint(rawWorld);
                Vector3 curvedLocal = rootLocal;
                Vector3 curvedWorld = rawWorld;
                if (rootCanvas != null && settings != null)
                {
                    curvedLocal = settings.TransformPointFromCanvasTo3D(new Vector2(rootLocal.x, rootLocal.y));
                    curvedWorld = rootCanvas.transform.TransformPoint(curvedLocal);
                }
                return new
                {
                    canvas = TransformPath(canvas.transform),
                    rootCanvas = TransformPath(rootCanvas == null ? null : rootCanvas.transform),
                    hasCurvedCanvasSettings = settings != null,
                    radius = settings == null ? (float?)null : settings.radius,
                    target = TransformPath(target),
                    rawWorld = rawWorld.ToString(),
                    rootLocal = rootLocal.ToString(),
                    curvedLocal = curvedLocal.ToString(),
                    curvedWorld = curvedWorld.ToString(),
                    worldDelta = (curvedWorld - rawWorld).ToString()
                };
            }
            catch (Exception exception)
            {
                return new { error = exception.GetBaseException().Message };
            }
        }

        static Vector3 ResolveCurvedCanvasFocus(Transform focusRoot)
        {
            if (focusRoot == null)
                return Vector3.zero;
            try
            {
                Vector3 rawWorld = GetRawFocusPoint(focusRoot);
                Canvas canvas = focusRoot.GetComponentInParent<Canvas>(true);
                Canvas rootCanvas = canvas == null ? null : canvas.rootCanvas;
                CurvedCanvasSettings settings = rootCanvas == null ? null : rootCanvas.GetComponent<CurvedCanvasSettings>();
                if (rootCanvas == null || settings == null)
                    return rawWorld;
                Vector3 rootLocal = rootCanvas.transform.InverseTransformPoint(rawWorld);
                Vector3 curvedLocal = settings.TransformPointFromCanvasTo3D(new Vector2(rootLocal.x, rootLocal.y));
                return rootCanvas.transform.TransformPoint(curvedLocal);
            }
            catch
            {
                return focusRoot.position;
            }
        }

        static Vector3 GetRawFocusPoint(Transform target)
        {
            if (target == null)
                return Vector3.zero;
            RectTransform rect = target as RectTransform;
            return rect == null ? target.position : rect.TransformPoint(rect.rect.center);
        }

        static object DescribePanel(VoicePanel panel, Component manager, Transform container)
        {
            return new
            {
                panel = TransformPath(panel == null ? null : panel.transform),
                manager = TransformPath(manager == null ? null : manager.transform),
                container = TransformPath(container),
                active = panel != null && panel.gameObject.activeInHierarchy,
                recordButton = GetMember<Button>(panel, "RecordButton", "recordButton")?.name,
                cancelButton = GetMember<Button>(panel, "CancelButton", "cancelButton")?.name,
                status = ReadStringMember(panel, "Status", "status"),
                timer = ReadStringMember(panel, "Timer", "timer")
            };
        }

        void Capture(string filename, bool wide = true, Transform focusRoot = null)
        {
            try
            {
                Camera camera = Resources.FindObjectsOfTypeAll<Camera>().FirstOrDefault(x => IsLiveSceneComponent(x) && x.name == "MenuMainCamera") ??
                    (IsLiveSceneComponent(Camera.main) ? Camera.main : null);
                if (camera == null)
                    throw new InvalidOperationException("menu camera unavailable");

                RenderTexture previousTarget = camera.targetTexture;
                RenderTexture previousActive = RenderTexture.active;
                Vector3 previousPosition = camera.transform.position;
                Quaternion previousRotation = camera.transform.rotation;
                float previousFov = camera.fieldOfView;
                Renderer[] avatarRenderers = Resources.FindObjectsOfTypeAll<Renderer>().Where(x => IsLiveSceneComponent(x) && x.enabled &&
                    x.GetComponentsInParent<MonoBehaviour>(true).Any(p => p != null && p.GetType().Assembly.GetName().Name == "CustomAvatar")).ToArray();
                RenderTexture target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
                Texture2D texture = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
                try
                {
                    foreach (Renderer renderer in avatarRenderers)
                        renderer.enabled = false;
                    if (focusRoot != null && focusRoot.gameObject.scene.IsValid() && focusRoot.gameObject.activeInHierarchy)
                    {
                        Vector3 targetPosition = ResolveCurvedCanvasFocus(focusRoot);
                        Vector3 direction = targetPosition - previousPosition;
                        if (direction.sqrMagnitude < 0.01f)
                            direction = focusRoot.forward;
                        camera.transform.position = targetPosition - direction.normalized * 1.5f;
                        camera.transform.rotation = Quaternion.LookRotation(targetPosition - camera.transform.position, Vector3.up);
                        camera.fieldOfView = 70;
                    }
                    else if (wide)
                    {
                        camera.transform.position = previousPosition - camera.transform.forward * 1.5f;
                        camera.fieldOfView = 100;
                    }
                    target.Create();
                    camera.targetTexture = target;
                    RenderPipeline.StandardRequest request = new RenderPipeline.StandardRequest { destination = target };
                    if (!RenderPipeline.SupportsRenderRequest<RenderPipeline.StandardRequest>(camera, request))
                        throw new NotSupportedException("URP StandardRequest unavailable");
                    RenderPipeline.SubmitRenderRequest(camera, request);
                    RenderTexture.active = target;
                    texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                    texture.Apply();
                    File.WriteAllBytes(Path.Combine(output, filename), texture.EncodeToPNG());
                }
                finally
                {
                    foreach (Renderer renderer in avatarRenderers)
                        if (renderer != null) renderer.enabled = true;
                    camera.transform.position = previousPosition;
                    camera.transform.rotation = previousRotation;
                    camera.fieldOfView = previousFov;
                    camera.targetTexture = previousTarget;
                    RenderTexture.active = previousActive;
                    target.Release();
                    Destroy(target);
                    Destroy(texture);
                }
            }
            catch (Exception exception)
            {
                errors.Add("capture " + filename + ": " + exception);
            }
        }

        void WriteProgress()
        {
            try
            {
                File.WriteAllText(Path.Combine(output, "progress.json"), JsonConvert.SerializeObject(new
                {
                    phase,
                    elapsed = timer.Elapsed.TotalSeconds,
                    scene = SceneManager.GetActiveScene().name,
                    keyboard = IsKeyboardVisible(),
                    panels = Resources.FindObjectsOfTypeAll<VoicePanel>().Count(IsLiveSceneComponent),
                    captureCreated = FakeCapture.Created,
                    activeCaptures = FakeCapture.ActiveCount,
                    recognitionRequests = recognizer == null ? 0 : recognizer.Requests,
                    errors = errors.Take(8).ToArray()
                }, Formatting.Indented));
            }
            catch (Exception exception) { diagnostics.Add("progress: " + exception.Message); }
        }

        void WriteJson(string name, object value)
        {
            File.WriteAllText(Path.Combine(output, name), JsonConvert.SerializeObject(value, Formatting.Indented));
        }

        void Check(string name, bool passed, string detail)
        {
            checks.Add(new { name, passed, detail });
            if (!passed)
                errors.Add(name + ": " + detail);
        }

        Task FinishAsync(string failure)
        {
            if (finishTask != null)
                return finishTask;
            finishTask = FinishCoreAsync(failure);
            return finishTask;
        }

        async Task FinishCoreAsync(string failure)
        {
            if (finished)
                return;
            if (!String.IsNullOrWhiteSpace(failure))
                errors.Add(failure);

            if (host != null)
            {
                Task completion = null;
                try
                {
                    host.Shutdown();
                    completion = ReadMember(host, "ShutdownCompletion", "shutdownCompletion") as Task;
                    bool drained = await WaitForTask(completion, 15);
                    Check("runtime-host.shutdown-drained", drained && (completion == null || completion.IsCompleted),
                        completion == null ? "ShutdownCompletion unavailable" : "completed=" + completion.IsCompleted);
                }
                catch (Exception exception)
                {
                    Check("runtime-host.shutdown-drained", false, exception.ToString());
                }
            }

            finished = true;
            WriteProgress();
            try
            {
                File.WriteAllText(Path.Combine(output, "report.json"), JsonConvert.SerializeObject(new
                {
                    formatVersion = 1,
                    passed = errors.Count == 0,
                    phase,
                    elapsed = timer.Elapsed.TotalSeconds,
                    xrActive = UnityEngine.XR.XRSettings.isDeviceActive,
                    screenshots = Directory.GetFiles(output, "*.png").Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    checks,
                    errors,
                    diagnostics
                }, Formatting.Indented));
            }
            catch (Exception exception) { UnityEngine.Debug.LogError("VoiceInput.RuntimeSmoke report: " + exception); }
            Application.logMessageReceived -= OnLog;
            Application.Quit(errors.Count == 0 ? 0 : 1);
        }

        async Task<bool> WaitForTask(Task task, int seconds)
        {
            if (task == null)
                return false;
            Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
            if (completed != task)
                return false;
            try { await task; return true; }
            catch (Exception exception) { diagnostics.Add("task completion: " + exception); return false; }
        }

        sealed class FakeCapture : IAudioCapture
        {
            static readonly List<FakeCapture> all = new List<FakeCapture>();
            readonly float[] samples = Enumerable.Repeat(0.08f, 32000).ToArray();
            bool recording;
            float elapsed;

            public static FakeCapture Last { get; private set; }
            public static int Created { get; private set; }
            public static int ActiveCount { get { return all.Count(x => x.recording); } }
            public bool IsRecording { get { return recording; } }
            public float Elapsed { get { return elapsed; } }
            public float Level { get { return LevelValue; } }
            public float LevelValue { get; set; } = 0.18f;
            public int CancelCount { get; private set; }
            public bool Disposed { get; private set; }

            public FakeCapture()
            {
                all.Add(this);
                Last = this;
                Created++;
            }

            public static void Reset()
            {
                all.Clear();
                Last = null;
                Created = 0;
            }

            public void Start(string device, int maxSeconds)
            {
                if (Disposed) throw new ObjectDisposedException("FakeCapture");
                elapsed = 0;
                recording = true;
            }

            public void Tick()
            {
                if (recording)
                    elapsed += Mathf.Max(Time.unscaledDeltaTime, 0.016f);
            }

            public AudioSamples Stop()
            {
                recording = false;
                return new AudioSamples(samples, 1, 16000);
            }

            public void Cancel()
            {
                recording = false;
                CancelCount++;
            }

            public void Dispose()
            {
                recording = false;
                Disposed = true;
            }
        }

        sealed class FakeRecognizer : ISpeechRecognizer
        {
            readonly Queue<TaskCompletionSource<string>> pending = new Queue<TaskCompletionSource<string>>();
            public int Requests { get; private set; }
            public int Cancelled { get; private set; }
            public bool Disposed { get; private set; }
            public bool IgnoreCancellation { get; set; }

            public Task<string> RecognizeAsync(byte[] pcm, CancellationToken token)
            {
                Requests++;
                TaskCompletionSource<string> source = new TaskCompletionSource<string>();
                pending.Enqueue(source);
                if (token.CanBeCanceled)
                    token.Register(delegate { Cancelled++; if (!IgnoreCancellation) source.TrySetCanceled(); });
                return source.Task;
            }

            public void CompleteNext(string value)
            {
                if (pending.Count > 0)
                    pending.Dequeue().TrySetResult(value);
            }

            public void FailNext(Exception exception)
            {
                if (pending.Count > 0)
                    pending.Dequeue().TrySetException(exception);
            }

            public void Dispose()
            {
                Disposed = true;
                while (pending.Count > 0)
                    pending.Dequeue().TrySetCanceled();
            }
        }
    }
}
