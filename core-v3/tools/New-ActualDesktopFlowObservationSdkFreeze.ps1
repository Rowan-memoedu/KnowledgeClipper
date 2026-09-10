[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ClientAssembly,
    [Parameter(Mandatory)] [string]$ContractsAssembly,
    [Parameter(Mandatory)] [string]$TargetedTestReceipt,
    [Parameter(Mandatory)] [string]$ContractValidationReceipt,
    [Parameter(Mandatory)] [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function WriteJson([string]$Path, [object]$Value) { $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$clientSource = [IO.Path]::GetFullPath($ClientAssembly)
$contractsSource = [IO.Path]::GetFullPath($ContractsAssembly)
$testPath = [IO.Path]::GetFullPath($TargetedTestReceipt)
$contractPath = [IO.Path]::GetFullPath($ContractValidationReceipt)
$target = [IO.Path]::GetFullPath($OutputRoot)

Require (Test-Path -LiteralPath $clientSource -PathType Leaf) 'Client assembly is missing.'
Require (Test-Path -LiteralPath $contractsSource -PathType Leaf) 'Contracts assembly is missing.'
Require (Test-Path -LiteralPath $testPath -PathType Leaf) 'Targeted test receipt is missing.'
Require (Test-Path -LiteralPath $contractPath -PathType Leaf) 'Contract validation receipt is missing.'
Require (-not (Test-Path -LiteralPath $target)) 'Append-only SDK freeze root already exists.'

$test = Get-Content -LiteralPath $testPath -Raw | ConvertFrom-Json -Depth 100
$contracts = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json -Depth 100
Require ($test.status -eq 'PASS') 'Targeted discovery/settings/action test did not pass.'
Require ($contracts.schemasChecked -ge 95 -and $contracts.validFixturesAccepted -ge 90 -and $contracts.invalidFixturesRejected -ge 75) 'Additive contract validation counts are below the required gate.'

$assembly = [Reflection.Assembly]::LoadFrom($clientSource)
$clientType = $assembly.GetType('KnowledgeClipper.Core.V3.Client.CoreV3Client', $true)
$projectionType = $assembly.GetType('KnowledgeClipper.Core.V3.Client.ReadAloudCardsProjectionSnapshot', $true)
$identityType = $assembly.GetType('KnowledgeClipper.Core.V3.Client.AcceptedOperationIdentity', $true)
$checkpointType = $assembly.GetType('KnowledgeClipper.Core.V3.Client.ReadToAnkiDiscoveryCheckpoint', $true)
$actionCheckpointType = $assembly.GetType('KnowledgeClipper.Core.V3.Client.ReadToAnkiActionDiscoveryCheckpoint', $true)
$settingsType = $assembly.GetType('KnowledgeClipper.Core.V3.Client.ReadToAnkiSettingsEvidence', $true)
$contextType = $assembly.GetType('KnowledgeClipper.Core.V3.Client.ReadToAnkiContextEvidence', $true)
$segmentType = $assembly.GetType('KnowledgeClipper.Core.V3.Client.LanguageContextSegment', $true)
$requiredMethods = @(
    'BeginReadToAnkiDiscoveryAsync','DiscoverAcceptedReadToAnkiAsync','WaitForAcceptedReadToAnkiAsync',
    'BeginReadToAnkiActionDiscoveryAsync','DiscoverAcceptedReadToAnkiActionAsync','WaitForAcceptedReadToAnkiActionAsync',
    'WaitReadAloudCardsActionAsync','GetReadAloudCardsProjectionAsync')
$methods = @($clientType.GetMethods().Name | Sort-Object -Unique)
foreach ($name in $requiredMethods) { Require ($methods -contains $name) "Typed SDK method missing: $name" }
foreach ($name in @('OperationId','CommandId','AcceptedSequence')) { Require (@($identityType.GetProperties().Name) -contains $name) "Accepted identity property missing: $name" }
foreach ($name in @('CorrelationMarker','Cursor')) { Require (@($checkpointType.GetProperties().Name) -contains $name) "Discovery checkpoint property missing: $name" }
foreach ($name in @('OperationId','CorrelationMarker','Cursor')) { Require (@($actionCheckpointType.GetProperties().Name) -contains $name) "Action discovery checkpoint property missing: $name" }
Require (@($projectionType.GetProperties().Name) -contains 'SettingsEvidence') 'Projection settings evidence is missing.'
Require (@($projectionType.GetProperties().Name) -contains 'ContextEvidence') 'Projection context evidence is missing.'
foreach ($name in @('SettingsSnapshot','AnkiConfiguration','Destination','PackageProvenance')) { Require (@($settingsType.GetProperties().Name) -contains $name) "Settings evidence property missing: $name" }
foreach ($name in @('ContextSnapshot','Capture','SelectedTextSha256','TargetTextSha256','SpokenTextSha256','SpeechContextMode','ContextSegments')) { Require (@($contextType.GetProperties().Name) -contains $name) "Context evidence property missing: $name" }
foreach ($name in @('Artifact','Role','Order')) { Require (@($segmentType.GetProperties().Name) -contains $name) "Role-bound context segment property missing: $name" }

$sourceRelatives = @(
    'core-v3/src/KnowledgeClipper.Core.V3.Client/DomainClient.cs',
    'core-v3/src/KnowledgeClipper.Core.V3.Kernel/Application/KernelApplication.cs',
    'core-v3/src/KnowledgeClipper.Core.V3.Kernel/Operations/ReadAloudCardsOperation.cs',
    'core-v3/src/KnowledgeClipper.Core.V3.Kernel/WorkerAdapter/CoreWorkerTaskPackageAdapter.cs',
    'core-v3/tests/KnowledgeClipper.Core.V3.Phase2Tests/Program.cs',
    'core-v3/tools/New-ActualDesktopFlowObservationSdkFreeze.ps1',
    'tools/validate_contracts.py',
    'contracts/core/v3/operations/clip.read-aloud-cards/discovery-checkpoint.v1.schema.json',
    'contracts/core/v3/operations/clip.read-aloud-cards/discovery-result.v1.schema.json',
    'contracts/core/v3/operations/clip.read-aloud-cards/action-discovery-checkpoint.v1.schema.json',
    'contracts/core/v3/operations/clip.read-aloud-cards/action-discovery-result.v1.schema.json',
    'contracts/core/v3/operations/clip.read-aloud-cards/context-evidence.v1.schema.json',
    'contracts/core/v3/operations/clip.read-aloud-cards/settings-evidence.v1.schema.json',
    'contracts/core/v3/operations/clip.read-aloud-cards/projection.v4.schema.json'
)
$sources = @()
foreach ($relative in $sourceRelatives) {
    $path = Join-Path $projectRoot ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
    Require (Test-Path -LiteralPath $path -PathType Leaf) "Freeze source missing: $relative"
    $sources += [ordered]@{ path=$relative; bytes=(Get-Item -LiteralPath $path).Length; sha256=Hash $path }
}

New-Item -ItemType Directory -Path $target | Out-Null
Copy-Item -LiteralPath $clientSource -Destination (Join-Path $target 'KnowledgeClipper.Core.V3.Client.dll')
Copy-Item -LiteralPath $contractsSource -Destination (Join-Path $target 'KnowledgeClipper.Core.V3.Contracts.dll')
$schemaRoot = Join-Path $target 'contracts\clip.read-aloud-cards'
New-Item -ItemType Directory -Path $schemaRoot | Out-Null
foreach ($relative in $sourceRelatives | Where-Object { $_ -like 'contracts/*' }) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)) -Destination $schemaRoot
}

