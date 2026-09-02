#Requires -Version 7.0

param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$ReleaseRoot,
    [Parameter(Mandatory = $true)][string]$JobRoot,
    [Parameter(Mandatory = $true)][string]$InputJson
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$JobRoot = [IO.Path]::GetFullPath($JobRoot)
$InputJson = [IO.Path]::GetFullPath($InputJson)
$assemblyPath = Join-Path $ProjectRoot 'orchestrator\bin\Release\net8.0\KnowledgeClipper.Orchestrator.dll'
if (-not (Test-Path -LiteralPath $assemblyPath)) { throw "Core assembly missing: $assemblyPath" }
[Reflection.Assembly]::LoadFrom($assemblyPath) | Out-Null

function New-JsonElement {
    param([Parameter(Mandatory = $true)]$Value)
    $text = $Value | ConvertTo-Json -Compress -Depth 32
    $document = [Text.Json.JsonDocument]::Parse($text)
    try { return $document.RootElement.Clone() }
    finally { $document.Dispose() }
}

function Read-Attempt {
    param([Parameter(Mandatory = $true)][string]$RunId)
    $runRoot = Join-Path $JobRoot ('runs\' + $RunId)
    $requests = @(Get-ChildItem -LiteralPath $runRoot -Filter 'request.json' -File -Recurse)
    $results = @(Get-ChildItem -LiteralPath $runRoot -Filter 'result.json' -File -Recurse)
    if ($requests.Count -ne 1 -or $results.Count -ne 1) { throw "Expected one request/result for $RunId" }
    return [pscustomobject]@{
        RequestPath = $requests[0].FullName
        ResultPath = $results[0].FullName
        Request = Get-Content -Raw -LiteralPath $requests[0].FullName | ConvertFrom-Json -Depth 64
        Result = Get-Content -Raw -LiteralPath $results[0].FullName | ConvertFrom-Json -Depth 64
    }
}

$inputValue = Get-Content -Raw -LiteralPath $InputJson | ConvertFrom-Json -Depth 64
if ($inputValue.schemaVersion -ne 'export.anki.core-settings-release-canary.input.v1') { throw 'Canary input schema mismatch.' }

$configRef = [KnowledgeClipper.Orchestrator.Core.ArtifactReference]::new(
    [string]$inputValue.configRef.artifactRef,
    [string]$inputValue.configRef.kind,
    [string]$inputValue.configRef.contractVersion,
    [string]$inputValue.configRef.manifestPath,
    [string]$inputValue.configRef.expectedSha256)
$cardRef = [KnowledgeClipper.Orchestrator.Core.ArtifactReference]::new(
    [string]$inputValue.cardRef.artifactRef,
    [string]$inputValue.cardRef.kind,
    [string]$inputValue.cardRef.contractVersion,
    [string]$inputValue.cardRef.manifestPath,
    [string]$inputValue.cardRef.expectedSha256)

$destinations = [string[]]@('anki')
$workflow = [KnowledgeClipper.Orchestrator.Core.WorkflowRouteSettings]::new('cards', $destinations)
$capture = [KnowledgeClipper.Orchestrator.Core.CaptureSettings]::new($false, 'sentence', 'ocr')
$voices = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
$voices.Add('zh-CN', 'zh-CN-XiaoxiaoNeural')
$speech = [KnowledgeClipper.Orchestrator.Core.SpeechSettings]::new($voices, 0, 0, 0, $false)
$profile = [KnowledgeClipper.Orchestrator.Core.WorkflowSettingsProfile]::new('Anki release canary', $workflow, $capture, $speech, $null)
$profiles = [Collections.Generic.Dictionary[string,KnowledgeClipper.Orchestrator.Core.WorkflowSettingsProfile]]::new([StringComparer]::Ordinal)
$profiles.Add('anki-release-canary', $profile)
$settings = [KnowledgeClipper.Orchestrator.Core.WorkflowSettingsDocument]::new(
    'knowledge-clipper-workflow-settings.v1', 'anki-release-canary', $profiles)
$resolverPath = Join-Path $ProjectRoot 'contracts\core\v2.1\profile-resolver.v2.1.json'
$resolver = [KnowledgeClipper.Orchestrator.Core.DesktopIntentResolver]::LoadConfiguration($resolverPath)
$adapter = [KnowledgeClipper.Orchestrator.Core.TaskSettingsAdapter]::new()
$inputs = [KnowledgeClipper.Orchestrator.Core.ArtifactReference[]]@($configRef, $cardRef)
$frozenAt = [DateTimeOffset]::Parse([string]$inputValue.frozenAt, [Globalization.CultureInfo]::InvariantCulture)
$settingsTask = $adapter.BuildAndPublishAsync(
    $JobRoot,
    [string]$inputValue.taskId,
    'clip.card',
    $settings,
    $null,
    $resolver,
    $inputs,
    $frozenAt,
    [Threading.CancellationToken]::None)
$settingsResult = $settingsTask.GetAwaiter().GetResult()
$adapter.MigrateLegacyManifestV2Async($JobRoot, $settingsResult, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

$frozenAnki = $settingsResult.Snapshot.Modules.Anki
if ($null -eq $frozenAnki) { throw 'Core settings snapshot omitted Anki.' }
if ($frozenAnki.ConfigArtifactRef -ne $configRef.ArtifactRef) { throw 'Core changed config artifact identity.' }
if (-not $frozenAnki.ExpectedSha256.Equals($configRef.ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Core changed config artifact SHA-256.' }

$entrypoint = [KnowledgeClipper.Orchestrator.Core.WorkerEntrypoint]::new(
    'workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe', [string[]]@())
$worker = [KnowledgeClipper.Orchestrator.Core.WorkerEntry]::new(
    'export.anki',
    '>=1.2.1 <2.0.0',
    $entrypoint,
    [string[]]@('anki-export'),
    [string[]]@('card-bundle', 'learning-bundle', 'anki-export-config'),
    [string[]]@('anki-export-evidence', 'anki-connection-status'),
    [string[]]@('cards'),
    'external',
    'reconcilable',
    $true)
$registry = [KnowledgeClipper.Orchestrator.Core.WorkerRegistry]::new(
    '1.0.0', 'export.anki-1.2.1-formal-canary', [string[]]@('cards'),
    [KnowledgeClipper.Orchestrator.Core.WorkerEntry[]]@($worker))
$configSnapshot = New-JsonElement @{ ankiConfigArtifactId = $configRef.ArtifactRef }
$routing = [KnowledgeClipper.Orchestrator.Core.PipelineRoutingSnapshot]::new('cards', [string[]]@('anki'), 'task-settings-snapshot')
$engine = [KnowledgeClipper.Orchestrator.Core.OrchestratorEngine]::new()

function Invoke-CoreRun {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$RunId,
        [Parameter(Mandatory = $true)]$StageInputs,
        [Parameter(Mandatory = $true)][string[]]$OutputKinds,
        [Parameter(Mandatory = $true)]$Payload
    )
    $retry = [KnowledgeClipper.Orchestrator.Core.RetryPolicy]::new(1, 0)
    $stage = [KnowledgeClipper.Orchestrator.Core.PipelineStage]::new(
        'anki-export',
        'export.anki',
        $Operation,
        [string[]]@(),
        [KnowledgeClipper.Orchestrator.Core.ArtifactReference[]]$StageInputs,
        $OutputKinds,
        120000,
        $retry,
        $configSnapshot,
        (New-JsonElement $Payload),
        $null,
        $null,
        $null)
    $plan = [KnowledgeClipper.Orchestrator.Core.PipelinePlan]::new(
        '2.0.0', '2.0.0', [string]$inputValue.taskId, 1,
        [KnowledgeClipper.Orchestrator.Core.PipelineStage[]]@($stage), $routing)
    $state = $engine.RunAsync($JobRoot, $plan, $registry, $ReleaseRoot, $RunId, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($state -ne 'succeeded') { throw "Core run failed: $RunId / $state" }
    return Read-Attempt $RunId
}

$validate = Invoke-CoreRun -Operation 'validate' -RunId 'run-validate' -StageInputs ([KnowledgeClipper.Orchestrator.Core.ArtifactReference[]]@($cardRef, $configRef)) -OutputKinds ([string[]]@('anki-export-evidence')) -Payload @{ validateOnly = $true }
$probePlan = [KnowledgeClipper.Orchestrator.Core.ModuleSettingsActionResolver]::ResolveAnkiProbe(
    [string]$inputValue.taskId,
    $configRef,
    $registry)
if ($probePlan.Routing.Source -ne 'explicit-module-action' -or $probePlan.Stages.Count -ne 1 -or $probePlan.Stages[0].Operation -ne 'probe') {
    throw 'Core ModuleSettingsActionResolver did not preserve the explicit read-only probe boundary.'
}
$probeState = $engine.RunAsync($JobRoot, $probePlan, $registry, $ReleaseRoot, 'run-probe', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
if ($probeState -ne 'succeeded') { throw "Core probe run failed: run-probe / $probeState" }
$probe = Read-Attempt 'run-probe'

foreach ($attempt in @($validate, $probe)) {
    if ($attempt.Request.workerId -ne 'export.anki') { throw 'Core WorkerRequest worker identity mismatch.' }
    if ($attempt.Request.configSnapshot.PSObject.Properties.Count -ne 1 -or $attempt.Request.configSnapshot.ankiConfigArtifactId -ne $configRef.ArtifactRef) {
        throw 'Core WorkerRequest configSnapshot copied content or changed identity.'
    }
    $requestConfig = @($attempt.Request.inputRefs | Where-Object kind -eq 'anki-export-config')
    if ($requestConfig.Count -ne 1 -or $requestConfig[0].artifactRef -ne $configRef.ArtifactRef -or $requestConfig[0].expectedSha256 -ne $configRef.ExpectedSha256) {
        throw 'Core WorkerRequest did not preserve the SHA-bound config artifact.'
    }
    if ($attempt.Result.status -ne 'completed' -or $attempt.Result.worker.id -ne 'export.anki' -or $attempt.Result.worker.version -ne '1.2.1') {
        throw 'Release worker did not complete as export.anki 1.2.1.'
    }
}
if (@($probe.Result.sideEffects).Count -ne 0) { throw 'Probe reported an external side effect.' }

$report = [ordered]@{
    schemaVersion = 'export.anki.core-settings-release-canary.core-evidence.v1'
    status = 'passed'
    externalProductionCalls = 0
    settings = [ordered]@{
        destinations = @($settingsResult.Snapshot.Workflow.Destinations)
        snapshotArtifact = [ordered]@{
            artifactRef = $settingsResult.ArtifactReference.ArtifactRef
            kind = $settingsResult.ArtifactReference.Kind
            contractVersion = $settingsResult.ArtifactReference.ContractVersion
            manifestPath = $settingsResult.ArtifactReference.ManifestPath
            expectedSha256 = $settingsResult.ArtifactReference.ExpectedSha256
        }
        ankiConfigArtifactRef = $frozenAnki.ConfigArtifactRef
        ankiConfigExpectedSha256 = $frozenAnki.ExpectedSha256
        secretReferences = @($frozenAnki.SecretReferences)
    }
    validateOnly = [ordered]@{
        status = $validate.Result.status
        workerId = $validate.Result.worker.id
        workerVersion = $validate.Result.worker.version
        operation = $validate.Request.operation
        requestPath = $validate.RequestPath
        resultPath = $validate.ResultPath
        configSnapshot = $validate.Request.configSnapshot
        configInput = @($validate.Request.inputRefs | Where-Object kind -eq 'anki-export-config')[0]
        outputKinds = @($validate.Result.outputs.kind)
        sideEffects = @($validate.Result.sideEffects)
    }
    offlineProbe = [ordered]@{
        status = $probe.Result.status
        workerId = $probe.Result.worker.id
        workerVersion = $probe.Result.worker.version
        operation = $probe.Request.operation
        requestPath = $probe.RequestPath
        resultPath = $probe.ResultPath
        configSnapshot = $probe.Request.configSnapshot
        configInput = @($probe.Request.inputRefs | Where-Object kind -eq 'anki-export-config')[0]
        outputKinds = @($probe.Result.outputs.kind)
        sideEffects = @($probe.Result.sideEffects)
        routingSource = $probePlan.Routing.Source
    }
}
$report | ConvertTo-Json -Compress -Depth 64
