using System;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LocalModContracts;
using VoiceInput.Contracts;

namespace VoiceInput.Core;

public sealed class TencentRecognizer : ISpeechRecognizer
{
    const string DefaultSigningSuffix = "tc3_request";

    readonly string secretId;
    readonly string secretKey;
    readonly string engineModel;
    readonly string sessionToken;
    readonly HttpClient http;
    readonly bool ownsHttp;
    readonly IVoiceClock clock;
    readonly JsonContractRegistry registry;
    int disposed;

    public TencentRecognizer(
        string secretId,
        string secretKey,
        string engineModel = "16k_zh",
        string sessionToken = "",
        HttpMessageHandler handler = null,
        IVoiceClock clock = null,
        JsonContractRegistry registry = null)
    {
        this.secretId = secretId ?? string.Empty;
        this.secretKey = secretKey ?? string.Empty;
        this.engineModel = engineModel ?? string.Empty;
        this.sessionToken = sessionToken ?? string.Empty;
        this.clock = clock ?? new SystemVoiceClock();
        this.registry = registry ?? new VoiceProtocol().Registry;
        http = RecognitionSupport.CreateClient(handler, out ownsHttp);
    }

    public async Task<string> RecognizeAsync(byte[] pcm, CancellationToken token)
    {
        ThrowIfDisposed();
        if (!AudioPcm.IsUsable(pcm)) throw SpeechFaultFactory.Audio();
        if (string.IsNullOrWhiteSpace(secretId) || string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(engineModel))
            throw SpeechFaultFactory.Credentials();

        string requestId = Guid.NewGuid().ToString("N");
        Uri endpoint = RecognitionSupport.Endpoint(registry, "tencentRecognize");
        HttpMethod endpointMethod = RecognitionSupport.EndpointMethod(registry, "tencentRecognize");
        var recognitionRequest = new TencentRecognitionRequest
        {
            EngSerViceType = engineModel,
            SourceType = TencentRecognitionRequestSourceType._1,
            VoiceFormat = TencentRecognitionRequestVoiceFormat.Pcm,
            Data = Convert.ToBase64String(pcm),
            DataLen = pcm.Length
        };
        JObject requestDocument = RecognitionSupport.ToValidatedObject(registry, "tencentRecognize", "request", recognitionRequest);
        byte[] payload = Encoding.UTF8.GetBytes(requestDocument.ToString(Formatting.None));
        long timestamp = clock.UtcNow.ToUnixTimeSeconds();
        JObject endpointMetadata = registry.Endpoint("tencentRecognize");
        string signingService = (string)endpointMetadata["signingService"];
        if (string.IsNullOrWhiteSpace(signingService))
            throw new SpeechFault("configuration", requestId, "语音服务端点配置无效。", false);
        string headerContract = RecognitionSupport.ContractName(registry, "tencentRecognize", "headers");
        string action = RecognitionSupport.RequiredSchemaConstant(registry, headerContract, "X-TC-Action");
        string version = RecognitionSupport.RequiredSchemaConstant(registry, headerContract, "X-TC-Version");
        string authorization = BuildAuthorization(secretId, secretKey, endpoint, payload, timestamp, signingService, endpointMethod.Method);
        var headerDocument = RecognitionSupport.ToValidatedObject(registry, "tencentRecognize", "headers", new TencentRecognitionHeaders
        {
            ContentType = "application/json; charset=utf-8",
            Host = endpoint.Authority,
            XTCAction = action,
            XTCVersion = version,
            XTCTimestamp = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Authorization = authorization,
            XTCToken = sessionToken
        });

        using (var request = new HttpRequestMessage(endpointMethod, endpoint))
        using (var content = new ByteArrayContent(payload))
        {
            // Keep the actual header byte-for-byte aligned with the canonical value used by TC3.
            content.Headers.Remove("Content-Type");
            content.Headers.TryAddWithoutValidation("Content-Type", (string)headerDocument["Content-Type"]);
            request.Content = content;
            request.Headers.Host = (string)headerDocument["Host"];
            request.Headers.TryAddWithoutValidation("X-TC-Action", (string)headerDocument["X-TC-Action"]);
            request.Headers.TryAddWithoutValidation("X-TC-Version", (string)headerDocument["X-TC-Version"]);
            request.Headers.TryAddWithoutValidation("X-TC-Timestamp", (string)headerDocument["X-TC-Timestamp"]);
            request.Headers.TryAddWithoutValidation("Authorization", (string)headerDocument["Authorization"]);
            string headerToken = (string)headerDocument["X-TC-Token"];
            if (!string.IsNullOrWhiteSpace(headerToken)) request.Headers.TryAddWithoutValidation("X-TC-Token", headerToken);
            try
            {
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw RecognitionSupport.HttpFault(response, requestId);
                    JObject data = await RecognitionSupport.ReadObjectAsync(response, token).ConfigureAwait(false);
                    TencentRecognitionResponse recognitionResponse = RecognitionSupport.FromValidatedObject<TencentRecognitionResponse>(registry, "tencentRecognize", "response", data);
                    string responseId = RecognitionSupport.RequestId(recognitionResponse.Response.RequestId, requestId);
                    if (recognitionResponse.Response.Error != null)
                    {
                        throw FaultForTencent(recognitionResponse.Response.Error.Code, responseId);
                    }
                    string result = (recognitionResponse.Response.Result ?? string.Empty).Trim();
                    if (result.Length == 0) throw SpeechFaultFactory.EmptyResult(responseId);
                    return result;
                }
            }
            catch (SpeechFault) { throw; }
            catch (InvalidDataException exception)
            { throw SpeechFaultFactory.Protocol(requestId, exception); }
            catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException)
            { throw RecognitionSupport.NetworkFault(exception, requestId, token); }
            catch (Exception exception)
            { throw SpeechFaultFactory.Provider(requestId, exception); }
        }
    }

    public static string BuildAuthorization(string id, string key, Uri endpoint, byte[] payload, long timestamp)
    {
        return BuildAuthorization(id, key, endpoint, payload, timestamp, "asr", "POST");
    }

    static string BuildAuthorization(string id, string key, Uri endpoint, byte[] payload, long timestamp, string service, string method)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Tencent credentials are required.");
        if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Tencent signing metadata is required.");
        string date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string host = endpoint.Authority;
        string canonicalUri = string.IsNullOrEmpty(endpoint.AbsolutePath) ? "/" : endpoint.AbsolutePath;
        string canonicalQueryString = string.IsNullOrEmpty(endpoint.Query) ? string.Empty : endpoint.Query.TrimStart('?');
        const string canonicalHeaders = "content-type:application/json; charset=utf-8\nhost:";
        string canonicalRequest = method.Trim().ToUpperInvariant() + "\n" + canonicalUri + "\n" + canonicalQueryString + "\n" +
            canonicalHeaders + host + "\n\ncontent-type;host\n" + RecognitionSupport.Sha256Hex(payload);
        string credentialScope = date + "/" + service + "/" + DefaultSigningSuffix;
        string stringToSign = "TC3-HMAC-SHA256\n" + timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
            credentialScope + "\n" + RecognitionSupport.Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest));
        byte[] secretDate = RecognitionSupport.HmacSha256(Encoding.UTF8.GetBytes("TC3" + key), date);
        byte[] secretService = RecognitionSupport.HmacSha256(secretDate, service);
        byte[] secretSigning = RecognitionSupport.HmacSha256(secretService, DefaultSigningSuffix);
        string signature = RecognitionSupport.Hex(RecognitionSupport.HmacSha256(secretSigning, stringToSign));
        return "TC3-HMAC-SHA256 Credential=" + id + "/" + credentialScope + ", SignedHeaders=content-type;host, Signature=" + signature;
    }

    static SpeechFault FaultForTencent(string code, string requestId)
    {
        string normalized = code ?? string.Empty;
        if (normalized.IndexOf("Permission", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("AccessDenied", StringComparison.OrdinalIgnoreCase) >= 0)
            return SpeechFaultFactory.Permission(requestId);
        if (normalized.IndexOf("Auth", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("Credential", StringComparison.OrdinalIgnoreCase) >= 0)
            return SpeechFaultFactory.Authentication(requestId);
        if (normalized.IndexOf("Limit", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("Quota", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("Frequency", StringComparison.OrdinalIgnoreCase) >= 0)
            return SpeechFaultFactory.Quota(requestId);
        if (normalized.IndexOf("Internal", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("Unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("Service", StringComparison.OrdinalIgnoreCase) >= 0)
            return SpeechFaultFactory.Server(requestId);
        return SpeechFaultFactory.Provider(requestId);
    }

    void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(TencentRecognizer));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (ownsHttp) http.Dispose();
    }
}
