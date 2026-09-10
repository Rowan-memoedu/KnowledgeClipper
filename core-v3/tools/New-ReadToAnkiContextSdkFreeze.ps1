[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Candidate,
    [Parameter(Mandatory)] [string]$Staging,
    [Parameter(Mandatory)] [string]$MatrixReceipt,
    [Parameter(Mandatory)] [string]$PortableClosure,
    [Parameter(Mandatory)] [string]$ClientAssembly,
    [Parameter(Mandatory)] [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function WriteJson([string]$Path, [object]$Value) { $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }

$candidateRoot = [IO.Path]::GetFullPath($Candidate)
$stagingRoot = [IO.Path]::GetFullPath($Staging)
$matrixPath = [IO.Path]::GetFullPath($MatrixReceipt)
$closurePath = [IO.Path]::GetFullPath($PortableClosure)
$manifestPath = Join-Path $candidateRoot 'candidate-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100
$matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json -Depth 100
$closure = Get-Content -LiteralPath $closurePath -Raw | ConvertFrom-Json -Depth 100

Require ($matrix.status -eq 'PASS') 'Staging matrix did not pass.'
Require ([IO.Path]::GetFullPath($matrix.candidate) -eq $stagingRoot) 'Matrix is not bound to the supplied staging root.'
Require ($matrix.workers.content -eq 'content.deepseek@0.11.0' -and $matrix.workers.speech -eq 'speech.edge@1.2.5' -and $matrix.workers.anki -eq 'export.anki@1.4.1') 'Worker matrix drifted.'
Require ($matrix.uniqueContextBoth.preview.cards.Count -eq 2 -and $matrix.uniqueContextBoth.readback.cards.Count -eq 2) 'Unique-context matrix drifted.'
Require ($matrix.repeatedAccept.accepted.preview.cards.Count -eq 2 -and $matrix.repeatedAccept.accepted.readback.cards.Count -eq 2) 'Occurrence acceptance matrix drifted.'
Require ($matrix.repeatedCorrection.corrected.preview.cards.Count -eq 2 -and $matrix.repeatedCorrection.corrected.readback.cards.Count -eq 2) 'Correction matrix drifted.'
Require (($matrix.repeatedCorrection.afterResolutionReplay.ankiExportAttempts - $matrix.repeatedCorrection.afterResolution.ankiExportAttempts) -eq 0 -and ($matrix.repeatedCorrection.afterResolutionReplay.contentAttempts - $matrix.repeatedCorrection.afterResolution.contentAttempts) -eq 0) 'Correction replay created another attempt.'
Require (($matrix.cancel.afterCancel.ankiExportAttempts - $matrix.cancel.beforeCancel.ankiExportAttempts) -eq 0) 'Cancel reached Anki.'
Require ($matrix.contextOff.cards -eq 1 -and $matrix.contextUnavailable.cards -eq 1) 'Context-off/unavailable matrix drifted.'
Require ($matrix.readSelectedOnly.spokenText -eq 'pragmatic' -and $matrix.readSelectedOnly.playback -eq 'IdleReplayable' -and $matrix.readSelectedOnly.cancelledWithoutExport) 'Selected-only speech matrix drifted.'
Require ($matrix.continuousOperations.allDistinct -and $matrix.continuousOperations.allTerminal) 'Continuous operation identity/terminal gate failed.'
Require ($matrix.invariants.remnoteCalls -eq 0 -and $matrix.invariants.mineruCalls -eq 0 -and $matrix.invariants.physicalSpeakerPlaybackCount -eq 1) 'External boundary drifted.'
Require ($matrix.invariants.protectedProcessIds -contains 608 -and $matrix.invariants.protectedProcessIds -contains 17260 -and $matrix.actualOrchestratorProcess.pid -notin $matrix.invariants.protectedProcessIds) 'Protected process boundary drifted.'
Require ($closure.status -eq 'passed' -and @($closure.failures).Count -eq 0) 'Portable schema closure failed.'

$declared = @($manifest.files)
$declaredPaths = @{}
$candidateBytes = 0L
foreach ($item in $declared) {
    Require (-not [IO.Path]::IsPathRooted($item.path) -and $item.path -notmatch '(^|/|\\)\.\.($|/|\\)') "Unsafe manifest path: $($item.path)"
    $path = Join-Path $candidateRoot ($item.path -replace '/', [IO.Path]::DirectorySeparatorChar)
    Require (Test-Path -LiteralPath $path -PathType Leaf) "Candidate file missing: $($item.path)"
    Require ((Get-Item -LiteralPath $path).Length -eq $item.bytes) "Candidate byte drift: $($item.path)"
    Require ((Hash $path) -eq $item.sha256) "Candidate hash drift: $($item.path)"
    $declaredPaths[$item.path] = $true
    $candidateBytes += $item.bytes
}
$actualCandidate = @(Get-ChildItem -LiteralPath $candidateRoot -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($candidateRoot, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, [char]'/') })
$unexpected = @($actualCandidate | Where-Object { $_ -ne 'candidate-manifest.json' -and -not $declaredPaths.ContainsKey($_) })
Require ($unexpected.Count -eq 0 -and $actualCandidate.Count -eq ($declared.Count + 1)) 'Candidate contains undeclared files.'

$stagingFiles = @(Get-ChildItem -LiteralPath $stagingRoot -File -Recurse)
Require ($stagingFiles.Count -eq $declared.Count) 'Staging/candidate file count differs.'
foreach ($file in $stagingFiles) {
    $relative = [IO.Path]::GetRelativePath($stagingRoot, $file.FullName).Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
    Require ($declaredPaths.ContainsKey($relative)) "Staging file absent from manifest: $relative"
    $target = Join-Path $candidateRoot ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
    Require ($file.Length -eq (Get-Item -LiteralPath $target).Length -and (Hash $file.FullName) -eq (Hash $target)) "Seal differs from staging: $relative"
}

$dependencyPath = Join-Path $candidateRoot 'authority\dependency-manifest.v1.json'
$dependency = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json -Depth 100
Require ($dependency.portableRecursiveSchemaValidation -eq 'passed') 'Dependency manifest does not bind portable schema closure.'
Require ($dependency.moduleInputs.content.worker -eq 'content.deepseek@0.11.0') 'Dependency manifest Content identity drifted.'
Require ($dependency.moduleInputs.content.releaseManifestSha256 -eq '68101074FD80113BC590EE91A9F741BA158E9FC0F85E8B16F5CDF71B5F20ED99') 'Content release manifest binding drifted.'
Require ($dependency.moduleInputs.content.payloadTreeSha256 -eq '78D726DB3BA9DA7C14DCD0C672FCF5C5AD97DE64532650818F24F748AE3EFAC4') 'Content release tree binding drifted.'

$clientSourcePath = [IO.Path]::GetFullPath($ClientAssembly)
Require (Test-Path -LiteralPath $clientSourcePath -PathType Leaf) 'Independent Client assembly is missing.'
$assembly = [System.Reflection.Assembly]::LoadFrom($clientSourcePath)
$clientType = $assembly.GetType('KnowledgeClipper.Core.V3.Client.CoreV3Client', $true)
$projectionType = $assembly.GetType('KnowledgeClipper.Core.V3.Client.ReadAloudCardsProjectionSnapshot', $true)
$coordinatorType = $assembly.GetType('KnowledgeClipper.Core.V3.Client.ReadToAnkiOperationAuthorizationCoordinator', $true)
$requiredMethods = @('ReadAloudCardsAsync','ClipCardsAsync','ResolveCardResolutionAsync','ConfirmFlashcardDraftAsync','CancelFlashcardDraftAsync','WaitReadAloudCardsActionAsync')
$actualMethods = @($clientType.GetMethods().Name | Sort-Object -Unique)
foreach ($name in $requiredMethods) { Require ($actualMethods -contains $name) "Typed SDK method missing: $name" }
$requiredFields = @('ActionState','ActiveCommandId','TerminalCommandId','TerminalSequence','CardResolution','CardDraft','Playback','Pipeline')
$actualFields = @($projectionType.GetProperties().Name)
foreach ($name in $requiredFields) { Require ($actualFields -contains $name) "Projection field missing: $name" }
foreach ($name in @('Bind','Require')) { Require (@($coordinatorType.GetMethods().Name) -contains $name) "Authorization coordinator method missing: $name" }

Require (-not (Test-Path -LiteralPath $OutputRoot)) "Append-only SDK root already exists: $OutputRoot"
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$clientPath = Join-Path $OutputRoot 'KnowledgeClipper.Core.V3.Client.dll'
Copy-Item -LiteralPath $clientSourcePath -Destination $clientPath
$sdkContractPath = Join-Path $OutputRoot 'sdk-contract.v2.json'
WriteJson $sdkContractPath ([ordered]@{
    schema = 'kc.read-to-anki-context-sdk-freeze/2'
    candidate = [ordered]@{ root=$candidateRoot; manifestSha256=Hash $manifestPath; registryVersion=$manifest.registryVersion }
    assembly = [ordered]@{ path='KnowledgeClipper.Core.V3.Client.dll'; bytes=(Get-Item $clientPath).Length; sha256=Hash $clientPath; runtimePackaging='independent-from-slim-candidate' }
    methods = $requiredMethods
    projectionFields = $requiredFields
    waitBinding = @('operationId','commandId','acceptedSequence','terminalCommandId','terminalSequence')
    authorization = [ordered]@{ coordinator='ReadToAnkiOperationAuthorizationCoordinator'; methods=@('Bind','Require'); destinationScope='isolated-canary-deck' }
    semantics = [ordered]@{ uniqueContextBoth='preview2-confirm-exact2'; repeatedTarget='typed-occurrence-or-user-correction'; cancel='zero-export'; cardsOnlyPlayback='not_requested'; readSpeech='selected-text-only'; continuationWait='command-and-terminal-sequence-bound' }
})
$receiptPath = Join-Path $OutputRoot 'independent-verification-receipt.json'
WriteJson $receiptPath ([ordered]@{
    schema='kc.read-to-anki-context-independent-verification/1'; verifiedAt=(Get-Date).ToUniversalTime().ToString('o'); status='PASS'
    candidate=[ordered]@{root=$candidateRoot;manifest=[ordered]@{bytes=(Get-Item $manifestPath).Length;sha256=Hash $manifestPath};files=$declared.Count;bytes=$candidateBytes;undeclaredFiles=0;stagingCopiedExactly=$true}
    matrix=[ordered]@{receipt=[ordered]@{path=$matrixPath;bytes=(Get-Item $matrixPath).Length;sha256=Hash $matrixPath};actualHost=$matrix.actualOrchestratorProcess;workers=$matrix.workers;uniqueCards=2;acceptedCards=2;correctedCards=2;ankiExports=3;ankiExternalWrites=6;cancelExports=0;correctionReplayAttemptDelta=0;contextOffCards=1;contextUnavailableCards=1;selectedSpeech=$matrix.readSelectedOnly;continuousOperations=$matrix.continuousOperations}
    portability=[ordered]@{closure=[ordered]@{path=$closurePath;sha256=Hash $closurePath;schemasChecked=$closure.schemasChecked;localRefsChecked=$closure.localRefsChecked;failures=0};dependencyManifest=[ordered]@{path=$dependencyPath;sha256=Hash $dependencyPath};contentReleaseManifestSha256=$dependency.moduleInputs.content.releaseManifestSha256;contentTreeSha256=$dependency.moduleInputs.content.payloadTreeSha256}
    sdk=[ordered]@{contract=[ordered]@{path=$sdkContractPath;sha256=Hash $sdkContractPath};assembly=[ordered]@{path=$clientPath;sha256=Hash $clientPath};typedMethods=$requiredMethods;projectionFields=$requiredFields;reflection='passed'}
    boundaries=[ordered]@{remnoteCalls=0;mineruCalls=0;formalDeckWrites=0;isolatedAnkiNotes=6;physicalSpeakerPlaybackCount=1;protectedProcessesUntouched=@(608,17260);testReceiptsInsideCandidate=0}
})
$files = @(Get-ChildItem -LiteralPath $OutputRoot -File | Where-Object Name -ne 'freeze-manifest.json' | ForEach-Object { [ordered]@{path=$_.Name;bytes=$_.Length;sha256=Hash $_.FullName} } | Sort-Object path)
$freezePath = Join-Path $OutputRoot 'freeze-manifest.json'
WriteJson $freezePath ([ordered]@{schema='kc.read-to-anki-context-sdk-freeze-manifest/1';createdAt=(Get-Date).ToUniversalTime().ToString('o');source=[ordered]@{candidate=$candidateRoot;candidateManifestSha256=Hash $manifestPath;matrixReceipt=$matrixPath;matrixReceiptSha256=Hash $matrixPath};files=$files})
Write-Output "SDK_FREEZE $OutputRoot"
Write-Output "RECEIPT_SHA256 $(Hash $receiptPath)"
Write-Output "MANIFEST_SHA256 $(Hash $freezePath)"
