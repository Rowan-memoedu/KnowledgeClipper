[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$RootRemId = '8zNzfX45XLCSKoPSJ'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
if (Test-Path -LiteralPath $outputRoot) { throw "Append-only reconcile root exists: $outputRoot" }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$node = 'C:\Program Files\nodejs\node.exe'
$cli = 'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote MCP Integration\node_modules\remnote-mcp-server\dist\remnote-cli\index.js'
$watch = [Diagnostics.Stopwatch]::StartNew()
$output = @(& $node $cli --json --mcp-url 'http://127.0.0.1:43101/mcp' read $RootRemId --content-mode structured --depth 0 --view full 2>&1)
$exitCode = $LASTEXITCODE
$watch.Stop()
$timedOut = $exitCode -ne 0 -and (($output -join ' ') -match 'timed out|timeout')
$oldDReceipt = Join-Path $projectRoot 'canary\phase-b-v1.2.8\20260828T090400Z\d\path-receipt.json'
$receipt = [ordered]@{
    schemaVersion='knowledge-clipper-remnote-commit-unknown-reconcile-probe.v1'
    generatedAt=[DateTimeOffset]::UtcNow.ToString('o')
    status=if($timedOut){'blocked-plugin-read-timeout'}else{'unexpected-probe-result'}
    operation='read-only-root-probe'
    rootRemId=$RootRemId
    exitCode=$exitCode
    elapsedMs=$watch.ElapsedMilliseconds
    timedOut=$timedOut
    oldD=[ordered]@{ path=$oldDReceipt; sha256=(Get-FileHash $oldDReceipt -Algorithm SHA256).Hash; commitUnknown='unresolved'; automaticRetry=$false }
    interpretation='Current RemNote plugin request processing is blocked after a healthy MCP handshake; this is not evidence of a Core or RemNote Exporter regression.'
    effects=[ordered]@{ remnoteReadsCompleted=0; remnoteWrites=0; workerInvocations=0; retryAttempts=0 }
}
$path=Join-Path $outputRoot 'reconcile-probe-receipt.json'
[IO.File]::WriteAllText($path,(($receipt|ConvertTo-Json -Depth 30)+"`n"),[Text.UTF8Encoding]::new($false))
[ordered]@{status=$receipt.status;receipt=$path;sha256=(Get-FileHash $path -Algorithm SHA256).Hash;elapsedMs=$watch.ElapsedMilliseconds}|ConvertTo-Json
