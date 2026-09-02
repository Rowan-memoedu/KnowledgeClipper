using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemNoteQuickClipper
{
    internal static class EdgeTtsWindowsCanary
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2) { Console.Error.WriteLine("Usage: EdgeTtsWindowsCanary <appsettings> <canary-root>"); return 2; }
            try
            {
                var root = Path.GetFullPath(args[1]);
                if (Directory.Exists(root)) throw new IOException("Immutable Canary root already exists: " + root);
                Directory.CreateDirectory(root);
                var config = AppConfig.Load(Path.GetFullPath(args[0])); config.WorkDirectory = root;
                var settings = UserSettings.Defaults(); settings.LanguageOverride = "en-US"; settings.EnglishVoice = "en-US-AvaMultilingualNeural"; settings.RatePercent = 0; settings.Save(config);
                var loaded = UserSettings.Load(config);
                if (loaded.LanguageOverride != "en-US" || loaded.EnglishVoice != "en-US-AvaMultilingualNeural") throw new InvalidOperationException("Edge TTS override settings did not persist.");
                var job = JobStore.Create(config);
                var audioPath = new TtsService(config).SynthesizeAsync("This is the RemNote Quick Clipper playback canary.", job, loaded).GetAwaiter().GetResult();
                if (!File.Exists(audioPath) || new FileInfo(audioPath).Length < 1000) throw new InvalidOperationException("Edge TTS output is invalid.");
                using (var audio = new ProcessAudio(config.FfplayPath))
                {
                    var ended = new ManualResetEvent(false); audio.PlaybackEnded += delegate { ended.Set(); };
                    audio.Play(audioPath); if (!ended.WaitOne(30000)) throw new InvalidOperationException("First playback did not complete naturally.");
                    ended.Reset(); audio.Play(audioPath); if (!ended.WaitOne(30000)) throw new InvalidOperationException("Second playback did not complete naturally.");
                }
                var receipt = new Dictionary<string, object>
                {
                    { "schemaVersion", "edge-tts-windows-canary.v1" }, { "createdAt", DateTime.UtcNow.ToString("o") },
                    { "result", "PASS" }, { "languageOverride", loaded.LanguageOverride }, { "voice", loaded.EnglishVoice },
                    { "audioPath", audioPath }, { "audioBytes", new FileInfo(audioPath).Length },
                    { "naturalCompletionCount", 2 }, { "legacySpeechKitRuntimeEnabled", false }
                };
                File.WriteAllText(Path.Combine(root, "receipt.json"), Json.New().Serialize(receipt), new System.Text.UTF8Encoding(false));
                Console.WriteLine("EDGE_TTS_WINDOWS_CANARY PASS audioBytes=" + new FileInfo(audioPath).Length + " naturalCompletions=2");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("EDGE_TTS_WINDOWS_CANARY FAIL " + ex); return 1; }
        }
    }
}
