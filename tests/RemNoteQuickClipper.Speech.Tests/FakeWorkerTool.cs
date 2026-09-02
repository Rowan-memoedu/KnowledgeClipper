using System;
using System.IO;
using System.Text;
using System.Threading;

namespace KnowledgeClipper.Speech.Tests
{
    internal static class FakeWorkerTool
    {
        private static int Main(string[] args)
        {
            var invocationPath = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_TOOL_INVOCATION_PATH");
            if (string.IsNullOrWhiteSpace(invocationPath))
                invocationPath = Environment.GetEnvironmentVariable("RQC_FAKE_TOOL_INVOCATION_PATH");
            if (!string.IsNullOrWhiteSpace(invocationPath))
                File.AppendAllText(invocationPath, string.Join(" ", args) + Environment.NewLine, new UTF8Encoding(false));
            if (args.Length == 1 && args[0] == "--list-voices")
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine("Name Gender ContentCategories VoicePersonalities");
                Console.WriteLine("zh-CN-XiaoxiaoNeural Female News,Novel Warm");
                Console.WriteLine("zh-CN-YunxiNeural Male Novel Lively");
                Console.WriteLine("en-US-AvaMultilingualNeural Female General Friendly");
                Console.WriteLine("en-US-GuyNeural Male General Passion");
                Console.WriteLine("es-ES-XimenaNeural Female General Friendly");
                Console.WriteLine("ja-JP-NanamiNeural Female General Friendly");
                Console.WriteLine("ko-KR-SunHiNeural Female General Friendly");
                return 0;
            }
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--write-media")
                {
                    File.WriteAllBytes(args[i + 1], Encoding.UTF8.GetBytes("ID3-fake-cli-audio"));
                    return 0;
                }
            }
            int playbackMilliseconds;
            var playbackDelay = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_FAKE_PLAYBACK_MS");
            if (string.IsNullOrWhiteSpace(playbackDelay)) playbackDelay = Environment.GetEnvironmentVariable("RQC_FAKE_PLAYBACK_MS");
            if (int.TryParse(playbackDelay, out playbackMilliseconds) && playbackMilliseconds > 0)
                Thread.Sleep(playbackMilliseconds);
            return 0;
        }
    }
}
