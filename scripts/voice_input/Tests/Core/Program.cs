using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using VoiceInput.Contracts;
using VoiceInput.Core;

namespace VoiceInput.Core.Tests;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "--live-config", StringComparison.OrdinalIgnoreCase))
                return RunLive(args);
            CoreTests.RunAll();
            Console.WriteLine("VoiceInput core tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    static int RunLive(string[] args)
    {
        if (args.Length < 4 || !string.Equals(args[2], "--pcm", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Usage: --live-config <gameRoot> --pcm <pcmFile> [--live-provider baidu|tencent]");
            return 2;
        }
        string gameRoot = args[1];
        string pcmPath = args[3];
        var protocol = new VoiceProtocol();
        Configuration configuration = protocol.LoadConfiguration(gameRoot);
        string expectedText = null;
        for (int n = 4; n + 1 < args.Length; n += 2)
        {
            if (string.Equals(args[n], "--expect-contains", StringComparison.OrdinalIgnoreCase)) { expectedText = args[n + 1]; continue; }
            if (!string.Equals(args[n], "--live-provider", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(args[n + 1], "baidu", StringComparison.OrdinalIgnoreCase)) configuration.Provider = Provider.Baidu;
            else if (string.Equals(args[n + 1], "tencent", StringComparison.OrdinalIgnoreCase)) configuration.Provider = Provider.Tencent;
            else
            {
                Console.Error.WriteLine("--live-provider must be baidu or tencent.");
                return 2;
            }
        }
        string provider = configuration.Provider.ToString();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using (ISpeechRecognizer recognizer = protocol.CreateRecognizer(configuration))
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(20, configuration.RequestTimeoutSeconds))))
            {
                string text = recognizer.RecognizeAsync(File.ReadAllBytes(pcmPath), cancellation.Token).GetAwaiter().GetResult();
                stopwatch.Stop();
                // The live fixture deliberately reports no transcript or credentials.
                bool expectedMatched = expectedText == null || text.IndexOf(expectedText, StringComparison.OrdinalIgnoreCase) >= 0;
                Console.WriteLine("provider=" + provider + " success=true expectedMatched=" + expectedMatched + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                return string.IsNullOrWhiteSpace(text) || !expectedMatched ? 1 : 0;
            }
        }
        catch (SpeechFault fault)
        {
            stopwatch.Stop();
            Console.WriteLine("provider=" + provider + " success=false elapsedMs=" + stopwatch.ElapsedMilliseconds +
                " category=" + fault.Category + " requestId=" + fault.RequestId + " retryable=" + fault.Retryable);
            return 1;
        }
    }
}
