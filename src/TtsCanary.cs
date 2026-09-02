using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace RemNoteQuickClipper
{
    internal static class TtsCanary
    {
        private static int Main()
        {
            try { Run().GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
        }

        private static async Task Run()
        {
            var config = AppConfig.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"));
            var service = new TtsService(config);
            var samples = new Dictionary<string, string>
            {
                { "zh", "这是中文自然语音测试。" },
                { "en", "This is an English natural voice test." },
                { "es", "Esta es una prueba de voz natural en español." },
                { "ja", "これは日本語の自然な音声テストです。" },
                { "ko", "이것은 한국어 자연스러운 음성 테스트입니다." }
            };
            foreach (var item in samples)
            {
                var job = JobStore.Create(config);
                var output = await service.SynthesizeAsync(item.Value, job);
                if (!File.Exists(output) || new FileInfo(output).Length < 1000) throw new InvalidOperationException(item.Key + " audio invalid");
                if (LanguageRouter.Detect(item.Value) != item.Key) throw new InvalidOperationException(item.Key + " route mismatch");
                Console.WriteLine("PASS TTS " + item.Key + " bytes=" + new FileInfo(output).Length);
            }
        }
    }
}
