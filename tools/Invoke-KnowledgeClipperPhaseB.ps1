#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$RemNoteMcpUrl = 'http://127.0.0.1:43101/mcp',
    [string]$RemRootId = '8zNzfX45XLCSKoPSJ',
    [string]$RemRootTitle = 'KnowledgeClipper PhaseB Canary 20260828T081109Z',
    [string]$AnkiDeck = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z',
    [switch]$PreAuthorityOnly,
    [switch]$DisableRemNoteExports,
    [switch]$UseProcessMineruCredential,
    [ValidateSet('A','B','C','D','E','F','G')][string[]]$Modes,
    [string]$CandidateRoot,
    [string]$AuthorityReleaseRoot,
    [string]$AuthorityDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$evidenceRoot = if ([IO.Path]::IsPathRooted($EvidenceRoot)) { [IO.Path]::GetFullPath($EvidenceRoot) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $EvidenceRoot)) }
if (Test-Path -LiteralPath $evidenceRoot) { throw "Append-only Phase B root exists: $evidenceRoot" }
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $evidenceRoot 'inputs')) | Out-Null

$candidateRoot = if ([string]::IsNullOrWhiteSpace($CandidateRoot)) { Join-Path $projectRoot 'releases\kc-integrated-settings-v1212-20260830T183500Z\win-x64' } else { [IO.Path]::GetFullPath($CandidateRoot) }
$releaseRoot = if ([string]::IsNullOrWhiteSpace($AuthorityReleaseRoot)) { Join-Path $candidateRoot 'authority\win-x64' } else { [IO.Path]::GetFullPath($AuthorityReleaseRoot) }
$authorityDirectory = if ([string]::IsNullOrWhiteSpace($AuthorityDirectory)) { Join-Path $candidateRoot 'authority\metadata' } else { [IO.Path]::GetFullPath($AuthorityDirectory) }
$registryPath = Join-Path $authorityDirectory 'worker-registry.v1.json'
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$harness = Join-Path $projectRoot 'tests\PhaseB.Canary\bin\Release\net8.0\PhaseB.Canary.dll'
$node = 'C:\Program Files\nodejs\node.exe'
$remnoteCli = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote MCP Integration\node_modules\remnote-mcp-server\dist\remnote-cli\index.js'
$mineruScript = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\MinerU OCR Integration\mineru_ocr.py'
$edgeTts = @('D:\Applications\KnowledgeClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe','D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$ffplay = 'D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe'
foreach ($path in @($releaseRoot,$registryPath,$dotnet,$harness,$node,$remnoteCli,$mineruScript,$edgeTts,$ffplay)) {
    if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) { throw "Required Phase B path missing." }
}

