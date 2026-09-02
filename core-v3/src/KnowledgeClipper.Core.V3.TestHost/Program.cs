using KnowledgeClipper.Core.V3.Kernel;

var arguments = ParseArguments(args);
var repositoryRoot = GetRequired(arguments, "repository-root");
var bindingPath = GetRequired(arguments, "binding");
var dataRoot = GetRequired(arguments, "data-root");
var pipeName = GetRequired(arguments, "pipe-name");
var delay = arguments.TryGetValue("fake-worker-delay-ms", out var delayText) && int.TryParse(delayText, out var milliseconds) && milliseconds >= 0
    ? TimeSpan.FromMilliseconds(milliseconds)
    : TimeSpan.Zero;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var server = await KernelComposition.CreateNamedPipeServerAsync(
    repositoryRoot, bindingPath, dataRoot, pipeName, cancellation.Token,
    WorkerRuntimeProfile.AuthorityBoundFakeTestOnly, delay);
Console.WriteLine($"CORE_V3_READY {pipeName}");
try { await server.ServeAsync(cancellation.Token); }
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

static Dictionary<string, string> ParseArguments(string[] values)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Arguments must be supplied as --name value pairs.");
        parsed.Add(values[index][2..], values[index + 1]);
    }
    return parsed;
}

static string GetRequired(IReadOnlyDictionary<string, string> arguments, string name)
    => arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing required --{name} argument.");
