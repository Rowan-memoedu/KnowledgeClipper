using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.Operations;
using KnowledgeClipper.Core.V3.Kernel.Application;

internal static class DestinationAdapterTests
{
    public static async Task<int> RunAsync(string root, string remnoteCandidate)
    {
        if (Directory.Exists(root)) throw new InvalidDataException("Evidence root exists.");
        Directory.CreateDirectory(root);
        try
        {
            var manifestPath = Path.Combine(remnoteCandidate, "candidate-manifest.json");
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
            using var manifest = JsonDocument.Parse(manifestBytes);
            var entries = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
            foreach (var item in entries)
            {
                var path = CoreWorkerTaskPackageAdapter.ResolveWithin(remnoteCandidate, item.GetProperty("path").GetString()!);
                var bytes = await File.ReadAllBytesAsync(path);
                if (bytes.LongLength != item.GetProperty("bytes").GetInt64() || !Hash(bytes).Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Candidate dependency mismatch: " + path);
            }
            DestinationTests.Check(entries.Length == 45, "RemNote candidate all 45 manifest dependencies verified");
            var exe = CoreWorkerTaskPackageAdapter.ResolveWithin(remnoteCandidate, manifest.RootElement.GetProperty("entrypoint").GetString()!);
            var exeSha = Hash(await File.ReadAllBytesAsync(exe));
            DestinationTests.Check(exeSha == "E104DD9A0183CC22CA793D075E35335C45FE0A01A56CC9A15098F1C17C724E18", "RemNote exact admitted executable SHA");
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            start.ArgumentList.Add("describe"); start.ArgumentList.Add("--json");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await output; var stderr = await error;
            using var describe = JsonDocument.Parse(stdout);
            DestinationTests.Check(process.ExitCode == 0 && describe.RootElement.GetProperty("workerId").GetString() == "export.remnote" && describe.RootElement.GetProperty("workerVersion").GetString() == "2.3.0", "independent release EXE describe from arbitrary cwd");
            await File.WriteAllTextAsync(Path.Combine(root, "describe.json"), stdout);
            await File.WriteAllTextAsync(Path.Combine(root, "describe.stderr.log"), stderr);
            var adapter = new CoreWorkerTaskPackageAdapter(Path.Combine(root, "packages"));
            var source = new ArtifactReference(KernelSchemas.ArtifactReference, "confirmed-source", "card-bundle", "1.0.0", new("sha256", new string('a', 64)), "test");
            var cards = new[] { new { cardId = "card-1", front = " exact question ", back = " exact answer ", template = "basic_qa" } };
            var settings = new { schemaVersion = "remnote-destination-settings.v1", enabled = true, target = new { rootRemId = "fixture-root", rootTitle = "Fixture" }, advanced = new { sink = "mcp-direct", clipRouting = "clip-daily-route", cardRouting = "card-export", timeZone = "Asia/Shanghai", dateFormat = "yyyy-MM-dd" } };
            var request = StrictJson.CloneElement(new { phase = "export", destination = "remnote", targetIdentity = "fixture-root", originalText = "source", configurationJson = JsonSerializer.Serialize(settings, KernelJson.Options), cards, sourceCardArtifact = source, cardBundleSha256 = source.ManifestDigest.Value,
                idempotencyKey = new string('b', 64), sideEffectAuthorization = new { mode = "explicit-user-action", scope = "confirmed-remnote-root", targetIdentity = "fixture-root" } });
            var worker = new AdmittedAuthorityWorker("export.remnote", "2.3.0", "1.0.0", exe, exeSha, new[] { "export-remnote" }, "external", "reconcilable");
            var invocation = new WorkerInvocation("offline-confirmed-cards", new("worker", "export.remnote", "=2.3.0", null), request, TimeSpan.FromSeconds(10));
            var package = await adapter.PrepareAsync(invocation, worker, default);
            using var taskManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(package.JobRoot, "task.manifest.v1.json")));
            DestinationTests.Check(package.Request.Operation == "remnote.export" && taskManifest.RootElement.GetProperty("executionProfile").GetString() == "cards-remnote" &&
                taskManifest.RootElement.GetProperty("requestedOutputs").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "remnote.write-receipt" }), "formal RemNote operation and task profile do not pretend Anki routing");
            DestinationTests.Check(package.Request.StageId == "export-remnote" && package.Request.InputRefs.Single().Kind == "card.bundle" && package.Request.ConfigSnapshot.EnumerateObject().Count() == 0, "real request adapter consumes card.bundle with no fake Anki configuration");
            DestinationTests.Check(package.Request.Payload.GetProperty("schemaVersion").GetString() == "2.0.0" && package.Request.Payload.GetProperty("route").EnumerateObject().Count() == 1 && package.Request.Payload.GetProperty("route").GetProperty("kind").GetString() == "card-export", "RemNote payload v2 has no date routing");
            var derived = package.Request.InputRefs.Single();
            using var derivedManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(package.JobRoot, derived.ManifestPath)));
            using var projection = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(package.JobRoot, derivedManifest.RootElement.GetProperty("metadata").GetProperty("entrypoint").GetString()!)));
            DestinationTests.Check(projection.RootElement.GetProperty("cards")[0].GetProperty("front").GetString() == cards[0].front && projection.RootElement.GetProperty("cards")[0].GetProperty("back").GetString() == cards[0].back && derivedManifest.RootElement.GetProperty("inputRefs").GetArrayLength() == 1, "destination projection preserves exact displayed text and ancestry");
            var altered = request.Deserialize<Dictionary<string, JsonElement>>(KernelJson.Options)!;
            altered["targetIdentity"] = StrictJson.CloneElement("foreign-root");
            await DestinationTests.Reject(() => adapter.PrepareAsync(invocation with { Request = StrictJson.CloneElement(altered) }, worker, default), "adapter rejects grant target drift before process");
            // Offline catalog fixture only: no product authority/candidate is written or promoted.
            var authorityBase = Path.GetFullPath(Path.Combine(remnoteCandidate, "..", ".."));
            var registryPath = Path.Combine(root, "offline-registry-fixture.json");
            var entrypointRelative = Path.GetRelativePath(remnoteCandidate, exe).Replace('\\', '/');
            var registryBytes = JsonSerializer.SerializeToUtf8Bytes(new { registryVersion = "offline-remnote-admission", workers = new[] { new { workerId = "export.remnote", entrypoint = new { path = entrypointRelative },
                sideEffectLevel = describe.RootElement.GetProperty("sideEffectLevel").GetString(), idempotencyLevel = describe.RootElement.GetProperty("idempotencyLevel").GetString() } } }, KernelJson.Options);
            await File.WriteAllBytesAsync(registryPath, registryBytes);
            var authority = new WorkerAuthorityBinding(Path.GetRelativePath(authorityBase, remnoteCandidate).Replace('\\', '/'), Path.GetRelativePath(authorityBase, registryPath).Replace('\\', '/'), Hash(registryBytes), entrypointRelative, exeSha, "2.3.0", "1.0.0");
            var capability = DestinationTests.Capability("remnote.discover-target") with { EffectClass = "read-only-external", AuthorizationPolicy = "none", WorkerBinding = new WorkerBinding("worker", "export.remnote", "=2.3.0", null) { Authority = authority } };
            var catalog = AdmittedCapabilityCatalog.CreateForTesting(new(KernelSchemas.CapabilityCatalog, "offline-remnote-admission", "1.0.0", new("offline-remnote-admission", "not-a-product-checkpoint", new string('a', 64)), new[] { capability }));
            var fake = new PublicFake(Path.Combine(remnoteCandidate, "evidence", "offline-discovery-result.json"));
            var runtime = await WorkerProcessRuntime.CreateAsync(catalog, authorityBase, Path.Combine(root, "runtime-packages"), default, fake);
            var store = new FileArtifactStore(Path.Combine(root, "artifacts"), "offline-test");
            var events = new FileEventStore(Path.Combine(root, "events"));
            var context = new OperationContext(capability, runtime, store, events);
            var command = DestinationTests.Command("offline-discovery", "remnote.discover-target", "kc.remnote-discovery/1", new { rootTitle = "零碎知识剪藏 Canary" });
            var publicResult = await DestinationTests.Execute(command, new RemNoteSettingsOperation("remnote.discover-target"), context);
            DestinationTests.Check(publicResult.Data.GetProperty("discovery").GetProperty("persistableBinding").GetBoolean() && publicResult.ExternalWriteCount == 0 && fake.Invocations == 1, "production handler through real admission runtime adapter with validating Fake public invoker");
            var binding = publicResult.Data.GetProperty("discovery").GetProperty("binding").Deserialize<RemNoteBindingReference>(KernelJson.Options)!;
            var probe = await DestinationTests.Execute(DestinationTests.Command("offline-probe", "remnote.probe", "kc.remnote-probe/1", new { binding }, new[] { binding.Evidence }), new RemNoteSettingsOperation("remnote.probe"), context);
            DestinationTests.Check(probe.Data.GetProperty("remNoteReadiness").GetProperty("readyForRequestedAction").GetBoolean() && fake.Invocations == 2 && fake.Exports.Invocations == 0,
                "production readiness handler validates durable binding and emits only read-only public request");
            var malformed = StrictJson.CloneElement(new { schemaVersion = "remnote-target-discovery-request.v1", discoveryId = "offline-discovery", rootTitle = "Fixture" });
            await DestinationTests.Reject(() => { PublicFake.Validate(malformed); return Task.CompletedTask; }, "Fake rejects obsolete incomplete discovery fixture");
            await fake.Exports.VerifyAsync(runtime, invocation);
            await File.WriteAllBytesAsync(Path.Combine(root, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(new { status = "PASS", boundary = "adapter-package-and-describe-only", candidateManifestSha256 = Hash(manifestBytes), executableSha256 = exeSha, dependencyFilesVerified = entries.Length,
                request = package.RequestPath, publicRequest = fake.RequestPath, publicRequestSha256 = Hash(await File.ReadAllBytesAsync(fake.RequestPath!)), productionPublicHandler = true, workerRunInvocations = 0, publicCommandInvocations = 0, fakePublicInvocations = fake.Invocations,
                probeRequest = fake.ProbePath, fakeExportInvocations = fake.Exports.Invocations, fakeResultPaths = fake.Exports.ResultPaths, describeInvocations = 2, externalCalls = 0, externalWrites = 0 }, KernelJson.Options));
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed class PublicFake(string source) : IWorkerProcessInvoker
    {
        public int Invocations;
        public string? RequestPath;
        public string? ProbePath;
        public DestinationRuntimeFake Exports = new();
        public static void Validate(JsonElement input)
        {
            var names = input.EnumerateObject().Select(x => x.Name).Order().ToArray();
            if (!names.SequenceEqual(new[] { "authorizationScope", "discoveryId", "operations", "schemaVersion", "selector", "timeoutMs" }) ||
                input.GetProperty("schemaVersion").GetString() != "remnote-target-discovery-request.v1" ||
                input.GetProperty("authorizationScope").GetString() != "configured-destination" || input.GetProperty("timeoutMs").GetInt32() != 15000 ||
                string.IsNullOrWhiteSpace(input.GetProperty("discoveryId").GetString()) ||
                !input.GetProperty("operations").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "status", "search", "list", "read" }))
                throw new InvalidDataException("Discovery public input contract mismatch.");
            var selector = input.GetProperty("selector");
            if (!selector.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(new[] { "expectedType", "match", "rootTitle", "uniqueness" }) ||
                selector.GetProperty("expectedType").GetString() != "document" || selector.GetProperty("match").GetString() != "exact" ||
                selector.GetProperty("uniqueness").GetString() != "required" || string.IsNullOrWhiteSpace(selector.GetProperty("rootTitle").GetString()))
                throw new InvalidDataException("Discovery selector contract mismatch.");
        }
        public Task<WorkerProcessInvocationResult> InvokeAsync(AdmittedAuthorityWorker worker, LegacyAttemptPackage package, TimeSpan timeout, CancellationToken ct)
            => Exports.InvokeAsync(worker, package, timeout, ct);
        public async Task<WorkerProcessInvocationResult> InvokePublicAsync(AdmittedAuthorityWorker worker, PublicWorkerCommandPackage package, TimeSpan timeout, CancellationToken ct)
        {
            if (package.Command is not ("discover-target-public" or "probe-public") || worker.WorkerId != "export.remnote") throw new InvalidDataException("Unexpected public command.");
            using var input = JsonDocument.Parse(await File.ReadAllBytesAsync(package.RequestPath, ct));
            var discover = package.Command == "discover-target-public";
            var value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllBytesAsync(discover ? source : Path.Combine(Path.GetDirectoryName(source)!, "offline-probe-result.json"), ct), KernelJson.Options)!;
            if (discover)
            {
                Validate(input.RootElement); RequestPath = package.RequestPath;
                if (value["selection"].GetProperty("selector").GetProperty("rootTitle").GetString() != input.RootElement.GetProperty("selector").GetProperty("rootTitle").GetString()) throw new InvalidDataException("Fixture selector differs from actual request.");
                value["discoveryId"] = input.RootElement.GetProperty("discoveryId").Clone();
            }
            else
            {
                var body = input.RootElement;
                if (!body.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(new[] { "authorizationScope", "operations", "probeId", "schemaVersion", "target", "timeoutMs" }) ||
                    body.GetProperty("schemaVersion").GetString() != "remnote-public-probe-request.v2" || body.GetProperty("timeoutMs").GetInt32() != 15000 || body.GetProperty("authorizationScope").GetString() != "configured-destination" ||
                    !body.GetProperty("operations").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "status", "search", "list", "read" }) ||
                    body.GetProperty("target").GetProperty("rootRemId").GetString() != value["targetBinding"].GetProperty("rootRemId").GetString() || body.GetProperty("target").GetProperty("rootTitle").GetString() != value["targetBinding"].GetProperty("expectedTitle").GetString())
                    throw new InvalidDataException("Public probe request contract mismatch.");
                ProbePath = package.RequestPath; value["probeId"] = body.GetProperty("probeId").Clone();
            }
            Invocations++;
            await File.WriteAllBytesAsync(package.StandardOutputPath, JsonSerializer.SerializeToUtf8Bytes(value, KernelJson.Options), ct);
            await File.WriteAllTextAsync(package.StandardErrorPath, "", ct);
            return new(0, false, false, package.StandardErrorPath);
        }
    }
}