function Get-TreeIdentity([string]$Root) {
    $relativePaths = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File) { $relativePaths.Add([IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\','/')) }
    $relativePaths.Sort([StringComparer]::Ordinal)
    $rows = [Collections.Generic.List[string]]::new()
    [long]$totalBytes = 0
    foreach ($relative in $relativePaths) {
        $full = Join-Path $Root $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $item = Get-Item -LiteralPath $full
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        $rows.Add(("{0}`t{1}`t{2}" -f $relative, $item.Length, $hash))
        $totalBytes += $item.Length
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    $tree = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    [ordered]@{ fileCount=$relativePaths.Count; totalBytes=$totalBytes; treeSha256=$tree }
}

$candidateManifest = Join-Path $candidateRoot 'candidate-file-manifest.v1.json'
if (-not (Test-Path -LiteralPath $candidateManifest)) { throw 'Integrated candidate manifest is missing.' }
$candidateManifestSha = (Get-FileHash -LiteralPath $candidateManifest -Algorithm SHA256).Hash
if ($candidateManifestSha -ne '5D03C90D42FA2A1145FF6DCC4874BD059AC3EE072628F0A8DC81FB23983B4F3C') { throw 'Integrated candidate manifest drifted.' }
$candidateTree = Get-TreeIdentity $candidateRoot
if ($candidateTree.treeSha256 -ne '4F1021038635984154290467011FBB25AB9D88DD7A1A035F059CFF309231416A') { throw 'Integrated candidate sealed tree drifted.' }

$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$preflightRoot = Join-Path $evidenceRoot 'preflight'
[IO.Directory]::CreateDirectory($preflightRoot) | Out-Null
$remnoteReadStatus = 'not-probed'
if (-not $DisableRemNoteExports) {
    $settingsSnapshotPath = Join-Path $preflightRoot 'remnote-settings-snapshot.json'
    $settingsSnapshot = [ordered]@{ schemaVersion='remnote-destination-settings-snapshot.v1'; destinationType='remnote'; enabled=$true; target=[ordered]@{rootRemId=$RemRootId;rootTitle=$RemRootTitle}; advanced=[ordered]@{sink='mcp-direct';clipRouting='clip-daily-route';cardRouting='card-export';timeZone='Asia/Shanghai';dateFormat='yyyy-MM-dd'}; secretReferences=@() }
    [IO.File]::WriteAllText($settingsSnapshotPath, (($settingsSnapshot | ConvertTo-Json -Depth 20) + "`n"), [Text.UTF8Encoding]::new($false))
    $settingsSnapshotSha = (Get-FileHash -LiteralPath $settingsSnapshotPath -Algorithm SHA256).Hash
    $probeRequestPath = Join-Path $preflightRoot 'remnote-probe-request.json'
    $probeRequest = [ordered]@{ schemaVersion='remnote-readonly-probe-request.v1'; probeId=('candidate-v1212-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss')); mode='health'; timeoutMs=15000; authorizationScope='isolated-canary'; target=[ordered]@{rootRemId=$RemRootId;rootTitle=$RemRootTitle}; operations=@('status','search','list','read'); evidence=[ordered]@{settingsSnapshot=[ordered]@{path=$settingsSnapshotPath;sha256=$settingsSnapshotSha}} }
    [IO.File]::WriteAllText($probeRequestPath, (($probeRequest | ConvertTo-Json -Depth 20) + "`n"), [Text.UTF8Encoding]::new($false))
    $remnoteWorker = Join-Path $releaseRoot 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
    $probeRaw = (& $remnoteWorker probe --request $probeRequestPath --json 2>&1) -join [Environment]::NewLine
    $probeExitCode = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $preflightRoot 'remnote-probe.stdout.log'), ($probeRaw + "`n"), [Text.UTF8Encoding]::new($false))
    if ($probeExitCode -ne 0) { throw ('Authorized isolated RemNote probe did not become healthy; exit={0}' -f $probeExitCode) }
    $remnoteProbe = $probeRaw | ConvertFrom-Json -Depth 100
    if ($remnoteProbe.state -ne 'healthy' -or $remnoteProbe.admission -ne 'ready' -or -not $remnoteProbe.targetBinding.matched -or $remnoteProbe.externalWriteCount -ne 0) { throw 'Authorized isolated RemNote probe did not provide exact zero-write readback.' }
    $remnoteReadStatus = 'passed'
}
$remRootId = $RemRootId
$remRootTitle = $RemRootTitle
$ankiDeck = $AnkiDeck
$ankiActions = [Collections.Generic.List[string]]::new()
function Invoke-AnkiReadOnly([string]$Action, [hashtable]$Parameters=@{}) {
    $ankiActions.Add($Action)
    $body = [ordered]@{ action=$Action; version=6; params=$Parameters } | ConvertTo-Json -Depth 20 -Compress
    $response = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:8765' -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 15
    if ($null -ne $response.error) { throw ('AnkiConnect read-only action failed: {0}: {1}' -f $Action,$response.error) }
    $response.result
}
$ankiProfile = $null
try {
    $ankiVersion = Invoke-AnkiReadOnly 'version'
    $ankiProfile = Invoke-AnkiReadOnly 'getActiveProfile'
    $ankiDecks = @(Invoke-AnkiReadOnly 'deckNames')
    if ($ankiDecks -notcontains $ankiDeck) { throw 'Authorized Anki Canary deck does not exist.' }
    $ankiExistingNoteIds = @(Invoke-AnkiReadOnly 'findNotes' @{query=('deck:"{0}"' -f $ankiDeck.Replace('"','\"'))})
    $ankiExistingNotes = if ($ankiExistingNoteIds.Count -gt 0) { @(Invoke-AnkiReadOnly 'notesInfo' @{notes=$ankiExistingNoteIds}) } else { @() }
    $ankiPreflight = [ordered]@{ state='ready'; version=$ankiVersion; activeProfile=$ankiProfile; deck=$ankiDeck; deckExists=$true; existingNoteCount=$ankiExistingNoteIds.Count; existingCardReadbackCount=(@($ankiExistingNotes).Count); actions=@($ankiActions); externalMutation=$false; writes=0 }
}
catch {
    $ankiPreflight = [ordered]@{ state='service-unreachable'; version=$null; activeProfile=$null; deck=$ankiDeck; deckExists='not-observed'; existingNoteCount=$null; existingCardReadbackCount=0; actions=@($ankiActions); errorClass=$_.Exception.GetType().FullName; detail=$_.Exception.Message; externalMutation=$false; writes=0; automaticRetryPerformed=$false; manualServiceSetupBoundary=$true }
}
[IO.File]::WriteAllText((Join-Path $preflightRoot 'anki-readonly-preflight.json'), (($ankiPreflight | ConvertTo-Json -Depth 30) + "`n"), [Text.UTF8Encoding]::new($false))
$rootReceipt = [ordered]@{ schemaVersion='phase-b-isolated-destinations.v1'; generatedAt=[DateTimeOffset]::UtcNow.ToString('o'); remnote=[ordered]@{ rootRemId=$remRootId; rootTitle=$remRootTitle; formalClipRootUsed=$false; readStatus=$remnoteReadStatus; writesDisabled=[bool]$DisableRemNoteExports }; anki=[ordered]@{ deck=$ankiDeck; existingDeckUsed=($ankiPreflight.state -eq 'ready'); readStatus=$ankiPreflight.state; activeProfile=$ankiProfile; writes=0 } }
[IO.File]::WriteAllText((Join-Path $evidenceRoot 'isolated-destinations.json'), (($rootReceipt | ConvertTo-Json -Depth 20) + "`n"), [Text.UTF8Encoding]::new($false))