$contractOutput = Join-Path $target 'sdk-contract.v4.json'
WriteJson $contractOutput ([ordered]@{
    schema='kc.actual-desktop-flow-observation-sdk/4'
    purpose='typed-read-only-observation-for-a-complete-capture-preview-confirm-export-loop'
    discovery=[ordered]@{ begin='BeginReadToAnkiDiscoveryAsync'; discover='DiscoverAcceptedReadToAnkiAsync'; wait='WaitForAcceptedReadToAnkiAsync'; result=@('operationId','commandId','acceptedSequence'); ambiguity='fail-closed'; oldOperations='excluded-by-signed-baseline' }
    actionDiscovery=[ordered]@{ begin='BeginReadToAnkiActionDiscoveryAsync'; discover='DiscoverAcceptedReadToAnkiActionAsync'; wait='WaitForAcceptedReadToAnkiActionAsync'; scope='one existing operation stream'; everyActionOwnIdentity=$true; ambiguity='fail-closed' }
    terminal=[ordered]@{ wait='WaitReadAloudCardsActionAsync(AcceptedOperationIdentity)'; binding=@('commandId','acceptedSequence','terminalCommandId','terminalSequence') }
    settingsEvidence=@('settingsSnapshot','ankiConfiguration','destination.routeKey','destination.deckIdentity','destination.deckSha256','packageProvenance')
    contextEvidence=@('contextSnapshot','capture','selectedTextSha256','targetTextSha256','spokenTextSha256','speechContextMode','contextSegments[].role','contextSegments[].order','contextSegments[].artifact')
    contextualSpeech=[ordered]@{ requiredOrder=@('prefix','selection','suffix'); selectionArtifact='capture'; roleBinding='explicit'; missingRoles='fail-closed'; spokenText='immutable-sha-bound'; sourceWindowProjected=$false }
    privacy=[ordered]@{ rawEvents=$false; rawWorkerJson=$false; secrets=$false; bodyText=$false; absoluteSensitivePaths=$false }
    mutation=[ordered]@{ constructsBusinessCommands=$false; externalWrites=0; workerRuns=0 }
})

