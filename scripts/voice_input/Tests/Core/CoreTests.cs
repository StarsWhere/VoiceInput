using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LocalModContracts;
using VoiceInput.Contracts;

namespace VoiceInput.Core.Tests;

internal static class CoreTests
{
    public static void RunAll()
    {
        TestPcmConversion();
        TestMalformedConfiguration();
        TestSettingsSave();
        TestContractBoundaryAndRegistryOnlyHost().GetAwaiter().GetResult();
        TestBaiduRefreshAndCaching().GetAwaiter().GetResult();
        TestBaiduFaultCategories().GetAwaiter().GetResult();
        TestTencentSignatureAndRequest().GetAwaiter().GetResult();
        TestTencentFaultCategories().GetAwaiter().GetResult();
        TestCancellation().GetAwaiter().GetResult();
    }

    static void TestSettingsSave()
    {
        string root = TempDirectory();
        try
        {
            var protocol = new VoiceProtocol();
            protocol.LoadConfiguration(root);
            string path = Path.Combine(root, "UserData", "VoiceInput.json");
            JObject external = JObject.Parse(File.ReadAllText(path));
            external["provider"] = "baidu";
            external["baidu"]["apiKey"] = "external-key";
            external["baidu"]["secretKey"] = "external-secret";
            external["baidu"]["devPid"] = 1737;
            external["tencent"]["engineModel"] = "16k_en";
            File.WriteAllText(path, external.ToString(Formatting.Indented));

            int minimum = protocol.ConfigurationLimit("maxRecordingSeconds", "minimum");
            int maximum = protocol.ConfigurationLimit("maxRecordingSeconds", "maximum");
            int timeout = protocol.ConfigurationLimit("requestTimeoutSeconds", "minimum");
            Configuration saved = protocol.SaveSettings(root, "test device", maximum, timeout);
            JObject result = JObject.Parse(File.ReadAllText(path));
            Assert(saved.Microphone == "test device" && saved.MaxRecordingSeconds == maximum && saved.RequestTimeoutSeconds == timeout,
                "settings save returns schema-validated persisted values");
            Assert((string)result["provider"] == "baidu" && (string)result["baidu"]["apiKey"] == "external-key" &&
                (string)result["baidu"]["secretKey"] == "external-secret" && (int)result["baidu"]["devPid"] == 1737 &&
                (string)result["tencent"]["engineModel"] == "16k_en", "settings save preserves external provider, model and credentials");
            Assert(protocol.LoadConfiguration(root).Microphone == "test device", "saved settings reload");

            string before = File.ReadAllText(path);
            AssertThrows<SpeechFault>(() => protocol.SaveSettings(root, "other", minimum - 1, timeout), "out-of-range draft rejected");
            Assert(File.ReadAllText(path) == before, "invalid draft leaves existing configuration unchanged");
            File.WriteAllText(path, "{ malformed");
            AssertThrows<SpeechFault>(() => protocol.SaveSettings(root, "other", minimum, timeout), "invalid on-disk configuration rejected");
            Assert(File.ReadAllText(path) == "{ malformed", "invalid external configuration is not overwritten");
            Assert(!Directory.GetFiles(Path.GetDirectoryName(path), ".VoiceInput.*.tmp").Any(), "temporary files cleaned up");
        }
        finally { TryDelete(root); }
    }

    static void TestPcmConversion()
    {
        var input = new float[8];
        input[0] = 1; input[1] = -1;
        input[2] = 0.5f; input[3] = 0.5f;
        input[4] = -0.5f; input[5] = -0.5f;
        input[6] = 0; input[7] = 0;
        byte[] pcm = AudioPcm.Encode(input, 2, 8000);
        Assert(pcm.Length == 16, "PCM resampling output frame count");
        Assert(AudioPcm.MeasureRms(pcm) > 0.01, "PCM RMS measurement");
        Assert(AudioPcm.IsSilent(new byte[320]), "PCM silence detection");
        Assert(AudioPcm.IsUsable(pcm), "non-silent PCM is usable");
        AssertThrows<ArgumentOutOfRangeException>(() => AudioPcm.Encode(input, 0, 16000), "PCM channel validation");
    }

