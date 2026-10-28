using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VoiceInput.Contracts;
using LocalModContracts;

namespace VoiceInput.Core;

/// <summary>Contract and service factory for the VoiceInput mod.</summary>
public sealed class VoiceProtocol
{
    readonly JsonContractRegistry registry;

    public VoiceProtocol()
        : this(new JsonContractRegistry(
            ReadResource("VoiceInput.Contracts.schema.json"),
            ReadResource("VoiceInput.Contracts.endpoints.json")))
    {
    }

    public VoiceProtocol(JsonContractRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public JsonContractRegistry Registry => registry;

    public Configuration LoadConfiguration(string gameRoot)
    {
        string path = ConfigurationPath(gameRoot);
        JObject document;
        if (!File.Exists(path))
        {
            document = DefaultConfiguration();
            ValidateConfiguration(document);
            try
            {
                WriteAtomic(path, document, false);
            }
            catch (IOException) when (File.Exists(path)) { return LoadExistingConfiguration(gameRoot); }
            catch (Exception exception)
            {
                throw SpeechFaultFactory.ConfigurationWrite(exception);
            }
        }
        else
        {
            document = ReadConfiguration(path);
        }
        return ValidateConfiguration(document);
    }

    public Configuration LoadExistingConfiguration(string gameRoot)
    {
        string path = ConfigurationPath(gameRoot);
        if (!File.Exists(path)) throw SpeechFaultFactory.Configuration();
        return ValidateConfiguration(ReadConfiguration(path));
    }

    // Re-read the file so edits to provider, model and credentials made outside
    // the game are preserved. Invalid on-disk data is never overwritten.
    public Configuration SaveSettings(string gameRoot, string microphone, int maxRecordingSeconds, int requestTimeoutSeconds)
    {
        string path = ConfigurationPath(gameRoot);
        if (!File.Exists(path)) throw SpeechFaultFactory.Configuration();
        JObject document = ReadConfiguration(path);
        ValidateConfiguration(document);
        document["microphone"] = microphone ?? string.Empty;
        document["maxRecordingSeconds"] = maxRecordingSeconds;
        document["requestTimeoutSeconds"] = requestTimeoutSeconds;
        Configuration candidate = ValidateConfiguration(document);
        try { WriteAtomic(path, document, true); }
        catch (Exception exception) { throw SpeechFaultFactory.ConfigurationWrite(exception); }
        return candidate;
    }

    public int ConfigurationLimit(string property, string bound)
    {
        JToken value = registry.Source["definitions"]?["Configuration"]?["properties"]?[property]?[bound];
        if (value == null || value.Type != JTokenType.Integer) throw SpeechFaultFactory.Configuration();
        return (int)value;
    }

    Configuration ValidateConfiguration(JObject document)
    {
        try
        {
            registry.Validate("Configuration", document);
            var configuration = document.ToObject<Configuration>();
            if (configuration == null) throw new InvalidDataException("Configuration is null.");
            return configuration;
        }
        catch (SpeechFault)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw SpeechFaultFactory.Configuration(exception);
        }
    }

    static string ConfigurationPath(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) throw new ArgumentException("Game root is required.", nameof(gameRoot));
        return Path.Combine(gameRoot, "UserData", "VoiceInput.json");
    }

    static JObject ReadConfiguration(string path)
    {
        try
        {
            var document = JObject.Parse(File.ReadAllText(path, Encoding.UTF8),
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            AddOptionalDefaults(document);
            return document;
        }
        catch (Exception exception) { throw SpeechFaultFactory.Configuration(exception); }
    }

    static void WriteAtomic(string path, JObject document, bool replaceExisting)
    {
        string directory = Path.GetDirectoryName(path);
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".VoiceInput." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] content = new UTF8Encoding(false).GetBytes(document.ToString(Formatting.Indented) + Environment.NewLine);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(true);
            }
            if (replaceExisting) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public ISpeechRecognizer CreateRecognizer(Configuration configuration)
    {
        if (configuration == null) throw SpeechFaultFactory.Configuration();
        try
        {
            registry.Validate("Configuration", JToken.FromObject(configuration));
        }
        catch (Exception exception)
        {
            throw SpeechFaultFactory.Configuration(exception);
        }

        switch (configuration.Provider)
        {
            case Provider.Baidu:
                if (configuration.Baidu == null || string.IsNullOrWhiteSpace(configuration.Baidu.ApiKey) ||
                    string.IsNullOrWhiteSpace(configuration.Baidu.SecretKey))
                    throw SpeechFaultFactory.Credentials();
                return new BaiduRecognizer(
                    configuration.Baidu.ApiKey,
                    configuration.Baidu.SecretKey,
                    configuration.Baidu.DevPid,
                    registry: registry);
            case Provider.Tencent:
                if (configuration.Tencent == null || string.IsNullOrWhiteSpace(configuration.Tencent.SecretId) ||
                    string.IsNullOrWhiteSpace(configuration.Tencent.SecretKey))
                    throw SpeechFaultFactory.Credentials();
                return new TencentRecognizer(
                    configuration.Tencent.SecretId,
                    configuration.Tencent.SecretKey,
                    configuration.Tencent.EngineModel,
                    configuration.Tencent.SessionToken,
                    registry: registry);
            default:
                throw SpeechFaultFactory.Configuration();
        }
    }

    JObject DefaultConfiguration()
    {
        var value = registry.Source["x-defaultConfiguration"] as JObject;
        if (value == null) throw SpeechFaultFactory.Configuration();
        return (JObject)value.DeepClone();
    }

    static void AddOptionalDefaults(JObject document)
    {
        var tencent = document["tencent"] as JObject;
        if (tencent != null && tencent["sessionToken"] == null) tencent["sessionToken"] = string.Empty;
    }

    static string ReadResource(string name)
    {
        var assembly = typeof(VoiceProtocol).Assembly;
        using (var stream = assembly.GetManifestResourceStream(name) ??
            throw new InvalidDataException("Missing embedded contract resource."))
        using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
    }
}

public interface ISpeechRecognizer : IDisposable
{
    System.Threading.Tasks.Task<string> RecognizeAsync(byte[] pcm, System.Threading.CancellationToken token);
}
