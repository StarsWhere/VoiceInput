using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LocalModContracts;
using VoiceInput.Contracts;

namespace VoiceInput.Core;

public sealed class BaiduRecognizer : ISpeechRecognizer
{
    readonly string apiKey;
    readonly string secretKey;
    readonly int devPid;
    readonly HttpClient http;
    readonly bool ownsHttp;
    readonly IVoiceClock clock;
    readonly JsonContractRegistry registry;
    readonly SemaphoreSlim tokenGate = new SemaphoreSlim(1, 1);
    string accessToken;
    DateTimeOffset tokenExpiresUtc;
    int disposed;

    public BaiduRecognizer(
        string apiKey,
        string secretKey,
        int devPid = 1537,
        HttpMessageHandler handler = null,
        IVoiceClock clock = null,
        JsonContractRegistry registry = null)
    {
        this.apiKey = apiKey ?? string.Empty;
        this.secretKey = secretKey ?? string.Empty;
        this.devPid = devPid;
        this.clock = clock ?? new SystemVoiceClock();
        this.registry = registry ?? new VoiceProtocol().Registry;
        http = RecognitionSupport.CreateClient(handler, out ownsHttp);
    }

    public async Task<string> RecognizeAsync(byte[] pcm, CancellationToken token)
    {
        ThrowIfDisposed();
        ValidateInput(pcm);
        EnsureCredentials();
        string requestId = NewRequestId();
        string currentToken = await GetAccessTokenAsync(token, false).ConfigureAwait(false);
        BaiduReply reply = await RecognizeOnceAsync(pcm, currentToken, requestId, token).ConfigureAwait(false);
        if (reply.ErrorCode == 3302)
        {
            InvalidateToken(currentToken);
            currentToken = await GetAccessTokenAsync(token, true).ConfigureAwait(false);
            reply = await RecognizeOnceAsync(pcm, currentToken, requestId, token).ConfigureAwait(false);
        }

        if (reply.ErrorCode != 0)
        {
            throw FaultForBaidu(reply.ErrorCode, reply.RequestId);
        }
        string result = FirstUsable(reply.Results);
        if (result.Length == 0) throw SpeechFaultFactory.EmptyResult(reply.RequestId);
        return result;
    }

    async Task<string> GetAccessTokenAsync(CancellationToken token, bool forceRefresh)
    {
        await tokenGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && !string.IsNullOrWhiteSpace(accessToken) && clock.UtcNow < tokenExpiresUtc)
                return accessToken;