Add-Type -AssemblyName System.Drawing
$imagePath = Join-Path $evidenceRoot 'inputs\synthetic-non-sensitive.png'
$bitmap = [Drawing.Bitmap]::new(1600,900)
try {
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $font = [Drawing.Font]::new('Arial',42,[Drawing.FontStyle]::Regular)
        $header = [Drawing.Font]::new('Arial',54,[Drawing.FontStyle]::Bold)
        try {
            $graphics.DrawString('Synthetic Knowledge Card', $header, [Drawing.Brushes]::DarkBlue, 80, 90)
            $graphics.DrawString('Question: Why is retry evidence auditable?', $font, [Drawing.Brushes]::Black, 80, 250)
            $graphics.DrawString('Answer: Every attempt preserves independent evidence.', $font, [Drawing.Brushes]::Black, 80, 340)
            $graphics.DrawString('Canary data only - no personal information.', $font, [Drawing.Brushes]::DarkGreen, 80, 500)
        } finally { $font.Dispose(); $header.Dispose() }
    } finally { $graphics.Dispose() }
    $bitmap.Save($imagePath, [Drawing.Imaging.ImageFormat]::Png)
} finally { $bitmap.Dispose() }

$modes = if ($Modes.Count -gt 0) { $Modes } elseif ($PreAuthorityOnly) { @('C','D') } else { @('A','B','C','D') }
$token = $null
$mineruCredentialSource = 'not-required'
if ($modes -contains 'C') {
    $token = [Environment]::GetEnvironmentVariable('MINERU_API_TOKEN','Process')
    if (-not [string]::IsNullOrWhiteSpace($token)) {
        $declaredSource = [Environment]::GetEnvironmentVariable('RQC_MINERU_CREDENTIAL_SOURCE','Process')
        $mineruCredentialSource = if ($declaredSource -in @('legacy-remnote-secret-node','desktop-dpapi-current-user')) { $declaredSource } else { 'process-environment' }
    }
    else {
        $token = [Environment]::GetEnvironmentVariable('MINERU_API_TOKEN','User')
        if (-not [string]::IsNullOrWhiteSpace($token)) { $mineruCredentialSource = 'windows-user-environment' }
        else { $mineruCredentialSource = 'unavailable-after-process-user-and-desktop-local-store-discovery' }
    }
}
$mineruCredentialAvailable = -not [string]::IsNullOrWhiteSpace($token)
$priorToken = [Environment]::GetEnvironmentVariable('MINERU_API_TOKEN','Process')
$priorDestinations = [Environment]::GetEnvironmentVariable('RQC_PHASE_B_DESTINATIONS','Process')
$runs = @()
try {
    if (-not [string]::IsNullOrWhiteSpace($token)) { [Environment]::SetEnvironmentVariable('MINERU_API_TOKEN', $token, 'Process') }
    [Environment]::SetEnvironmentVariable('RQC_PHASE_B_CANDIDATE_ROOT', $candidateRoot, 'Process')
    if ($DisableRemNoteExports) { [Environment]::SetEnvironmentVariable('RQC_PHASE_B_DESTINATIONS', 'anki', 'Process') }
    elseif ($ankiPreflight.state -ne 'ready') { [Environment]::SetEnvironmentVariable('RQC_PHASE_B_DESTINATIONS', 'remnote', 'Process') }
    if ($PreAuthorityOnly) { [Environment]::SetEnvironmentVariable('RQC_PHASE_B_NO_DESTINATIONS', '1', 'Process') }
    foreach ($mode in $modes) {
        $pathRoot = Join-Path $evidenceRoot $mode.ToLowerInvariant()
        Write-Output "PHASE_B_PATH_START $mode $([DateTimeOffset]::UtcNow.ToString('o'))"
        $stdout = Join-Path $evidenceRoot ("path-" + $mode.ToLowerInvariant() + '.stdout.log')
        $stderr = Join-Path $evidenceRoot ("path-" + $mode.ToLowerInvariant() + '.stderr.log')
        $arguments = @($harness,$projectRoot,$releaseRoot,$registryPath,$pathRoot,$mode,$stamp,$remRootId,$remRootTitle,$ankiDeck,$mineruScript,$edgeTts,$ffplay,$imagePath)
        & $dotnet @arguments 1> $stdout 2> $stderr
        $pathExitCode = $LASTEXITCODE
        $runs += [ordered]@{ path=$mode; exitCode=$pathExitCode; receipt=if (Test-Path (Join-Path $pathRoot 'path-receipt.json')) { Join-Path $pathRoot 'path-receipt.json' } else { $null }; stdout=$stdout; stderr=$stderr }
        Write-Output "PHASE_B_PATH_END $mode exit=$pathExitCode $([DateTimeOffset]::UtcNow.ToString('o'))"
    }
} finally {
    [Environment]::SetEnvironmentVariable('MINERU_API_TOKEN', $priorToken, 'Process')
    [Environment]::SetEnvironmentVariable('RQC_PHASE_B_DESTINATIONS', $priorDestinations, 'Process')
    [Environment]::SetEnvironmentVariable('RQC_PHASE_B_NO_DESTINATIONS', $null, 'Process')
    [Environment]::SetEnvironmentVariable('RQC_PHASE_B_CANDIDATE_ROOT', $null, 'Process')
    $token = $null
}

