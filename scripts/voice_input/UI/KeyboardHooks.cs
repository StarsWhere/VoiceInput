using System;
using BeatSaberMarkupLanguage.Components;
using HarmonyLib;
using HMUI;
using UnityEngine;

namespace VoiceInput;

[HarmonyPatch(typeof(InputFieldView), "ActivateKeyboard")]
internal static class NativeKeyboardOpen
{
    static void Postfix(InputFieldView __instance, HMUI.UIKeyboard __0)
    {
        try { RuntimeHost.Current?.Attach(new NativeTextTarget(__instance, __0)); }
        catch (Exception ex) { Plugin.Diagnostic("native_keyboard", ex); }
    }
}

[HarmonyPatch(typeof(InputFieldView), "DeactivateKeyboard")]
internal static class NativeKeyboardClose
{
    static void Prefix(InputFieldView __instance) => RuntimeHost.Current?.Detach(__instance);
}

[HarmonyPatch(typeof(KEYBOARD), MethodType.Constructor, typeof(RectTransform), typeof(string), typeof(bool), typeof(float), typeof(float))]
internal static class BsmlKeyboardCreated
{
    static void Postfix(KEYBOARD __instance, RectTransform __0, bool __2)
    {
        if (!__2) return;
        try
        {
            RuntimeHost.Current?.Attach(new BsmlTextTarget(__instance, __0));
        }
        catch (Exception ex) { Plugin.Diagnostic("bsml_keyboard", ex); }
    }
}

[HarmonyPatch(typeof(KEYBOARD), "Enter")]
internal static class BsmlKeyboardEnter
{
    static void Prefix(KEYBOARD __instance) => RuntimeHost.Current?.Detach(__instance);
}