            string requestId = NewRequestId();
            var endpoint = RecognitionSupport.Endpoint(registry, "baiduToken");
            var tokenRequest = new BaiduTokenRequest
            {
                Grant_type = BaiduTokenRequestGrant_type.Client_credentials,
                Client_id = apiKey,
                Client_secret = secretKey
            };
            JObject tokenDocument = RecognitionSupport.ToValidatedObject(registry, "baiduToken", "request", tokenRequest);
            using (var request = new HttpRequestMessage(RecognitionSupport.EndpointMethod(registry, "baiduToken"), endpoint))
            using (var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", (string)tokenDocument["grant_type"]),
                new KeyValuePair<string, string>("client_id", (string)tokenDocument["client_id"]),
                new KeyValuePair<string, string>("client_secret", (string)tokenDocument["client_secret"])
            }))
            {
                request.Content = form;
                try
                {
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) throw RecognitionSupport.HttpFault(response, requestId);
                        JObject data = await RecognitionSupport.ReadObjectAsync(response, token).ConfigureAwait(false);
                        BaiduTokenResponse tokenResponse = RecognitionSupport.FromValidatedObject<BaiduTokenResponse>(registry, "baiduToken", "response", data);
                        string value = tokenResponse.Access_token;
                        int expires = tokenResponse.Expires_in;
                        if (string.IsNullOrWhiteSpace(value) || expires <= 0)
                            throw SpeechFaultFactory.Authentication(requestId);
                        accessToken = value;
                        tokenExpiresUtc = clock.UtcNow.AddSeconds(Math.Max(1, expires - 60));
                        return accessToken;
                    }
                }
                catch (SpeechFault) { throw; }
                catch (InvalidDataException exception)
                { throw SpeechFaultFactory.Protocol(requestId, exception); }
                catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException)
                { throw RecognitionSupport.NetworkFault(exception, requestId, token); }
                catch (Exception exception)
                { throw SpeechFaultFactory.Authentication(requestId, exception); }
            }
        }
        finally { tokenGate.Release(); }
    }

    async Task<BaiduReply> RecognizeOnceAsync(byte[] pcm, string tokenValue, string requestId, CancellationToken cancellationToken)
    {
        var endpoint = RecognitionSupport.Endpoint(registry, "baiduRecognize");
        var recognitionRequest = new BaiduRecognitionRequest
        {
            Format = BaiduRecognitionRequestFormat.Pcm,
            Rate = BaiduRecognitionRequestRate._16000,
            Channel = BaiduRecognitionRequestChannel._1,
            Cuid = "BeatSaberVoiceInput",
            Token = tokenValue,
            Dev_pid = devPid,
            Speech = Convert.ToBase64String(pcm),
            Len = pcm.Length
        };
        JObject requestDocument = RecognitionSupport.ToValidatedObject(registry, "baiduRecognize", "request", recognitionRequest);
        string json = requestDocument.ToString(Formatting.None);
        using (var request = new HttpRequestMessage(RecognitionSupport.EndpointMethod(registry, "baiduRecognize"), endpoint))
        using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
        {
            request.Content = content;
            try
            {
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw RecognitionSupport.HttpFault(response, requestId);
                    JObject data = await RecognitionSupport.ReadObjectAsync(response, cancellationToken).ConfigureAwait(false);
                    BaiduRecognitionResponse recognitionResponse = RecognitionSupport.FromValidatedObject<BaiduRecognitionResponse>(registry, "baiduRecognize", "response", data);
                    int code = recognitionResponse.Err_no;
                    string responseId = RecognitionSupport.RequestId(recognitionResponse.Sn, requestId);
                    return new BaiduReply(code, responseId, recognitionResponse.Result == null
                        ? new List<string>()
                        : new List<string>(recognitionResponse.Result));
                }
            }
            catch (SpeechFault) { throw; }
            catch (InvalidDataException exception)
            { throw SpeechFaultFactory.Protocol(requestId, exception); }
            catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException)
            { throw RecognitionSupport.NetworkFault(exception, requestId, cancellationToken); }
            catch (Exception exception)
            { throw SpeechFaultFactory.Provider(requestId, exception); }
        }
    }

    void InvalidateToken(string rejectedToken)
    {
        if (string.Equals(accessToken, rejectedToken, StringComparison.Ordinal))
        {
            accessToken = null;
            tokenExpiresUtc = DateTimeOffset.MinValue;
        }
    }

    void EnsureCredentials()
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(secretKey) || devPid <= 0)
            throw SpeechFaultFactory.Credentials();
    }

    static void ValidateInput(byte[] pcm)
    {
        if (!AudioPcm.IsUsable(pcm)) throw SpeechFaultFactory.Audio();
    }

    static string FirstUsable(IList<string> results)
    {
        if (results == null) return string.Empty;
        foreach (string value in results)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return string.Empty;
    }

    static SpeechFault FaultForBaidu(int errorCode, string requestId)
    {
        // Baidu error codes: https://ai.baidu.com/ai-doc/SPEECH/Zlbxew2qk
        if (errorCode == 3300) return new SpeechFault("protocol", requestId, "语音请求格式无效，请重新录音。", false);
        if (errorCode == 3301) return SpeechFaultFactory.Audio(requestId);
        if (errorCode == 3302) return SpeechFaultFactory.Authentication(requestId);
        if (errorCode == 3304 || errorCode == 3305 || errorCode == 17 || errorCode == 18 || errorCode == 19)
            return SpeechFaultFactory.Quota(requestId);
        if (errorCode == 6) return SpeechFaultFactory.Permission(requestId);
        if (errorCode == 3303 || errorCode == 3307) return SpeechFaultFactory.Server(requestId);
        return SpeechFaultFactory.Provider(requestId);
    }

    static string NewRequestId() => Guid.NewGuid().ToString("N");

    void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(BaiduRecognizer));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        tokenGate.Dispose();
        if (ownsHttp) http.Dispose();
    }

    sealed class BaiduReply
    {
        public BaiduReply(int errorCode, string requestId, IList<string> results)
        { ErrorCode = errorCode; RequestId = requestId; Results = results; }
        public int ErrorCode { get; }
        public string RequestId { get; }
        public IList<string> Results { get; }
    }
}