$execution = [ordered]@{
    schemaVersion='knowledge-clipper-phase-b-execution.v1'; generatedAt=[DateTimeOffset]::UtcNow.ToString('o'); stamp=$stamp
    candidate=[ordered]@{ root=$candidateRoot; manifestPath=$candidateManifest; manifestSha256=$candidateManifestSha; sealed=$candidateTree }
    authority=[ordered]@{ registryVersion='1.2.12'; releaseRoot=$releaseRoot; registryPath=$registryPath; registrySha256=(Get-FileHash $registryPath -Algorithm SHA256).Hash; checkpointPath=(Join-Path $authorityDirectory 'checkpoint.json'); checkpointSha256=(Get-FileHash (Join-Path $authorityDirectory 'checkpoint.json') -Algorithm SHA256).Hash }
    coreSettings=[ordered]@{ integrationCheckpoint='2.5.1'; runtimeCoreVersion='2.5.0'; checkpointPath=(Join-Path $projectRoot 'contracts\core\v2.5\checkpoints\v2.5.1-20260830T075400Z\checkpoint.json'); checkpointSha256='62996EF471F46470090BE40259BF95ACD84EB7640084A1FCD4EC85E680AEE4F3' }
    isolatedDestinations=$rootReceipt; runs=$runs
    preflight=[ordered]@{ remnote=$remnoteReadStatus; anki=$ankiPreflight; deepSeek=[ordered]@{available=(-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('AI_PRODUCER_API_KEY','Process'))); source='process-environment'; secretPersisted=$false} }
    mineruCredential=[ordered]@{ required=($modes -contains 'C'); source=$mineruCredentialSource; available=$mineruCredentialAvailable; legacyBootstrapSource=($mineruCredentialSource -eq 'legacy-remnote-secret-node'); secretPersisted=$false; workerProcessEnvironmentOnly=$true; desktopCredentialChainPassed=($mineruCredentialSource -eq 'desktop-dpapi-current-user'); manualCredentialSetupBoundary=($mineruCredentialSource -like 'unavailable*'); longTermMigrationPending=($mineruCredentialSource -eq 'legacy-remnote-secret-node') }
    harness=[ordered]@{ path=$harness; sha256=(Get-FileHash $harness -Algorithm SHA256).Hash; sourceSha256=(Get-FileHash (Join-Path $projectRoot 'tests\PhaseB.Canary\Program.cs') -Algorithm SHA256).Hash }
    secretsWritten=0; preAuthorityOnly=[bool]$PreAuthorityOnly; externalExportsDisabled=[bool]$PreAuthorityOnly; remnoteWritesDisabled=[bool]$DisableRemNoteExports
}
$executionPath = Join-Path $evidenceRoot 'execution-receipt.json'
[IO.File]::WriteAllText($executionPath, (($execution | ConvertTo-Json -Depth 40) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{ status=if (@($runs | Where-Object exitCode -ne 0).Count -eq 0) {'completed'} else {'completed-with-path-failures'}; evidenceRoot=$evidenceRoot; receipt=$executionPath; sha256=(Get-FileHash $executionPath -Algorithm SHA256).Hash; remnoteRootRemId=$remRootId; ankiDeck=$ankiDeck } | ConvertTo-Json -Depth 10