    static void TestMalformedConfiguration()
    {
        string root = TempDirectory();
        try
        {
            var protocol = new VoiceProtocol();
            Configuration initial = protocol.LoadConfiguration(root);
            Assert((int)initial.SchemaVersion == 1 && initial.MaxRecordingSeconds == 30, "default configuration is generated");
            Configuration malformedObject = initial;
            malformedObject.MaxRecordingSeconds = 56;
            SpeechFault malformedFault = AssertThrows<SpeechFault>(() => protocol.CreateRecognizer(malformedObject), "generated configuration is schema-validated");
            Assert(malformedFault.Category == "configuration" && malformedFault.UserMessage.IndexOf("UserData/VoiceInput.json", StringComparison.Ordinal) >= 0,
                "schema validation has actionable startup-only message");
            initial.MaxRecordingSeconds = 30;
            SpeechFault missingCredentials = AssertThrows<SpeechFault>(() => protocol.CreateRecognizer(initial), "missing credentials fail at recognizer creation");
            Assert(missingCredentials.Category == "credentials" && missingCredentials.UserMessage.IndexOf("凭据", StringComparison.Ordinal) >= 0,
                "missing credentials have an actionable stable message");
            string path = Path.Combine(root, "UserData", "VoiceInput.json");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"provider\":\"baidu\"}");
            SpeechFault fault = AssertThrows<SpeechFault>(() => protocol.LoadConfiguration(root), "malformed configuration is rejected");
            Assert(fault.Category == "configuration" && fault.UserMessage.IndexOf("VoiceInput.json", StringComparison.Ordinal) >= 0,
                "malformed configuration has stable actionable fault");
        }
        finally { TryDelete(root); }
    }

    static async Task TestContractBoundaryAndRegistryOnlyHost()
    {
        var protocol = new VoiceProtocol();
        JsonContractRegistry registry = protocol.Registry;
        AssertThrows<InvalidDataException>(() => registry.Validate("BaiduRecognitionRequest", new JObject
        {
            ["format"] = "pcm",
            ["rate"] = "16000",
            ["channel"] = 1,
            ["cuid"] = "test",
            ["token"] = "token",
            ["dev_pid"] = 1537,
            ["speech"] = "AA==",
            ["len"] = 2
        }), "malformed request is rejected by the schema");
        AssertThrows<InvalidDataException>(() => registry.Validate("TencentRecognitionHeaders", new JObject
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Host"] = "asr.tencentcloudapi.com",
            ["X-TC-Version"] = "wrong",
            ["X-TC-Timestamp"] = "123",
            ["Authorization"] = "signed"
        }), "required Tencent header metadata is rejected when malformed");

        var malformedResponseHandler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"token\",\"expires_in\":3600}"),
            _ => Json(HttpStatusCode.OK, "{\"err_no\":0,\"result\":{\"wrong\":true}}"));
        using (var recognizer = new BaiduRecognizer("ak", "sk", handler: malformedResponseHandler, registry: registry))
        {
            SpeechFault fault = await AssertThrowsAsync<SpeechFault>(() => recognizer.RecognizeAsync(SpeechPcm(), CancellationToken.None),
                "malformed successful response is rejected by the schema");
            Assert(fault.Category == "protocol", "malformed successful response has protocol fault");
        }

        string endpointJson = ReadResource("VoiceInput.Contracts.endpoints.json");
        var endpoints = JObject.Parse(endpointJson);
        var services = (JObject)endpoints["services"];
        services["baiduAuth"] = "https://registry.example/auth/";
        services["baiduSpeech"] = "https://registry.example/speech/";
        var registryOnly = new JsonContractRegistry(registry.Source.ToString(Formatting.None), endpoints.ToString(Formatting.None));
        var hostHandler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"token\",\"expires_in\":3600}"),
            _ => Json(HttpStatusCode.OK, "{\"err_no\":0,\"result\":[\"host\"]}"));
        using (var recognizer = new BaiduRecognizer("ak", "sk", handler: hostHandler, registry: registryOnly))
        {
            string result = await recognizer.RecognizeAsync(SpeechPcm(), CancellationToken.None).ConfigureAwait(false);
            Assert(result == "host" && hostHandler.Requests.All(request => request.RequestUri.Host == "registry.example"),
                "provider uses registry service hosts exclusively");
        }
    }

    static string ReadResource(string name)
    {
        using (var stream = typeof(VoiceProtocol).Assembly.GetManifestResourceStream(name))
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8)) return reader.ReadToEnd();
    }

    static async Task TestBaiduRefreshAndCaching()
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"token-one\",\"expires_in\":3600}"),
            _ => Json(HttpStatusCode.OK, "{\"err_no\":3302,\"sn\":\"auth-first\"}"),
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"token-two\",\"expires_in\":3600}"),
            _ => Json(HttpStatusCode.OK, "{\"err_no\":0,\"sn\":\"ok-1\",\"result\":[\"你好\"]}")
        );
        using (var recognizer = new BaiduRecognizer("ak", "sk", 1537, handler, new FixedClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"))))
        {
            string result = await recognizer.RecognizeAsync(SpeechPcm(), CancellationToken.None).ConfigureAwait(false);
            Assert(result == "你好", "Baidu recognition result");
            Assert(handler.Requests.Count == 4, "Baidu refresh happens exactly once after 3302");
            Assert(handler.Requests[0].RequestUri.AbsolutePath.EndsWith("/oauth/2.0/token", StringComparison.Ordinal), "Baidu token endpoint");
            Assert(handler.Requests[1].RequestUri.Query.Length == 0 && handler.Bodies[1].IndexOf("token-one", StringComparison.Ordinal) >= 0,
                "Baidu first token is sent in canonical JSON body");
            JObject body = JObject.Parse(handler.Bodies[3]);
            Assert((int)body["dev_pid"] == 1537 && (int)body["len"] == SpeechPcm().Length && (string)body["format"] == "pcm",
                "Baidu request audio contract");
        }

        var cached = new QueueHandler(
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"cached\",\"expires_in\":3600}"),
            _ => Json(HttpStatusCode.OK, "{\"err_no\":0,\"result\":[\"一\"]}"),
            _ => Json(HttpStatusCode.OK, "{\"err_no\":0,\"result\":[\"二\"]}")
        );
        using (var recognizer = new BaiduRecognizer("ak", "sk", handler: cached))
        {
            await recognizer.RecognizeAsync(SpeechPcm(), CancellationToken.None).ConfigureAwait(false);
            await recognizer.RecognizeAsync(SpeechPcm(), CancellationToken.None).ConfigureAwait(false);
            Assert(cached.Requests.Count == 3, "Baidu token is cached until expiry");
        }
    }

    static async Task TestBaiduFaultCategories()
    {
        await AssertBaiduFault(3301, "audio", false).ConfigureAwait(false);
        await AssertBaiduFault(3305, "quota", true).ConfigureAwait(false);
    }

    static async Task AssertBaiduFault(int errorCode, string category, bool retryable)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"token\",\"expires_in\":3600}"),
            _ => Json(HttpStatusCode.OK, "{\"err_no\":" + errorCode + ",\"err_msg\":\"server detail must not escape\"}"));
        using (var recognizer = new BaiduRecognizer("ak", "sk", handler: handler))
        {
            SpeechFault fault = await AssertThrowsAsync<SpeechFault>(() => recognizer.RecognizeAsync(SpeechPcm(), CancellationToken.None),
                "Baidu HTTP 200 error " + errorCode).ConfigureAwait(false);
            Assert(fault.Category == category && fault.Retryable == retryable &&
                   fault.Message.IndexOf("server detail", StringComparison.OrdinalIgnoreCase) < 0,
                "Baidu error category is stable and safe: " + errorCode + ":" + fault.Category);
        }
    }

    static async Task TestTencentSignatureAndRequest()
    {
        var handler = new QueueHandler(_ => Json(HttpStatusCode.OK, "{\"Response\":{\"RequestId\":\"req-1\",\"Result\":\"腾讯\"}}"));
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(1599140162));
        using (var recognizer = new TencentRecognizer("AKIDexample", "secret-example", "16k_zh", handler: handler, clock: clock))
        {
            string result = await recognizer.RecognizeAsync(SpeechPcm(), CancellationToken.None).ConfigureAwait(false);
            Assert(result == "腾讯", "Tencent recognition result");
            HttpRequestMessage request = handler.Requests.Single();
            string authorization = request.Headers.GetValues("Authorization").Single();
            byte[] payload = handler.BodyBytes.Single();
            string expected = IndependentTencentAuthorization("AKIDexample", "secret-example", request.RequestUri, payload, 1599140162);
            Assert(authorization == expected, "Tencent TC3 signature matches independent signer");
            Assert(request.Headers.GetValues("X-TC-Action").Single() == "SentenceRecognition", "Tencent action header");
            Assert(request.Headers.GetValues("X-TC-Version").Single() == "2019-06-14", "Tencent version header");
            Assert(request.Content.Headers.GetValues("Content-Type").Single() == "application/json; charset=utf-8", "Tencent signed content type is sent exactly");
            JObject body = JObject.Parse(EncodingUtf8(payload));
            Assert((string)body["EngSerViceType"] == "16k_zh" && (int)body["SourceType"] == 1 &&
                   (string)body["VoiceFormat"] == "pcm" && (int)body["DataLen"] == SpeechPcm().Length,
                "Tencent request audio contract");
        }
    }

    static async Task TestTencentFaultCategories()
    {
        await AssertTencentFault("RequestLimitExceeded", "quota").ConfigureAwait(false);
        await AssertTencentFault("UnauthorizedOperation", "permission").ConfigureAwait(false);
        await AssertTencentFault("AuthFailure", "authentication").ConfigureAwait(false);
        await AssertTencentFault("InternalError", "server").ConfigureAwait(false);
    }

    static async Task AssertTencentFault(string code, string category)
    {
        var handler = new QueueHandler(_ => Json(HttpStatusCode.OK,
            "{\"Response\":{\"RequestId\":\"req-" + code + "\",\"Error\":{\"Code\":\"" + code + "\",\"Message\":\"secret server detail\"}}}"));
        using (var recognizer = new TencentRecognizer("id", "key", handler: handler))
        {
            SpeechFault fault = await AssertThrowsAsync<SpeechFault>(() => recognizer.RecognizeAsync(SpeechPcm(), CancellationToken.None),
                "Tencent error category " + code).ConfigureAwait(false);
            Assert(fault.Category == category && fault.Message.IndexOf("secret server detail", StringComparison.OrdinalIgnoreCase) < 0,
                "Tencent error category is stable and safe: " + code);
        }
    }

    static async Task TestCancellation()
    {
        var handler = new QueueHandler(async (request, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return Json(HttpStatusCode.OK, "{\"access_token\":\"never\",\"expires_in\":3600}");
        });
        using (var recognizer = new BaiduRecognizer("ak", "sk", handler: handler))
        using (var cancellation = new CancellationTokenSource())
        {
            Task<string> task = recognizer.RecognizeAsync(SpeechPcm(), cancellation.Token);
            cancellation.CancelAfter(20);
            await AssertThrowsAsync<OperationCanceledException>(() => task, "provider I/O observes cancellation");
        }
    }

    static string IndependentTencentAuthorization(string id, string key, Uri endpoint, byte[] payload, long timestamp)
    {
        string date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd");
        string canonical = "POST\n" + endpoint.AbsolutePath + "\n\n" +
            "content-type:application/json; charset=utf-8\n" + "host:" + endpoint.Host +
            "\n\ncontent-type;host\n" + Sha256(payload);
        string scope = date + "/asr/tc3_request";
        string stringToSign = "TC3-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + Sha256(Encoding.UTF8.GetBytes(canonical));
        byte[] dateKey = Hmac(Encoding.UTF8.GetBytes("TC3" + key), date);
        byte[] serviceKey = Hmac(dateKey, "asr");
        byte[] signingKey = Hmac(serviceKey, "tc3_request");
        return "TC3-HMAC-SHA256 Credential=" + id + "/" + scope + ", SignedHeaders=content-type;host, Signature=" + ToHex(Hmac(signingKey, stringToSign));
    }

    static byte[] Hmac(byte[] key, string value)
    {
        using (var hmac = new System.Security.Cryptography.HMACSHA256(key)) return hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
    }

    static string Sha256(byte[] data)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create()) return ToHex(sha.ComputeHash(data));
    }

    static string ToHex(byte[] bytes) => string.Concat(bytes.Select(value => value.ToString("x2")));
    static string EncodingUtf8(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    static byte[] SpeechPcm() => new byte[] { 0, 16, 0, 32, 0, 48, 0, 64, 0, 48, 0, 32, 0, 16, 0, 8 };
    static string TempDirectory() => Path.Combine(Directory.GetCurrentDirectory(), ".tmp", "voice-input", "configuration-" + Guid.NewGuid().ToString("N"));
    static void TryDelete(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }

    static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        return response;
    }

    static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("VoiceInput core test failed: " + name);
    }

    static T AssertThrows<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException("VoiceInput core test failed: " + name);
    }

    static async Task<T> AssertThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException("VoiceInput core test failed: " + name);
    }

    sealed class FixedClock : IVoiceClock
    {
        public FixedClock(DateTimeOffset utcNow) { UtcNow = utcNow; }
        public DateTimeOffset UtcNow { get; }
    }

    sealed class QueueHandler : HttpMessageHandler
    {
        readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> responses = new Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>();
        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] factories)
        {
            foreach (var factory in factories) responses.Enqueue((request, token) => Task.FromResult(factory(request)));
        }
        public QueueHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] factories)
        { foreach (var factory in factories) responses.Enqueue(factory); }
        public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();
        public List<string> Bodies { get; } = new List<string>();
        public List<byte[]> BodyBytes { get; } = new List<byte[]>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            byte[] bytes = request.Content == null
                ? Array.Empty<byte>()
                : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            BodyBytes.Add(bytes);
            Bodies.Add(System.Text.Encoding.UTF8.GetString(bytes));
            if (responses.Count == 0) throw new InvalidOperationException("Unexpected HTTP request.");
            return responses.Dequeue()(request, cancellationToken);
        }
    }
}
