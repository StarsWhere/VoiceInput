using System;
using System.IO;
using System.Reflection;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Parser;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VoiceInput.Contracts;
using VRUIControls;

namespace VoiceInput;

internal sealed class VoicePanel : MonoBehaviour
{
    [UIComponent("record")] internal Button RecordButton;
    [UIComponent("cancel")] internal Button CancelButton;
    [UIComponent("status")] internal TMP_Text Status;
    [UIComponent("timer")] internal TMP_Text Timer;
    [UIComponent("meter")] HorizontalLayoutGroup meter;
    [UIComponent("busy")] ImageView busy;
    internal ITextTarget Target;
    RectTransform level;
    ImageView icon;
    Texture2D iconTexture;
    Sprite iconSprite, meterSprite;
    Canvas inputCanvas, keyboardCanvas;
    TMP_Text recordLabel, cancelLabel;
    SessionState current;
    bool retry;
    float successUntil;

    internal static VoicePanel Create(ITextTarget target)
    {
        var go = new GameObject("VoiceInput.Panel", typeof(RectTransform), typeof(LayoutElement));
        go.layer = 5;
        var rect = (RectTransform)go.transform;
        rect.SetParent(target.Root, false);
        rect.anchorMin = rect.anchorMax = new Vector2(1, .5f);
        rect.pivot = new Vector2(0, .5f);
        rect.anchoredPosition = new Vector2(5, 0);
        rect.sizeDelta = new Vector2(38, 56);
        go.GetComponent<LayoutElement>().ignoreLayout = true;
        // Curved VR raycasts reject graphics outside their canvas rectangle.
        // The side panel therefore needs its own bounds and injected raycaster.
        var parentCanvas = target.Root.GetComponentInParent<Canvas>(true);
        if (parentCanvas == null) { Destroy(go); throw new InvalidOperationException("Keyboard canvas is unavailable."); }
        var canvas = go.AddComponent<Canvas>();
        canvas.additionalShaderChannels = parentCanvas.additionalShaderChannels | AdditionalCanvasShaderChannels.TexCoord2;
        canvas.sortingLayerID = parentCanvas.sortingLayerID;
        canvas.sortingOrder = parentCanvas.sortingOrder;
        try
        {
            var container = BeatSaberUI.DiContainer;
            if (container.IsInstalling) container.QueueForInject(go.AddComponent<VRGraphicRaycaster>());
            else container.InstantiateComponent<VRGraphicRaycaster>(go);
        }
        catch { Destroy(go); throw; }
        var panel = go.AddComponent<VoicePanel>();
        panel.inputCanvas = canvas;
        panel.keyboardCanvas = parentCanvas;
        panel.Target = target;
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VoiceInput.UI.VoicePanel.bsml"))
        using (var reader = new StreamReader(stream)) BSMLParser.Instance.Parse(reader.ReadToEnd(), go, panel);
        panel.recordLabel = panel.RecordButton.GetComponentInChildren<TMP_Text>(true);
        panel.cancelLabel = panel.CancelButton.GetComponentInChildren<TMP_Text>(true);
        Material uiMaterial = panel.RecordButton.GetComponentInChildren<ImageView>(true).material;
        panel.icon = NewImage(panel.RecordButton.transform, "Microphone", Color.white, uiMaterial);
        var iconRect = panel.icon.rectTransform;
        iconRect.anchorMin = iconRect.anchorMax = new Vector2(.12f, .5f);
        iconRect.sizeDelta = new Vector2(5, 7);
        panel.iconTexture = CreateMicrophoneTexture();
        panel.iconSprite = Sprite.Create(panel.iconTexture, new Rect(0, 0, panel.iconTexture.width, panel.iconTexture.height), new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect);
        panel.icon.sprite = panel.iconSprite;
        panel.icon.preserveAspect = true;
        panel.meterSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height), new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect);
        var track = NewImage(panel.meter.transform, "VolumeTrack", new Color(.16f, .22f, .28f, 1), uiMaterial);
        track.sprite = panel.meterSprite;
        Stretch(track.rectTransform);
        var fill = NewImage(track.transform, "Volume", new Color(.2f, .9f, .6f, 1), uiMaterial);
        fill.sprite = panel.meterSprite;
        Stretch(fill.rectTransform); panel.level = fill.rectTransform;
        panel.SetState(SessionState.Idle, false, null);
        return panel;
    }

    static ImageView NewImage(Transform parent, string name, Color color, Material material)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(LayoutElement), typeof(ImageView));
        go.layer = 5; go.transform.SetParent(parent, false); go.GetComponent<LayoutElement>().ignoreLayout = true;
        var image = go.GetComponent<ImageView>(); image.material = material; image.color = color; image.raycastTarget = false; return image;
    }
    static Texture2D CreateMicrophoneTexture()
    {
        var texture = new Texture2D(36, 56, TextureFormat.RGBA32, false) { name = "VoiceInput.Microphone", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < texture.height; y++)
        for (int x = 0; x < texture.width; x++)
        {
            int coverage = 0;
            for (int sample = 0; sample < 4; sample++)
            {
                float px = (x + ((sample & 1) == 0 ? .25f : .75f) - 18) / 6;
                float py = (y + ((sample & 2) == 0 ? .25f : .75f) - 28) / 7;
                float dy = Mathf.Max(-.3f - py, Mathf.Max(0, py - 2.6f));
                float radiusSquared = px * px + (py - .1f) * (py - .1f);
                if (px * px + dy * dy <= 1.44f ||
                    (py <= .1f && radiusSquared >= 1.65f * 1.65f && radiusSquared <= 2.2f * 2.2f) ||
                    (Mathf.Abs(px) <= .28f && py >= -3 && py <= -1.9f) ||
                    (Mathf.Abs(px) <= 1.3f && py >= -3.2f && py <= -2.7f)) coverage++;
            }
            texture.SetPixel(x, y, new Color(1, 1, 1, coverage / 4f));
        }
        texture.Apply(false, true);
        return texture;
    }
    static void Stretch(RectTransform rect) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero; }

    [UIAction("Toggle")] internal void Toggle() => RuntimeHost.Current?.Toggle(this);
    [UIAction("Cancel")] internal void Cancel()
    {
        if (current == SessionState.Failed && retry) RuntimeHost.Current?.Begin(this);
        else RuntimeHost.Current?.Cancel(this);
    }
    void OnEnable()
    {
        if (Target == null) return;
        RuntimeHost.Current?.PanelShown(this);
        SetState(SessionState.Idle, false, null);
    }
    void OnDisable() => RuntimeHost.Current?.PanelHidden(this);
    void OnDestroy()
    {
        RuntimeHost.Current?.PanelDestroyed(this);
        if (iconSprite != null) Destroy(iconSprite);
        if (meterSprite != null) Destroy(meterSprite);
        if (iconTexture != null) Destroy(iconTexture);
    }
    void Start()
    {
        if (Target is BsmlTextTarget bsml) bsml.ReservePanelSpace(((RectTransform)transform).rect.width, 5);
    }

    internal void SetState(SessionState state, bool canRetry, string message)
    {
        current = state; retry = canRetry; successUntil = 0;
        if (Status == null || Timer == null || RecordButton == null || CancelButton == null || meter == null || busy == null || recordLabel == null || cancelLabel == null || icon == null) return;
        Status.text = message ?? (state == SessionState.Recording ? "正在录音" : state == SessionState.Starting ? "正在打开麦克风" : state == SessionState.Recognizing ? "正在识别" : "语音输入");
        Status.color = state == SessionState.Failed ? new Color(1, .67f, .4f) : state == SessionState.Recording ? new Color(1, .25f, .3f) : Color.white;
        Timer.gameObject.SetActive(state == SessionState.Recording || state == SessionState.Starting);
        meter.gameObject.SetActive(state == SessionState.Recording);
        busy.gameObject.SetActive(state == SessionState.Recognizing || state == SessionState.Starting);
        RecordButton.gameObject.SetActive(state != SessionState.Recognizing);
        RecordButton.interactable = state != SessionState.Starting;
        recordLabel.text = state == SessionState.Recording ? "停止录音" : state == SessionState.Failed && canRetry ? "重试识别" : state == SessionState.Failed ? "重新录音" : "开始录音";
        CancelButton.gameObject.SetActive(state != SessionState.Idle);
        cancelLabel.text = state == SessionState.Failed && canRetry ? "重新录音" : "取消";
        icon.color = state == SessionState.Recording ? new Color(1, .3f, .35f) : Color.white;
    }
    internal void ShowSuccess() { SetState(SessionState.Idle, false, "已填入"); successUntil = Time.unscaledTime + 2; }
    internal void RenderAudio(float elapsed, float volume, int maxSeconds)
    {
        Timer.text = TimeSpan.FromSeconds(Math.Max(0, elapsed)).ToString(@"mm\:ss") + " / " + TimeSpan.FromSeconds(maxSeconds).ToString(@"mm\:ss");
        level.anchorMax = new Vector2(Mathf.Clamp01(volume), 1);
        if (current == SessionState.Recording) icon.color = Color.Lerp(new Color(1, .18f, .25f), new Color(1, .65f, .65f), (Mathf.Sin(Time.unscaledTime * 5) + 1) / 2);
    }
    void Update()
    {
        // Modal sorting can change when the keyboard is reopened. The VR
        // raycaster reads the canvas order even when rendering inherits it.
        if (inputCanvas != null && keyboardCanvas != null)
        {
            if (inputCanvas.sortingLayerID != keyboardCanvas.sortingLayerID) inputCanvas.sortingLayerID = keyboardCanvas.sortingLayerID;
            if (inputCanvas.sortingOrder != keyboardCanvas.sortingOrder) inputCanvas.sortingOrder = keyboardCanvas.sortingOrder;
        }
        if (successUntil > 0 && Time.unscaledTime >= successUntil) SetState(SessionState.Idle, false, null);
    }
}
