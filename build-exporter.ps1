$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $project 'src'
$dist = Join-Path $project 'dist'
$obj = Join-Path $project 'obj'
$worker = Join-Path $project 'workers\remnote'
New-Item -ItemType Directory -Force -Path $dist,$obj,$worker | Out-Null
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$csc = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\sdk\8.0.424\Roslyn\bincore\csc.dll'
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath $dotnet)) { throw "Missing deterministic compiler host: $dotnet" }
if (-not (Test-Path -LiteralPath $csc)) { throw "Missing deterministic compiler: $csc" }
$refs = @(
    "/reference:$framework\mscorlib.dll",
    "/reference:$framework\System.dll",
    "/reference:$framework\System.Core.dll",
    "/reference:$framework\System.Drawing.dll",
    "/reference:$framework\System.IO.Compression.dll",
    "/reference:$framework\System.IO.Compression.FileSystem.dll",
    "/reference:$framework\System.Net.Http.dll",
    "/reference:$framework\System.Web.Extensions.dll",
    "/reference:$framework\System.Windows.Forms.dll"
)
$canonicalWorker = Join-Path $worker 'KnowledgeClipper.RemNoteExporter.Worker.exe'
$legacyWorkerAlias = Join-Path $worker 'RemNoteQuickClipper.RemNoteExporter.Worker.exe'
& $dotnet $csc /nologo /noconfig /nostdlib+ /warnaserror+ /optimize+ /deterministic+ /target:exe /main:KnowledgeClipper.RemNoteExporterCli "/out:$canonicalWorker" @refs "$src\RemNoteExporterRuntime.cs" "$src\RemNoteOriginalMedia.cs" "$src\RemNoteExporter.cs" "$src\RemNoteExporterWorker.cs" "$src\RemNoteReadOnlyProbe.cs" "$src\RemNotePublicProbe.cs" "$src\RemNoteTargetDiscovery.cs" "$src\RemNoteSettingsAdapter.cs" "$src\RemNoteExporterCli.cs" "$src\RemNoteExporterAssemblyInfo.cs"
if ($LASTEXITCODE -ne 0) { throw "RemNote Exporter worker compilation failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath $canonicalWorker -Destination (Join-Path $dist 'KnowledgeClipper.RemNoteExporter.Worker.exe') -Force
Copy-Item -LiteralPath $canonicalWorker -Destination $legacyWorkerAlias -Force
Copy-Item -LiteralPath $canonicalWorker -Destination (Join-Path $dist 'RemNoteExporter.exe') -Force
& $dotnet $csc /nologo /noconfig /nostdlib+ /warnaserror+ /optimize+ /deterministic+ /target:exe /main:KnowledgeClipper.RemNoteExporterTests "/out:$obj\KnowledgeClipper.RemNoteExporter.Tests.exe" @refs "$src\RemNoteExporterRuntime.cs" "$src\RemNoteOriginalMedia.cs" "$src\RemNoteExporter.cs" "$src\RemNoteExporterWorker.cs" "$src\RemNoteReadOnlyProbe.cs" "$src\RemNotePublicProbe.cs" "$src\RemNoteTargetDiscovery.cs" "$src\RemNoteSettingsAdapter.cs" "$src\RemNoteExporterTests.cs" "$src\RemNoteExporterTestAssemblyInfo.cs"
if ($LASTEXITCODE -ne 0) { throw "RemNote Exporter test compilation failed with exit code $LASTEXITCODE" }
$workerConfig = Join-Path $worker 'appsettings.json'
Copy-Item -LiteralPath $workerConfig -Destination (Join-Path $dist 'appsettings.json') -Force
& "$obj\KnowledgeClipper.RemNoteExporter.Tests.exe"
if ($LASTEXITCODE -ne 0) { throw "RemNote Exporter tests failed with exit code $LASTEXITCODE" }
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($canonicalWorker)
$assemblyName = [Reflection.AssemblyName]::GetAssemblyName($canonicalWorker).Name
if ($assemblyName -ne 'KnowledgeClipper.RemNoteExporter.Worker' -or $versionInfo.ProductName -ne '零碎知识剪藏' -or $versionInfo.FileDescription -ne 'KnowledgeClipper RemNote Exporter Worker') {
    throw "Canonical worker assembly metadata does not match KnowledgeClipper product identity"
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $canonicalWorker).Hash -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $legacyWorkerAlias).Hash) {
    throw "Legacy worker alias bytes do not match the canonical KnowledgeClipper assembly"
}
Write-Host "Built canonical: $canonicalWorker"
Write-Host "Legacy compatibility alias: $legacyWorkerAlias"
