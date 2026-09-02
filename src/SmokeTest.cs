using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;

namespace RemNoteQuickClipper
{
    internal static class SmokeTest
    {
        private static int Main(string[] args)
        {
            try { Run().GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
        }

        private static async Task Run()
        {
            var config = AppConfig.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"));
            var remnote = new RemNoteClient(config);
            await remnote.CheckStatusAsync();
            Console.WriteLine("PASS RemNote bridge");
            var token = await remnote.GetMineruTokenAsync();
            if (string.IsNullOrWhiteSpace(token) || token.Length < 20) throw new InvalidOperationException("MinerU token invalid");
            Console.WriteLine("PASS MinerU token discovery (secret not printed)");
            var source = "这是需要保留的测试正文。\n\n分享到微信\nCopyright 2026 Example.com";
            var cleaned = await new DeepSeekCleaner(config).CleanAsync(source);
            if (cleaned.IndexOf("测试正文", StringComparison.Ordinal) < 0) throw new InvalidOperationException("DeepSeek removed source text");
            Console.WriteLine("PASS DeepSeek clean chars=" + cleaned.Length);

            var job = JobStore.Create(config);
            string ocrText;
            using (var bitmap = new Bitmap(1200, 280))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = new Font("Arial", 38, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.Black))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("RemNote Quick Clipper Canary 2026-08-24", font, brush, 30, 55);
                graphics.DrawString("OCR formula: E = mc^2", font, brush, 30, 145);
                ocrText = await new MinerUOcr(config, remnote).ExtractAsync(bitmap, job);
            }
            if (ocrText.IndexOf("RemNote", StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException("MinerU OCR missed canary marker");
            var cleanedOcr = await new DeepSeekCleaner(config).CleanAsync(ocrText);
            if (string.IsNullOrWhiteSpace(cleanedOcr)) throw new InvalidOperationException("DeepSeek returned empty OCR clean result");
            Console.WriteLine("PASS MinerU image OCR -> DeepSeek chars=" + cleanedOcr.Length);

            var marker = "RemNote Quick Clipper Canary 2026-08-24";
            var container = await remnote.CreateClipContainerAsync(DateTime.Now);
            await remnote.InsertAsync(container, marker + "\n\n文本 → DeepSeek → 确认窗 → RemNote 写入链路测试。");
            if (!await remnote.ReadContainsAsync(container, marker)) throw new InvalidOperationException("RemNote canary read-back failed");
            Console.WriteLine("PASS RemNote write/read-back container=" + container);
        }
    }
}
