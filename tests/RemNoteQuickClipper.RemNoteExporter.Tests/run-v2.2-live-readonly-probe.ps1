[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$ReleaseRoot,
 [Parameter(Mandatory=$true)][string]$RootRemId,
 [Parameter(Mandatory=$true)][string]$RootTitle,
 [string]$OutputRoot
)
$ErrorActionPreference='Stop'
$project=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ReleaseRoot=(Resolve-Path -LiteralPath $ReleaseRoot).Path
$worker=Join-Path $ReleaseRoot 'KnowledgeClipper.RemNoteExporter.Worker.exe'
if(!(Test-Path -LiteralPath $worker -PathType Leaf)){throw 'release worker missing'}
if($RootTitle -eq '剪藏'){throw 'Formal root is forbidden for isolated read-only probe'}
$stamp=(Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
if([string]::IsNullOrWhiteSpace($OutputRoot)){$OutputRoot=Join-Path $project "canary\remnote-exporter-v2.2-live-readonly-probe\$stamp"}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot);if(Test-Path -LiteralPath $OutputRoot){throw "Append-only output exists: $OutputRoot"};New-Item -ItemType Directory -Path $OutputRoot|Out-Null
function Write-Utf8([string]$p,[string]$v){[IO.File]::WriteAllText($p,$v,[Text.UTF8Encoding]::new($false))}
function Evidence([string]$p){$i=Get-Item -LiteralPath $p;[ordered]@{path=$i.FullName;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $i.FullName).Hash;bytes=$i.Length}}
$requestPath=Join-Path $OutputRoot 'request.json'
$request=[ordered]@{schemaVersion='remnote-public-probe-request.v2';probeId="live-readonly-$stamp";authorizationScope='isolated-canary';timeoutMs=15000;operations=@('status','search','list','read');target=[ordered]@{rootRemId=$RootRemId;rootTitle=$RootTitle}}
Write-Utf8 $requestPath (($request|ConvertTo-Json -Depth 6)+"`n")
$resultPath=Join-Path $OutputRoot 'result.json'
$text=(& $worker probe-public --request $requestPath --json 2>&1|Out-String).Trim();$exit=$LASTEXITCODE
Write-Utf8 $resultPath ($text+"`n")
$result=$text|ConvertFrom-Json
if($exit -ne 0 -or $result.state -ne 'healthy' -or $result.admission -ne 'ready'){throw "read-only probe blocked: exit=$exit state=$($result.state) reasons=$(@($result.reasonCodes)-join ',')"}
if($result.externalWriteCount -ne 0 -or -not $result.sanitized -or -not $result.targetBinding.matched -or $result.targetBinding.observedType -ne 'document'){throw 'read-only probe safety/binding mismatch'}
$receiptPath=Join-Path $OutputRoot 'receipt.json'
$receipt=[ordered]@{schemaVersion='remnote-exporter-v2.2-live-readonly-probe.v1';status='passed';createdAt=[DateTime]::UtcNow.ToString('o');releaseRoot=$ReleaseRoot;worker=(Evidence $worker);request=(Evidence $requestPath);result=(Evidence $resultPath);rootBinding=$result.targetBinding;connection=$result.connection;toolChecks=$result.toolChecks;externalCallCount=$result.externalCallCount;externalWriteCount=0;exportInvoked=$false;knowledgeContentReturned=$false;formalRootTouched=$false;originalMediaCapability=$result.routeCapabilities.originalMedia}
Write-Utf8 $receiptPath (($receipt|ConvertTo-Json -Depth 12)+"`n");Write-Output $receiptPath
