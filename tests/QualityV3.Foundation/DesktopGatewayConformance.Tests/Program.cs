using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;

namespace QualityV3.DesktopGatewayConformance.Tests
{
    internal static class Program
    {
        private static int assertions;

        private static async Task<int> Main()
        {
            try
            {
                await HappyPathUsesOpaqueArtifacts();
                await UnavailableCapabilitiesFailClosed();
                Console.WriteLine("QUALITY_DESKTOP_GATEWAY_CONFORMANCE PASS assertions=" + assertions);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("QUALITY_DESKTOP_GATEWAY_CONFORMANCE FAIL " + exception);
                return 1;
            }
        }

        private static async Task HappyPathUsesOpaqueArtifacts()
        {
            var sdk = new FakeFacade();
            var gateway = new NamedPipeCoreGateway(sdk);

            var initial = await gateway.SubmitQuestionAsync(new QuestionSubmission("first", false, null), CancellationToken.None);
            Assert(initial.Accepted && initial.TaskId == "question-session", "initial submission crosses the typed facade");
            Assert(sdk.InitialAttempts == 1, "initial submission makes exactly one SDK attempt");

            var firstProjection = await gateway.GetQuestionAsync(initial.TaskId, CancellationToken.None);
            Assert(firstProjection.Status == OperationStatus.Succeeded && firstProjection.AnswerAvailable, "initial answer is projected as an opaque artifact");
            var firstText = await gateway.ReadAsync<QuestionAnswerContent>(firstProjection.Answer, CancellationToken.None);
            Assert(firstText.Text == "first-answer", "initial answer content is read only through the artifact gateway");

            var followUp = await gateway.SubmitQuestionAsync(new QuestionSubmission("follow", true, initial.TaskId), CancellationToken.None);
            Assert(followUp.Accepted && sdk.FollowUpAttempts == 1, "follow-up reuses the typed question session");
            var secondProjection = await gateway.GetQuestionAsync(initial.TaskId, CancellationToken.None);
            var secondText = await gateway.ReadAsync<QuestionAnswerContent>(secondProjection.Answer, CancellationToken.None);
            Assert(secondText.Text == "follow-answer", "follow-up replaces the projected answer artifact");

            var makeCards = await gateway.MakeCardsAsync(new MakeCardsSubmission(initial.TaskId, "basic", secondProjection.Answer), CancellationToken.None);
            Assert(makeCards.Accepted && sdk.MakeCardsAttempts == 1, "make-cards crosses the typed facade once");
            Assert(sdk.MakeCardsAnswerToken == secondProjection.Answer.Id, "make-cards receives the selected opaque answer handle");

            var configuration = await gateway.PublishAsync("question-context", "anki-config", CancellationToken.None);
            var anki = await gateway.ExecuteAsync(new ModuleActionRequest(ModuleActionKind.AnkiProbe, new Dictionary<string, object> { ["configuration"] = configuration }), CancellationToken.None);
            Assert(anki.Accepted && sdk.AnkiAttempts == 1, "Anki probe crosses the typed facade once");

            var unsupported = await gateway.ExecuteAsync(new ModuleActionRequest(ModuleActionKind.RemNoteProbe, new Dictionary<string, object>()), CancellationToken.None);
            Assert(!unsupported.Accepted && unsupported.NoticeCode == "core-v3.typed-facade-unavailable", "absent module facade fails closed");
            var stage = await gateway.RequestPlaybackStopAsync(initial.TaskId, CancellationToken.None);
            Assert(!stage.Accepted && stage.NoticeCode == "core-v3.stage-control-unavailable", "absent stage facade fails closed");
        }

