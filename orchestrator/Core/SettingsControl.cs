using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Orchestrator.Core;

public sealed record WorkflowSettingsDocument(
    string SchemaVersion,
    string ActiveProfileId,
    IReadOnlyDictionary<string, WorkflowSettingsProfile> Profiles);

public sealed record WorkflowSettingsProfile(
    string DisplayName,
    WorkflowRouteSettings? Workflow = null,
    CaptureSettings? Capture = null,
    SpeechSettings? Speech = null,
    RemNoteDestinationSettings? Remnote = null,
    ImageOcrSettings? ImageOcr = null,
    ContentSettingsReference? Content = null);

public sealed record WorkflowRouteSettings(string? ProcessingProfile = null, IReadOnlyList<string>? Destinations = null);
public sealed record CaptureSettings(bool? ContextEnabled = null, string? ContextGranularity = null, string? ImageHandling = null);
public sealed record ImageOcrSettings(
    string SchemaVersion,
    ImageOcrProviderSettings ProviderSettings,
    string? MineruScriptPath = null);
public sealed record ImageOcrProviderSettings(
    string ModelVersion,
    string Language,
    bool EnableTable,
    bool EnableFormula,
    int TimeoutSeconds,
    int PollIntervalSeconds);
public sealed record ContentSettingsReference(string ConfigArtifactRef, string ExpectedSha256, string SettingsRevisionId);
public sealed record SpeechSettings(
    IReadOnlyDictionary<string, string>? VoicesByLanguage = null,
    int? RatePercent = null,
    int? VolumePercent = null,
    int? PitchHz = null,
    bool? AutoPlay = null,
    string? LanguageStrategy = null,
    string? LanguageOverride = null,
    SpeechDeploymentSettings? Deployment = null);
public sealed record SpeechDeploymentSettings(string? EdgeTtsPath = null, string? FfplayPath = null);
public sealed record RemNoteDestinationSettings(
    string SchemaVersion,
    bool Enabled,
    RemNoteTargetSettings Target,
    RemNoteAdvancedSettings Advanced);
public sealed record RemNoteTargetSettings(string? RootRemId, string RootTitle);
public sealed record RemNoteAdvancedSettings(
    string Sink,
    string ClipRouting,
    string CardRouting,
    string TimeZone,
    string DateFormat);

public sealed record TaskSettingsOverride(
    string SchemaVersion,
    WorkflowRouteSettings? Workflow = null,
    CaptureSettings? Capture = null,
    SpeechSettings? Speech = null,
    RemNoteDestinationSettings? Remnote = null,
    ImageOcrSettings? ImageOcr = null,
    ContentSettingsReference? Content = null);

public sealed record TaskSettingsSnapshot(
    string SchemaVersion,
    string TaskId,
    DateTimeOffset FrozenAt,
    TaskSettingsSource Source,
    EffectiveWorkflowSettings Workflow,
    TaskModuleSettings Modules,
    IReadOnlyList<string> SecretReferences);
public sealed record TaskSettingsSource(string ActiveProfileId, string SettingsSha256, IReadOnlyList<string> Precedence);
public sealed record EffectiveWorkflowSettings(string ProcessingProfile, IReadOnlyList<string> Destinations);
public sealed record EffectiveCaptureSettings(bool ContextEnabled, string ContextGranularity, string ImageHandling);
public sealed record EffectiveImageOcrSettings(
    string SchemaVersion,
    ImageOcrProviderSettings ProviderSettings,
    string? MineruScriptPath = null);
public sealed record EffectiveContentSettings(
    string PromptSet,
    string CategoryRegistry,
    string ProviderMode,
    string? ConfigArtifactRef = null,
    string? ExpectedSha256 = null,
    string? SettingsRevisionId = null,
    string? ConfigSchemaVersion = null,
    string? PromptSetSha256 = null,
    string? CategoryRegistrySha256 = null,
    EffectiveContentRuntimePolicy? RuntimePolicy = null);
public sealed record EffectiveContentRuntimePolicy(
    string ProfileId,
    string ProfileVersion,
    string LanguagePracticeDefault,
    string ProviderModel,
    int ProviderMaxRetries);
public sealed record EffectiveSpeechSettings(
    IReadOnlyDictionary<string, string> VoicesByLanguage,
    int RatePercent,
    int VolumePercent,
    int PitchHz,
    bool AutoPlay,
    string LanguageStrategy = "auto",
    string? LanguageOverride = null,
    SpeechDeploymentSettings? Deployment = null);
public sealed record RemNoteDestinationSettingsSnapshot(
    string SchemaVersion,
    string DestinationType,
    bool Enabled,
    RemNoteTargetSettings Target,
    RemNoteAdvancedSettings Advanced,
    IReadOnlyList<string> SecretReferences);
public sealed record EffectiveRemNoteSettings(RemNoteDestinationSettingsSnapshot Settings, JsonElement Route);
public sealed record EffectiveAnkiSettings(string ConfigArtifactRef, string ExpectedSha256, IReadOnlyList<string> SecretReferences);
public sealed record TaskModuleSettings(
    EffectiveCaptureSettings Capture,
    EffectiveContentSettings Content,
    EffectiveSpeechSettings? Speech = null,
    EffectiveRemNoteSettings? Remnote = null,
    EffectiveAnkiSettings? Anki = null,
    EffectiveImageOcrSettings? ImageOcr = null);
public sealed record TaskSettingsBuildResult(TaskSettingsSnapshot Snapshot, ArtifactReference ArtifactReference);
public sealed record WorkflowSettingsLoadResult(WorkflowSettingsDocument Document, string Source, string? BackupPath);

