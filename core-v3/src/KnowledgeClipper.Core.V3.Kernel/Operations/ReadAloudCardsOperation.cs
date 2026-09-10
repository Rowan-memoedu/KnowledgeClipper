using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Artifacts;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;
using KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

namespace KnowledgeClipper.Core.V3.Kernel.Operations;

/// <summary>
/// Product-level ordinary-text vertical slice. Capture and role-bound context
/// inputs are immutable; Speech consumes the frozen prefix-selection-suffix
/// composition while Content independently produces cards.
/// </summary>
internal sealed class ReadAloudCardsOperation : IOperationHandler
{
    public const string Id = "clip.read-aloud-cards";
    public string OperationId => Id;

    public async Task<OperationExecutionResult> ExecuteAsync(CommandEnvelope command, OperationContext context, CancellationToken cancellationToken)
    {
        Require(CardDestinations.SchemaMatches(command, "kc.read-aloud-cards-command/1") && command.Body.Data.ValueKind == JsonValueKind.Object,
            "Read-aloud cards command body is invalid.");
        if (command.Body.Data.TryGetProperty("action", out var action))
        {
            Require(action.ValueKind == JsonValueKind.String, "Read-aloud cards action is invalid.");
            return action.GetString() switch
            {
                "replay" => await ReplayAsync(command, context, cancellationToken).ConfigureAwait(false),
                "choose_language_card" => await ChooseLanguageCardAsync(command, context, cancellationToken).ConfigureAwait(false),
                "confirm_card_draft" => await ConfirmCardDraftAsync(command, context, cancellationToken).ConfigureAwait(false),
                "cancel_card_draft" => await CancelCardDraftAsync(command, context, cancellationToken).ConfigureAwait(false),
                "cancel_card_resolution" => CancelCardResolution(command, context),
                "resolve_card_resolution" => await ResolveCardResolutionAsync(command, context, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidDataException("Read-aloud cards action is unsupported.")
            };
        }
        var capture = ReadReference(command.Body.Data, "capture");
        var neutral = CardDestinations.IsNeutral(command);
        var configuration = neutral ? await CardDestinations.FreezeAsync(command, context, cancellationToken).ConfigureAwait(false) : ReadReference(command.Body.Data, "configuration");
        var selection = await ReadSelectionAsync(command, context, capture, cancellationToken).ConfigureAwait(false);
        Require(neutral ? command.Inputs.SequenceEqual(new[] { capture }.Concat(CardDestinations.Inputs(CardDestinations.Selections(command))).Concat(selection.ContextReferences)) :
            command.Inputs.Count == 2 + selection.ContextReferences.Count && command.Inputs[0] == capture && command.Inputs[1] == configuration && command.Inputs.Skip(2).SequenceEqual(selection.ContextReferences),
            "Read-aloud cards inputs must be ordered capture, Anki configuration, then context segments.");
        Require(neutral ? command.Authorization is null : command.Authorization is { Policy: "isolated-canary-deck" }, "Read-aloud cards authorization is invalid.");

        var originalText = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, capture, cancellationToken).ConfigureAwait(false);
        Require(!string.IsNullOrWhiteSpace(originalText), "Capture text is empty.");
        var configurationJson = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, configuration, cancellationToken).ConfigureAwait(false);
        var speechBinding = context.WorkerRuntime.GetAdmittedBinding("speech.edge");
        var languageSettings = ReadLanguageSettings(command.Body.Data);
        var includeSpeech = !command.Body.Data.TryGetProperty("includeSpeech", out var includeSpeechValue) || includeSpeechValue.ValueKind != JsonValueKind.False;
        var taskSettingsSnapshot = neutral ? configuration : await PublishTaskSettingsSnapshotAsync(
            context.ArtifactStore, command, configuration, configurationJson, languageSettings, includeSpeech, cancellationToken).ConfigureAwait(false);
        var taskContextSnapshot = await PublishTaskContextSnapshotAsync(
            context.ArtifactStore, command, capture, selection, cancellationToken).ConfigureAwait(false);
        var immutableTaskArtifacts = new[] { capture, configuration, taskSettingsSnapshot, taskContextSnapshot }.Distinct()
            .Concat(selection.ContextReferences)
            .ToArray();

        RecordStage(context, command, "accepted", "reviewing");

