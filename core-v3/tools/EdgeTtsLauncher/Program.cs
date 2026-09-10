using System;
using System.Diagnostics;
using System.IO;

var root = AppContext.BaseDirectory;
var pythonRoot = Path.Combine(root, "python");
var python = Path.Combine(pythonRoot, "python.exe");
var sitePackages = Path.Combine(root, "site-packages");
if (!File.Exists(python) || !Directory.Exists(sitePackages))
{
    Console.Error.WriteLine("EDGE_TTS_RUNTIME_INCOMPLETE: candidate-local Python or site-packages is missing.");
    return 78;
}

using var process = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = python,
        UseShellExecute = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
        CreateNoWindow = true,
        WorkingDirectory = root
    }
};
process.StartInfo.Environment["PYTHONHOME"] = pythonRoot;
process.StartInfo.Environment["PYTHONPATH"] = sitePackages;
process.StartInfo.ArgumentList.Add("-m");
process.StartInfo.ArgumentList.Add("edge_tts");
foreach (var argument in args) process.StartInfo.ArgumentList.Add(argument);
process.Start();
await process.WaitForExitAsync();
return process.ExitCode;