public static partial class SettingsControlValidator
{
    private static readonly HashSet<string> Profiles = new(new[] { "learning", "cards" }, StringComparer.Ordinal);
    private static readonly HashSet<string> Destinations = new(new[] { "remnote", "anki" }, StringComparer.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdPattern();

    public static void Validate(WorkflowSettingsDocument document)
    {
        if (document.SchemaVersion is not ("knowledge-clipper-workflow-settings.v1" or "knowledge-clipper-workflow-settings.v2" or "knowledge-clipper-workflow-settings.v3"))
            Fail("settings.version_unknown", "Workflow settings must use an admitted knowledge-clipper-workflow-settings version.");
        if (document.Profiles.Count == 0 || !document.Profiles.ContainsKey(document.ActiveProfileId))
            Fail("settings.active_profile_missing", "activeProfileId must identify one profile in the same document.");
        foreach (var (id, profile) in document.Profiles)
        {
            if (!ProfileIdPattern().IsMatch(id) || string.IsNullOrWhiteSpace(profile.DisplayName))
                Fail("settings.profile_invalid", $"Invalid workflow profile: {id}");
            if (document.SchemaVersion.EndsWith(".v1", StringComparison.Ordinal) && (profile.ImageOcr is not null || profile.Content is not null))
                Fail("settings.version_unknown", "Image and Content module settings require knowledge-clipper-workflow-settings.v2.");
            if (document.SchemaVersion.EndsWith(".v3", StringComparison.Ordinal) && profile.Speech?.AutoPlay is not null)
                Fail("settings.speech_read_only", "autoPlay is fixed read-only in workflow settings v3.");
            ValidatePartial(profile.Workflow, profile.Capture, profile.Speech, profile.Remnote, profile.ImageOcr, profile.Content);
        }
    }

    public static void Validate(TaskSettingsOverride? taskOverride)
    {
        if (taskOverride is null) return;
        if (taskOverride.SchemaVersion is not ("knowledge-clipper-task-settings-override.v1" or "knowledge-clipper-task-settings-override.v2" or "knowledge-clipper-task-settings-override.v3"))
            Fail("settings.override_version_unknown", "Task override must use an admitted knowledge-clipper-task-settings-override version.");
        if (taskOverride.SchemaVersion.EndsWith(".v1", StringComparison.Ordinal) && (taskOverride.ImageOcr is not null || taskOverride.Content is not null))
            Fail("settings.override_version_unknown", "Image and Content module overrides require knowledge-clipper-task-settings-override.v2.");
        if (taskOverride.SchemaVersion.EndsWith(".v3", StringComparison.Ordinal) && taskOverride.Speech?.AutoPlay is not null)
            Fail("settings.speech_read_only", "autoPlay is fixed read-only in task settings override v3.");
        ValidatePartial(taskOverride.Workflow, taskOverride.Capture, taskOverride.Speech, taskOverride.Remnote, taskOverride.ImageOcr, taskOverride.Content);
    }

    public static void Validate(TaskSettingsSnapshot snapshot)
    {
        if (snapshot.SchemaVersion is not ("knowledge-clipper-task-settings-snapshot.v1" or "knowledge-clipper-task-settings-snapshot.v2" or "knowledge-clipper-task-settings-snapshot.v3") ||
            string.IsNullOrWhiteSpace(snapshot.TaskId) || snapshot.TaskId.Length > 128 ||
            !Regex.IsMatch(snapshot.Source.SettingsSha256, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant) ||
            snapshot.Source.Precedence.Count != 3 ||
            !snapshot.Source.Precedence.SequenceEqual(new[] { "taskOverride", "activeProfile", "moduleDefault" }) ||
            snapshot.SecretReferences.Count != 0)
            Fail("settings.snapshot_invalid", "Task settings snapshot identity, precedence, or secret boundary is invalid.");
        if (snapshot.Modules.Capture.ContextGranularity is not ("sentence" or "paragraph") ||
            snapshot.Modules.Capture.ImageHandling is not ("ocr" or "original") ||
            string.IsNullOrWhiteSpace(snapshot.Modules.Content.PromptSet) || snapshot.Modules.Content.CategoryRegistry != "category-registry/2.0.0" ||
            snapshot.Modules.Content.ProviderMode != "live" ||
            snapshot.Modules.Speech is { } speech &&
                 (speech.VoicesByLanguage.Count == 0 || speech.VoicesByLanguage.Any(item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)) ||
                  !InRange(speech.RatePercent) || !InRange(speech.VolumePercent) || !InRange(speech.PitchHz) ||
                  !ValidSpeechLanguage(speech.LanguageStrategy, speech.LanguageOverride, speech.VoicesByLanguage) ||
                  !ValidOptionalAbsolutePath(speech.Deployment?.EdgeTtsPath) || !ValidOptionalAbsolutePath(speech.Deployment?.FfplayPath)))
            Fail("settings.snapshot_invalid", "Task module settings snapshot contains an unsupported value.");
        if (snapshot.SchemaVersion.EndsWith(".v1", StringComparison.Ordinal) &&
            (snapshot.Modules.ImageOcr is not null || snapshot.Modules.Content.ConfigArtifactRef is not null))
            Fail("settings.snapshot_invalid", "Image and Content module snapshots require knowledge-clipper-task-settings-snapshot.v2.");
        if (!snapshot.SchemaVersion.EndsWith(".v1", StringComparison.Ordinal) &&
            snapshot.Modules.Capture.ImageHandling == "ocr" && snapshot.Modules.ImageOcr is null)
            Fail("settings.image_ocr_missing", "OCR capture requires a frozen Image/OCR module snapshot.");
        if (snapshot.SchemaVersion.EndsWith(".v3", StringComparison.Ordinal) && snapshot.Modules.Speech is { AutoPlay: not true })
            Fail("settings.speech_read_only", "Task settings snapshot v3 must preserve the fixed autoPlay=true contract policy.");
        ValidateEffective(snapshot.Workflow, snapshot.Modules);
    }

