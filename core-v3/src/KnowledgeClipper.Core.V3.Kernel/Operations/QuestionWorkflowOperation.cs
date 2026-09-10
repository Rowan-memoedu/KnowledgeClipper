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
    private sealed record ProjectedCard(string CardId, string Front, string Back, string Template);
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
    {
        if (command.Body.Data.ValueKind == JsonValueKind.Object &&
            command.Body.Data.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String)
        {
            return action.GetString() switch
            {
                "confirm_card_draft" => ConfirmCardDraftAsync(command, context, cancellationToken),
                "cancel_card_draft" => CancelCardDraftAsync(command, context),
                _ => throw new InvalidDataException("Question flashcard action is unsupported.")
            };
        }
        return _operationId switch
        {
            "question.initial" => ExecuteAnswerAsync(command, context, initial: true, cancellationToken),
            "question.follow-up" => ExecuteAnswerAsync(command, context, initial: false, cancellationToken),
            "question.make-cards" => ExecuteMakeCardsAsync(command, context, cancellationToken),
            _ => throw new InvalidOperationException()
        };
    }

    private static async Task<OperationExecutionResult> ExecuteAnswerAsync(
        CommandEnvelope command,
        OperationContext context,
        bool initial,
        CancellationToken cancellationToken)
    {
        var expectedSchema = initial ? "kc.question-initial-command/2" : "kc.question-follow-up-command/1";
        Require(CardDestinations.SchemaMatches(command, expectedSchema) && command.Body.Data.ValueKind == JsonValueKind.Object, "Question answer command body is invalid.");
        var sessionId = GetRequiredToken(command.Body.Data, "sessionId");
        var inputName = initial ? "question" : "followUp";
        var questionReference = ReadReference(command.Body.Data, inputName);
        Require(command.Inputs.Contains(questionReference), "Question input is not present in the ordered command artifacts.");
        var neutral = CardDestinations.IsNeutral(command);
        ArtifactReference? ankiConfiguration = neutral ? null : ReadOptionalConfiguration(command, context.Capability.AuthorizationPolicy == "explicit");
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

        if (neutral)
        {
            Require(command.Authorization is null, "Neutral question cannot carry destination write authorization.");
            ankiConfiguration = initial ? await CardDestinations.FreezeAsync(command, context, cancellationToken).ConfigureAwait(false) : FrozenSessionDestinations(events, command);
        }
        else Require(!events.SelectMany(x => x.Artifacts).Any(x => x.Kind == "destination-snapshot"), "Neutral session cannot switch to legacy destination configuration.");
        var revision = events.Count;
        var claim = SessionEvent(command, streamId, "question.session-command-claimed", new { sessionId, phase = initial ? "initial" : "follow-up", command.IdempotencyKey },
            neutral ? command.Inputs.Append(ankiConfiguration!).ToArray() : command.Inputs);
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
            var updatedEvents = events.Concat(new[] { claim, recorded }).ToArray();
            JsonElement? legacyCardsWorkflow = context.WorkerRuntime.RequiresLegacyQuestionWorkflow
                ? await BuildLegacyQuestionWorkflowAsync(context, updatedEvents, null, false, cancellationToken).ConfigureAwait(false)
                : null;
            var cards = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
                command.OperationId,
                context.Capability.WorkerBinding,
                StrictJson.CloneElement(new { phase = "make-cards", question = prompt, answer = answerText, sessionId, command.IdempotencyKey, legacyQuestionWorkflow = legacyCardsWorkflow }),
                TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
            Require(cards.Status == "completed" && cards.ExternalWriteCount == 0 && cards.ExternalStateKnown, "Question card generation violated destination-neutral semantics.");
            var projectedCards = ReadProjectedCards(cards.Payload, sessionId);
            var cardArtifact = await PublishQuestionCardArtifactAsync(context, command, sessionId, projectedCards, questionReference, answer, cancellationToken).ConfigureAwait(false);
            var draft = FlashcardDraftProjection.Create(command.Operation, command.OperationId, cardArtifact,
                projectedCards.Select(card => (card.CardId, card.Front, card.Back, card.Template)).ToArray());
            var draftArtifacts = ankiConfiguration is null ? new[] { answer, cardArtifact } : new[] { answer, cardArtifact, ankiConfiguration };
            context.EventStore.Append(streamId, revision + 3, new[]
            {
                SessionEvent(command, streamId, "question.cards-drafted", new
                {
                    sessionId, sourceOperationId = command.OperationId, answerArtifactId = answer.ArtifactId, cardArtifactId = cardArtifact.ArtifactId,
                    draftToken = draft.GetProperty("token").GetString(), draftRevision = draft.GetProperty("revision").GetString(), draftSha256 = draft.GetProperty("digest").GetString(),
                    destination = ankiConfiguration is null ? "none" : ankiConfiguration.Kind == "destination-snapshot" ? "frozen-destinations" : "Anki"
                }, ankiConfiguration is null ? new[] { cardArtifact } : new[] { cardArtifact, ankiConfiguration })
            });
            return new OperationExecutionResult(
                StrictJson.CloneElement(new { status = "waiting_input", pipeline = "awaiting_card_confirmation", sessionId, answerArtifactId = answer.ArtifactId, cardCount = projectedCards.Count, cardDraft = draft, externalWriteCount = 0 }),
                draftArtifacts, 0, true);
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
        Require(CardDestinations.SchemaMatches(command, "kc.question-make-cards-command/1") && command.Body.Data.ValueKind == JsonValueKind.Object, "Question make-cards command body is invalid.");
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
        Require(CardDestinations.IsNeutral(command) || !events.SelectMany(x => x.Artifacts).Any(x => x.Kind == "destination-snapshot"),
            "A destination-neutral session cannot switch to legacy Anki configuration.");
        if (CardDestinations.IsNeutral(command))
        {
            Require(command.Authorization is null, "Neutral make-cards cannot carry destination write authorization.");
            ankiConfiguration = FrozenSessionDestinations(events, command);
        }
        else if (context.Capability.AuthorizationPolicy == "explicit")
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
            var cards = ReadProjectedCards(content.Payload, sessionId);
            var cardArtifact = await PublishQuestionCardArtifactAsync(context, command, sessionId, cards, questionReference, answerReference, cancellationToken).ConfigureAwait(false);

            var draft = FlashcardDraftProjection.Create("question.make-cards", command.OperationId, cardArtifact,
                cards.Select(card => (card.CardId, card.Front, card.Back, card.Template)).ToArray());
            context.EventStore.Append(streamId, revision + 2, new[]
            {
                SessionEvent(command, streamId, "question.cards-drafted", new
                {
                    sessionId,
                    cardArtifactId = cardArtifact.ArtifactId,
                    draftToken = draft.GetProperty("token").GetString(),
                    draftRevision = draft.GetProperty("revision").GetString(),
                    draftSha256 = draft.GetProperty("digest").GetString(),
                    sourceOperationId = command.OperationId, destination = ankiConfiguration is null ? "none" : ankiConfiguration.Kind == "destination-snapshot" ? "frozen-destinations" : "Anki"
                }, ankiConfiguration is null ? new[] { cardArtifact } : new[] { cardArtifact, ankiConfiguration })
            });
            return new OperationExecutionResult(
                StrictJson.CloneElement(new { status = "awaiting_card_confirmation", pipeline = "awaiting_card_confirmation", sessionId, destination = ankiConfiguration is null ? "none" : ankiConfiguration.Kind == "destination-snapshot" ? "frozen-destinations" : "Anki", cardCount = cards.Count, cardDraft = draft, externalWriteCount = 0 }),
                ankiConfiguration is null ? new[] { cardArtifact } : new[] { cardArtifact, ankiConfiguration },
                0,
                true);
        }
        catch (Exception ex)
        {
            TryAppendSessionFailure(context.EventStore, command, streamId, context.EventStore.GetRevision(streamId), sessionId, ex is WorkerExternalStateUnknownException);
            throw;
        }
    }

    private static async Task<OperationExecutionResult> ConfirmCardDraftAsync(
        CommandEnvelope command,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        Require(CardDestinations.SchemaMatches(command, "kc.question-make-cards-command/1") && command.Inputs.Count == 0,
            "Question card confirmation must not introduce new inputs.");
        Require(CardDestinations.HasActionAuthority(command, context), "Question card confirmation authority is invalid.");
        var source = GetAwaitingDraft(command, context);
        if (CardDestinations.IsNeutral(command))
        {
            var result = await CardDestinations.ConfirmAsync(command, context, source.Result, source.Artifacts, cancellationToken).ConfigureAwait(false);
            var neutralSession = GetRequiredString(source.Result, "sessionId");
            var stream = SessionStream(neutralSession);
            context.EventStore.Append(stream, context.EventStore.GetRevision(stream), new[] { SessionEvent(command, stream, "question.cards-exported", result.Data, result.Artifacts) });
            return result;
        }
        var draft = source.Result.GetProperty("cardDraft");
        FlashcardDraftProjection.RequireBinding(command.Body.Data, draft);
        var sessionId = GetRequiredString(source.Result, "sessionId");
        var cardArtifact = source.Artifacts.Single(item => item.Kind == "question-card-bundle");
        var configuration = source.Artifacts.Single(item => item.Kind is "anki-configuration" or "anki-export-config");
        var cardText = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, cardArtifact, cancellationToken).ConfigureAwait(false);
        using var cardDocument = StrictJson.Parse(Encoding.UTF8.GetBytes(cardText), "Question card draft");
        var cards = cardDocument.RootElement.GetProperty("cards");
        Require(cards.ValueKind == JsonValueKind.Array && cards.GetArrayLength() is >= 1 and <= 2, "Question card draft cardinality is invalid.");
        var configurationJson = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, configuration, cancellationToken).ConfigureAwait(false);
        var anki = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
            command.OperationId,
            context.WorkerRuntime.GetAdmittedBinding("export.anki"),
            StrictJson.CloneElement(new
            {
                phase = "export", sessionId,
                cards = cards.EnumerateArray().Select(card => new { cardId = GetRequiredString(card, "cardId"), front = GetRequiredString(card, "front"), back = GetRequiredString(card, "back"), template = card.TryGetProperty("template", out var template) ? template.GetString() : "basic_qa" }).ToArray(),
                originalText = string.Join("\n\n", cards.EnumerateArray().Select(card => GetRequiredString(card, "front") + "\n" + GetRequiredString(card, "back"))),
                configurationJson,
                idempotencyKey = Hash("question-card-confirm\n" + command.OperationId + "\n" + GetRequiredString(draft, "digest")),
                sideEffectAuthorization = new { authorizationId = command.Authorization!.AuthorizationId, mode = "explicit-user-action", scope = command.Authorization.Policy }
            }), TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
        Require(anki.Status == "completed" && anki.ExternalWriteCount >= 0 && anki.ExternalWriteCount <= cards.GetArrayLength() && anki.ExternalStateKnown &&
                anki.Payload.TryGetProperty("exactReadback", out var exact) && exact.ValueKind == JsonValueKind.True &&
                anki.Payload.TryGetProperty("semanticReadback", out var projectedSemantic) && projectedSemantic.ValueKind == JsonValueKind.Object,
            "Question card confirmation lacks a bounded SHA-bound semantic destination readback.");
        var semantic = anki.Payload.GetProperty("semanticReadback");
        var evidence = await DomainArtifactFactory.PublishJsonAsync(
            context.ArtifactStore, command, "anki-export-evidence", "1.0.0", "kc.anki-export-evidence/1", anki.Payload,
            new[] { cardArtifact, configuration }, cancellationToken).ConfigureAwait(false);
        var streamId = SessionStream(sessionId);
        context.EventStore.Append(streamId, context.EventStore.GetRevision(streamId), new[]
        {
            SessionEvent(command, streamId, "question.cards-exported", new
            {
                sessionId, destination = "Anki", cardArtifactId = cardArtifact.ArtifactId,
                evidenceArtifactId = evidence.ArtifactId, anki.ExternalWriteCount, exactReadback = true
            }, new[] { cardArtifact, evidence })
        });
        return new OperationExecutionResult(
            StrictJson.CloneElement(new { status = "succeeded", pipeline = "completed", sessionId, destination = "Anki", cardCount = cards.GetArrayLength(), cardDraft = draft, semanticReadback = semantic, anki.ExternalWriteCount, exactReadback = true }),
            source.Artifacts.Concat(new[] { evidence }).ToArray(), anki.ExternalWriteCount, true);
    }

    private static Task<OperationExecutionResult> CancelCardDraftAsync(CommandEnvelope command, OperationContext context)
    {
        Require(CardDestinations.SchemaMatches(command, "kc.question-make-cards-command/1") && command.Inputs.Count == 0,
            "Question card cancellation must not introduce new inputs.");
        var source = GetAwaitingDraft(command, context);
        var draft = source.Result.GetProperty("cardDraft");
        FlashcardDraftProjection.RequireBinding(command.Body.Data, draft);
        var sessionId = GetRequiredString(source.Result, "sessionId");
        var streamId = SessionStream(sessionId);
        context.EventStore.Append(streamId, context.EventStore.GetRevision(streamId), new[]
        {
            SessionEvent(command, streamId, "question.cards-cancelled", new { sessionId, draftToken = GetRequiredString(draft, "token") }, source.Artifacts)
        });
        return Task.FromResult(new OperationExecutionResult(
            StrictJson.CloneElement(new { status = "cancelled", pipeline = "cancelled", sessionId, cardDraft = draft, externalWriteCount = 0 }),
            source.Artifacts, 0, true));
    }

    private static (JsonElement Result, IReadOnlyList<ArtifactReference> Artifacts) GetAwaitingDraft(CommandEnvelope command, OperationContext context)
    {
        Require(command.Body.Data.TryGetProperty("sourceOperationId", out var sourceOperation) && sourceOperation.ValueKind == JsonValueKind.String &&
                sourceOperation.GetString() == command.OperationId, "Question card action must target its own operation.");
        var terminal = context.EventStore.ReadStream(command.OperationId).LastOrDefault(item => item.EventType == "operation.completed");
        Require(terminal is not null && terminal.Operation.StartsWith("question.", StringComparison.Ordinal),
            "Question card action requires the current awaiting_card_confirmation draft.");
        var result = terminal!.Data.GetProperty("result");
        Require(result.TryGetProperty("pipeline", out var pipeline) && pipeline.GetString() == "awaiting_card_confirmation" &&
                result.TryGetProperty("cardDraft", out var draft) && draft.ValueKind == JsonValueKind.Object,
            "Question card action requires the current awaiting_card_confirmation draft.");
        Require(terminal!.Artifacts.Count(item => item.Kind == "question-card-bundle") == 1 &&
                terminal.Artifacts.Count(CardDestinations.IsConfiguration) == 1,
            "Question card action source artifacts are incomplete.");
        var sessionId = GetRequiredString(result, "sessionId");
        var latestDraft = context.EventStore.ReadStream(SessionStream(sessionId)).LastOrDefault(item => item.EventType == "question.cards-drafted");
        Require(latestDraft is not null && GetRequiredString(latestDraft.Data, "sourceOperationId") == command.OperationId,
            "Question card action targets a stale session revision.");
        return (terminal.Data.GetProperty("result").Clone(), terminal.Artifacts);
    }

    private static ArtifactReference? ReadOptionalConfiguration(CommandEnvelope command, bool requiresAuthorization)
    {
        if (!command.Body.Data.TryGetProperty("configuration", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var configuration = value.Deserialize<ArtifactReference>(KernelJson.Options) ?? throw new InvalidDataException("Question Anki configuration is invalid.");
        Require(command.Inputs.Contains(configuration), "Question Anki configuration is not an ordered command input.");
        if (requiresAuthorization) Require(command.Authorization is { Policy: "isolated-canary-deck" }, "Question draft destination requires isolated-canary-deck authorization.");
        return configuration;
    }

    private static ArtifactReference FrozenSessionDestinations(IReadOnlyList<KernelEvent> events, CommandEnvelope command)
    {
        Require(!command.Body.Data.TryGetProperty("destinations", out _) && !command.Body.Data.TryGetProperty("configuration", out _), "Question continuation cannot replace frozen destinations.");
        var snapshots = events.SelectMany(x => x.Artifacts).Where(x => x.Kind == "destination-snapshot").Distinct().ToArray();
        Require(snapshots.Length == 1, "Question continuation has no unique initial destination snapshot.");
        return snapshots[0];
    }

    private static IReadOnlyList<ProjectedCard> ReadProjectedCards(JsonElement payload, string sessionId)
    {
        if (payload.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array)
        {
            var projected = cards.EnumerateArray().Select((card, index) => new ProjectedCard(
                card.TryGetProperty("cardId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : "card-" + Hash(sessionId + "\n" + index)[..16].ToLowerInvariant(),
                GetRequiredString(card, "front"), GetRequiredString(card, "back"),
                card.TryGetProperty("template", out var template) && template.ValueKind == JsonValueKind.String ? template.GetString()! : "basic_qa")).ToArray();
            Require(projected.Length is >= 1 and <= 2, "Question generated card cardinality is invalid.");
            return projected;
        }
        return new[] { new ProjectedCard("card-" + Hash(sessionId)[..16].ToLowerInvariant(), GetRequiredString(payload, "front"), GetRequiredString(payload, "back"), "basic_qa") };
    }

    private static Task<ArtifactReference> PublishQuestionCardArtifactAsync(OperationContext context, CommandEnvelope command, string sessionId,
        IReadOnlyList<ProjectedCard> cards, ArtifactReference question, ArtifactReference answer, CancellationToken cancellationToken)
        => DomainArtifactFactory.PublishJsonAsync(context.ArtifactStore, command, "question-card-bundle", "1.0.0", "kc.question-card-bundle/1",
            new { schema = "kc.question-card-bundle/1", sessionId, cards = cards.Select(card => new { cardId = card.CardId, front = card.Front, back = card.Back, template = card.Template }).ToArray() },
            new[] { question, answer }, cancellationToken);

    private static bool CanContinue(IReadOnlyList<KernelEvent> events)
        => events.Any(item => item.EventType == "question.answer-recorded") &&
           events[^1].EventType is "question.answer-recorded" or "question.cards-drafted" or "question.session-command-aborted" &&
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
