using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LocalModContracts;

namespace VoiceInput.Core;

public interface IVoiceClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemVoiceClock : IVoiceClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal static class RecognitionSupport
{
    public static HttpClient CreateClient(HttpMessageHandler handler, out bool ownsClient)
    {
        ownsClient = true;
        var client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSaberVoiceInput/1.0");
        return client;
    }

    public static Uri Endpoint(JsonContractRegistry registry, string name)
    {
        if (registry == null) throw new SpeechFault("configuration", "", "语音服务端点配置无效。", false);
        Uri endpoint;
        try { endpoint = registry.Url(name); }
        catch (Exception exception) when (exception is InvalidDataException || exception is UriFormatException)
        {
            throw new SpeechFault("configuration", "", "语音服务端点配置无效。", false, exception);
        }
        if (endpoint == null)
            throw new SpeechFault("configuration", "", "语音服务端点配置无效。", false);
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new SpeechFault("configuration", "", "语音服务必须使用 HTTPS。", false);
        return endpoint;
    }

    public static HttpMethod EndpointMethod(JsonContractRegistry registry, string endpointName)
    {
        string method = (string)registry?.Endpoint(endpointName)?["method"];
        if (string.IsNullOrWhiteSpace(method))
            throw new SpeechFault("configuration", "", "语音服务端点配置无效。", false);
        switch (method.Trim().ToUpperInvariant())
        {
            case "GET": return HttpMethod.Get;
            case "POST": return HttpMethod.Post;
            case "PUT": return HttpMethod.Put;
            case "DELETE": return HttpMethod.Delete;
            case "HEAD": return HttpMethod.Head;
            case "OPTIONS": return HttpMethod.Options;
            case "PATCH": return new HttpMethod("PATCH");
            default: throw new SpeechFault("configuration", "", "语音服务端点配置无效。", false);
        }
    }

    public static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken token)
    {
        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var output = new MemoryStream())
        {
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > 4 * 1024 * 1024)
                    throw new InvalidDataException("Response is too large.");
                output.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    public static async Task<JObject> ReadObjectAsync(HttpResponseMessage response, CancellationToken token)
    {
        string text = await ReadTextAsync(response, token).ConfigureAwait(false);
        try { return JObject.Parse(text); }
        catch (JsonException exception) { throw new InvalidDataException("Response is not JSON.", exception); }
    }

    public static SpeechFault HttpFault(HttpResponseMessage response, string requestId)
    {
        int status = (int)response.StatusCode;
        bool retryable = status == 408 || status == 429 || status >= 500;
        if (status == 401)
            return SpeechFaultFactory.Authentication(requestId);
        if (status == 403) return SpeechFaultFactory.Permission(requestId);
        if (status == 429) return SpeechFaultFactory.Quota(requestId);
        if (status >= 500) return SpeechFaultFactory.Server(requestId);
        return new SpeechFault("http", requestId, "语音服务暂时不可用，请稍后重试。", retryable);
    }

    public static SpeechFault NetworkFault(Exception exception, string requestId, CancellationToken token)
    {
        if (exception is OperationCanceledException && token.IsCancellationRequested) throw exception;
        return SpeechFaultFactory.Network(requestId, exception);
    }

    public static string RequestId(string candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return fallback ?? string.Empty;
        var builder = new StringBuilder(Math.Min(candidate.Length, 128));
        foreach (char character in candidate)
        {
            if (builder.Length == 128) break;
            if (char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == ':' || character == '.')
                builder.Append(character);
        }
        return builder.Length == 0 ? fallback ?? string.Empty : builder.ToString();
    }

    public static string Sha256Hex(byte[] bytes)
    {
        using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
    }

    public static byte[] HmacSha256(byte[] key, string value)
    {
        using (var hmac = new HMACSHA256(key)) return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    public static string Hex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) builder.Append(value.ToString("x2"));
        return builder.ToString();
    }

    public static string ContractName(JsonContractRegistry registry, string endpointName, string role)
    {
        if (registry == null) throw new ArgumentNullException(nameof(registry));
        if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("Endpoint contract role is required.", nameof(role));
        string contractName = (string)registry.Endpoint(endpointName)[role];
        if (string.IsNullOrWhiteSpace(contractName))
            throw new SpeechFault("configuration", "", "语音服务端点配置无效。", false);
        return contractName;
    }

    public static JObject ToValidatedObject(JsonContractRegistry registry, string endpointName, string role, object value)
    {
        if (registry == null) throw new ArgumentNullException(nameof(registry));
        if (value == null) throw new ArgumentNullException(nameof(value));
        JObject document = JObject.FromObject(value);
        registry.Validate(ContractName(registry, endpointName, role), document);
        return document;
    }

    public static T FromValidatedObject<T>(JsonContractRegistry registry, string endpointName, string role, JObject document)
    {
        if (registry == null) throw new ArgumentNullException(nameof(registry));
        if (document == null) throw new ArgumentNullException(nameof(document));
        registry.Validate(ContractName(registry, endpointName, role), document);
        return document.ToObject<T>();
    }

    public static string RequiredSchemaConstant(JsonContractRegistry registry, string contractName, string propertyName)
    {
        if (registry == null) throw new ArgumentNullException(nameof(registry));
        var definitions = registry.Source["definitions"] as JObject;
        var definition = definitions?[contractName] as JObject;
        var properties = definition?["properties"] as JObject;
        var property = properties?[propertyName] as JObject;
        string value = (string)property?["const"];
        if (string.IsNullOrWhiteSpace(value))
            throw new SpeechFault("configuration", "", "语音服务端点配置无效。", false);
        return value;
    }

}