    private static void ValidatePartial(
        WorkflowRouteSettings? workflow,
        CaptureSettings? capture,
        SpeechSettings? speech,
        RemNoteDestinationSettings? remnote,
        ImageOcrSettings? imageOcr = null,
        ContentSettingsReference? content = null)
    {
        if (workflow?.ProcessingProfile is string profile && !Profiles.Contains(profile))
            Fail("settings.processing_profile_invalid", $"Unsupported processing profile: {profile}");
        if (workflow?.Destinations is { } destinations &&
            (destinations.Distinct(StringComparer.Ordinal).Count() != destinations.Count || destinations.Any(item => !Destinations.Contains(item))))
            Fail("settings.destinations_invalid", "Destinations must be a unique subset of remnote and anki.");
        if (capture?.ContextGranularity is string granularity && granularity is not ("sentence" or "paragraph"))
            Fail("settings.capture_invalid", "contextGranularity must be sentence or paragraph.");
        if (capture?.ImageHandling is string imageHandling && imageHandling is not ("ocr" or "original"))
            Fail("settings.capture_invalid", "imageHandling must be ocr or original.");
        if (imageOcr is not null) ValidateImageOcr(imageOcr.SchemaVersion, imageOcr.ProviderSettings, imageOcr.MineruScriptPath);
        if (content is not null &&
            (string.IsNullOrWhiteSpace(content.ConfigArtifactRef) ||
             !Regex.IsMatch(content.ExpectedSha256, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant) ||
             !ProfileIdPattern().IsMatch(content.SettingsRevisionId)))
            Fail("settings.content_invalid", "Content settings must identify one SHA-bound content-config-snapshot revision.");
        if (speech is not null)
        {
            if (speech.VoicesByLanguage is { Count: 0 } || speech.VoicesByLanguage?.Any(item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)) == true ||
                !InRange(speech.RatePercent) || !InRange(speech.VolumePercent) || !InRange(speech.PitchHz) ||
                !ValidSpeechLanguage(speech.LanguageStrategy, speech.LanguageOverride, speech.VoicesByLanguage) ||
                !ValidOptionalAbsolutePath(speech.Deployment?.EdgeTtsPath) || !ValidOptionalAbsolutePath(speech.Deployment?.FfplayPath))
                Fail("settings.speech_invalid", "Speech voices must be non-empty and numeric values must be within -100..100.");
        }
        if (remnote is not null)
        {
            if (remnote.SchemaVersion != "remnote-destination-settings.v1" ||
                string.IsNullOrWhiteSpace(remnote.Target.RootTitle) ||
                (remnote.Enabled && string.IsNullOrWhiteSpace(remnote.Target.RootRemId)) ||
                remnote.Advanced.Sink is not ("mcp-direct" or "markdown-zip") ||
                remnote.Advanced.ClipRouting != "clip-daily-route" || remnote.Advanced.CardRouting != "card-export" ||
                remnote.Advanced.TimeZone != "Asia/Shanghai" || remnote.Advanced.DateFormat != "yyyy-MM-dd")
                Fail("settings.remnote_invalid", "RemNote settings do not match the admitted additive destination contract.");
        }
    }

    private static void ValidateEffective(EffectiveWorkflowSettings workflow, TaskModuleSettings modules)
    {
        if (!Profiles.Contains(workflow.ProcessingProfile) ||
            workflow.Destinations.Distinct(StringComparer.Ordinal).Count() != workflow.Destinations.Count ||
            workflow.Destinations.Any(item => !Destinations.Contains(item)))
            Fail("settings.route_invalid", "Resolved workflow route is invalid.");
        if (workflow.Destinations.Contains("anki", StringComparer.Ordinal) && workflow.ProcessingProfile != "cards")
            Fail("settings.route_unsupported", "The anki destination requires the cards processing profile.");
        if (workflow.Destinations.Contains("remnote", StringComparer.Ordinal) != (modules.Remnote is not null) ||
            workflow.Destinations.Contains("anki", StringComparer.Ordinal) != (modules.Anki is not null))
            Fail("settings.snapshot_module_mismatch", "Task snapshot modules do not match the resolved destinations.");
        if (modules.Remnote is not null)
        {
            var settings = modules.Remnote.Settings;
            if (settings.SchemaVersion != "remnote-destination-settings-snapshot.v1" || settings.DestinationType != "remnote" ||
                !settings.Enabled || settings.SecretReferences.Count != 0 || string.IsNullOrWhiteSpace(settings.Target.RootRemId) ||
                string.IsNullOrWhiteSpace(settings.Target.RootTitle) || settings.Advanced.Sink is not ("mcp-direct" or "markdown-zip") ||
                settings.Advanced.ClipRouting != "clip-daily-route" || settings.Advanced.CardRouting != "card-export" ||
                settings.Advanced.TimeZone != "Asia/Shanghai" || settings.Advanced.DateFormat != "yyyy-MM-dd")
                Fail("settings.remnote_invalid", "Selected RemNote settings must be enabled and secret-free.");
            var kind = modules.Remnote.Route.GetProperty("kind").GetString();
            if ((workflow.ProcessingProfile == "learning" &&
                    (kind != "clip-daily-route" || modules.Remnote.Route.EnumerateObject().Count() != 4 ||
                     modules.Remnote.Route.GetProperty("timeZone").GetString() != "Asia/Shanghai" ||
                     modules.Remnote.Route.GetProperty("dateFormat").GetString() != "yyyy-MM-dd" ||
                     !DateOnly.TryParseExact(modules.Remnote.Route.GetProperty("localDate").GetString(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))) ||
                (workflow.ProcessingProfile == "cards" && (kind != "card-export" || modules.Remnote.Route.EnumerateObject().Count() != 1)))
                Fail("settings.remnote_route_invalid", "RemNote date routing is only valid for learning clipping; card export must be date-free.");
        }
        if (modules.Anki is { } anki &&
            (anki.SecretReferences.Count > 0 || string.IsNullOrWhiteSpace(anki.ConfigArtifactRef) || !Regex.IsMatch(anki.ExpectedSha256, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)))
            Fail("settings.anki_invalid", "Anki settings snapshot must contain only a sha256-bound task artifact reference.");
        if (modules.ImageOcr is { } image)
            ValidateImageOcr(image.SchemaVersion, image.ProviderSettings, image.MineruScriptPath);
        var content = modules.Content;
        var contentFields = new[] { content.ConfigArtifactRef, content.ExpectedSha256, content.SettingsRevisionId };
        if (contentFields.Any(item => item is not null) &&
            (contentFields.Any(string.IsNullOrWhiteSpace) || !Regex.IsMatch(content.ExpectedSha256!, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)))
            Fail("settings.content_invalid", "Content module snapshot must contain one complete SHA-bound config reference.");
        if (content.ConfigSchemaVersion is not null)
        {
            if (content.ConfigSchemaVersion is not ("content-config-snapshot/1.0.0" or "content-config-snapshot/1.1.0") ||
                !Regex.IsMatch(content.PromptSetSha256 ?? "", "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(content.CategoryRegistrySha256 ?? "", "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
                Fail("settings.content_invalid", "Content task snapshot does not identify an admitted SHA-bound module config.");
            if (content.ConfigSchemaVersion == "content-config-snapshot/1.1.0" &&
                (content.RuntimePolicy is not { } runtime || !ProfileIdPattern().IsMatch(runtime.ProfileId) ||
                 !Regex.IsMatch(runtime.ProfileVersion, "^\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant) ||
                 runtime.LanguagePracticeDefault is not ("ask" or "meaning-reading" or "writing" or "both") ||
                 runtime.ProviderModel is not ("deepseek-chat" or "deepseek-reasoner") || runtime.ProviderMaxRetries is < 1 or > 5))
                Fail("settings.content_invalid", "Content config snapshot 1.1 runtimePolicy is invalid.");
            if (content.ConfigSchemaVersion == "content-config-snapshot/1.0.0" && content.RuntimePolicy is not null)
                Fail("settings.content_invalid", "Content config snapshot 1.0 cannot carry the additive 1.1 runtime policy.");
        }
    }

    private static void ValidateImageOcr(string schemaVersion, ImageOcrProviderSettings provider, string? mineruScriptPath)
    {
        if (schemaVersion != "image-ocr-settings.v1" || provider.ModelVersion is not ("vlm" or "pipeline" or "MinerU-HTML") ||
            !Regex.IsMatch(provider.Language, "^[A-Za-z][A-Za-z0-9_-]{0,31}$", RegexOptions.CultureInvariant) ||
            provider.TimeoutSeconds is < 30 or > 3600 || provider.PollIntervalSeconds is < 1 or > 60 ||
            provider.PollIntervalSeconds >= provider.TimeoutSeconds ||
            mineruScriptPath is not null && !Path.IsPathRooted(mineruScriptPath))
            Fail("settings.image_ocr_invalid", "Image/OCR settings do not match the admitted module adapter.");
    }

    private static bool InRange(int? value) => value is null or >= -100 and <= 100;
    private static bool ValidOptionalAbsolutePath(string? path) => path is null || Path.IsPathFullyQualified(path);
    private static bool ValidSpeechLanguage(string? strategy, string? languageOverride, IReadOnlyDictionary<string, string>? voices)
    {
        strategy ??= "auto";
        if (strategy is not ("auto" or "override")) return false;
        if (strategy == "auto") return languageOverride is null;
        if (languageOverride is null || !Regex.IsMatch(languageOverride, "^[a-z]{2}-[A-Z]{2}$", RegexOptions.CultureInvariant)) return false;
        return voices is null || voices.ContainsKey(languageOverride);
    }
    private static void Fail(string code, string message) => throw new OrchestratorDispatchException(code, message);
}

public sealed class WorkflowSettingsStore
{
    private static readonly JsonSerializerOptions StrictOptions = new(ContractJson.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<WorkflowSettingsLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        try
        {
            return new WorkflowSettingsLoadResult(await ReadAsync(path, cancellationToken), "primary", null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or OrchestratorDispatchException)
        {
            var backupRoot = BackupRoot(path);
            if (!Directory.Exists(backupRoot)) throw;
            foreach (var backup in Directory.GetFiles(backupRoot, Path.GetFileName(path) + ".*.bak.json").OrderDescending())
            {
                try
                {
                    return new WorkflowSettingsLoadResult(await ReadAsync(backup, cancellationToken), "backup", backup);
                }
                catch (Exception backupException) when (backupException is JsonException or IOException or OrchestratorDispatchException) { }
            }
            throw;
        }
    }

    public async Task<string?> SaveAsync(string path, WorkflowSettingsDocument document, CancellationToken cancellationToken = default)
    {
        SettingsControlValidator.Validate(document);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string? backup = null;
        if (File.Exists(path))
        {
            var backupRoot = BackupRoot(path);
            Directory.CreateDirectory(backupRoot);
            backup = Path.Combine(backupRoot, $"{Path.GetFileName(path)}.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.{Guid.NewGuid():N}.bak.json");
            File.Copy(path, backup, false);
        }
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await JsonSerializer.SerializeAsync(stream, document, ContractJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return backup;
    }

    private static async Task<WorkflowSettingsDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<WorkflowSettingsDocument>(stream, StrictOptions, cancellationToken)
            ?? throw new JsonException("Workflow settings document is empty.");
        SettingsControlValidator.Validate(document);
        return document;
    }

    private static string BackupRoot(string path) => Path.Combine(Path.GetDirectoryName(path)!, "backups");
}

public sealed class TaskSettingsAdapter
{
    private static readonly JsonSerializerOptions StrictOptions = new(ContractJson.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<TaskSettingsBuildResult> BuildAndPublishAsync(
        string jobRoot,
        string taskId,
        string intent,
        WorkflowSettingsDocument settings,
        TaskSettingsOverride? taskOverride,
        DestinationNeutralResolverConfiguration resolverDefaults,
        IReadOnlyList<ArtifactReference> currentTaskInputs,
        DateTimeOffset frozenAt,
        CancellationToken cancellationToken = default)
    {
        SettingsControlValidator.Validate(settings);
        SettingsControlValidator.Validate(taskOverride);
        if (resolverDefaults.SchemaVersion != "knowledge-clipper-profile-resolver.v2.1")
            throw new OrchestratorDispatchException("settings.resolver_version_unsupported", "Settings control v1 requires the admitted v2.1 resolver defaults.");
        var profile = settings.Profiles[settings.ActiveProfileId];
        var workflow = MergeWorkflow(resolverDefaults, profile.Workflow, taskOverride?.Workflow);
        if (intent is not ("clip.card" or "clip.read-aloud" or "clip.question"))
            throw new OrchestratorDispatchException("settings.intent_unsupported", $"No settings adapter is admitted for intent: {intent}");
        if (intent == "clip.question" && workflow.ProcessingProfile != "cards")
            throw new OrchestratorDispatchException("settings.question_route_unsupported", "Question settings require the cards processing profile; frozen destinations are applied only after question.make-cards.");
        var capture = MergeCapture(profile.Capture, taskOverride?.Capture);
        var imageOcr = MergeImageOcr(profile.ImageOcr, taskOverride?.ImageOcr);
        var speech = MergeSpeech(resolverDefaults.Speech, profile.Speech, taskOverride?.Speech);
        var remnote = taskOverride?.Remnote ?? profile.Remnote ?? DefaultRemNote(resolverDefaults.RemNoteExport);
        var destinations = workflow.Destinations.ToArray();
        EffectiveRemNoteSettings? remnoteSnapshot = null;
        if (destinations.Contains("remnote", StringComparer.Ordinal))
        {
            SettingsControlValidator.Validate(new TaskSettingsOverride("knowledge-clipper-task-settings-override.v1", Remnote: remnote));
            if (!remnote.Enabled || string.IsNullOrWhiteSpace(remnote.Target.RootRemId))
                throw new OrchestratorDispatchException("settings.remnote_conditional_unavailable", "The remnote destination requires enabled settings and an exact rootRemId.");
            var route = workflow.ProcessingProfile == "learning"
                ? JsonSerializer.SerializeToElement(new { kind = remnote.Advanced.ClipRouting, timeZone = remnote.Advanced.TimeZone, localDate = ShanghaiDate(frozenAt), dateFormat = remnote.Advanced.DateFormat }, ContractJson.Options)
                : JsonSerializer.SerializeToElement(new { kind = remnote.Advanced.CardRouting }, ContractJson.Options);
            remnoteSnapshot = new EffectiveRemNoteSettings(
                new RemNoteDestinationSettingsSnapshot("remnote-destination-settings-snapshot.v1", "remnote", true, remnote.Target, remnote.Advanced, Array.Empty<string>()),
                route);
        }
        var ankiReference = currentTaskInputs.SingleOrDefault(item => item.Kind == "anki-export-config");
        EffectiveAnkiSettings? ankiSnapshot = null;
        if (destinations.Contains("anki", StringComparer.Ordinal))
        {
            if (workflow.ProcessingProfile != "cards")
                throw new OrchestratorDispatchException("settings.route_unsupported", "The anki destination requires cards.");
            if (ankiReference is null)
                throw new OrchestratorDispatchException("settings.anki_conditional_unavailable", "The anki destination requires one sha256-bound anki-export-config task artifact.");
            ankiSnapshot = new EffectiveAnkiSettings(ankiReference.ArtifactRef, ankiReference.ExpectedSha256.ToLowerInvariant(), Array.Empty<string>());
        }
        var contentSelection = taskOverride?.Content ?? profile.Content;
        var contentReference = currentTaskInputs.SingleOrDefault(item => item.Kind == "content-config-snapshot");
        var contentSnapshot = new EffectiveContentSettings("open-md-v2", "category-registry/2.0.0", "live");
        if (contentSelection is not null)
        {
            if (contentReference is null || contentReference.ArtifactRef != contentSelection.ConfigArtifactRef ||
                !contentReference.ExpectedSha256.Equals(contentSelection.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new OrchestratorDispatchException("settings.content_snapshot_mismatch", "Selected Content settings do not match the SHA-bound task artifact.");
            var contentManifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, contentReference, cancellationToken);
            var entrypoint = contentManifest.Metadata.TryGetProperty("entrypoint", out var entrypointElement) ? entrypointElement.GetString() : null;
            var entryFile = contentManifest.Files.SingleOrDefault(item => item.Path == entrypoint)
                ?? throw new OrchestratorDispatchException("settings.content_invalid", "Content config artifact does not declare its entrypoint.");
            using var contentDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(PathPolicy.ResolveJobRelative(jobRoot, entryFile.Path), cancellationToken));
            var contentRoot = contentDocument.RootElement;
            var configSchemaVersion = contentRoot.TryGetProperty("schemaVersion", out var configVersionElement) ? configVersionElement.GetString() : null;
            var revision = contentRoot.TryGetProperty("settingsRevisionId", out var revisionElement) ? revisionElement.GetString() : null;
            if (configSchemaVersion is not ("content-config-snapshot/1.0.0" or "content-config-snapshot/1.1.0") || revision != contentSelection.SettingsRevisionId)
                throw new OrchestratorDispatchException("settings.content_snapshot_mismatch", "Content settings revision does not match the selected config artifact.");
            var promptSet = RequiredContentObject(contentRoot, "promptSet");
            var categoryRegistry = RequiredContentObject(contentRoot, "categoryRegistry");
            var providerBoundary = RequiredContentObject(contentRoot, "providerBoundary");
            var promptSetId = RequiredContentText(promptSet, "id");
            var promptSetSha256 = RequiredContentSha256(promptSet, "sha256");
            var categoryRegistryId = RequiredContentText(categoryRegistry, "id");
            var categoryRegistrySha256 = RequiredContentSha256(categoryRegistry, "sha256");
            if (categoryRegistryId != "category-registry/2.0.0" ||
                RequiredContentText(providerBoundary, "provider") != "deepseek" ||
                RequiredContentText(providerBoundary, "credentials") != "process-injected-only")
                throw new OrchestratorDispatchException("settings.content_invalid", "Content config does not preserve the admitted category/provider/credential boundary.");
            EffectiveContentRuntimePolicy? runtimePolicy = null;
            if (configSchemaVersion == "content-config-snapshot/1.1.0")
            {
                var runtime = RequiredContentObject(contentRoot, "runtimePolicy");
                var provider = RequiredContentObject(runtime, "provider");
                runtimePolicy = new EffectiveContentRuntimePolicy(
                    RequiredContentText(runtime, "profileId"),
                    RequiredContentText(runtime, "profileVersion"),
                    RequiredContentText(runtime, "languagePracticeDefault"),
                    RequiredContentText(provider, "model"),
                    provider.TryGetProperty("maxRetries", out var retries) && retries.TryGetInt32(out var maxRetries)
                        ? maxRetries
                        : throw new OrchestratorDispatchException("settings.content_invalid", "Content runtimePolicy.provider.maxRetries is invalid."));
            }
            contentSnapshot = new EffectiveContentSettings(promptSetId, categoryRegistryId, "live",
                contentReference.ArtifactRef, contentReference.ExpectedSha256.ToLowerInvariant(), revision,
                configSchemaVersion, promptSetSha256, categoryRegistrySha256, runtimePolicy);
        }
        else if (contentReference is not null)
        {
            throw new OrchestratorDispatchException("settings.content_selection_missing", "A content-config-snapshot task artifact requires an explicit v2 settings selection.");
        }
        var sourceBytes = JsonSerializer.SerializeToUtf8Bytes(settings, ContractJson.Options);
        var snapshot = new TaskSettingsSnapshot(
            settings.SchemaVersion.EndsWith(".v3", StringComparison.Ordinal) ? "knowledge-clipper-task-settings-snapshot.v3" :
            settings.SchemaVersion.EndsWith(".v2", StringComparison.Ordinal) ? "knowledge-clipper-task-settings-snapshot.v2" : "knowledge-clipper-task-settings-snapshot.v1", taskId, frozenAt,
            new TaskSettingsSource(settings.ActiveProfileId, Integrity.Sha256Hex(sourceBytes).ToLowerInvariant(), new[] { "taskOverride", "activeProfile", "moduleDefault" }),
            workflow,
            new TaskModuleSettings(capture, contentSnapshot, intent == "clip.read-aloud" ? speech : null, remnoteSnapshot, ankiSnapshot,
                !settings.SchemaVersion.EndsWith(".v1", StringComparison.Ordinal) && capture.ImageHandling == "ocr" ? imageOcr : null),
            Array.Empty<string>());
        SettingsControlValidator.Validate(snapshot);
        var snapshotInputs = new List<ArtifactReference>();
        if (ankiReference is not null) snapshotInputs.Add(ankiReference);
        if (contentReference is not null) snapshotInputs.Add(contentReference);
        var reference = await PublishSnapshotAsync(jobRoot, snapshot, snapshotInputs, cancellationToken);
        return new TaskSettingsBuildResult(snapshot, reference);
    }

    public async Task<TaskSettingsSnapshot> ReadSnapshotAsync(string jobRoot, ArtifactReference reference, CancellationToken cancellationToken = default)
    {
        if (reference.Kind != "task-settings-snapshot" || reference.ContractVersion != "1.0.0")
            throw new OrchestratorDispatchException("settings.snapshot_reference_invalid", "Task settings snapshot reference identity is invalid.");
        var manifest = await CoreArtifactIO.ReadManifestAsync(jobRoot, reference, cancellationToken);
        var entrypoint = manifest.Metadata.GetProperty("entrypoint").GetString();
        var file = manifest.Files.Single(item => item.Path == entrypoint);
        await using var stream = File.OpenRead(PathPolicy.ResolveJobRelative(jobRoot, file.Path));
        var snapshot = await JsonSerializer.DeserializeAsync<TaskSettingsSnapshot>(stream, StrictOptions, cancellationToken)
            ?? throw new OrchestratorDispatchException("settings.snapshot_invalid", "Task settings snapshot is empty.");
        SettingsControlValidator.Validate(snapshot);
        return snapshot;
    }

    public async Task<string> MigrateLegacyManifestV2Async(
        string jobRoot,
        TaskSettingsBuildResult settings,
        CancellationToken cancellationToken = default)
    {
        jobRoot = Path.GetFullPath(jobRoot);
        var legacyPath = Path.Combine(jobRoot, "task.manifest.v1.json");
        using var legacyDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(legacyPath, cancellationToken));
        var legacy = legacyDocument.RootElement;
        if (legacy.GetProperty("schemaVersion").GetString() != "1.0.0" || legacy.GetProperty("taskId").GetString() != settings.Snapshot.TaskId)
            throw new OrchestratorDispatchException("settings.legacy_manifest_invalid", "Legacy task manifest identity does not match the settings snapshot.");
        var inputRefs = legacy.GetProperty("inputRefs").EnumerateArray().Select(item => (object)item.Clone()).ToList();
        inputRefs.Add(new
        {
            artifactRef = settings.ArtifactReference.ArtifactRef,
            kind = settings.ArtifactReference.Kind,
            contractVersion = settings.ArtifactReference.ContractVersion,
            expectedSha256 = settings.ArtifactReference.ExpectedSha256.ToLowerInvariant()
        });
        var canonicalPath = Path.Combine(jobRoot, "task.manifest.v2.json");
        await AtomicFile.WriteJsonOnceAsync(canonicalPath, new
        {
            schemaVersion = "2.0.0",
            taskId = settings.Snapshot.TaskId,
            createdAt = legacy.GetProperty("createdAt").GetDateTimeOffset(),
            createdBy = new { application = "KnowledgeClipper", version = "2.4.3" },
            intent = legacy.GetProperty("intent").GetString(),
            executionProfile = settings.Snapshot.Workflow.ProcessingProfile,
            destinations = settings.Snapshot.Workflow.Destinations,
            inputRefs,
            requestedOutputs = legacy.GetProperty("requestedOutputs").Clone(),
            initialPlanRef = legacy.GetProperty("initialPlanRef").GetString(),
            retentionClass = legacy.GetProperty("retentionClass").GetString(),
            metadata = new { migratedFrom = "task.manifest.v1.json", settingsSnapshotArtifactRef = settings.ArtifactReference.ArtifactRef }
        }, cancellationToken);
        return canonicalPath;
    }

    private static EffectiveWorkflowSettings MergeWorkflow(DestinationNeutralResolverConfiguration defaults, WorkflowRouteSettings? profile, WorkflowRouteSettings? task) =>
        new(task?.ProcessingProfile ?? profile?.ProcessingProfile ?? defaults.DefaultProcessingProfile,
            (task?.Destinations ?? profile?.Destinations ?? defaults.DefaultDestinations).ToArray());

    private static EffectiveCaptureSettings MergeCapture(CaptureSettings? profile, CaptureSettings? task) =>
        new(task?.ContextEnabled ?? profile?.ContextEnabled ?? false,
            task?.ContextGranularity ?? profile?.ContextGranularity ?? "sentence",
            task?.ImageHandling ?? profile?.ImageHandling ?? "ocr");

    private static EffectiveImageOcrSettings MergeImageOcr(ImageOcrSettings? profile, ImageOcrSettings? task)
    {
        var selected = task ?? profile;
        return selected is null
            ? new EffectiveImageOcrSettings("image-ocr-settings.v1", new ImageOcrProviderSettings("vlm", "ch", true, true, 900, 5))
            : new EffectiveImageOcrSettings(selected.SchemaVersion, selected.ProviderSettings, selected.MineruScriptPath);
    }

    private static EffectiveSpeechSettings MergeSpeech(SpeechProfile defaults, SpeechSettings? profile, SpeechSettings? task)
    {
        var voices = task?.VoicesByLanguage ?? profile?.VoicesByLanguage ?? defaults.VoicesByLanguage;
        var strategy = task?.LanguageStrategy ?? profile?.LanguageStrategy ?? "auto";
        var languageOverride = task?.LanguageOverride ?? profile?.LanguageOverride;
        return new EffectiveSpeechSettings(voices,
            task?.RatePercent ?? profile?.RatePercent ?? defaults.RatePercent,
            task?.VolumePercent ?? profile?.VolumePercent ?? defaults.VolumePercent,
            task?.PitchHz ?? profile?.PitchHz ?? defaults.PitchHz,
            task?.AutoPlay ?? profile?.AutoPlay ?? true,
            strategy,
            languageOverride,
            new SpeechDeploymentSettings(
                task?.Deployment?.EdgeTtsPath ?? profile?.Deployment?.EdgeTtsPath ?? defaults.EdgeTtsPath,
                task?.Deployment?.FfplayPath ?? profile?.Deployment?.FfplayPath ?? defaults.FfplayPath));
    }

    private static RemNoteDestinationSettings DefaultRemNote(RemNoteExportProfile? profile) => new(
        "remnote-destination-settings.v1", profile is not null,
        new RemNoteTargetSettings(profile?.RootRemId, profile?.RootTitle ?? "剪藏"),
        new RemNoteAdvancedSettings(profile?.Sink ?? "markdown-zip", "clip-daily-route", "card-export", "Asia/Shanghai", "yyyy-MM-dd"));

    private static string ShanghaiDate(DateTimeOffset instant)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        return TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static JsonElement RequiredContentObject(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new OrchestratorDispatchException("settings.content_invalid", $"Content config object is missing: {property}");

    private static string RequiredContentText(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new OrchestratorDispatchException("settings.content_invalid", $"Content config text is missing: {property}");

    private static string RequiredContentSha256(JsonElement root, string property)
    {
        var value = RequiredContentText(root, property);
        if (!Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new OrchestratorDispatchException("settings.content_invalid", $"Content config SHA-256 is invalid: {property}");
        return value.ToLowerInvariant();
    }

    private static async Task<ArtifactReference> PublishSnapshotAsync(
        string jobRoot,
        TaskSettingsSnapshot snapshot,
        IReadOnlyList<ArtifactReference> inputRefs,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, ContractJson.Options);
        var digest = Integrity.Sha256Hex(payload).ToLowerInvariant();
        var artifactId = $"task-settings-snapshot-{digest[..24]}";
        var root = $"artifacts/{artifactId}";
        var payloadRelative = $"{root}/task-settings-snapshot.v1.json";
        await WriteBytesOnceOrVerifyAsync(PathPolicy.ResolveJobRelative(jobRoot, payloadRelative), payload, cancellationToken);
        var manifestRelative = $"{root}/artifact.json";
        var manifestPath = PathPolicy.ResolveJobRelative(jobRoot, manifestRelative);
        if (!File.Exists(manifestPath))
        {
            await AtomicFile.WriteJsonOnceAsync(manifestPath, new
            {
                schemaVersion = "1.0.0", artifactId, kind = "task-settings-snapshot", contractVersion = "1.0.0",
                mediaType = "application/vnd.knowledge-clipper.task-settings-snapshot+json",
                files = new[] { new { path = payloadRelative, sha256 = digest, bytes = payload.LongLength, mediaType = "application/json" } },
                createdAt = snapshot.FrozenAt,
                producer = new { stageId = "task-settings", workerId = "orchestrator.settings-adapter", workerVersion = "1.0.0", attemptId = $"settings-{artifactId}" },
                inputRefs, dataClass = "sensitive",
                metadata = new { entrypoint = payloadRelative, sourceSettingsSha256 = snapshot.Source.SettingsSha256 }
            }, cancellationToken);
        }
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var reference = new ArtifactReference(artifactId, "task-settings-snapshot", "1.0.0", manifestRelative, Integrity.Sha256Hex(manifestBytes).ToLowerInvariant());
        await CoreArtifactIO.ValidateReferenceAsync(jobRoot, reference, cancellationToken);
        return reference;
    }

    private static async Task WriteBytesOnceOrVerifyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            if (!(await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan().SequenceEqual(bytes))
                throw new IOException($"Append-only settings snapshot conflict: {path}");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class ModuleSettingsActionResolver
{
    public static PipelinePlan ResolveAnkiProbe(string taskId, ArtifactReference configReference, WorkerRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(taskId) || configReference.Kind != "anki-export-config" || configReference.ContractVersion != "1.0.0" ||
            !Regex.IsMatch(configReference.ExpectedSha256, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new OrchestratorDispatchException("settings.anki_probe_input_invalid", "Anki probe requires one SHA-bound anki-export-config artifact.");
        var worker = registry.Workers.SingleOrDefault(item => item.WorkerId == "export.anki" && item.EnabledByDefault)
            ?? throw new OrchestratorDispatchException("settings.anki_probe_unavailable", "The authoritative registry does not admit export.anki.");
        if (!worker.Stages.Contains("anki-export", StringComparer.Ordinal) ||
            !worker.InputKinds.Contains("anki-export-config", StringComparer.Ordinal) ||
            !worker.OutputKinds.Contains("anki-connection-status", StringComparer.Ordinal))
            throw new OrchestratorDispatchException("settings.anki_probe_unavailable", "The admitted export.anki worker does not expose the read-only probe capability.");
        return new PipelinePlan(
            "2.1.0", "2.5.0", taskId, 1,
            new[]
            {
                new PipelineStage(
                    "anki-probe", "export.anki", "probe", Array.Empty<string>(), new[] { configReference },
                    new[] { "anki-connection-status" }, 30_000, new RetryPolicy(1, 0),
                    JsonSerializer.SerializeToElement(new { ankiConfigArtifactId = configReference.ArtifactRef }, ContractJson.Options),
                    JsonSerializer.SerializeToElement(new { }, ContractJson.Options))
            },
            new PipelineRoutingSnapshot("cards", Array.Empty<string>(), "explicit-module-action"));
    }

    public static PipelinePlan ResolveAnkiManagedInitialization(
        string taskId,
        ArtifactReference configReference,
        JsonElement sideEffectAuthorization,
        WorkerRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(taskId) || configReference.Kind != "anki-export-config" || configReference.ContractVersion != "1.0.0" ||
            !Regex.IsMatch(configReference.ExpectedSha256, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new OrchestratorDispatchException("settings.anki_initialize_input_invalid", "Managed initialization requires one SHA-bound anki-export-config artifact.");
        if (sideEffectAuthorization.ValueKind != JsonValueKind.Object ||
            !sideEffectAuthorization.TryGetProperty("schemaVersion", out var schema) || schema.GetString() != "1.0.0" ||
            !sideEffectAuthorization.TryGetProperty("authorizationId", out var authorizationId) || string.IsNullOrWhiteSpace(authorizationId.GetString()) ||
            !sideEffectAuthorization.TryGetProperty("mode", out var mode) || mode.GetString() != "explicit-user-action" ||
            !sideEffectAuthorization.TryGetProperty("operation", out var operation) || operation.GetString() != "initialize-managed" ||
            !sideEffectAuthorization.TryGetProperty("scope", out var scope) || scope.GetString() != "managed-models-and-decks" ||
            !sideEffectAuthorization.TryGetProperty("taskId", out var authorizedTaskId) || authorizedTaskId.GetString() != taskId ||
            !sideEffectAuthorization.TryGetProperty("configArtifactId", out var configArtifactId) || configArtifactId.GetString() != configReference.ArtifactRef ||
            !sideEffectAuthorization.TryGetProperty("authorizedAt", out var authorizedAt) || authorizedAt.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(authorizedAt.GetString(), out _))
            throw new OrchestratorDispatchException("settings.anki_initialize_authorization_invalid", "Managed initialization requires a current explicit authorization bound to this task and config artifact.");
        var worker = registry.Workers.SingleOrDefault(item => item.WorkerId == "export.anki" && item.EnabledByDefault)
            ?? throw new OrchestratorDispatchException("settings.anki_initialize_unavailable", "The authoritative registry does not admit export.anki.");
        if (!worker.Stages.Contains("anki-initialize-managed", StringComparer.Ordinal) ||
            !worker.InputKinds.Contains("anki-export-config", StringComparer.Ordinal) ||
            !worker.OutputKinds.Contains("anki-managed-initialization-status", StringComparer.Ordinal))
            throw new OrchestratorDispatchException("settings.anki_initialize_unavailable", "The admitted export.anki worker does not expose managed initialization.");
        return new PipelinePlan(
            "2.1.0", "2.8.0", taskId, 1,
            new[] { new PipelineStage("anki-initialize-managed", "export.anki", "initialize-managed", Array.Empty<string>(), new[] { configReference },
                new[] { "anki-managed-initialization-status" }, 60_000, new RetryPolicy(1, 0),
                JsonSerializer.SerializeToElement(new { ankiConfigArtifactId = configReference.ArtifactRef }, ContractJson.Options),
                JsonSerializer.SerializeToElement(new { sideEffectAuthorization }, ContractJson.Options)) },
            new PipelineRoutingSnapshot("cards", Array.Empty<string>(), "explicit-module-action"));
    }
}
