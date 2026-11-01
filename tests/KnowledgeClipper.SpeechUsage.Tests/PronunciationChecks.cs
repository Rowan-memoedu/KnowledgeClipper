using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class PronunciationChecks
{
    // Explicit live canary with public inputs. Saves audio without using an audio
    // device, changing application settings, or touching existing user recordings.
    public static async Task RunAsync(string output)
    {
        var directory = Path.GetFullPath(output);
        Directory.CreateDirectory(directory);
        var settings = new LanguageSettings { Voice = "Mia", EnglishVoice = "Mia", SpeechAutoPlay = false };
        var helper = Path.Combine(Environment.CurrentDirectory, "helpers", "stream_speech.py");
        using var speech = new StreamingSpeech(helper, directory);
        var results = new List<object>();
        foreach (var (name, text) in new[] {
            ("english-word", "resilient"),
            ("english-sentence", "A patient gardener noticed a resilient plant beside the old stone wall."),
            ("chinese-word", "严谨"),
            ("chinese-sentence", "她做事一向严谨，从不忽略细节。遇到问题时，她会先仔细观察，再耐心寻找答案。") })
        {
            var draft = new ClipDraft { Id = name + "-" + Guid.NewGuid().ToString("N"), Original = new(text), Settings = settings };
            await speech.SpeakAsync(draft, false, () => { });
            if (draft.SpeechError != null || !draft.Times.ContainsKey("audioComplete") || draft.AudioPath == null)
                throw new InvalidOperationException(name + " failed: " + draft.SpeechError);
            var info = new ProcessStartInfo(@"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffmpeg.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-nostdin", "-v", "error", "-i", draft.AudioPath, "-f", "null", "-" }) info.ArgumentList.Add(argument);
            using var decoder = Process.Start(info)!;
            var error = decoder.StandardError.ReadToEndAsync();
            await decoder.WaitForExitAsync();
            if (decoder.ExitCode != 0) throw new InvalidOperationException(name + " decode failed: " + await error);
            var published = Path.Combine(directory, name + ".mp3");
            File.Copy(draft.AudioPath, published, overwrite: false);
            results.Add(new { name, text, style = StreamingSpeech.SpeechStyle(text, settings), path = published,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(published))), decoded = true, settings.Voice });
            Console.WriteLine("PASS live synthesis and decode: " + name);
        }
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new {
            passed = true, scope = "Real Clipper C# to Python to MiMo synthesis and MP3 decode; subjective pronunciation requires listening.", results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
