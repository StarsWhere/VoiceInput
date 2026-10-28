using System;
using BeatSaberMarkupLanguage.Components;
using HMUI;
using UnityEngine;

namespace VoiceInput;

internal interface ITextTarget
{
    object Identity { get; }
    RectTransform Root { get; }
    bool IsAvailable { get; }
    string Text { get; }
    bool TryAppend(string expected, string recognized);
}

internal sealed class NativeTextTarget : ITextTarget
{
    readonly InputFieldView field;
    readonly RectTransform root;
    public NativeTextTarget(InputFieldView field, HMUI.UIKeyboard keyboard)
    { this.field = field; root = keyboard.transform as RectTransform; }
    public object Identity => field;
    public RectTransform Root => root;
    public bool IsAvailable => field != null && root != null && root.gameObject.activeInHierarchy && field.gameObject.activeInHierarchy && field._hasKeyboardAssigned;
    public string Text => field == null ? "" : field.text ?? "";
    public bool TryAppend(string expected, string recognized)
    {
        if (!IsAvailable || !String.Equals(Text, expected, StringComparison.Ordinal)) return false;
        string addition = field._useUppercase ? recognized.ToUpper() : recognized;
        int capacity = Math.Max(0, field._textLengthLimit - expected.Length);
        if (addition.Length > capacity)
        {
            if (capacity > 0 && char.IsHighSurrogate(addition[capacity - 1])) capacity--;
            addition = addition.Substring(0, capacity);
        }
        if (addition.Length == 0) return false;
        field.SetText(expected + addition);
        field.onValueChanged.Invoke(field);
        return true;
    }
}

internal sealed class BsmlTextTarget : ITextTarget
{
    readonly KEYBOARD keyboard;
    readonly RectTransform root;
    public BsmlTextTarget(KEYBOARD keyboard, RectTransform root) { this.keyboard = keyboard; this.root = root; }
    public object Identity => keyboard;
    public RectTransform Root => root;
    public bool IsAvailable => root != null && root.gameObject.activeInHierarchy && keyboard.KeyboardText != null && keyboard.KeyboardText.gameObject.activeInHierarchy;
    public string Text => keyboard.KeyboardText == null ? "" : keyboard.KeyboardText.text ?? "";
    internal void ReservePanelSpace(float panelWidth, float gap)
    {
        // ModalKeyboardTag scales KeyboardParent after the KEYBOARD constructor.
        // Keep the complete keyboard and voice panel inside the modal's clip rect.
        if (root == null || root.parent == null || root.parent.GetComponent<ModalView>() == null) return;
        var modal = root.parent as RectTransform;
        if (modal == null) return;
        float scale = Mathf.Abs(root.localScale.x);
        float extra = (panelWidth + gap) * scale;
        modal.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Max(modal.rect.width, root.rect.width * scale + extra + 4));
        root.anchoredPosition = new Vector2(-root.rect.center.x * scale - extra / 2, root.anchoredPosition.y);
    }
    public bool TryAppend(string expected, string recognized)
    {
        if (!IsAvailable || !String.Equals(Text, expected, StringComparison.Ordinal)) return false;
        keyboard.KeyboardText.text = expected + recognized;
        return true;
    }
}
