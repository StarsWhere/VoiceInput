using System;

namespace VoiceInput.Core;

/// <summary>A stable, user-facing failure from the voice input pipeline.</summary>
public sealed class SpeechFault : Exception
{
    public string Category { get; }
    public string RequestId { get; }
    public string UserMessage { get; }
    public bool Retryable { get; }

    public SpeechFault(
        string category,
        string requestId,
        string userMessage,
        bool retryable = false,
        Exception innerException = null)
        : base(userMessage ?? string.Empty, innerException)
    {
        Category = string.IsNullOrWhiteSpace(category) ? "speech" : category;
        RequestId = requestId ?? string.Empty;
        UserMessage = userMessage ?? "语音识别失败，请重试。";
        Retryable = retryable;
    }
}

internal static class SpeechFaultFactory
{
    public static SpeechFault Audio(string requestId = "") =>
        new SpeechFault("audio", requestId, "没有可识别的语音。", false);

    public static SpeechFault Credentials(string requestId = "") =>
        new SpeechFault("credentials", requestId, "请在 UserData/VoiceInput.json 填写语音服务凭据后重新加载配置。", false);

    public static SpeechFault Configuration(Exception inner = null) =>
        new SpeechFault("configuration", "", "请修正 UserData/VoiceInput.json 后重新加载配置。", false, inner);

    public static SpeechFault ConfigurationWrite(Exception inner = null) =>
        new SpeechFault("configuration-write", "", "无法保存 UserData/VoiceInput.json，请检查权限后重试。", false, inner);

    public static SpeechFault Network(string requestId = "", Exception inner = null) =>
        new SpeechFault("network", requestId, "语音服务网络请求失败，请稍后重试。", true, inner);

    public static SpeechFault Http(string requestId = "", Exception inner = null) =>
        new SpeechFault("http", requestId, "语音服务暂时不可用，请稍后重试。", true, inner);

    public static SpeechFault Quota(string requestId = "", Exception inner = null) =>
        new SpeechFault("quota", requestId, "语音服务已达到用量限制，请稍后重试或检查配额。", true, inner);

    public static SpeechFault Permission(string requestId = "", Exception inner = null) =>
        new SpeechFault("permission", requestId, "语音服务没有执行此操作的权限，请检查账户权限。", false, inner);

    public static SpeechFault Server(string requestId = "", Exception inner = null) =>
        new SpeechFault("server", requestId, "语音服务暂时不可用，请稍后重试。", true, inner);

    public static SpeechFault Protocol(string requestId = "", Exception inner = null) =>
        new SpeechFault("protocol", requestId, "语音服务返回了无法识别的数据。", true, inner);

    public static SpeechFault Authentication(string requestId = "", Exception inner = null) =>
        new SpeechFault("authentication", requestId, "语音服务认证失败，请检查凭据。", false, inner);

    public static SpeechFault Provider(string requestId = "", Exception inner = null) =>
        new SpeechFault("provider", requestId, "语音识别服务返回了无法处理的结果。", true, inner);

    public static SpeechFault EmptyResult(string requestId = "") =>
        new SpeechFault("empty-result", requestId, "未识别到有效文字，请重试。", true);
}
