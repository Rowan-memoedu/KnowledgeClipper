using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Application;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.Operations;

internal sealed class QuestionWorkflowOperation : IOperationHandler
{
    private readonly string _operationId;

    public QuestionWorkflowOperation(string operationId)
    {
        if (operationId is not ("question.initial" or "question.follow-up" or "question.make-cards"))
        {
            throw new ArgumentOutOfRangeException(nameof(operationId));
        }
        _operationId = operationId;
    }

    public string OperationId => _operationId;

    public Task<OperationExecutionResult> ExecuteAsync(CommandEnvelope command, OperationContext context, CancellationToken cancellationToken)
        => _operationId switch
        {
            "question.initial" => ExecuteAnswerAsync(command, context, initial: true, cancellationToken),
            "question.follow-up" => ExecuteAnswerAsync(command, context, initial: false, cancellationToken),
            "question.make-cards" => ExecuteMakeCardsAsync(command, context, cancellationToken),
            _ => throw new InvalidOperationException()
        };

    private static async Task<OperationExecutionResult> ExecuteAnswerAsync(
        CommandEnvelope command,
        OperationContext context,
        bool initial,
        CancellationToken cancellationToken)
    {
        var expectedSchema = initial ? "kc.question-initial-command/2" : "kc.question-follow-up-command/1";
        Require(command.Body.Schema == expectedSchema && command.Body.Data.ValueKind == JsonValueKind.Object, "Question answer command body is invalid.");
        var sessionId = GetRequiredToken(command.Body.Data, "sessionId");
        var inputName = initial ? "question" : "followUp";
        var questionReference = ReadReference(command.Body.Data, inputName);
        Require(command.Inputs.Contains(questionReference), "Question input is not present in the ordered command artifacts.");
        var streamId = SessionStream(sessionId);
        var events = context.EventStore.ReadStream(streamId);
        if (initial)
        {
            Require(events.Count == 0, "Question session already exists.");
        }
        else
        {
            Require(CanContinue(events), "Question follow-up is out of order or the session is terminal.");
        }

        var revision = events.Count;
        var claim = SessionEvent(command, streamId, "question.session-command-claimed", new { sessionId, phase = initial ? "initial" : "follow-up", command.IdempotencyKey }, command.Inputs);
        context.EventStore.Append(streamId, revision, new[] { claim });
        try
        {
            context.EventStore.Append(streamId, revision + 1, new[]
            {
                SessionEvent(command, streamId, "question.worker-dispatch-started", new { sessionId, phase = initial ? "initial" : "follow-up" }, Array.Empty<ArtifactReference>())
            });
            var prompt = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, questionReference, cancellationToken).ConfigureAwait(false);
            JsonElement? legacyQuestionWorkflow = context.WorkerRuntime.RequiresLegacyQuestionWorkflow
                ? await BuildLegacyQuestionWorkflowAsync(
                    context,
                    events,
                    prompt,
                    includeCurrentWithoutAnswer: true,
                    cancellationToken).ConfigureAwait(false)
                : null;
            var worker = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
                command.OperationId,
                context.Capability.WorkerBinding,
                StrictJson.CloneElement(new { phase = initial ? "initial" : "follow-up", prompt, sessionId, command.IdempotencyKey, legacyQuestionWorkflow }),
                TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
            Require(worker.Status == "completed" && worker.ExternalWriteCount == 0 && worker.ExternalStateKnown, "Question content proxy violated read-only semantics.");
            var answerText = GetRequiredString(worker.Payload, "answerText");
            var answer = await DomainArtifactFactory.PublishTextAsync(
                context.ArtifactStore,
                command,
                "question-answer",
                "1.0.0",
                "kc.question-answer/1",
                answerText,
                command.Inputs,
                cancellationToken).ConfigureAwait(false);
            var recorded = SessionEvent(command, streamId, "question.answer-recorded", new
            {
                sessionId,
                phase = initial ? "initial" : "follow-up",
                turnIndex = events.Count(item => item.EventType == "question.answer-recorded"),
                questionArtifactId = questionReference.ArtifactId,
                answerArtifactId = answer.ArtifactId
            }, new[] { questionReference, answer });
            context.EventStore.Append(streamId, revision + 2, new[] { recorded });
            return new OperationExecutionResult(
                StrictJson.CloneElement(new { status = "waiting_input", sessionId, answerArtifactId = answer.ArtifactId }),
                new[] { answer },
                0,
                true);
        }
        catch (Exception ex)
        {
            TryAppendSessionFailure(context.EventStore, command, streamId, context.EventStore.GetRevision(streamId), sessionId, ex is WorkerExternalStateUnknownException);
            throw;
        }
    }

    private static async Task<OperationExecutionResult> ExecuteMakeCardsAsync(
        CommandEnvelope command,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        Require(command.Body.Schema == "kc.question-make-cards-command/1" && command.Body.Data.ValueKind == JsonValueKind.Object, "Question make-cards command body is invalid.");
        var sessionId = GetRequiredToken(command.Body.Data, "sessionId");
        var suppliedAnswer = ReadReference(command.Body.Data, "answer");
        var streamId = SessionStream(sessionId);
        var events = context.EventStore.ReadStream(streamId);
        Require(CanContinue(events), "Question make-cards is out of order or the session is terminal.");
        var latestAnswer = events.Last(item => item.EventType == "question.answer-recorded");
        Require(latestAnswer.Artifacts.Count == 2, "Question session answer event is malformed.");
        var questionReference = latestAnswer.Artifacts[0];
        var answerReference = latestAnswer.Artifacts[1];
        Require(answerReference == suppliedAnswer && command.Inputs.Contains(suppliedAnswer), "Question make-cards answer does not match the latest persisted session answer.");
        ArtifactReference? ankiConfiguration = null;
        string? ankiConfigurationJson = null;
        if (context.Capability.AuthorizationPolicy == "explicit")
        {
            Require(command.Authorization is not null && command.Authorization.Policy == "isolated-canary-deck",
                "Question make-cards requires explicit isolated-canary-deck authorization.");
            Require(command.Body.Data.TryGetProperty("configuration", out var configurationElement) && configurationElement.ValueKind == JsonValueKind.Object,
                "Question make-cards requires an Anki configuration artifact before Content dispatch.");
            ankiConfiguration = configurationElement.Deserialize<ArtifactReference>(KernelJson.Options)
                ?? throw new InvalidDataException("Question make-cards Anki configuration is invalid.");
            Require(command.Inputs.Contains(ankiConfiguration), "Question make-cards Anki configuration is not an ordered command input.");
            var verifiedConfiguration = await context.ArtifactStore.OpenVerifiedAsync(
                ankiConfiguration,
                ankiConfiguration.Kind,
                ankiConfiguration.Contract,
                cancellationToken).ConfigureAwait(false);
            ankiConfigurationJson = Encoding.UTF8.GetString(verifiedConfiguration.Primary.Content);
        }

        var revision = events.Count;
        context.EventStore.Append(streamId, revision, new[]
        {
            SessionEvent(command, streamId, "question.session-command-claimed", new { sessionId, phase = "make-cards", command.IdempotencyKey }, command.Inputs)
        });
        try
        {
            context.EventStore.Append(streamId, revision + 1, new[]
            {
                SessionEvent(command, streamId, "question.worker-dispatch-started", new { sessionId, phase = "make-cards" }, Array.Empty<ArtifactReference>())
            });
            var question = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, questionReference, cancellationToken).ConfigureAwait(false);
            var answer = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, answerReference, cancellationToken).ConfigureAwait(false);
            JsonElement? legacyQuestionWorkflow = context.WorkerRuntime.RequiresLegacyQuestionWorkflow
                ? await BuildLegacyQuestionWorkflowAsync(
                    context,
                    events,
                    currentQuestion: null,
                    includeCurrentWithoutAnswer: false,
                    cancellationToken).ConfigureAwait(false)
                : null;
            var content = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
                command.OperationId,
                context.Capability.WorkerBinding,
                StrictJson.CloneElement(new { phase = "make-cards", question, answer, sessionId, command.IdempotencyKey, legacyQuestionWorkflow }),
                TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
            Require(content.Status == "completed" && content.ExternalWriteCount == 0, "Question card generation performed an external write.");
            var card = new
            {
                schema = "kc.question-card-bundle/1",
                sessionId,
                cards = new[] { new { cardId = "card-" + Hash(sessionId)[..16].ToLowerInvariant(), front = GetRequiredString(content.Payload, "front"), back = GetRequiredString(content.Payload, "back") } }
            };
            var cardArtifact = await DomainArtifactFactory.PublishJsonAsync(
                context.ArtifactStore,
                command,
                "question-card-bundle",
                "1.0.0",
                "kc.question-card-bundle/1",
                card,
                new[] { questionReference, answerReference },
                cancellationToken).ConfigureAwait(false);

            var ankiBinding = context.WorkerRuntime.GetAdmittedBinding("export.anki");
            var ankiRequest = new Dictionary<string, object?>
            {
                ["phase"] = "export",
                ["sessionId"] = sessionId,
                ["card"] = new { front = GetRequiredString(content.Payload, "front"), back = GetRequiredString(content.Payload, "back") },
                ["idempotencyKey"] = command.IdempotencyKey
            };
            if (ankiConfiguration is not null)
            {
                ankiRequest["configurationJson"] = ankiConfigurationJson;
            }
            if (command.Authorization is not null)
            {
                ankiRequest["sideEffectAuthorization"] = new
                {
                    authorizationId = command.Authorization.AuthorizationId,
                    mode = "explicit-user-action",
                    scope = command.Authorization.Policy
                };
            }
            var anki = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
                command.OperationId,
                ankiBinding,
                StrictJson.CloneElement(ankiRequest),
                TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
            Require(anki.Status == "completed" && anki.ExternalWriteCount is 0 or 1 && anki.ExternalStateKnown,
                "Fake Anki proxy did not return a bounded known result.");
            Require(anki.Payload.GetProperty("exactReadback").GetBoolean(), "Fake Anki proxy did not perform exact readback.");
            var evidence = await DomainArtifactFactory.PublishJsonAsync(
                context.ArtifactStore,
                command,
                "anki-export-evidence",
                "1.0.0",
                "kc.anki-export-evidence/1",
                anki.Payload,
                new[] { cardArtifact },
                cancellationToken).ConfigureAwait(false);
            context.EventStore.Append(streamId, revision + 2, new[]
            {
                SessionEvent(command, streamId, "question.cards-exported", new
                {
                    sessionId,
                    destination = "Anki",
                    cardArtifactId = cardArtifact.ArtifactId,
                    evidenceArtifactId = evidence.ArtifactId,
                    anki.ExternalWriteCount,
                    exactReadback = true
                }, new[] { cardArtifact, evidence })
            });
            return new OperationExecutionResult(
                StrictJson.CloneElement(new { status = "succeeded", sessionId, destination = "Anki", cardCount = 1, anki.ExternalWriteCount, exactReadback = true }),
                new[] { cardArtifact, evidence },
                anki.ExternalWriteCount,
                true);
        }
        catch (Exception ex)
        {
            TryAppendSessionFailure(context.EventStore, command, streamId, context.EventStore.GetRevision(streamId), sessionId, ex is WorkerExternalStateUnknownException);
            throw;
        }
    }

    private static bool CanContinue(IReadOnlyList<KernelEvent> events)
        => events.Any(item => item.EventType == "question.answer-recorded") &&
           events[^1].EventType is "question.answer-recorded" or "question.session-command-aborted" &&
           !events.Any(item => item.EventType is "question.cards-exported" or "question.session-failed" or "question.session-unknown-external-state");

    private static async Task<JsonElement> BuildLegacyQuestionWorkflowAsync(
        OperationContext context,
        IReadOnlyList<KernelEvent> events,
        string? currentQuestion,
        bool includeCurrentWithoutAnswer,
        CancellationToken cancellationToken)
    {
        var turns = new List<object>();
        var ancestry = new List<object>();
        string? parentAnswerId = null;
        var answerEvents = events.Where(item => item.EventType == "question.answer-recorded").ToArray();
        for (var index = 0; index < answerEvents.Length; index++)
        {
            var item = answerEvents[index];
            Require(item.Artifacts.Count == 2, "Persisted question answer ancestry is malformed.");
            var question = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, item.Artifacts[0], cancellationToken).ConfigureAwait(false);
            var answer = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, item.Artifacts[1], cancellationToken).ConfigureAwait(false);
            var turnId = "turn-" + index;
            turns.Add(parentAnswerId is null
                ? new { turnId, turnIndex = index, question }
                : new { turnId, turnIndex = index, question, parentAnswerId });
            var answerId = "answer-" + Hash(item.Artifacts[1].ArtifactId)[..24].ToLowerInvariant();
            ancestry.Add(parentAnswerId is null
                ? new
                {
                    answerId, artifactRef = item.Artifacts[1].ArtifactId, turnId, turnIndex = index,
                    questionSha256 = Hash(question).ToLowerInvariant(), answerText = answer, answerTextSha256 = Hash(answer).ToLowerInvariant()
                }
                : new
                {
                    answerId, artifactRef = item.Artifacts[1].ArtifactId, turnId, turnIndex = index,
                    questionSha256 = Hash(question).ToLowerInvariant(), answerText = answer, answerTextSha256 = Hash(answer).ToLowerInvariant(), parentAnswerId
                });
            parentAnswerId = answerId;
        }

        if (includeCurrentWithoutAnswer)
        {
            Require(!string.IsNullOrWhiteSpace(currentQuestion), "Current question is required for answer workflow.");
            var index = turns.Count;
            var turnId = "turn-" + index;
            turns.Add(parentAnswerId is null
                ? new { turnId, turnIndex = index, question = currentQuestion! }
                : new { turnId, turnIndex = index, question = currentQuestion!, parentAnswerId });
        }

        return StrictJson.CloneElement(new { questionTurns = turns, answerAncestry = ancestry });
    }

    private static KernelEvent SessionEvent(CommandEnvelope command, string streamId, string eventType, object data, IReadOnlyList<ArtifactReference> artifacts)
        => new(
            KernelSchemas.Event,
            "evt-" + Guid.NewGuid().ToString("N"),
            streamId,
            Sequence: 0,
            eventType,
            EventVersion: 1,
            DateTimeOffset.UtcNow,
            command.CommandId,
            command.CommandId,
            command.Operation,
            command.Operation == "question.make-cards" ? "external-write" : "read-only-external",
            StrictJson.CloneElement(data),
            artifacts);

    private static void TryAppendSessionFailure(
        IEventStore eventStore,
        CommandEnvelope command,
        string streamId,
        long expectedRevision,
        string sessionId,
        bool externalStateUnknown)
    {
        try
        {
            eventStore.Append(streamId, expectedRevision, new[]
            {
                SessionEvent(
                    command,
                    streamId,
                    externalStateUnknown ? "question.session-unknown-external-state" : "question.session-failed",
                    new { sessionId, code = externalStateUnknown ? "session_external_state_unknown" : "session_operation_failed" },
                    Array.Empty<ArtifactReference>())
            });
        }
        catch (EventStoreConcurrencyException)
        {
        }
    }

    private static string SessionStream(string sessionId) => "question-session-" + sessionId;

    private static ArtifactReference ReadReference(JsonElement element, string name)
        => element.GetProperty(name).Deserialize<ArtifactReference>(KernelJson.Options)
           ?? throw new InvalidDataException($"Question '{name}' reference is invalid.");

    private static string GetRequiredToken(JsonElement element, string name)
    {
        var value = GetRequiredString(element, name);
        Require(value.Length <= 80 && value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'), $"Question '{name}' is not a valid token.");
        return value;
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        Require(element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()), $"Question '{name}' is required.");
        return value.GetString()!;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
