[CmdletBinding()]
param([string]$OutputRoot)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $project "canary\remnote-exporter-v2.3-live-readonly-discovery\$stamp" }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "Append-only output exists: $OutputRoot" }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

function Write-Utf8([string]$Path, [string]$Value) { [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false)) }
function Evidence([string]$Path) { $item=Get-Item -LiteralPath $Path; [ordered]@{path=$item.FullName;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $item.FullName).Hash;bytes=$item.Length} }

$worker = Join-Path $project 'workers\remnote\KnowledgeClipper.RemNoteExporter.Worker.exe'
$requestPath = Join-Path $OutputRoot 'request.json'
$request = [ordered]@{
    schemaVersion='remnote-target-discovery-request.v1'; discoveryId=('live-formal-title-discovery-' + $stamp)
    authorizationScope='configured-destination'; timeoutMs=15000; operations=@('status','search','list','read')
    selector=[ordered]@{rootTitle='剪藏';expectedType='document';match='exact';uniqueness='required'}
}
Write-Utf8 $requestPath (($request | ConvertTo-Json -Depth 8) + "`n")
$resultPath = Join-Path $OutputRoot 'result.json'
$resultText = (& $worker discover-target-public --request $requestPath --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "live read-only discovery failed exit=$LASTEXITCODE`n$resultText" }
Write-Utf8 $resultPath ($resultText + "`n")
$result = $resultText | ConvertFrom-Json
if ($result.state -ne 'healthy' -or $result.admission -ne 'ready' -or $result.externalWriteCount -ne 0 -or -not $result.sanitized -or $result.selection.resolution -ne 'unique' -or $result.selection.candidateCount -ne 1 -or -not $result.selection.exactReadback -or -not $result.selection.persistableBinding -or $result.selection.observedTitle -ne '剪藏' -or $result.selection.observedType -ne 'document') { throw 'live read-only discovery result mismatch' }
$ops = @($result.toolChecks | ForEach-Object operation)
if (($ops -join ',') -ne 'status,search,list,read' -or @($result.toolChecks | Where-Object outcome -ne 'passed').Count -ne 0) { throw 'live discovery tool checks mismatch' }

$descriptorPath = Join-Path $OutputRoot 'descriptor.json'
$descriptorText = (& $worker settings companion --version 3 --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'descriptor failed' }
Write-Utf8 $descriptorPath ($descriptorText + "`n")
$schemaLog = Join-Path $OutputRoot 'schema-validation.log'
$schemaText = (& python (Join-Path $PSScriptRoot 'validate-additive-v2.3.py') --descriptor $descriptorPath --result $resultPath 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { throw "schema validation failed`n$schemaText" }
Write-Utf8 $schemaLog $schemaText

$settingsPath = Join-Path $env:LOCALAPPDATA 'KnowledgeClipper\workflow-settings.v3.json'
$settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
$active = $settings.profiles.($settings.activeProfileId)
$diagnostic = [ordered]@{
    schemaVersion='remnote-v1233-probe-failure-diagnostic.v1'; observedAt=[DateTime]::UtcNow.ToString('o')
    activeProfile=[ordered]@{id=$settings.activeProfileId;remnoteEnabled=[bool]$active.remnote.enabled;rootTitle=$active.remnote.target.rootTitle;rootRemIdPresent=-not [string]::IsNullOrWhiteSpace([string]$active.remnote.target.rootRemId);settingsFile=(Evidence $settingsPath)}
    desktopDispatch=[ordered]@{latestRemnoteProbeArtifactFound=$false;reason='pre-dispatch target binding guard';externalWriteCount=0}
    liveReadOnly=[ordered]@{state=$result.state;connected=$result.connection.connected;acceptWriteOperations=$result.connection.acceptWriteOperations;serverVersion=$result.connection.serverVersion;pluginVersion=$result.connection.pluginVersion;resolvedRootRemId=$result.selection.resolvedRootRemId;observedTitle=$result.selection.observedTitle;observedType=$result.selection.observedType;exactReadback=$result.selection.exactReadback;externalWriteCount=0}
    rootCause='active profile has no authoritative rootRemId, so Desktop v1.2.33 stops before Core; MCP and exact target discovery are healthy'
}
$diagnosticPath = Join-Path $OutputRoot 'v1.2.33-diagnostic.json'; Write-Utf8 $diagnosticPath (($diagnostic | ConvertTo-Json -Depth 10) + "`n")

$receiptPath = Join-Path $OutputRoot 'receipt.json'
$receipt = [ordered]@{
    schemaVersion='remnote-exporter-v2.3-live-readonly-discovery-receipt.v1';status='passed';createdAt=[DateTime]::UtcNow.ToString('o')
    realMcpCalled=$true;realRemNoteWriteCalled=$false;externalWriteCount=0;profileModified=$false;targetModified=$false
    worker=[ordered]@{id='export.remnote';version='2.3.0';executable=(Evidence $worker)}
    request=(Evidence $requestPath);result=(Evidence $resultPath);descriptor=(Evidence $descriptorPath);schemaValidation=(Evidence $schemaLog);diagnostic=(Evidence $diagnosticPath)
    binding=[ordered]@{rootTitle='剪藏';resolvedRootRemId=$result.selection.resolvedRootRemId;observedType=$result.selection.observedType;candidateCount=$result.selection.candidateCount;exactReadback=$result.selection.exactReadback;persistableBinding=$result.selection.persistableBinding;effectiveAt='next-task'}
    connection=[ordered]@{connected=$result.connection.connected;acceptWriteOperations=$result.connection.acceptWriteOperations;serverVersion=$result.connection.serverVersion;pluginVersion=$result.connection.pluginVersion}
    toolChecks=$result.toolChecks;reasonCodes=$result.reasonCodes
}
Write-Utf8 $receiptPath (($receipt | ConvertTo-Json -Depth 12) + "`n")
Write-Output $receiptPath