        private static async Task UnavailableCapabilitiesFailClosed()
        {
            var sdk = new FakeFacade { QuestionFailure = "capability_unavailable", AnkiFailure = "capability_unavailable" };
            var gateway = new NamedPipeCoreGateway(sdk);
            var question = await gateway.SubmitQuestionAsync(new QuestionSubmission("blocked", false, null), CancellationToken.None);
            Assert(!question.Accepted && question.NoticeCode == "capability_unavailable" && sdk.InitialAttempts == 1, "unavailable question makes one typed attempt and fails closed");
            var configuration = await gateway.PublishAsync("question-context", "anki-config", CancellationToken.None);
            var anki = await gateway.ExecuteAsync(new ModuleActionRequest(ModuleActionKind.AnkiProbe, new Dictionary<string, object> { ["configuration"] = configuration }), CancellationToken.None);
            Assert(!anki.Accepted && anki.NoticeCode == "capability_unavailable" && sdk.AnkiAttempts == 1, "unavailable Anki makes one typed attempt and fails closed");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
            assertions++;
            Console.WriteLine("PASS " + message);
        }

        private sealed class FakeFacade : ISdkDomainFacade
        {
            private readonly Dictionary<string, byte[]> content = new(StringComparer.Ordinal);
            private readonly Dictionary<string, SdkProjection> projections = new(StringComparer.Ordinal);
            private int artifactSequence;
            internal string QuestionFailure;
            internal string AnkiFailure;
            internal int InitialAttempts;
            internal int FollowUpAttempts;
            internal int MakeCardsAttempts;
            internal int AnkiAttempts;
            internal string MakeCardsAnswerToken;

            public Task<SdkArtifact> PublishTextAsync(SdkArtifactPurpose purpose, string text, CancellationToken cancellationToken)
                => Task.FromResult(NewArtifact(text));

            public Task<byte[]> ReadArtifactAsync(SdkArtifact artifact, CancellationToken cancellationToken)
                => Task.FromResult(content[artifact.Token]);

            public Task<SdkSubmission> AnkiProbeAsync(SdkArtifact configuration, CancellationToken cancellationToken)
            {
                AnkiAttempts++;
                if (AnkiFailure != null) throw new SdkDomainException(AnkiFailure, new InvalidOperationException(AnkiFailure));
                return Task.FromResult(new SdkSubmission("anki-probe", Array.Empty<SdkArtifact>()));
            }

            public Task<SdkSubmission> QuestionInitialAsync(SdkArtifact question, IReadOnlyList<SdkArtifact> context, CancellationToken cancellationToken)
            {
                InitialAttempts++;
                if (QuestionFailure != null) throw new SdkDomainException(QuestionFailure, new InvalidOperationException(QuestionFailure));
                var answer = NewArtifact("first-answer");
                projections["question-session"] = new SdkProjection(OperationStatus.Succeeded, "answer-ready");
                return Task.FromResult(new SdkSubmission("question-session", new[] { answer }));
            }

            public Task<SdkSubmission> QuestionFollowUpAsync(string sessionId, SdkArtifact followUp, CancellationToken cancellationToken)
            {
                FollowUpAttempts++;
                var answer = NewArtifact("follow-answer");
                projections[sessionId] = new SdkProjection(OperationStatus.Succeeded, "answer-ready");
                return Task.FromResult(new SdkSubmission(sessionId, new[] { answer }));
            }

            public Task<SdkSubmission> QuestionMakeCardsAsync(string sessionId, SdkArtifact answer, CancellationToken cancellationToken)
            {
                MakeCardsAttempts++;
                MakeCardsAnswerToken = answer.Token;
                return Task.FromResult(new SdkSubmission(sessionId, Array.Empty<SdkArtifact>()));
            }

            public Task<SdkProjection> GetQuestionAsync(string taskId, CancellationToken cancellationToken)
                => Task.FromResult(projections[taskId]);

            public Task<SdkProjection> GetTaskAsync(string taskId, CancellationToken cancellationToken)
                => Task.FromResult(projections[taskId]);

            private SdkArtifact NewArtifact(string text)
            {
                var token = "quality-artifact-" + (++artifactSequence).ToString("D4");
                content[token] = Encoding.UTF8.GetBytes(text);
                return new SdkArtifact(token, token);
            }
        }
    }
}
