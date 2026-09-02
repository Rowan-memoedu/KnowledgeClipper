using System.Diagnostics;

var runtimeRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
if (runtimeRoot is null)
{
    Console.Error.WriteLine("EDGE_TTS_RUNTIME_ROOT_MISSING");
    return 127;
}

var pythonPath = Path.Combine(runtimeRoot.FullName, "python-edge-tts-3.14.6", "python.exe");
if (!File.Exists(pythonPath))
{
    Console.Error.WriteLine($"EDGE_TTS_PYTHON_MISSING {pythonPath}");
    return 127;
}

var startInfo = new ProcessStartInfo
{
    FileName = pythonPath,
    UseShellExecute = false,
    CreateNoWindow = true,
    WorkingDirectory = runtimeRoot.FullName,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};
startInfo.ArgumentList.Add("-B");
startInfo.ArgumentList.Add("-m");
startInfo.ArgumentList.Add("edge_tts");
foreach (var argument in args)
    startInfo.ArgumentList.Add(argument);

try
{
    using var child = Process.Start(startInfo);
    if (child is null)
        return 127;
    var stdoutTask = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
    var stderrTask = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
    await child.WaitForExitAsync();
    await Task.WhenAll(stdoutTask, stderrTask);
    return child.ExitCode;
}
catch (Exception error)
{
    Console.Error.WriteLine($"EDGE_TTS_START_FAILED {error.Message}");
    return 127;
}
