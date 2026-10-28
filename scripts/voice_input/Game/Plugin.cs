using System;
using HarmonyLib;
using IPA;
using SiraUtil.Zenject;
using UnityEngine;

namespace VoiceInput;

[Plugin(RuntimeOptions.SingleStartInit)]
public sealed class Plugin
{
    internal static IPA.Logging.Logger Log;
    GameObject owner;
    Harmony harmony;

    [Init] public void Init(IPA.Logging.Logger logger, Zenjector zenjector)
    {
        Log = logger;
        zenjector.Install<VoiceMenuInstaller>(Location.Menu);
    }
    [OnStart] public void Start()
    {
        owner = new GameObject("VoiceInput.Runtime");
        UnityEngine.Object.DontDestroyOnLoad(owner);
        var runtime = owner.AddComponent<RuntimeHost>();
        runtime.Initialize(IPA.Utilities.UnityGame.InstallPath);
        harmony = new Harmony("local.beatsaber.voice-input");
        harmony.PatchAll(typeof(Plugin).Assembly);
        Log.Info("Voice Input 0.1.1 ready for Beat Saber 1.45.2");
    }
    [OnExit] public void Exit()
    {
        RuntimeHost.Current?.Shutdown();
        harmony?.UnpatchSelf();
        if (owner != null) UnityEngine.Object.Destroy(owner);
    }
    internal static void Diagnostic(string category, Exception exception)
    {
        Log?.Warn("VoiceInput [" + category + "]: " + exception.GetType().Name);
    }
}
