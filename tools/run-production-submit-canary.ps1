param([string]$CanaryId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ'))
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$root = Join-Path $project (Join-Path 'canary\desktop-production-submit' $CanaryId)
if (Test-Path -LiteralPath $root) { throw "Immutable Canary already exists: $root" }
$jobs = Join-Path ([IO.Path]::GetTempPath()) ("rqc-prod-submit-" + $CanaryId); $release = Join-Path $root 'release'
if (Test-Path -LiteralPath $jobs) { throw "Immutable short-path Canary jobs root already exists: $jobs" }
New-Item -ItemType Directory -Path $root,$jobs,$release | Out-Null

& $dotnet build (Join-Path $project 'orchestrator\RemNoteQuickClipper.Orchestrator.csproj') --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$src = Join-Path $project 'src'; $core = Get-ChildItem -LiteralPath (Join-Path $src 'RemNoteQuickClipper.Desktop.Core') -Filter '*.cs' | Where-Object Name -notlike 'SemanticPortProgram*.cs' | ForEach-Object FullName
$refs = @('/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll','/reference:System.Net.Http.dll','/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll')
$sources = @($core)
$client = Join-Path $root 'ProductionPipeCanary.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /optimize+ /target:exe /main:KnowledgeClipper.ProductionPipeCanary "/out:$client" @refs @sources "$src\ProductionPipeCanary.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$stdout = Join-Path $root 'orchestrator.stdout.log'; $stderr = Join-Path $root 'orchestrator.stderr.log'; $clientLog = Join-Path $root 'desktop-submit.log'
$arguments = @('"' + (Join-Path $project 'orchestrator\bin\Release\net8.0\KnowledgeClipper.Orchestrator.dll') + '"', 'serve', '--ipc-config', '"' + (Join-Path $project 'contracts\core\v2.3\playback-control\orchestrator-ipc.v1.1.json') + '"', '--jobs-root', '"' + $jobs + '"', '--registry', '"' + (Join-Path $project 'contracts\v1\registry\worker-registry.v1.json') + '"', '--release-root', '"' + $release + '"', '--max-active-tasks', '1')
$process = Start-Process -FilePath $dotnet -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$clientExit = 99
try {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        & $client submit $jobs *> $clientLog; $clientExit = $LASTEXITCODE
        if ($clientExit -ne 1) { break }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 200
    }
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit(5000) | Out-Null }
}
$events = @(Get-ChildItem -LiteralPath $jobs -Filter '*.json' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like '*\events\*' })
$failureEvent = $events | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -like '*command.execution_failed*' } | Select-Object -First 1
$result = if ($clientExit -eq 0) { 'PASS' } else { 'FAIL' }
$receipt = [ordered]@{
    schemaVersion='desktop-production-submit-canary.v1'; canaryId=$CanaryId; createdAt=(Get-Date).ToUniversalTime().ToString('o'); result=$result; clientExitCode=$clientExit; jobsRoot=$jobs;
    desktopOutput=(Get-Content -LiteralPath $clientLog -Raw).Trim(); commandExecutionFailedEvent=if($failureEvent){$failureEvent.FullName}else{$null};
    externalOcrInvoked=$false; remNoteInvoked=$false; registryWorkerCount=0
}
$receiptPath=Join-Path $root 'receipt.json';$receipt|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
Write-Host "PRODUCTION_DESKTOP_SUBMIT_CANARY ${result}: $receiptPath"
exit $clientExit
