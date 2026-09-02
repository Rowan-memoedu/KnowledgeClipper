using System.Reflection;
using System.Runtime.Loader;

if (args.Length == 3 && string.Equals(args[0], "single-instance", StringComparison.Ordinal))
{
    var singleInstanceCandidateRoot = Path.GetFullPath(args[1]);
    var evidenceRoot = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(evidenceRoot);
    var singleInstanceApplication = Path.Combine(singleInstanceCandidateRoot, "KnowledgeClipper.dll");
    if (!File.Exists(singleInstanceApplication))
    {
        Console.Error.WriteLine("candidate application DLL missing");
        return 3;
    }

    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        var dependency = Path.Combine(singleInstanceCandidateRoot, name.Name + ".dll");
        return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
    };
    var singleInstanceAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(singleInstanceApplication);
    var coordinator = singleInstanceAssembly.GetType("KnowledgeClipper.SingleInstanceCoordinator", throwOnError: true)!;
    var notify = coordinator.GetMethod("NotifyExistingInstance", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(coordinator.FullName, "NotifyExistingInstance");

    using var collision = new Mutex(true, "Local\\KnowledgeClipper.SingleInstance", out var collisionCreated);
    var notification = notify.Invoke(null, new object?[] { false, evidenceRoot })!;
    var diagnosticPath = Path.Combine(evidenceRoot, "single-instance.log");
    var diagnostic = File.ReadLines(diagnosticPath).Last();
    using var parsed = System.Text.Json.JsonDocument.Parse(diagnostic);
    var root = parsed.RootElement;
    var passed = root.GetProperty("event").GetString() == "existing-instance-detected"
        && root.GetProperty("canonicalMutexDetected").GetBoolean()
        && root.GetProperty("result").GetString() == "not-silent";
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        result = passed ? "PASS" : "FAIL",
        collisionMutexCreatedByHarness = collisionCreated,
        canonicalMutexDetected = root.GetProperty("canonicalMutexDetected").GetBoolean(),
        notificationResultType = notification.GetType().FullName,
        userFeedbackMode = "activate-existing-window-and-message-box",
        diagnosticPath,
        existingUserProcessTerminated = false
    }));
    return passed ? 0 : 5;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: CandidateIpc.QualityHarness <candidate-root> <isolated-jobs-root> | single-instance <candidate-root> <evidence-root>");
    return 2;
}

var candidateRoot = Path.GetFullPath(args[0]);
var jobsRoot = Path.GetFullPath(args[1]);
var application = Path.Combine(candidateRoot, "KnowledgeClipper.dll");
if (!File.Exists(application))
{
    Console.Error.WriteLine("candidate application DLL missing");
    return 3;
}

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var dependency = Path.Combine(candidateRoot, name.Name + ".dll");
    return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(application);
var type = assembly.GetType("KnowledgeClipper.CandidateIntegrationCanary", throwOnError: true)!;
var method = type.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(type.FullName, "Run");
var result = method.Invoke(null, new object[] { candidateRoot, jobsRoot, Console.Out });
return result is int exitCode ? exitCode : 4;