$receiptOutput = Join-Path $target 'qualification-receipt.json'
WriteJson $receiptOutput ([ordered]@{
    schema='kc.actual-desktop-flow-observation-sdk-qualification/1'
    createdAt=(Get-Date).ToUniversalTime().ToString('o')
    status='PASS'
    targetedTests=[ordered]@{ path=$testPath; sha256=Hash $testPath; status=$test.status }
    contracts=[ordered]@{ path=$contractPath; sha256=Hash $contractPath; schemas=$contracts.schemasChecked; valid=$contracts.validFixturesAccepted; invalid=$contracts.invalidFixturesRejected }
    assemblies=[ordered]@{
        client=[ordered]@{ path='KnowledgeClipper.Core.V3.Client.dll'; bytes=(Get-Item $clientSource).Length; sha256=Hash $clientSource }
        contracts=[ordered]@{ path='KnowledgeClipper.Core.V3.Contracts.dll'; bytes=(Get-Item $contractsSource).Length; sha256=Hash $contractsSource }
    }
    sourceFiles=$sources
    cases=@('cursor-boundary','old-operation-excluded','unique-accepted-identity','per-action-accepted-identity','concurrent-click-ambiguity-fail-closed','tampered-checkpoint-rejected','cross-operation-action-checkpoint-rejected','terminal-command-sequence-match','missing-settings-rejected','tampered-settings-rejected','roleless-context-rejected','context-off-artifact-rejected','prefix-selection-suffix-spoken-text-hash-bound','button-destination-override-ignored')
    boundaries=[ordered]@{ testDirectoryScanning=$false; businessWorkerRuns=0; ankiCalls=0; externalWrites=0; speakerCalls=0; sealedCandidateMutation=$false }
})

$files = @(Get-ChildItem -LiteralPath $target -File -Recurse | Where-Object Name -ne 'freeze-manifest.json' | ForEach-Object {
    [ordered]@{ path=[IO.Path]::GetRelativePath($target,$_.FullName).Replace([IO.Path]::DirectorySeparatorChar,[char]'/'); bytes=$_.Length; sha256=Hash $_.FullName }
} | Sort-Object path)
$manifestOutput = Join-Path $target 'freeze-manifest.json'
WriteJson $manifestOutput ([ordered]@{ schema='kc.actual-desktop-flow-observation-sdk-freeze-manifest/1'; createdAt=(Get-Date).ToUniversalTime().ToString('o'); files=$files })

Write-Output "SDK_FREEZE $target"
Write-Output "SDK_RECEIPT_SHA256 $(Hash $receiptOutput)"
Write-Output "SDK_MANIFEST_SHA256 $(Hash $manifestOutput)"