        // Start Speech before Content and never feed it reviewed text. Contextual
        // speech is composed only from explicitly role-bound immutable capture
        // artifacts; list order is never interpreted as a semantic role.
        // Schedule both branches before either worker adapter can synchronously
        // prepare a package. A Content business failure must never prevent an
        // already accepted Speech branch from reaching its own durable terminal.
        var synthesisTask = includeSpeech ? Task.Run(() => context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
            command.OperationId,
            speechBinding,
            StrictJson.CloneElement(new
            {
                phase = "synthesize",
                operationId = command.OperationId,
                originalText,
                spokenText = selection.SpokenText,
                spokenTextSha256 = Hash(selection.SpokenText),
                contextMode = selection.SpeechContextMode,
                configurationJson,
                idempotencyKey = command.IdempotencyKey
            }),
            TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken), cancellationToken) : null;
        var contentTask = Task.Run(() => context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
            command.OperationId,
            context.Capability.WorkerBinding,
            StrictJson.CloneElement(new { phase = "review", originalText, configurationJson, languageChoice = languageSettings.DefaultChoice, languageSettingsRevision = languageSettings.Revision, languageSelection = selection.Payload, inputVersion = "1", idempotencyKey = command.IdempotencyKey }),
            TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken), cancellationToken);

        var playbackState = includeSpeech ? "failed" : "not_requested";
        var speechArtifacts = new List<ArtifactReference>();
        try
        {
            if (synthesisTask is null) goto SpeechBranchComplete;
            var synthesis = await synthesisTask.ConfigureAwait(false);
            Require(synthesis.Status == "completed" && synthesis.ExternalWriteCount == 0 && synthesis.ExternalStateKnown,
                "Speech synthesis did not complete as a known local effect.");
            var synthesisArtifact = await DomainArtifactFactory.PublishJsonAsync(context.ArtifactStore, command, "speech-synthesis", "1.0.0", "kc.speech-synthesis/1", synthesis.Payload,
                new[] { capture, taskContextSnapshot }.Concat(selection.ContextReferences).ToArray(), cancellationToken).ConfigureAwait(false);
            speechArtifacts.Add(synthesisArtifact);
            RecordStage(context, command, "playing", "reviewing", synthesisArtifact);

            // Playback is intentionally a fresh invocation/attempt. Its idempotency key
            // is phase-qualified so a command replay can re-play without duplicating Anki.
            var playback = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
                command.OperationId,
                speechBinding,
                StrictJson.CloneElement(new { phase = "playback", actionCommandId = command.CommandId, synthesis = synthesisArtifact, audioArtifact = synthesis.Payload.GetProperty("audioArtifact"), originalText, configurationJson, idempotencyKey = PlaybackIdempotency(command.IdempotencyKey) }),
                TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
            Require(playback.Status == "completed" && playback.ExternalWriteCount == 0 && playback.ExternalStateKnown,
                "Speech playback did not naturally complete as a known local effect.");
            var playbackArtifact = await DomainArtifactFactory.PublishJsonAsync(context.ArtifactStore, command, "speech-playback", "1.0.0", "kc.speech-playback/1", playback.Payload,
                new[] { synthesisArtifact }, cancellationToken).ConfigureAwait(false);
            speechArtifacts.Add(playbackArtifact);
            playbackState = RequiredString(playback.Payload, "status");
            Require(playbackState is "stopped" or "idle_replayable", "Speech playback terminal is not known.");
            RecordStage(context, command, playbackState, "reviewing", playbackArtifact);
        SpeechBranchComplete:;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Speech is an independent convenience branch. A known pre-destination
            // failure must not discard a ready Content review or its immutable draft.
            RecordStage(context, command, playbackState, "reviewing");
        }

        WorkerExecutionResult content;
        try
        {
            content = await contentTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reasonCode = ex.Message.Contains("FAILED_CONTENT_GATE", StringComparison.Ordinal)
                ? "content.gate-failed"
                : "content.worker-failed";
            RecordStage(context, command, playbackState, "partially_failed");
            return new OperationExecutionResult(
                StrictJson.CloneElement(new
                {
                    status = "partial", playback = playbackState, pipeline = "partially_failed",
                    attentionRequired = true, cardGeneration = new { status = "failed", reasonCode },
                    expectedCardCount = 0, actualCardCount = 0, cardDraft = (object?)null, ankiExternalWriteCount = 0
                }),
                immutableTaskArtifacts.Concat(speechArtifacts).ToArray(), ExternalWriteCount: 0, ExternalStateKnown: true);
        }
        Require(content.Status == "completed" && content.ExternalWriteCount == 0 && content.ExternalStateKnown,
            "Content review did not complete as a known read-only effect.");
        var cleanedText = RequiredString(content.Payload, "cleanedText");
        if (content.Payload.TryGetProperty("status", out var contentState) && contentState.GetString() == "language_choice_required")
        {
            var decision = content.Payload.GetProperty("languageDecision");
            Require(decision.ValueKind == JsonValueKind.Object &&
                RequiredString(decision, "categoryId") is "language_zh" or "language_en" && RequiredString(decision, "sourceOperationId") == command.OperationId,
                "Language choice requires an actual validated language classification.");
            Require(RequiredString(decision, "selectedTextSha256") == RequiredString(selection.Payload, "selectedTextSha256") &&
                RequiredString(decision, "targetTextSha256") == RequiredString(selection.Payload, "targetTextSha256"),
                "Language decision selection identity drifted.");
            var targetToken = Hash("language-card-choice\n" + command.OperationId + "\n" + languageSettings.Revision + "\n" + decision.GetRawText());
            var review = await DomainArtifactFactory.PublishTextAsync(context.ArtifactStore, command, "content-review", "1.0.0", "kc.content-review/1", cleanedText,
                new[] { capture, taskContextSnapshot }, cancellationToken).ConfigureAwait(false);
            RecordStage(context, command, playbackState, "waiting_language_card_choice", review);
            return new OperationExecutionResult(StrictJson.CloneElement(new
            {
                status = "waiting_language_card_choice", playback = playbackState, pipeline = "waiting_language_card_choice", attentionRequired = true,
                languageCardChoice = new { targetToken, settingsRevision = languageSettings.Revision, choices = new[] { "meaning-reading", "writing", "both" } },
                languageDecision = decision, languageSelection = selection.Payload,
                expectedCardCount = 0, actualCardCount = 0, cardDraft = (object?)null, ankiExternalWriteCount = 0
            }), immutableTaskArtifacts.Concat(speechArtifacts).Append(review).ToArray(), 0, true);
        }
        if (IsCardResolutionRequired(content.Payload))
        {
            var review = await DomainArtifactFactory.PublishTextAsync(context.ArtifactStore, command, "content-review", "1.0.0", "kc.content-review/1", cleanedText,
                new[] { capture }, cancellationToken).ConfigureAwait(false);
            var resolution = CardResolution(content.Payload);
            RecordStage(context, command, playbackState, "awaiting_card_resolution", review);
            return new OperationExecutionResult(
                StrictJson.CloneElement(new { status = "awaiting_card_resolution", playback = playbackState, pipeline = "awaiting_card_resolution", attentionRequired = true, frozenLanguageCardChoice = languageSettings.DefaultChoice, languageSettingsSnapshot = languageSettings.Snapshot, languageSelection = selection.Payload, cardResolution = resolution, expectedCardCount = 0, actualCardCount = 0, cardDraft = (object?)null, ankiExternalWriteCount = 0 }),
                immutableTaskArtifacts.Concat(new[] { review }).Concat(speechArtifacts).ToArray(), 0, true);
        }
        var cards = ReadCards(content.Payload);
        var reviewArtifact = await DomainArtifactFactory.PublishTextAsync(context.ArtifactStore, command, "content-review", "1.0.0", "kc.content-review/1", cleanedText,
            new[] { capture }, cancellationToken).ConfigureAwait(false);
        var cardArtifact = await DomainArtifactFactory.PublishJsonAsync(context.ArtifactStore, command, "card-bundle", "1.0.0", "kc.card-bundle/1",
            new { cards = cards.Select(card => new { card.cardId, card.front, card.back, card.template }).ToArray(), source = capture.ArtifactId, languageChoice = languageSettings.DefaultChoice }, new[] { reviewArtifact }, cancellationToken).ConfigureAwait(false);
        var draft = FlashcardDraftProjection.Create(Id, command.OperationId, cardArtifact, cards.Select(card => (card.cardId, card.front, card.back, card.template)).ToArray());
        RecordStage(context, command, playbackState, "awaiting_card_confirmation", cardArtifact);

        // A card draft is intentionally terminal-but-resumable: no destination
        // worker request is constructed until a separate, token-bound confirm.
        return new OperationExecutionResult(
            StrictJson.CloneElement(new
            {
                status = "awaiting_card_confirmation", playback = playbackState, pipeline = "awaiting_card_confirmation",
                expectedCardCount = cards.Count, actualCardCount = cards.Count, frozenLanguageCardChoice = languageSettings.DefaultChoice, languageSettingsSnapshot = languageSettings.Snapshot, cardDraft = draft, ankiExternalWriteCount = 0
            }),
            immutableTaskArtifacts.Concat(new[] { reviewArtifact }).Concat(speechArtifacts).Append(cardArtifact).ToArray(), ExternalWriteCount: 0, ExternalStateKnown: true);
    }

    private static async Task<OperationExecutionResult> ConfirmCardDraftAsync(CommandEnvelope command, OperationContext context, CancellationToken cancellationToken)
    {
        Require(command.Inputs.Count == 0, "Card draft confirmation must not introduce new inputs.");
        Require(CardDestinations.HasActionAuthority(command, context), "Card draft confirmation authorization is invalid.");
        var source = GetAwaitingCardDraft(command, context);
        if (CardDestinations.IsNeutral(command)) return await CardDestinations.ConfirmAsync(command, context, source.Result, source.Artifacts, cancellationToken).ConfigureAwait(false);
        var expectedDraft = source.Result.GetProperty("cardDraft");
        FlashcardDraftProjection.RequireBinding(command.Body.Data, expectedDraft);

        var cardArtifact = source.Artifacts.Single(item => item.Kind == "card-bundle");
        var configuration = source.Artifacts.Single(IsAnkiConfiguration);
        var capture = source.Artifacts.Single(item => item.Kind == "capture-text");
        var cardJson = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, cardArtifact, cancellationToken).ConfigureAwait(false);
        using var cardDocument = StrictJson.Parse(Encoding.UTF8.GetBytes(cardJson), "Read-aloud card draft");
        var card = cardDocument.RootElement.GetProperty("cards");
        Require(card.ValueKind == JsonValueKind.Array && card.GetArrayLength() is >= 1 and <= 2, "Card draft has invalid cardinality.");
        var cards = card.EnumerateArray().Select(item => new { cardId = RequiredString(item, "cardId"), front = RequiredString(item, "front"), back = RequiredString(item, "back"), template = item.TryGetProperty("template", out var template) ? template.GetString() : "basic_qa" }).ToArray();
        var originalText = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, capture, cancellationToken).ConfigureAwait(false);
        var configurationJson = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, configuration, cancellationToken).ConfigureAwait(false);
        var ankiBinding = context.WorkerRuntime.GetAdmittedBinding("export.anki");
        var playbackState = RequiredString(source.Result, "playback");

        RecordStage(context, command, playbackState, "writing_anki", cardArtifact);
        var anki = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
            command.OperationId,
            ankiBinding,
            StrictJson.CloneElement(new
            {
                phase = "export", card = cards[0], cards, originalText, configurationJson,
                idempotencyKey = StableAnkiIdempotency(capture, configuration, RequiredString(expectedDraft, "digest")),
                sideEffectAuthorization = new { authorizationId = command.Authorization!.AuthorizationId, mode = "explicit-user-action", scope = command.Authorization.Policy }
            }), TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
        Require(anki.Status == "completed" && anki.ExternalStateKnown && anki.Payload.TryGetProperty("exactReadback", out var readback) && readback.ValueKind == JsonValueKind.True &&
                anki.Payload.TryGetProperty("semanticReadback", out var projectedReadback) && projectedReadback.ValueKind == JsonValueKind.Object,
            "Anki export did not produce SHA-bound semantic readback evidence.");
        var semanticReadback = anki.Payload.GetProperty("semanticReadback");
        var ankiArtifact = await DomainArtifactFactory.PublishJsonAsync(context.ArtifactStore, command, "anki-readback", "1.0.0", "kc.anki-readback/1", anki.Payload,
            new[] { cardArtifact, configuration }, cancellationToken).ConfigureAwait(false);

        return new OperationExecutionResult(
            StrictJson.CloneElement(new { status = "completed", playback = playbackState, pipeline = "completed", expectedCardCount = cards.Length, actualCardCount = cards.Length, cardDraft = expectedDraft, semanticReadback, anki.ExternalWriteCount }),
            source.Artifacts.Concat(new[] { ankiArtifact }).ToArray(), anki.ExternalWriteCount, true);
    }

    private static async Task<OperationExecutionResult> ChooseLanguageCardAsync(CommandEnvelope command, OperationContext context, CancellationToken cancellationToken)
    {
        Require(command.Inputs.Count == 0 && CardDestinations.HasActionAuthority(command, context), "Language card choice requires the source operation's authority.");
        Require(command.Body.Data.TryGetProperty("sourceOperationId", out var sourceId) && sourceId.GetString() == command.OperationId,
            "Language card choice must target its own operation.");
        var choice = RequiredString(command.Body.Data, "choice");
        Require(choice is "meaning-reading" or "writing" or "both", "Language card choice is unsupported.");
        var source = context.EventStore.ReadStream(command.OperationId).LastOrDefault(item => item.EventType == "operation.completed");
        Require(source is not null && source.Operation == Id && source.Data.GetProperty("result").GetProperty("pipeline").GetString() == "waiting_language_card_choice",
            "Language card choice requires the current waiting_language_card_choice terminal.");
        var sourceResult = source!.Data.GetProperty("result");
        var prompt = sourceResult.GetProperty("languageCardChoice");
        Require(RequiredString(command.Body.Data, "targetToken") == RequiredString(prompt, "targetToken"), "Language card choice token is stale.");
        var capture = source.Artifacts.Single(item => item.Kind == "capture-text");
        var configuration = source.Artifacts.Single(IsAnkiConfiguration);
        var originalText = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, capture, cancellationToken).ConfigureAwait(false);
        var configurationJson = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, configuration, cancellationToken).ConfigureAwait(false);
        var priorDecision = sourceResult.GetProperty("languageDecision");
        Require(RequiredString(priorDecision, "categoryId") is "language_zh" or "language_en", "Language choice cannot resume a non-language decision.");
        Require(RequiredString(priorDecision, "sourceOperationId") == command.OperationId &&
            RequiredString(prompt, "targetToken") == Hash("language-card-choice\n" + command.OperationId + "\n" + RequiredString(prompt, "settingsRevision") + "\n" + priorDecision.GetRawText()),
            "Language choice durable decision binding drifted.");
        var nextInputVersion = (int.Parse(RequiredString(priorDecision, "inputVersion"), System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var content = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
            command.OperationId, context.Capability.WorkerBinding,
            StrictJson.CloneElement(new { phase = "review", originalText, configurationJson, languageChoice = choice, languageSettingsRevision = RequiredString(prompt, "settingsRevision"), languageSelection = sourceResult.GetProperty("languageSelection"), languageDecision = priorDecision, inputVersion = nextInputVersion, idempotencyKey = command.IdempotencyKey }),
            TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
        Require(content.Status == "completed" && content.ExternalWriteCount == 0 && content.ExternalStateKnown, "Language card generation did not complete as a known read-only effect.");
        var cleanedText = RequiredString(content.Payload, "cleanedText");
        if (IsCardResolutionRequired(content.Payload))
        {
            var review = await DomainArtifactFactory.PublishTextAsync(context.ArtifactStore, command, "content-review", "1.0.0", "kc.content-review/1", cleanedText,
                new[] { capture }, cancellationToken).ConfigureAwait(false);
            var resolution = CardResolution(content.Payload);
            RecordStage(context, command, RequiredString(sourceResult, "playback"), "awaiting_card_resolution", review);
            return new OperationExecutionResult(
                StrictJson.CloneElement(new { status = "awaiting_card_resolution", playback = RequiredString(sourceResult, "playback"), pipeline = "awaiting_card_resolution", attentionRequired = true, frozenLanguageCardChoice = choice, languageSettingsSnapshot = new { revision = RequiredString(prompt, "settingsRevision"), defaultChoice = "ask", resolvedChoice = choice }, languageSelection = sourceResult.GetProperty("languageSelection"), cardResolution = resolution, expectedCardCount = 0, actualCardCount = 0, cardDraft = (object?)null, ankiExternalWriteCount = 0 }),
                ReplaceContentRevisionArtifacts(source.Artifacts, review), 0, true);
        }
        var cards = ReadCards(content.Payload);
        var reviewArtifact = await DomainArtifactFactory.PublishTextAsync(context.ArtifactStore, command, "content-review", "1.0.0", "kc.content-review/1", cleanedText,
            new[] { capture }, cancellationToken).ConfigureAwait(false);
        var cardArtifact = await DomainArtifactFactory.PublishJsonAsync(context.ArtifactStore, command, "card-bundle", "1.0.0", "kc.card-bundle/1",
            new { cards = cards.Select(card => new { card.cardId, card.front, card.back, card.template }).ToArray(), source = capture.ArtifactId, languageChoice = choice }, new[] { reviewArtifact }, cancellationToken).ConfigureAwait(false);
        var draft = FlashcardDraftProjection.Create(Id, command.OperationId, cardArtifact, cards.Select(card => (card.cardId, card.front, card.back, card.template)).ToArray());
        var playbackState = RequiredString(sourceResult, "playback");
        RecordStage(context, command, playbackState, "awaiting_card_confirmation", cardArtifact);
        return new OperationExecutionResult(
            StrictJson.CloneElement(new { status = "awaiting_card_confirmation", playback = playbackState, pipeline = "awaiting_card_confirmation", expectedCardCount = cards.Count, actualCardCount = cards.Count, frozenLanguageCardChoice = choice, languageSettingsSnapshot = new { revision = RequiredString(prompt, "settingsRevision"), defaultChoice = "ask", resolvedChoice = choice }, cardDraft = draft, ankiExternalWriteCount = 0 }),
            ReplaceContentRevisionArtifacts(source.Artifacts, reviewArtifact, cardArtifact), 0, true);
    }

    private static Task<OperationExecutionResult> CancelCardDraftAsync(CommandEnvelope command, OperationContext context, CancellationToken cancellationToken)
    {
        Require(command.Inputs.Count == 0, "Card draft cancellation must not introduce new inputs.");
        var source = GetAwaitingCardDraft(command, context);
        FlashcardDraftProjection.RequireBinding(command.Body.Data, source.Result.GetProperty("cardDraft"));
        return Task.FromResult(new OperationExecutionResult(
            StrictJson.CloneElement(new { status = "cancelled", playback = RequiredString(source.Result, "playback"), pipeline = "cancelled", cardDraft = source.Result.GetProperty("cardDraft"), ankiExternalWriteCount = 0 }),
            source.Artifacts, ExternalWriteCount: 0, ExternalStateKnown: true));
    }

    private static OperationExecutionResult CancelCardResolution(CommandEnvelope command, OperationContext context)
    {
        Require(command.Inputs.Count == 0 && CardDestinations.HasActionAuthority(command, context), "Card resolution cancellation requires the source operation's authority.");
        var terminal = context.EventStore.ReadStream(command.OperationId).LastOrDefault(item => item.EventType == "operation.completed");
        Require(terminal is not null && terminal.Operation == Id, "Card resolution cancellation requires a durable terminal.");
        var result = terminal!.Data.GetProperty("result");
        Require(RequiredString(result, "pipeline") == "awaiting_card_resolution", "Card resolution is not awaiting input.");
        var resolution = result.GetProperty("cardResolution");
        Require(RequiredString(command.Body.Data, "targetToken") == RequiredString(resolution, "targetToken"), "Card resolution token is stale.");
        return new OperationExecutionResult(StrictJson.CloneElement(new { status = "cancelled", playback = RequiredString(result, "playback"), pipeline = "cancelled", attentionRequired = false, cardResolution = resolution, ankiExternalWriteCount = 0 }), terminal.Artifacts, 0, true);
    }

    private static async Task<OperationExecutionResult> ResolveCardResolutionAsync(CommandEnvelope command, OperationContext context, CancellationToken cancellationToken)
    {
        Require(command.Inputs.Count == 0 && CardDestinations.HasActionAuthority(command, context), "Card resolution requires the source operation's authority.");
        var terminal = context.EventStore.ReadStream(command.OperationId).LastOrDefault(item => item.EventType == "operation.completed");
        Require(terminal is not null && terminal.Operation == Id, "Card resolution requires a durable terminal.");
        var source = terminal!.Data.GetProperty("result");
        Require(RequiredString(source, "pipeline") == "awaiting_card_resolution", "Card resolution is not awaiting input.");
        var prompt = source.GetProperty("cardResolution");
        Require(RequiredString(command.Body.Data, "targetToken") == RequiredString(prompt, "targetToken"), "INPUT_STALE card resolution target token.");
        Require(RequiredString(command.Body.Data, "decisionId") == RequiredString(prompt, "decisionId"), "INPUT_STALE card resolution decision id.");
        var action = RequiredString(command.Body.Data, "resolutionAction");
        if (action == "cancel") return CancelCardResolution(command with { Body = new ContractBody(command.Body.Schema, StrictJson.CloneElement(new { destinationMode = CardDestinations.IsNeutral(command) ? "confirmed-cards" : null, action = "cancel_card_resolution", sourceOperationId = command.OperationId, targetToken = RequiredString(prompt, "targetToken") })) }, context);
        Require(action is "accept_candidate" or "user_correction", "Card resolution action is unsupported.");
        var selectedText = RequiredString(command.Body.Data, "selectedText");
        var languageSelection = source.GetProperty("languageSelection");
        JsonElement? candidateOccurrence = null;
        if (action == "accept_candidate")
        {
            Require(selectedText == RequiredString(languageSelection, "targetText"), "Accepted candidate must equal the frozen target text.");
            candidateOccurrence = RequireCandidateOccurrence(command.Body.Data, prompt);
        }
        else
        {
            Require(!command.Body.Data.TryGetProperty("candidateOccurrence", out var forbiddenOccurrence) || forbiddenOccurrence.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
                "User correction must not carry a candidate occurrence.");
        }
        var inputVersion = "resolution-" + Hash(command.IdempotencyKey)[..16];
        var capture = terminal.Artifacts.Single(item => item.Kind == "capture-text");
        var configuration = terminal.Artifacts.Single(IsAnkiConfiguration);
        var originalText = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, capture, cancellationToken).ConfigureAwait(false);
        var configurationJson = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, configuration, cancellationToken).ConfigureAwait(false);
        var resolutionPayload = new Dictionary<string, object?>
        {
            ["schema_version"] = "language-target-resolution/2.0.0",
            ["decision_id"] = RequiredString(prompt, "decisionId"),
            ["target_token"] = RequiredString(prompt, "targetToken"),
            ["action"] = action,
            ["selected_text"] = selectedText,
            ["selected_text_sha256"] = Hash(selectedText),
            ["confirmed"] = true
        };
        if (candidateOccurrence is { } occurrence) resolutionPayload["candidateOccurrence"] = occurrence;
        var content = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(command.OperationId, context.Capability.WorkerBinding,
            StrictJson.CloneElement(new { phase = "review", originalText, configurationJson, languageChoice = RequiredString(source, "frozenLanguageCardChoice"), languageSettingsRevision = source.GetProperty("languageSettingsSnapshot").GetProperty("revision").GetString(), languageSelection, inputVersion, humanResolution = resolutionPayload, idempotencyKey = command.IdempotencyKey }),
            TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
        Require(content.Status == "completed" && content.ExternalWriteCount == 0 && content.ExternalStateKnown && !IsCardResolutionRequired(content.Payload), "Resolved Content revision did not become ready.");
        var cleanedText = RequiredString(content.Payload, "cleanedText");
        var cards = ReadCards(content.Payload);
        var review = await DomainArtifactFactory.PublishTextAsync(context.ArtifactStore, command, "content-review", "1.0.0", "kc.content-review/1", cleanedText, new[] { capture }, cancellationToken).ConfigureAwait(false);
        var bundle = await DomainArtifactFactory.PublishJsonAsync(context.ArtifactStore, command, "card-bundle", "1.0.0", "kc.card-bundle/1", new { cards = cards.Select(card => new { card.cardId, card.front, card.back, card.template }).ToArray(), source = capture.ArtifactId, languageChoice = RequiredString(source, "frozenLanguageCardChoice"), resolution = new { actor = "user", action, sourceRevision = RequiredString(prompt, "revision"), inputVersion } }, new[] { review }, cancellationToken).ConfigureAwait(false);
        var draft = FlashcardDraftProjection.Create(Id, command.OperationId, bundle, cards.Select(card => (card.cardId, card.front, card.back, card.template)).ToArray());
        RecordStage(context, command, RequiredString(source, "playback"), "awaiting_card_confirmation", bundle);
        return new OperationExecutionResult(StrictJson.CloneElement(new { status = "awaiting_card_confirmation", playback = RequiredString(source, "playback"), pipeline = "awaiting_card_confirmation", attentionRequired = false, frozenLanguageCardChoice = RequiredString(source, "frozenLanguageCardChoice"), languageSettingsSnapshot = source.GetProperty("languageSettingsSnapshot"), languageSelection = source.GetProperty("languageSelection"), cardDraft = draft, expectedCardCount = cards.Count, actualCardCount = cards.Count, ankiExternalWriteCount = 0 }), ReplaceContentRevisionArtifacts(terminal.Artifacts, review, bundle), 0, true);
    }

    private static IReadOnlyList<ArtifactReference> ReplaceContentRevisionArtifacts(
        IReadOnlyList<ArtifactReference> existing,
        params ArtifactReference[] revisionArtifacts)
        => existing.Where(item => item.Kind is not "content-review" and not "card-bundle" and not "anki-readback")
            .Concat(revisionArtifacts)
            .ToArray();

    private static bool IsCardResolutionRequired(JsonElement payload)
        => payload.TryGetProperty("moduleReleaseState", out var state) && state.GetString() == "unresolved";

    private static object CardResolution(JsonElement payload)
    {
        var evidence = payload.GetProperty("evidence");
        return new { targetToken = RequiredString(evidence, "targetToken"), revision = RequiredString(evidence, "inputVersion"), decisionId = RequiredString(evidence, "decisionId"), reasonCode = RequiredString(payload, "reasonCode"), evidence, actions = new[] { "accept_candidate", "user_correction", "cancel" } };
    }

    private static JsonElement RequireCandidateOccurrence(JsonElement commandData, JsonElement prompt)
    {
        Require(commandData.TryGetProperty("candidateOccurrence", out var occurrence) && occurrence.ValueKind == JsonValueKind.Object,
            "Accepted candidate requires a typed candidate occurrence.");
        var segmentId = RequiredString(occurrence, "segmentId");
        var exactText = RequiredString(occurrence, "exactText");
        var sourceSha256 = RequiredString(occurrence, "sourceSha256");
        var order = -1;
        var start = -1;
        var end = -1;
        Require(occurrence.TryGetProperty("order", out var orderValue) && orderValue.TryGetInt32(out order),
            "Candidate occurrence order is invalid.");
        Require(occurrence.TryGetProperty("start", out var startValue) && startValue.TryGetInt32(out start),
            "Candidate occurrence start is invalid.");
        Require(occurrence.TryGetProperty("end", out var endValue) && endValue.TryGetInt32(out end),
            "Candidate occurrence end is invalid.");
        Require(order >= 0 && start >= 0 && end > start && sourceSha256.Length == 64 && sourceSha256.All(Uri.IsHexDigit),
            "Candidate occurrence coordinates or source hash are invalid.");
        var candidates = prompt.GetProperty("evidence").GetProperty("candidatePositions");
        Require(candidates.ValueKind == JsonValueKind.Array, "Card resolution evidence lacks candidate positions.");
        var exactMatches = candidates.EnumerateArray().Count(candidate =>
            RequiredString(candidate, "segment_id") == segmentId &&
            candidate.GetProperty("order").GetInt32() == order &&
            candidate.GetProperty("start").GetInt32() == start &&
            candidate.GetProperty("end").GetInt32() == end &&
            RequiredString(candidate, "exact_text") == exactText &&
            string.Equals(RequiredString(candidate, "source_sha256"), sourceSha256, StringComparison.OrdinalIgnoreCase));
        Require(exactMatches == 1, "INPUT_STALE candidate occurrence does not uniquely match the frozen decision evidence.");
        return occurrence.Clone();
    }

    private sealed record SelectionSnapshot(
        IReadOnlyList<ArtifactReference> ContextReferences,
        JsonElement Payload,
        string SpokenText,
        string SpeechContextMode);

    private static async Task<SelectionSnapshot> ReadSelectionAsync(CommandEnvelope command, OperationContext context, ArtifactReference capture, CancellationToken cancellationToken)
    {
        var selectedText = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, capture, cancellationToken).ConfigureAwait(false);
        if (!command.Body.Data.TryGetProperty("selection", out var selection) || selection.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new SelectionSnapshot(
                Array.Empty<ArtifactReference>(),
                StrictJson.CloneElement(new { selectedText, selectedTextSha256 = Hash(selectedText), targetText = selectedText, targetTextSha256 = Hash(selectedText), spokenTextSha256 = Hash(selectedText), speechContextMode = "selection_only", contextRequested = false, contextApplied = false, contextStatus = "off", contextGranularity = "none", sourceWindow = (string?)null, contextSegments = Array.Empty<object>() }),
                selectedText,
                "selection_only");
        var target = RequiredString(selection, "targetText");
        Require(selectedText.Contains(target, StringComparison.Ordinal), "Language target is outside selectedText.");
        var requested = selection.TryGetProperty("contextRequested", out var requestedValue) && requestedValue.ValueKind == JsonValueKind.True;
        var granularity = requested ? RequiredString(selection, "contextGranularity") : "none";
        Require(granularity is "none" or "sentence" or "paragraph", "Language context granularity is unsupported.");
        var encodedSegments = selection.TryGetProperty("contextSegments", out var contextSegments) && contextSegments.ValueKind == JsonValueKind.Array
            ? contextSegments.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        Require(requested || encodedSegments.Length == 0, "Context segments are not allowed when context is off.");
        var refs = new List<ArtifactReference>();
        var segments = new List<object>();
        var prefix = new List<string>();
        var suffix = new List<string>();
        var suffixStarted = false;
        for (var index = 0; index < encodedSegments.Length; index++)
        {
            var encoded = encodedSegments[index];
            Require(encoded.ValueKind == JsonValueKind.Object && encoded.GetProperty("order").GetInt32() == index,
                "Language context segment order is invalid.");
            var role = RequiredString(encoded, "role");
            Require(role is "prefix" or "suffix", "Language context segment role is unsupported.");
            if (role == "suffix") suffixStarted = true;
            Require(!suffixStarted || role == "suffix", "Language context prefix must precede every suffix.");
            var reference = encoded.GetProperty("reference").Deserialize<ArtifactReference>(KernelJson.Options)
                ?? throw new InvalidDataException("Language context artifact reference is invalid.");
            Require(reference.Kind == "question-context", "Language context artifact kind is invalid.");
            Require(!refs.Contains(reference), "Language context artifact is duplicated.");
            var text = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, reference, cancellationToken).ConfigureAwait(false);
            Require(!string.IsNullOrEmpty(text), "Language context segment is empty.");
            refs.Add(reference);
            if (role == "prefix") prefix.Add(text); else suffix.Add(text);
            segments.Add(new { segmentId = reference.ArtifactId, role, order = index, text, sha256 = Hash(text) });
        }
        var applied = requested && refs.Count > 0;
        var spokenText = applied ? string.Concat(prefix) + selectedText + string.Concat(suffix) : selectedText;
        var speechContextMode = applied ? "selection_plus_" + granularity : "selection_only";
        return new SelectionSnapshot(
            refs,
            StrictJson.CloneElement(new
            {
                selectedText,
                selectedTextSha256 = Hash(selectedText),
                targetText = target,
                targetTextSha256 = Hash(target),
                spokenTextSha256 = Hash(spokenText),
                speechContextMode,
                contextRequested = requested,
                contextApplied = applied,
                contextStatus = requested ? applied ? "applied" : "unavailable" : "off",
                contextGranularity = granularity,
                sourceWindow = selection.TryGetProperty("sourceWindow", out var window) && window.ValueKind == JsonValueKind.String ? window.GetString() : null,
                contextSegments = segments
            }),
            spokenText,
            speechContextMode);
    }

    private static async Task<OperationExecutionResult> ReplayAsync(CommandEnvelope command, OperationContext context, CancellationToken cancellationToken)
    {
        Require(command.Inputs.Count == 0, "Read-aloud replay must not introduce new inputs.");
        Require(CardDestinations.HasActionAuthority(command, context), "Read-aloud replay requires operation authority.");
        Require(command.Body.Data.TryGetProperty("sourceOperationId", out var sourceOperationId) &&
                sourceOperationId.ValueKind == JsonValueKind.String &&
                string.Equals(sourceOperationId.GetString(), command.OperationId, StringComparison.Ordinal),
            "Read-aloud replay must target its own durable operation stream.");

        var sourceTerminal = context.EventStore.ReadStream(command.OperationId)
            .LastOrDefault(item => item.EventType is "operation.completed" or "operation.failed" or "operation.cancelled" or "operation.unknown-external-state");
        Require(sourceTerminal is not null && sourceTerminal.Operation == Id && sourceTerminal.EventType == "operation.completed",
            "Read-aloud replay requires a previously completed read-aloud operation.");
        var completedTerminal = sourceTerminal!;
        if (CardDestinations.IsNeutral(command))
            Require(command.Body.Data.TryGetProperty("sourceRevision", out var sourceRevision) && sourceRevision.GetInt64() == completedTerminal.Sequence, "Replay source revision is stale.");
        Require(completedTerminal.Data.TryGetProperty("result", out var sourceResult) &&
                sourceResult.TryGetProperty("pipeline", out var sourcePipeline) &&
                sourceResult.TryGetProperty("playback", out var sourcePlayback) && sourcePlayback.GetString() is "idle_replayable" or "stopped",
            "Read-aloud replay requires verified complete audio and natural/stopped playback.");
        if (context.WorkerRuntime.PlaybackControl is { } playbackControl)
            Require(playbackControl.Snapshot(command.OperationId).GetProperty("state").GetString() is "idle_replayable" or "stopped",
                "Replay refuses active, ambiguous or unknown playback.");
        var pipelineState = sourceResult.GetProperty("pipeline").GetString()!;

        var synthesisArtifacts = completedTerminal.Artifacts.Where(item => item.Kind == "speech-synthesis").ToArray();
        Require(synthesisArtifacts.Length == 1, "Read-aloud replay source is missing its unique synthesis artifact.");
        var synthesisArtifact = synthesisArtifacts[0];
        var synthesisJson = await DomainArtifactFactory.ReadTextAsync(context.ArtifactStore, synthesisArtifact, cancellationToken).ConfigureAwait(false);
        using var synthesisDocument = StrictJson.Parse(Encoding.UTF8.GetBytes(synthesisJson), "Read-aloud replay synthesis artifact");
        var synthesisPayload = synthesisDocument.RootElement;
        Require(synthesisPayload.TryGetProperty("audioArtifact", out var audioArtifactElement),
            "Read-aloud replay synthesis artifact is missing its audio reference.");
        var audioArtifact = audioArtifactElement.Deserialize<LegacyArtifactReference>(KernelJson.Options)
            ?? throw new InvalidDataException("Read-aloud replay audio reference is invalid.");

        var speechBinding = context.WorkerRuntime.GetAdmittedBinding("speech.edge");
        RecordStage(context, command, "playing", pipelineState, synthesisArtifact);
        var playback = await context.WorkerRuntime.ExecuteAsync(new WorkerInvocation(
            command.OperationId,
            speechBinding,
            StrictJson.CloneElement(new
            {
                phase = "playback",
                actionCommandId = command.CommandId,
                synthesis = synthesisArtifact,
                audioArtifact,
                idempotencyKey = PlaybackIdempotency(command.IdempotencyKey)
            }),
            TimeSpan.FromMilliseconds(context.Capability.TimeoutMilliseconds)), cancellationToken).ConfigureAwait(false);
        Require(playback.Status == "completed" && playback.ExternalWriteCount == 0 && playback.ExternalStateKnown,
            "Speech replay did not naturally complete as a known local effect.");
        var playbackArtifact = await DomainArtifactFactory.PublishJsonAsync(context.ArtifactStore, command, "speech-playback", "1.0.0", "kc.speech-playback/1", playback.Payload,
            new[] { synthesisArtifact }, cancellationToken).ConfigureAwait(false);
        var replayState = RequiredString(playback.Payload, "status");
        Require(replayState is "idle_replayable" or "stopped", "Replay terminal must be verified natural/stopped.");
        RecordStage(context, command, replayState, pipelineState, playbackArtifact);

        var artifacts = completedTerminal.Artifacts.Where(item => item.Kind != "speech-playback")
            .Concat(new[] { playbackArtifact })
            .ToArray();
        var replayResult = sourceResult.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
        replayResult["playback"] = StrictJson.CloneElement(replayState);
        replayResult["replayed"] = StrictJson.CloneElement(true);
        replayResult["externalWriteCount"] = StrictJson.CloneElement(0);
        replayResult["ankiExternalWriteCount"] = StrictJson.CloneElement(0);
        return new OperationExecutionResult(
            StrictJson.CloneElement(replayResult), artifacts, ExternalWriteCount: 0, ExternalStateKnown: true);
    }

    private static (KernelEvent Terminal, JsonElement Result, IReadOnlyList<ArtifactReference> Artifacts) GetAwaitingCardDraft(CommandEnvelope command, OperationContext context)
    {
        Require(command.Body.Data.TryGetProperty("sourceOperationId", out var sourceOperationId) && sourceOperationId.ValueKind == JsonValueKind.String &&
                string.Equals(sourceOperationId.GetString(), command.OperationId, StringComparison.Ordinal),
            "Card draft action must target its own durable operation stream.");
        var terminal = context.EventStore.ReadStream(command.OperationId)
            .LastOrDefault(item => item.EventType == "operation.completed");
        Require(terminal is not null && terminal.Operation == Id && terminal.Data.TryGetProperty("result", out var terminalResult) &&
                terminalResult.TryGetProperty("pipeline", out var pipeline) && pipeline.ValueKind == JsonValueKind.String && pipeline.GetString() == "awaiting_card_confirmation" &&
                terminalResult.TryGetProperty("cardDraft", out var draft) && draft.ValueKind == JsonValueKind.Object,
            "Card draft action requires the current immutable awaiting_card_confirmation draft.");
        Require(terminal!.Artifacts.Count(item => item.Kind == "card-bundle") == 1 && terminal.Artifacts.Count(IsAnkiConfiguration) == 1 &&
                terminal.Artifacts.Count(item => item.Kind == "capture-text") == 1,
            "Card draft action source artifacts are incomplete.");
        return (terminal, terminal!.Data.GetProperty("result").Clone(), terminal.Artifacts);
    }

    private static bool IsAnkiConfiguration(ArtifactReference artifact)
        => CardDestinations.IsConfiguration(artifact);

    private static async Task<ArtifactReference> PublishTaskSettingsSnapshotAsync(
        FileArtifactStore store,
        CommandEnvelope command,
        ArtifactReference configuration,
        string configurationJson,
        LanguageSettings languageSettings,
        bool includeSpeech,
        CancellationToken cancellationToken)
    {
        using var document = StrictJson.Parse(Encoding.UTF8.GetBytes(configurationJson), "Anki configuration");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("deck_routes", out var routes) || routes.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Anki configuration must define deck_routes.");
        var deck = RequiredString(routes, "canary");
        Require(deck == "零碎知识剪藏::Canary" || deck.StartsWith("零碎知识剪藏::Canary::", StringComparison.Ordinal),
            "Read-to-Anki destination must remain inside the isolated Canary deck namespace.");
        var deckSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deck))).ToLowerInvariant();
        return await DomainArtifactFactory.PublishJsonAsync(
            store,
            command,
            "task-settings-snapshot",
            "1.0.0",
            "kc.read-to-anki-task-settings-snapshot/1",
            new
            {
                schema = "kc.read-to-anki-task-settings-snapshot/1",
                effectiveAt = "task_creation",
                sourceOperationId = command.OperationId,
                languageCards = languageSettings.Snapshot,
                includeSpeech,
                destination = new { routeKey = "canary", deckIdentity = deck, deckSha256 },
                ankiConfiguration = new
                {
                    artifactId = configuration.ArtifactId,
                    manifestSha256 = configuration.ManifestDigest.Value
                }
            },
            new[] { configuration },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ArtifactReference> PublishTaskContextSnapshotAsync(
        FileArtifactStore store,
        CommandEnvelope command,
        ArtifactReference capture,
        SelectionSnapshot selection,
        CancellationToken cancellationToken)
    {
        var payload = selection.Payload;
        var projectedSegments = payload.GetProperty("contextSegments").EnumerateArray()
            .Select((segment, index) => new
            {
                order = index,
                role = RequiredString(segment, "role"),
                artifactId = selection.ContextReferences[index].ArtifactId,
                manifestSha256 = selection.ContextReferences[index].ManifestDigest.Value,
                sha256 = RequiredString(segment, "sha256")
            })
            .ToArray();
        return await DomainArtifactFactory.PublishJsonAsync(
            store,
            command,
            "task-context-snapshot",
            "1.0.0",
            "kc.read-to-anki-context-snapshot/1",
            new
            {
                schema = "kc.read-to-anki-context-snapshot/1",
                sourceOperationId = command.OperationId,
                capture = new { artifactId = capture.ArtifactId, manifestSha256 = capture.ManifestDigest.Value },
                selectedTextSha256 = RequiredString(payload, "selectedTextSha256"),
                targetTextSha256 = RequiredString(payload, "targetTextSha256"),
                spokenTextSha256 = RequiredString(payload, "spokenTextSha256"),
                speechContextMode = RequiredString(payload, "speechContextMode"),
                contextRequested = payload.GetProperty("contextRequested").GetBoolean(),
                contextApplied = payload.GetProperty("contextApplied").GetBoolean(),
                contextStatus = RequiredString(payload, "contextStatus"),
                contextGranularity = RequiredString(payload, "contextGranularity"),
                contextSegments = projectedSegments
            },
            new[] { capture }.Concat(selection.ContextReferences).ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    private static ArtifactReference ReadReference(JsonElement data, string name)
        => data.GetProperty(name).Deserialize<ArtifactReference>(KernelJson.Options)
           ?? throw new InvalidDataException($"Read-aloud cards '{name}' is invalid.");

    private static string RequiredString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidDataException($"Read-aloud cards worker result requires '{name}'.");

    private sealed record LanguageSettings(string Revision, string DefaultChoice, JsonElement Snapshot);

    private static LanguageSettings ReadLanguageSettings(JsonElement data)
    {
        if (!data.TryGetProperty("languageCards", out var settings) || settings.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new LanguageSettings("ordinary-default", "ordinary", StrictJson.CloneElement(new { revision = "ordinary-default", defaultChoice = "ordinary" }));
        }
        Require(settings.ValueKind == JsonValueKind.Object, "Language card settings are invalid.");
        var revision = RequiredString(settings, "revision");
        var choice = RequiredString(settings, "defaultChoice");
        Require(choice is "ask" or "meaning-reading" or "writing" or "both", "Language card defaultChoice is unsupported.");
        return new LanguageSettings(revision, choice, StrictJson.CloneElement(new { revision, defaultChoice = choice }));
    }

    private static IReadOnlyList<(string cardId, string front, string back, string template)> ReadCards(JsonElement payload)
    {
        if (payload.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array)
        {
            var projected = cards.EnumerateArray().Select((card, index) => (
                card.TryGetProperty("cardId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : "card-" + index,
                RequiredString(card, "front"), RequiredString(card, "back"),
                card.TryGetProperty("template", out var template) && template.ValueKind == JsonValueKind.String ? template.GetString()! : "basic_qa")).ToArray();
            Require(projected.Length is >= 1 and <= 2, "Language card count must be one or two.");
            return projected;
        }
        return new[] { ("", RequiredString(payload, "front"), RequiredString(payload, "back"), "basic_qa") };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static string StableAnkiIdempotency(ArtifactReference capture, ArtifactReference configuration, string draftDigest)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "clip.read-aloud-cards\n" + capture.ArtifactId + "\n" + capture.ManifestDigest.Value + "\n" + configuration.ArtifactId + "\n" + configuration.ManifestDigest.Value + "\n" + draftDigest)));

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string PlaybackIdempotency(string commandIdempotency)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("speech.playback\n" + commandIdempotency)));

    private static void RecordStage(OperationContext context, CommandEnvelope command, string playback, string pipeline, ArtifactReference? artifact = null)
    {
        var @event = new KernelEvent(
            KernelSchemas.Event,
            "evt-" + Guid.NewGuid().ToString("N"),
            command.OperationId,
            Sequence: 0,
            "read-aloud-cards.stage",
            EventVersion: 1,
            DateTimeOffset.UtcNow,
            command.CommandId,
            command.CommandId,
            command.Operation,
            "external-write",
            StrictJson.CloneElement(new { playback, pipeline, attentionRequired = false }),
            artifact is null ? Array.Empty<ArtifactReference>() : new[] { artifact });
        context.EventStore.Append(command.OperationId, context.EventStore.GetRevision(command.OperationId), new[] { @event });
    }
}
