[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OfflineCanaryReceipt,
    [Parameter(Mandatory=$true)][string]$LiveReadOnlyReceipt,
    [string]$ReleaseRoot
)
$ErrorActionPreference='Stop'
$project=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$offlinePath=(Resolve-Path -LiteralPath $OfflineCanaryReceipt).Path
$livePath=(Resolve-Path -LiteralPath $LiveReadOnlyReceipt).Path
$offline=Get-Content -Raw -LiteralPath $offlinePath | ConvertFrom-Json
$live=Get-Content -Raw -LiteralPath $livePath | ConvertFrom-Json
if($offline.status -ne 'passed' -or $offline.worker.workerVersion -ne '2.3.0' -or $offline.realMcpCalled){throw 'Only passed offline export.remnote 2.3.0 receipt is admissible'}
if($live.status -ne 'passed' -or -not $live.realMcpCalled -or $live.realRemNoteWriteCalled -or $live.externalWriteCount -ne 0 -or -not $live.binding.exactReadback){throw 'Live evidence must be exact read-only discovery with zero writes'}
$stamp=(Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
if([string]::IsNullOrWhiteSpace($ReleaseRoot)){$ReleaseRoot=Join-Path $project "releases\knowledge-clipper-remnote-exporter-v2.3.0-$stamp\win-x64"}
$ReleaseRoot=[IO.Path]::GetFullPath($ReleaseRoot)
if(Test-Path -LiteralPath $ReleaseRoot){throw "Append-only release exists: $ReleaseRoot"}
New-Item -ItemType Directory -Path $ReleaseRoot|Out-Null
function Write-Utf8([string]$p,[string]$v){[IO.File]::WriteAllText($p,$v,[Text.UTF8Encoding]::new($false))}
function RelEvidence([string]$p){$i=Get-Item -LiteralPath $p;[ordered]@{path=[IO.Path]::GetRelativePath($ReleaseRoot,$i.FullName).Replace('\','/');sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $i.FullName).Hash;bytes=$i.Length}}
$map=[ordered]@{
 'KnowledgeClipper.RemNoteExporter.Worker.exe'='workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe';'appsettings.json'='workers\remnote\appsettings.json';'README.md'='workers\remnote\README.md';
 'contracts/remnote-target-discovery-request.v1.schema.json'='contracts\remnote-target-discovery-request.v1.schema.json';'contracts/remnote-target-discovery-result.v1.schema.json'='contracts\remnote-target-discovery-result.v1.schema.json';'contracts/remnote-destination-companion-descriptor.v3.schema.json'='contracts\remnote-destination-companion-descriptor.v3.schema.json';
 'contracts/remnote-public-probe-request.v2.schema.json'='contracts\remnote-public-probe-request.v2.schema.json';'contracts/remnote-public-probe-result.v2.schema.json'='contracts\remnote-public-probe-result.v2.schema.json';'contracts/remnote-destination-companion-descriptor.v2.schema.json'='contracts\remnote-destination-companion-descriptor.v2.schema.json';
 'contracts/remnote-original-media.v2.schema.json'='contracts\remnote-original-media.v2.schema.json';'contracts/remnote-export-payload.v3.schema.json'='contracts\remnote-export-payload.v3.schema.json';'contracts/remnote-export-task.v3.schema.json'='contracts\remnote-export-task.v3.schema.json';'contracts/remnote-write-receipt.v3.schema.json'='contracts\remnote-write-receipt.v3.schema.json';
 'contracts/remnote-export-payload.v2.schema.json'='contracts\remnote-export-payload.v2.schema.json';'contracts/remnote-export-task.v2.schema.json'='contracts\remnote-export-task.v2.schema.json';'contracts/remnote-write-receipt.v2.schema.json'='contracts\remnote-write-receipt.v2.schema.json';'contracts/remnote-destination-settings.v1.schema.json'='contracts\remnote-destination-settings.v1.schema.json';'contracts/remnote-destination-settings-snapshot.v1.schema.json'='contracts\remnote-destination-settings-snapshot.v1.schema.json';'contracts/remnote-readonly-probe-request.v1.schema.json'='contracts\remnote-readonly-probe-request.v1.schema.json';'contracts/remnote-readonly-probe-result.v1.schema.json'='contracts\remnote-readonly-probe-result.v1.schema.json'
}
foreach($entry in $map.GetEnumerator()){$destination=Join-Path $ReleaseRoot $entry.Key;$parent=Split-Path -Parent $destination;if(!(Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent|Out-Null};Copy-Item -LiteralPath (Join-Path $project $entry.Value) -Destination $destination}
$evidenceDir=Join-Path $ReleaseRoot 'evidence';New-Item -ItemType Directory -Path $evidenceDir|Out-Null
Copy-Item -LiteralPath $offlinePath -Destination (Join-Path $evidenceDir 'offline-canary-receipt.json')
Copy-Item -LiteralPath $livePath -Destination (Join-Path $evidenceDir 'live-readonly-discovery-receipt.json')
$offlineDir=Split-Path -Parent $offlinePath
foreach($name in @('readable-report.md','settings-companion-descriptor-v3.json','target-discovery-request.json','target-discovery-result.json','additive-schema-validation.log','public-contract-validation.log')){Copy-Item -LiteralPath (Join-Path $offlineDir $name) -Destination (Join-Path $evidenceDir $name)}
$liveDir=Split-Path -Parent $livePath
foreach($name in @('request.json','result.json','v1.2.33-diagnostic.json','schema-validation.log')){Copy-Item -LiteralPath (Join-Path $liveDir $name) -Destination (Join-Path $evidenceDir ('live-' + $name))}
$worker=Join-Path $ReleaseRoot 'KnowledgeClipper.RemNoteExporter.Worker.exe';$workerHash=(Get-FileHash -Algorithm SHA256 -LiteralPath $worker).Hash
if($workerHash -ne $offline.worker.executable.sha256){throw 'worker hash drift'}
$payloadFiles=@(Get-ChildItem -LiteralPath $ReleaseRoot -Recurse -File|Sort-Object FullName|ForEach-Object{RelEvidence $_.FullName})
$material=($payloadFiles|ForEach-Object{"$($_.path)`t$($_.sha256)`t$($_.bytes)"})-join "`n"
$treeSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material)))
$manifestPath=Join-Path $ReleaseRoot 'release-manifest.json'
$manifest=[ordered]@{schemaVersion='knowledge-clipper-remnote-exporter-release-manifest.v1';releaseId="knowledge-clipper-remnote-exporter-v2.3.0-$stamp";workerId='export.remnote';workerVersion='2.3.0';platform='win-x64';additive=$true;frozenCommonV1Modified=$false;payloadTreeSha256=$treeSha;files=$payloadFiles}
Write-Utf8 $manifestPath (($manifest|ConvertTo-Json -Depth 12)+"`n")
$receiptPath=Join-Path $ReleaseRoot 'release-receipt.json'
$receipt=[ordered]@{
 schemaVersion='knowledge-clipper-remnote-exporter-release-receipt.v1';status='passed';releaseId=$manifest.releaseId;releaseRoot=$ReleaseRoot
 worker=[ordered]@{workerId='export.remnote';workerVersion='2.3.0';executableSha256=$workerHash}
 contracts=[ordered]@{companion='remnote-destination-companion-descriptor.v3';targetDiscoveryRequest='remnote-target-discovery-request.v1';targetDiscoveryResult='remnote-target-discovery-result.v1';boundProbeRequest='remnote-public-probe-request.v2';boundProbeResult='remnote-public-probe-result.v2'}
 sourceCanary=[ordered]@{offline=[ordered]@{path=$offlinePath;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $offlinePath).Hash};liveReadOnly=[ordered]@{path=$livePath;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $livePath).Hash}}
 manifest=[ordered]@{path=$manifestPath;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash};payloadTreeSha256=$treeSha
 externalWriteCount=0;profileModified=$false;targetModified=$false;originalMediaPolicy='by-design-unsupported-anki-only';authorityRegistryAdmission='pending-core-owned-checkpoint';createdAt=[DateTime]::UtcNow.ToString('o')
}
Write-Utf8 $receiptPath (($receipt|ConvertTo-Json -Depth 10)+"`n")
Write-Output $receiptPath
