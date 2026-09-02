[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$AuthorityReleaseRoot = 'releases/knowledge-clipper-authority-v1.2.6-20260828T074944Z/win-x64',
    [string]$AuthorityDirectory = 'contracts/authority/worker-registry/v1.2.6-20260828T074944Z',
    [string]$AnkiEndpoint = 'http://127.0.0.1:8765',
    [string]$RemNoteMcpUrl = 'http://127.0.0.1:43101/mcp'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$EvidenceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $EvidenceRoot))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $AuthorityReleaseRoot))
$authorityRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $AuthorityDirectory))
$receiptPath = Join-Path $EvidenceRoot 'preflight-receipt.json'
if (Test-Path -LiteralPath $EvidenceRoot) { throw "Append-only preflight root exists: $EvidenceRoot" }
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null

function Invoke-Anki([string]$Action, [hashtable]$Params = @{}) {
    $body = @{ action = $Action; version = 6; params = $Params } | ConvertTo-Json -Depth 20 -Compress
    $response = Invoke-RestMethod -Uri $AnkiEndpoint -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 10
    if ($null -ne $response.error) { throw "AnkiConnect $Action failed without mutation." }
    return $response.result
}
function Opaque-Token([object]$Value) {
    $json = $Value | ConvertTo-Json -Depth 100 -Compress
    $matches = [regex]::Matches($json, '[A-Za-z0-9._~+/=\-]{40,}') | ForEach-Object Value | Sort-Object Length -Descending
    return @($matches | Where-Object { $_ -notmatch '^(contentStructured|ancestor|parentRemId|serverVersion)' }) | Select-Object -First 1
}

$orchestrator = Join-Path $releaseRoot 'orchestrator\KnowledgeClipper.Orchestrator.exe'
$registryPath = Join-Path $authorityRoot 'worker-registry.v1.json'
$validationText = (& $orchestrator validate-registry --registry $registryPath --release-root $releaseRoot 2>&1) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) { throw 'Authority registry preflight failed.' }
$validation = $validationText | ConvertFrom-Json
if ($validation.status -ne 'valid' -or $validation.registryVersion -ne '1.2.6' -or $validation.workers -ne 5) { throw 'Authority identity drifted.' }

$node = 'C:\Program Files\nodejs\node.exe'
$remnoteCli = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote MCP Integration\node_modules\remnote-mcp-server\dist\remnote-cli\index.js'
$statusRaw = (& $node $remnoteCli --json --mcp-url $RemNoteMcpUrl status 2>&1) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) { throw 'RemNote MCP status failed.' }
$remnoteStatus = $statusRaw | ConvertFrom-Json
if (-not $remnoteStatus.connected -or -not $remnoteStatus.acceptWriteOperations) { throw 'RemNote MCP is not connected with write operations enabled.' }
$searchRaw = (& $node $remnoteCli --json --mcp-url $RemNoteMcpUrl search 'mineru' --limit 30 --view full --ancestor-depth 3 --content-mode structured 2>&1) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) { throw 'RemNote credential lookup failed.' }
$search = $searchRaw | ConvertFrom-Json -Depth 100
$mineruAvailable = $false
foreach ($candidate in @($search.results)) {
    if ([string]$candidate.title -ine 'mineru') { continue }
    $readRaw = (& $node $remnoteCli --json --mcp-url $RemNoteMcpUrl read ([string]$candidate.remId) --content-mode structured --depth 1 --view full --ancestor-depth 3 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { continue }
    $opaque = Opaque-Token ($readRaw | ConvertFrom-Json -Depth 100)
    if (-not [string]::IsNullOrWhiteSpace($opaque)) { $mineruAvailable = $true; break }
}
if (-not $mineruAvailable) { throw 'MinerU credential is unavailable from process/user environment and the local RemNote secret node.' }

$deepSeekAvailable = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('DEEPSEEK_API_KEY', 'Process')) -or
    -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('DEEPSEEK_API_KEY', 'User'))
