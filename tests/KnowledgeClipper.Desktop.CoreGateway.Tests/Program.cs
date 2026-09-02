using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper.Desktop.CoreGateway.Tests
{
    internal static class Program
    {
        private static int assertions;

        private static async Task<int> Main()
        {
            try
            {
                await FoundationUnavailableIsProjectedWithoutLegacyFallback();
                await FakeDesktopJourneyUsesOneGatewayAndNormalizedState();
                GatewaySourceUsesOnlyPublicDomainFacade();
                ImmutableSdkBindingIsClosed();
                Console.WriteLine("DESKTOP_CORE_GATEWAY_TESTS PASS assertions=" + assertions);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("DESKTOP_CORE_GATEWAY_TESTS FAIL " + exception);
                return 1;
            }
        }

        private static async Task FoundationUnavailableIsProjectedWithoutLegacyFallback()
        {
            var sdk = new FakeSdkDomainFacade { QuestionFailure = "capability_unavailable", AnkiFailure = "capability_unavailable" };
            var gateway = new NamedPipeCoreGateway(sdk);
            var local = new LocalPorts();
            var root = new DesktopCompositionRoot(gateway, gateway, gateway, gateway, gateway, local, local, local, new QuestionObservationPolicy(1, TimeSpan.Zero, TimeSpan.Zero));

            var question = await root.QuestionUseCases.SubmitInitialAsync("foundation unavailable", CancellationToken.None);
            Assert(!question.Accepted && root.State.Snapshot().Question.Status == OperationStatus.Failed && root.State.Snapshot().Question.NoticeCode == "capability_unavailable", "typed unavailable question becomes a normalized failed notice without legacy fallback");

            var configuration = await gateway.PublishAsync("anki-configuration", "{}", CancellationToken.None);
            var anki = await root.ModuleActionUseCases.ExecuteAsync(ModuleActionKind.AnkiProbe, new Dictionary<string, object> { ["configuration"] = configuration }, CancellationToken.None);
            Assert(!anki.Accepted && anki.NoticeCode == "capability_unavailable", "typed unavailable Anki probe is not advertised as available");
            Assert(sdk.QuestionInitialCount == 1 && sdk.AnkiProbeAttemptCount == 1, "foundation unavailable makes one typed attempt and invokes no fallback path");
        }

        private static async Task FakeDesktopJourneyUsesOneGatewayAndNormalizedState()
        {
            var sdk = new FakeSdkDomainFacade();
            var gateway = new NamedPipeCoreGateway(sdk);
            var local = new LocalPorts();
            var root = new DesktopCompositionRoot(gateway, gateway, gateway, gateway, gateway, local, local, local, new QuestionObservationPolicy(2, TimeSpan.Zero, TimeSpan.Zero));

            await root.Commands.InvokeAsync(InvocationSource.Keyboard, new ApplicationCommand(ApplicationCommandKind.QuestionInitial, "初问"), CancellationToken.None);
            Assert(root.State.Snapshot().Question.Status == OperationStatus.Succeeded && root.State.Snapshot().Question.AnswerText == "初问答案", "initial question publishes reads and projects answer through the single typed gateway");

            await root.Commands.InvokeAsync(InvocationSource.UiAutomation, new ApplicationCommand(ApplicationCommandKind.QuestionFollowUp, "追问"), CancellationToken.None);
            Assert(root.State.Snapshot().Question.AnswerText == "追问答案" && sdk.FollowUpCount == 1, "follow-up from UI Automation replaces answer through Application state only");

            await root.Commands.InvokeAsync(InvocationSource.Pointer, new ApplicationCommand(ApplicationCommandKind.QuestionMakeCards, CardStructureId: "basic"), CancellationToken.None);
            Assert(sdk.MakeCardsCount == 1 && sdk.LastMakeCardsAnswerToken == root.State.Snapshot().Question.Answer.Id, "make-cards uses the opaque answer handle selected by Application state");

            var configuration = await gateway.PublishAsync("anki-configuration", "anki-config", CancellationToken.None);
            var anki = await root.ModuleActionUseCases.ExecuteAsync(ModuleActionKind.AnkiProbe, new Dictionary<string, object> { ["configuration"] = configuration }, CancellationToken.None);
            var readiness = root.State.Snapshot().ModuleActions[ModuleActionKind.AnkiProbe].AnkiReadiness;
            Assert(anki.Accepted && sdk.AnkiProbeCount == 1 && readiness.CurrentTargetReady && readiness.OptionalRoutesUninitialized, "Anki health consumes typed current-target readiness without treating optional uninitialized routes as failure");

            var remnote = await root.ModuleActionUseCases.ExecuteAsync(ModuleActionKind.RemNoteDiscoverTarget, null, CancellationToken.None);
            var stage = await gateway.RequestPlaybackStopAsync("task", CancellationToken.None);
            Assert(!remnote.Accepted && remnote.NoticeCode == "core-v3.typed-facade-unavailable" && !stage.Accepted && stage.NoticeCode == "core-v3.stage-control-unavailable", "facades absent from the packet remain explicit unavailable instead of using a side channel");
        }

        private static void GatewaySourceUsesOnlyPublicDomainFacade()
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(root, "src", "KnowledgeClipper.Desktop.Adapters", "NamedPipeCoreGateway.cs");
            var source = File.ReadAllText(path);
            var forbidden = new[]
            {
                "KnowledgeClipper.Core.V3.Contracts", "CoreV3ProtocolClient", "CommandEnvelope", "QueryEnvelope",
                "WireRequestEnvelope", "KernelSchemas", "protocolVersion", "schemaVersion", "idempotencyKey",
                "authorityReleaseRoot", "artifactPath", "NamedPipeCommonIpcClient", "DesktopAnkiProbeHost",
                "DesktopContentSettingsHost", "FileQuestionAnswerArtifactReader"
            };
            Assert(forbidden.All(value => source.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0), "gateway source references only the public typed SDK facade and normalized Desktop contracts");
            var forbiddenHeuristics = new[] { ".Single(", "Outputs[", "GetProperty(", "GetType(", "JsonDocument", "connection-status.json" };
            Assert(forbiddenHeuristics.All(value => source.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0), "gateway uses named typed projections with no array ordering reflection raw worker JSON or UI heuristic");
            Assert(Directory.GetFiles(Path.GetDirectoryName(path), "*NamedPipeCoreGateway*.cs").Length == 1, "Desktop contains exactly one NamedPipeCoreGateway implementation");
        }

        private static void ImmutableSdkBindingIsClosed()
        {
            var root = FindRepositoryRoot();
            var release = Path.Combine(root, "core-v3", "releases", "20260902T060054Z-foundation.2");
            var packet = Path.Combine(release, "typed-sdk-packet.json");
            var clientPath = Path.Combine(release, "artifacts", "net8.0", "KnowledgeClipper.Core.V3.Client.dll");
            var contractsPath = Path.Combine(release, "artifacts", "net8.0", "KnowledgeClipper.Core.V3.Contracts.dll");
            Assert(Hash(packet) == "88076433af2e4bc2c2a3fe3a5f96e22cd1461a71daa7a58b0081d7e914a6a425" &&
                   Hash(clientPath) == "7c191017324f6464d0a8328cf5506f1a6960bc0148b25fac4f5bd1d3b20f5d8b" &&
                   Hash(contractsPath) == "831dbe54e8202a513f94e8eccb419d8eb5847e587f981db30de585fa74e78dfc",
                "Gateway binds the exact immutable foundation.2 packet and SDK artifacts");

            var contracts = Assembly.LoadFrom(contractsPath);
            Assert(contracts.GetExportedTypes().Length == 0, "runtime Contracts dependency exports zero public protocol types");
            var client = Assembly.LoadFrom(clientPath);
            var forbidden = new[] { "CommandEnvelope", "QueryEnvelope", "ArtifactReference", "KernelSchemas", "CoreV3ProtocolClient" };
            Assert(client.GetExportedTypes().All(type => !forbidden.Contains(type.Name, StringComparer.Ordinal)), "Client public surface excludes every low-level protocol DTO");

            using (var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(release, "snapshot", "contracts", "core", "v3", "capabilities", "capability-catalog.v1.json"))))
            {
                var unavailable = catalog.RootElement.GetProperty("operations").EnumerateArray()
                    .Where(item => !item.GetProperty("available").GetBoolean())
                    .Select(item => item.GetProperty("operationId").GetString())
                    .ToArray();
                Assert(new[] { "anki.probe", "question.initial", "question.follow-up", "question.make-cards" }.All(unavailable.Contains), "immutable foundation catalog keeps Anki and all question actions typed unavailable");
            }

            var projects = new[]
            {
                Path.Combine(root, "desktop", "RemNoteQuickClipper.Desktop.csproj"),
                Path.Combine(root, "desktop", "RemNoteQuickClipper.Desktop.WindowsCanary.csproj"),
                Path.Combine(root, "tests", "KnowledgeClipper.Desktop.CoreGateway.Tests", "KnowledgeClipper.Desktop.CoreGateway.Tests.csproj")
            };
            var projectText = string.Join("\n", projects.Select(File.ReadAllText));
            Assert(projectText.IndexOf("core-v3\\src", StringComparison.OrdinalIgnoreCase) < 0 &&
                   projectText.IndexOf("core-v3\\releases\\20260902T060054Z-foundation.2", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   projectText.IndexOf("<ProjectReference", StringComparison.OrdinalIgnoreCase) < 0,
                "Desktop projects reference only the immutable SDK root and no mutable Core source or project");
        }

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src", "KnowledgeClipper.Desktop.Adapters"))) return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("repository-root-not-found");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
            assertions++;
            Console.WriteLine("PASS " + message);
        }

        private sealed class LocalPorts : ISettingsRepository, ICredentialStore, IWindowActivationPort
        {
            public Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new SettingsSnapshot("default", "revision", new Dictionary<string, object>()));
            public Task<SettingsSnapshot> SaveAsync(SettingsSnapshot settings, CancellationToken cancellationToken) => Task.FromResult(settings);
            public Task<CredentialMetadata> DescribeAsync(string credentialId, CancellationToken cancellationToken) => Task.FromResult(new CredentialMetadata(credentialId, false, false, "CurrentUser"));
            public Task StoreAsync(string credentialId, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<WindowActivationSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new WindowActivationSnapshot(1, "runtime", "selection", 1));
            public Task<bool> RestoreAsync(long nativeWindowId, CancellationToken cancellationToken) => Task.FromResult(true);
        }

        private sealed class FakeSdkDomainFacade : ISdkDomainFacade
        {
            private readonly Dictionary<string, byte[]> content = new(StringComparer.Ordinal);
            private readonly Dictionary<string, SdkQuestionProjection> projections = new(StringComparer.Ordinal);
            private int artifactSequence;
            internal string QuestionFailure;
            internal string AnkiFailure;
            internal int QuestionInitialCount;
            internal int AnkiProbeAttemptCount;
            internal int FollowUpCount;
            internal int MakeCardsCount;
            internal int AnkiProbeCount;
            internal string LastMakeCardsAnswerToken;

            public Task<SdkArtifact> PublishTextAsync(SdkArtifactPurpose purpose, string text, CancellationToken cancellationToken)
            {
                var artifact = NewArtifact(text);
                return Task.FromResult(artifact);
            }

            public Task<byte[]> ReadArtifactAsync(SdkArtifact artifact, CancellationToken cancellationToken) => Task.FromResult(content[artifact.Token]);

            public Task<SdkSubmission> AnkiProbeAsync(SdkArtifact configuration, CancellationToken cancellationToken)
            {
                AnkiProbeAttemptCount++;
                if (AnkiFailure != null) throw new SdkDomainException(AnkiFailure, new InvalidOperationException(AnkiFailure));
                AnkiProbeCount++;
                return Task.FromResult(new SdkSubmission("anki-probe-operation", null));
            }

            public Task<SdkAnkiReadiness> ObserveAnkiReadinessAsync(string operationId, CancellationToken cancellationToken)
            {
                var evidence = NewArtifact("typed readiness evidence");
                return Task.FromResult(new SdkAnkiReadiness(OperationStatus.Succeeded, "current-target-ready", true, true, "default", new[] { "optional-writing" }, evidence));
            }

            public Task<SdkSubmission> QuestionInitialAsync(SdkArtifact question, IReadOnlyList<SdkArtifact> context, CancellationToken cancellationToken)
            {
                QuestionInitialCount++;
                if (QuestionFailure != null) throw new SdkDomainException(QuestionFailure, new InvalidOperationException(QuestionFailure));
                var answer = NewArtifact("初问答案");
                projections["question-operation-initial"] = new SdkQuestionProjection(OperationStatus.Succeeded, "answer-ready", 1, answer, null, null);
                return Task.FromResult(new SdkSubmission("question-operation-initial", "question-session"));
            }

            public Task<SdkSubmission> QuestionFollowUpAsync(string sessionId, SdkArtifact followUp, CancellationToken cancellationToken)
            {
                FollowUpCount++;
                var answer = NewArtifact("追问答案");
                projections["question-operation-follow-up"] = new SdkQuestionProjection(OperationStatus.Succeeded, "answer-ready", 2, answer, null, null);
                return Task.FromResult(new SdkSubmission("question-operation-follow-up", sessionId));
            }

            public Task<SdkSubmission> QuestionMakeCardsAsync(string sessionId, SdkArtifact answer, CancellationToken cancellationToken)
            {
                MakeCardsCount++;
                LastMakeCardsAnswerToken = answer.Token;
                var cards = NewArtifact("typed card bundle");
                var evidence = NewArtifact("typed destination evidence");
                projections["question-operation-make-cards"] = new SdkQuestionProjection(OperationStatus.Succeeded, "cards-ready", 2, answer, cards, evidence);
                return Task.FromResult(new SdkSubmission("question-operation-make-cards", sessionId));
            }

            public Task<SdkQuestionProjection> GetQuestionAsync(string taskId, CancellationToken cancellationToken) => Task.FromResult(projections[taskId]);
            public Task<SdkProjection> GetTaskAsync(string taskId, CancellationToken cancellationToken)
            {
                var projection = projections[taskId];
                return Task.FromResult(new SdkProjection(projection.Status, projection.NoticeCode));
            }

            private SdkArtifact NewArtifact(string text)
            {
                var token = "artifact-" + (++artifactSequence).ToString("D4");
                content[token] = Encoding.UTF8.GetBytes(text);
                return new SdkArtifact(token, token);
            }
        }
    }
}
