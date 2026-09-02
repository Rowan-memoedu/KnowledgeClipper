using System.Diagnostics;

var configuredWorkerRoot = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_CONTENT_WORKER_ROOT");
var workerRoot = ResolveWorkerRoot(configuredWorkerRoot);
var cliPath = Path.Combine(workerRoot, "dist", "src", "cli.js");
if (!File.Exists(cliPath))
{
    Console.Error.WriteLine($"CONTENT_WORKER_MISSING_CLI {cliPath}");
    return 127;
}

var nodePath = Environment.GetEnvironmentVariable("KNOWLEDGE_CLIPPER_NODE_PATH");
// Legacy compatibility alias for already-installed launch configurations.
if (string.IsNullOrWhiteSpace(nodePath))
    nodePath = Environment.GetEnvironmentVariable("RQC_NODE_PATH");
if (string.IsNullOrWhiteSpace(nodePath))
{
    nodePath = FindFromAncestors(AppContext.BaseDirectory, Path.Combine("runtime", "node-v24.18.0-win-x64", "node.exe")) ?? "node";
}

var startInfo = new ProcessStartInfo
{
    FileName = nodePath,
    UseShellExecute = false,
    CreateNoWindow = true,
    WorkingDirectory = workerRoot,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
startInfo.ArgumentList.Add(cliPath);
foreach (var argument in args)
    startInfo.ArgumentList.Add(argument);

try
{
    using var child = Process.Start(startInfo);
    if (child is null)
    {
        Console.Error.WriteLine("CONTENT_WORKER_NODE_START_FAILED");
        return 127;
    }
    var stdoutTask = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
    var stderrTask = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
    await child.WaitForExitAsync();
    await Task.WhenAll(stdoutTask, stderrTask);
    return child.ExitCode;
}
catch (Exception error)
{
    Console.Error.WriteLine($"CONTENT_WORKER_NODE_START_FAILED {error.Message}");
    return 127;
}

static string ResolveWorkerRoot(string? configuredWorkerRoot)
{
    if (!string.IsNullOrWhiteSpace(configuredWorkerRoot))
        return Path.GetFullPath(configuredWorkerRoot);

    var localApp = Path.Combine(AppContext.BaseDirectory, "app");
    if (File.Exists(Path.Combine(localApp, "dist", "src", "cli.js")))
        return localApp;

    var sourceOrReleaseRoot = FindFromAncestors(AppContext.BaseDirectory, Path.Combine("content-worker", "dist", "src", "cli.js"));
    if (sourceOrReleaseRoot is not null)
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceOrReleaseRoot)!, "..", ".."));

    return localApp;
}

static string? FindFromAncestors(string startDirectory, string relativePath)
{
    var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, relativePath);
        if (File.Exists(candidate))
            return candidate;
        directory = directory.Parent;
    }
    return null;
}
