using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace KnowledgeClipper.Speech.Worker
{
    internal interface ISpeechSynthesizer
    {
        Task<SynthesisProcessResult> SynthesizeAsync(string textPath, string outputPath, SpeechRoute route, VoiceSnapshot voiceSnapshot);
    }

    internal sealed class SynthesisProcessResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
    }

    internal sealed class EdgeTtsSynthesizer : ISpeechSynthesizer
    {
        private readonly string executablePath;
        private readonly int timeoutMilliseconds;

        public EdgeTtsSynthesizer(string executablePath, int timeoutMilliseconds)
        {
            this.executablePath = executablePath;
            this.timeoutMilliseconds = timeoutMilliseconds;
        }

        public Task<SynthesisProcessResult> SynthesizeAsync(string textPath, string outputPath, SpeechRoute route, VoiceSnapshot snapshot)
        {
            return Task.Run(delegate
            {
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) throw new FileNotFoundException("找不到 Edge TTS 运行时", executablePath);
                var arguments = string.Join(" ", new[]
                {
                    "--file", Quote(textPath),
                    "--voice", Quote(route.Voice),
                    "--rate", Quote(Signed(snapshot.ratePercent, "%")),
                    "--volume", Quote(Signed(snapshot.volumePercent, "%")),
                    "--pitch", Quote(Signed(snapshot.pitchHz, "Hz")),
                    "--write-media", Quote(outputPath)
                });
                var start = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(outputPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var process = Process.Start(start))
                {
                    if (process == null) throw new InvalidOperationException("无法启动 Edge TTS");
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("Edge TTS 合成超时");
                    }
                    Task.WaitAll(stdoutTask, stderrTask);
                    return new SynthesisProcessResult { ExitCode = process.ExitCode, StandardOutput = stdoutTask.Result, StandardError = stderrTask.Result };
                }
            });
        }

        internal static string Signed(int value, string suffix)
        {
            return (value >= 0 ? "+" : "") + value.ToString(CultureInfo.InvariantCulture) + suffix;
        }

        internal static string Quote(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
