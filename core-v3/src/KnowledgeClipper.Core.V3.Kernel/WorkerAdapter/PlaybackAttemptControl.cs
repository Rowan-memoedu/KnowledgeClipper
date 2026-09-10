using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Application;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

// Process-local liveness plus append-only evidence. Recovered registrations are
// deliberately not live: recovery must never send a signal to a guessed process.
internal sealed class PlaybackAttemptControl(IEventStore events)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Active> _active = new(StringComparer.Ordinal);
    private sealed record Active(string OperationId, string CommandId, string Token,
        LegacyAttemptPackage Package, string RequestSha256, Task Process);

    internal static string Stream(string operationId) => "playback-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId))).ToLowerInvariant();

    public string Register(string operationId, string commandId, LegacyAttemptPackage package, Task process)
    {
        lock (_gate)
        {
            if (package.Request.Operation != "speech.playback" || package.Request.WorkerId != "speech.edge")
                throw new InvalidDataException("playback.operation_mismatch");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var active = new Active(operationId, commandId, token, package, HashFile(package.RequestPath), process);
            Append(active, "playback.registered", "playing", commandId);
            _active.Add(token, active);
            return token;
        }
    }

    public void Complete(string token, string state)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(token, out var active)) return;
            Append(active, "playback.terminal", state, active.CommandId);
            _active.Remove(token);
        }
    }

    public JsonElement Snapshot(string operationId)
    {
        lock (_gate)
        {
            var live = _active.Values.Where(item => item.OperationId == operationId).ToArray();
            var latest = events.ReadStream(Stream(operationId)).LastOrDefault(item => item.EventType is "playback.registered" or "playback.terminal");
            if (live.Length != 1)
                return StrictJson.CloneElement(new { operationId, state = live.Length > 1 ? "ambiguous" : latest?.EventType == "playback.terminal" ? Text(latest.Data, "state") : "unknown", canStop = false,
                    targetToken = latest is null ? null : Text(latest.Data, "targetToken"), actionCommandId = latest is null ? null : Text(latest.Data, "actionCommandId"),
                    attemptId = latest is null ? null : Text(latest.Data, "attemptId"), sequence = latest?.Sequence ?? 0 });
            var active = live[0];
            var stopRequested = File.Exists(active.Package.CancelPath);
            return StrictJson.CloneElement(new { operationId, state = active.Process.IsCompleted ? "completing" : stopRequested ? "stop_requested" : "playing", canStop = !active.Process.IsCompleted && !stopRequested && !File.Exists(active.Package.ResultPath),
                targetToken = active.Token, actionCommandId = active.CommandId, attemptId = active.Package.Request.AttemptId, sequence = events.GetRevision(Stream(operationId)) });
        }
    }

    public JsonElement RequestStop(string operationId, string targetToken, string commandId)
    {
        lock (_gate)
        {
            var matches = _active.Values.Where(item => item.OperationId == operationId && !item.Process.IsCompleted).ToArray();
            if (matches.Length != 1) throw new KernelContractException(matches.Length == 0 ? "playback.no_active_attempt" : "playback.ambiguous_attempt", "No unique live playback attempt.");
            var active = matches[0];
            if (active.Token != targetToken) throw new KernelContractException("playback.stale_target", "Playback target was superseded or belongs to another operation.");
            var package = active.Package;
            if (File.Exists(package.ResultPath) || File.Exists(package.CancelPath)) throw new KernelContractException("playback.already_terminal_or_signalled", "Playback is terminal or already has a cancel signal.");
            if (HashFile(package.RequestPath) != active.RequestSha256) throw new KernelContractException("playback.request_drift", "Immutable worker request changed.");
            using var document = StrictJson.Parse(File.ReadAllBytes(package.RequestPath), package.RequestPath);
            var request = document.RootElement.Deserialize<LegacyWorkerRequest>(KernelJson.Options)!;
            if (request.Operation != "speech.playback" || request.OperationId != package.Request.OperationId ||
                request.AttemptId != package.Request.AttemptId || request.CancelSignal.Path != package.Request.CancelSignal.Path)
                throw new KernelContractException("playback.operation_mismatch", "Worker request playback identity mismatch.");
            // Durable intent precedes the signal. An interruption here is unknown,
            // never a reason to auto-retry against a later playback attempt.
            Append(active, "playback.stop.resolved", "stop_requested", commandId);
            var now = DateTimeOffset.UtcNow;
            var signal = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "1.0.0", attemptId = request.AttemptId,
                operationId = request.OperationId, reason = "user_stop", requestedAt = now,
                graceDeadline = now.AddMilliseconds(request.CancelSignal.GracePeriodMs) }, KernelJson.Options);
            var pending = package.CancelPath + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { file.Write(signal); file.Flush(true); }
                File.Move(pending, package.CancelPath, overwrite: false);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            Append(active, "playback.stop.signalled", "stop_requested", commandId);
            return StrictJson.CloneElement(new { operationId, targetToken, state = "stop_requested", request.AttemptId, request.RunId, request.StageId, workerOperationId = request.OperationId });
        }
    }

    private void Append(Active active, string type, string state, string causation)
    {
        var stream = Stream(active.OperationId);
        var request = active.Package.Request;
        events.Append(stream, events.GetRevision(stream), new[] { new KernelEvent(KernelSchemas.Event, "evt-" + Guid.NewGuid().ToString("N"), stream, 0,
            type, 1, DateTimeOffset.UtcNow, causation, causation, "clip.read-aloud-cards", "playback-control",
            StrictJson.CloneElement(new { operationId = active.OperationId, actionCommandId = active.CommandId, targetToken = active.Token, state,
                request.TaskId, request.RunId, request.StageId, request.AttemptId, workerOperationId = request.OperationId, operation = request.Operation, requestSha256 = active.RequestSha256 }), Array.Empty<ArtifactReference>()) });
    }
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) ? item.GetString() : null;
}