if (-not $deepSeekAvailable) { throw 'DeepSeek credential is unavailable.' }

$ankiVersion = Invoke-Anki 'version'
$ankiPermission = Invoke-Anki 'requestPermission'
$ankiProfile = Invoke-Anki 'getActiveProfile'
$ankiActions = @('modelNames','modelFieldNames','modelTemplates','modelStyling','createModel','deckNames','createDeck','findNotes','notesInfo','cardsInfo','addNote','updateNoteFields','addTags','changeDeck','retrieveMediaFile','storeMediaFile')
$reflected = Invoke-Anki 'apiReflect' @{ scopes = @('actions'); actions = $ankiActions }
$availableActions = @($reflected.actions)
$missingActions = @($ankiActions | Where-Object { $_ -notin $availableActions })
if ($ankiVersion -lt 6 -or $ankiPermission.permission -ne 'granted' -or [string]::IsNullOrWhiteSpace($ankiProfile) -or $missingActions.Count -ne 0) {
    throw 'AnkiConnect preflight is not ready for the isolated write/readback path.'
}

$mineruScript = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\MinerU OCR Integration\mineru_ocr.py'
$edgeTts = @('D:\Applications\KnowledgeClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe','D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Scripts\edge-tts.exe') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$ffplay = 'D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe'
foreach ($runtime in @($mineruScript, $edgeTts, $ffplay)) { if ([string]::IsNullOrWhiteSpace($runtime) -or -not (Test-Path -LiteralPath $runtime -PathType Leaf)) { throw 'Phase B provider runtime is missing.' } }

$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-phase-b-preflight.v1'
    status = 'passed'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    authority = [ordered]@{ registryVersion = '1.2.6'; releaseRoot = $releaseRoot; registrySha256 = (Get-FileHash $registryPath -Algorithm SHA256).Hash; validateRegistry = $validation }
    coreSettingsCheckpoint = [ordered]@{ version = '2.4.3'; path = (Join-Path $projectRoot 'contracts\core\v2.4\settings-control\checkpoints\v2.4.3-20260828T073934Z\checkpoint.json'); sha256 = '8A9D2765804B7E7ED9A2B2B98C28DB25559C222DFBB757CDFFB6406FBAE61B32' }
    remnote = [ordered]@{ connected = $true; acceptWriteOperations = $true; acceptReplaceOperation = [bool]$remnoteStatus.acceptReplaceOperation; serverVersion = $remnoteStatus.serverVersion; pluginVersion = $remnoteStatus.pluginVersion; endpoint = $RemNoteMcpUrl }
    anki = [ordered]@{ apiVersion = $ankiVersion; permission = 'granted'; profile = $ankiProfile; actionsVerified = $ankiActions; endpoint = $AnkiEndpoint; mutatingActionsExecuted = @() }
    credentials = [ordered]@{ deepseek = [ordered]@{ available = $true; source = 'process-or-user-environment' }; mineru = [ordered]@{ available = $true; source = 'local-remnote-secret-node'; secretPersisted = $false } }
    runtimes = [ordered]@{
        mineruScript = [ordered]@{ path = $mineruScript; sha256 = (Get-FileHash $mineruScript -Algorithm SHA256).Hash }
        edgeTts = [ordered]@{ path = $edgeTts; sha256 = (Get-FileHash $edgeTts -Algorithm SHA256).Hash }
        ffplay = [ordered]@{ path = $ffplay; sha256 = (Get-FileHash $ffplay -Algorithm SHA256).Hash }
    }
    secretsWritten = 0
    externalMutations = 0
    physicalSpeakerInvocations = 0
}
[IO.File]::WriteAllText($receiptPath, (($receipt | ConvertTo-Json -Depth 50) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{ status = 'passed'; receipt = $receiptPath; sha256 = (Get-FileHash $receiptPath -Algorithm SHA256).Hash } | ConvertTo-Json -Depth 5
