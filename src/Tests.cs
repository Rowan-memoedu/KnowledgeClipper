using System;
using System.Collections.Generic;
using System.IO;

namespace RemNoteQuickClipper
{
    internal static class Tests
    {
        private static int failures;
        private static void Equal(string name, string expected, string actual)
        {
            if (expected == actual) Console.WriteLine("PASS " + name);
            else { failures++; Console.WriteLine("FAIL " + name + " expected=[" + expected + "] actual=[" + actual + "]"); }
        }

        private static void Main()
        {
            Equal("quote plain", "abc", ProcessRunner.QuoteArgument("abc"));
            Equal("quote spaces", "\"a b\"", ProcessRunner.QuoteArgument("a b"));
            var token = new string('A', 64);
            var read = new Dictionary<string, object>
            {
                { "contentStructured", new Dictionary<string, object> { { "title", "mineru >> " + token } } }
            };
            Equal("mineru token", token, RemNoteClient.ExtractMineruToken(read));
            Equal("valid clean", null, DeepSeekCleaner.Validate("正文\n版权尾注", "正文"));
            Equal("reject empty", "cleaned_text 为空", DeepSeekCleaner.Validate("正文", ""));
            Equal("reject added", "输出比原文明显更长，疑似添加了内容", DeepSeekCleaner.Validate("x", new string('y', 400)));
            Equal("reject math damage", "公式定界符数量改变", DeepSeekCleaner.Validate("$$x$$", "x"));
            Equal("native INPUT size", IntPtr.Size == 8 ? "40" : "28", NativeKeyboard.InputSize.ToString());
            Equal("route Chinese", "zh", LanguageRouter.Detect("这是中文测试。"));
            Equal("route English", "en", LanguageRouter.Detect("This is an English test."));
            Equal("route Japanese", "ja", LanguageRouter.Detect("これはテストです。"));
            Equal("route Korean", "ko", LanguageRouter.Detect("이것은 테스트입니다."));
            Equal("route Spanish", "es", LanguageRouter.Detect("¿Cómo estás? Esta es una prueba."));
            var settings = UserSettings.Defaults();
            Equal("default edge Chinese voice", "zh-CN-XiaoxiaoNeural", settings.Voice("zh"));
            Equal("default edge English voice", "en-US-AvaMultilingualNeural", settings.Voice("en"));
            Equal("edge positive rate", "+15%", TtsService.Signed(15, "%"));
            Equal("edge negative pitch", "-8Hz", TtsService.Signed(-8, "Hz"));
            var settingsTest = Path.Combine("D:\\RemNoteQuickClipperData", "settings-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(settingsTest);
            try
            {
                var config = new AppConfig { WorkDirectory = settingsTest };
                settings.RatePercent = 15; settings.Save(config); settings.RatePercent = 22; settings.Save(config);
                var loaded = UserSettings.Load(config);
                Equal("settings overwrite readback", "22", loaded.RatePercent.ToString());
                Equal("settings voice readback", settings.ChineseVoice, loaded.ChineseVoice);
            }
            finally { Directory.Delete(settingsTest, true); }
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }
    }
}
