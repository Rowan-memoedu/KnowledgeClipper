param([string]$CanaryId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ'))
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$root = Join-Path $project (Join-Path 'canary\desktop-production-pipe' $CanaryId)
if (Test-Path -LiteralPath $root) { throw "Immutable Canary already exists: $root" }
$jobs = Join-Path $root 'jobs'; $release = Join-Path $root 'release'
New-Item -ItemType Directory -Path $root,$jobs,$release | Out-Null

& $dotnet build (Join-Path $project 'orchestrator\RemNoteQuickClipper.Orchestrator.csproj') --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$src = Join-Path $project 'src'
$core = Get-ChildItem -LiteralPath (Join-Path $src 'RemNoteQuickClipper.Desktop.Core') -Filter '*.cs' | Where-Object Name -notlike 'SemanticPortProgram*.cs' | ForEach-Object FullName
$refs = @('/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll','/reference:System.Net.Http.dll','/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll')
$sources = @($core)
$client = Join-Path $root 'ProductionPipeCanary.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /optimize+ /target:exe /main:KnowledgeClipper.ProductionPipeCanary "/out:$client" @refs @sources "$src\ProductionPipeCanary.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$orchestratorDll = Join-Path $project 'orchestrator\bin\Release\net8.0\KnowledgeClipper.Orchestrator.dll'
$stdout = Join-Path $root 'orchestrator.stdout.log'; $stderr = Join-Path $root 'orchestrator.stderr.log'
$arguments = @('"' + $orchestratorDll + '"', 'serve', '--ipc-config', '"' + (Join-Path $project 'contracts\core\v2.3\playback-control\orchestrator-ipc.v1.1.json') + '"', '--jobs-root', '"' + $jobs + '"', '--registry', '"' + (Join-Path $project 'contracts\v1\registry\worker-registry.v1.json') + '"', '--release-root', '"' + $release + '"', '--max-active-tasks', '1')
$process = Start-Process -FilePath $dotnet -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$queryOutput = Join-Path $root 'desktop-query.log'; $passed = $false
try {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        & $client $jobs *> $queryOutput
        if ($LASTEXITCODE -eq 0) { $passed = $true; break }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $passed) { throw "Production orchestrator Named Pipe Canary failed. See $stderr and $queryOutput" }
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit(5000) | Out-Null }
}

$receipt = [ordered]@{
    schemaVersion = 'desktop-production-pipe-canary.v1'
    canaryId = $CanaryId
    createdAt = (Get-Date).ToUniversalTime().ToString('o')
    orchestratorProcessId = $process.Id
    orchestratorWasStartedByCanary = $true
    action = 'task.status.query-recent'
    externalOcrInvoked = $false
    remNoteInvoked = $false
    result = 'PASS'
    queryOutput = (Get-Content -LiteralPath $queryOutput -Raw).Trim()
}
$receiptPath = Join-Path $root 'receipt.json'
$receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
Write-Host "PRODUCTION_ORCHESTRATOR_PIPE_CANARY PASS: $receiptPath"
