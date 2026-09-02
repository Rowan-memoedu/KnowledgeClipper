param(
    [string]$DesktopOutputName = 'RemNoteQuickClipper.exe',
    [switch]$IncludeLegacyExternalCanaries
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $project 'src'
$dist = Join-Path $project 'dist'
$obj = Join-Path $project 'obj'
$worker = Join-Path $project 'workers\remnote'
$desktopCore = Get-ChildItem -LiteralPath (Join-Path $src 'RemNoteQuickClipper.Desktop.Core') -Filter '*.cs' |
    Where-Object { $_.Name -ne 'SemanticPortProgram.cs' } |
    ForEach-Object { $_.FullName }
$desktopSources = @("$src\Core.cs", "$src\Program.cs", "$src\WindowsDesktopProviders.cs", "$src\DesktopUi.cs") + $desktopCore
New-Item -ItemType Directory -Force -Path $dist,$obj,$worker | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exporterDotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$exporterCsc = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\sdk\8.0.424\Roslyn\bincore\csc.dll'
$exporterFramework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$refs = @(
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll',
    '/reference:System.Net.Http.dll',
    '/reference:System.Web.Extensions.dll',
    '/reference:System.Windows.Forms.dll'
)
$desktopOutput = Join-Path $dist $DesktopOutputName
& $csc /nologo /optimize+ /target:winexe "/out:$desktopOutput" @refs @desktopSources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $csc /nologo /optimize+ /target:exe "/out:$obj\CoreTests.exe" @refs "$src\Core.cs" "$src\Tests.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($IncludeLegacyExternalCanaries) {
    Write-Warning 'Building legacy external Canary executables. Do not run them against production RemNote/Anki targets.'
    & $csc /nologo /optimize+ /target:exe "/out:$dist\SmokeTest.exe" @refs "$src\Core.cs" "$src\SmokeTest.cs"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
& $csc /nologo /optimize+ /target:winexe /main:RemNoteQuickClipper.ReviewPreview "/out:$obj\ReviewPreview.exe" @refs @desktopSources "$src\ReviewPreview.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $csc /nologo /optimize+ /target:winexe /main:RemNoteQuickClipper.FloatingPreview "/out:$obj\FloatingPreview.exe" @refs @desktopSources "$src\FloatingPreview.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $csc /nologo /optimize+ /target:winexe /main:RemNoteQuickClipper.SettingsPreview "/out:$obj\SettingsPreview.exe" @refs @desktopSources "$src\SettingsPreview.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $csc /nologo /optimize+ /target:exe "/out:$obj\InputCanary.exe" @refs "$src\Core.cs" "$src\InputCanary.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($IncludeLegacyExternalCanaries) {
    & $csc /nologo /optimize+ /target:exe "/out:$dist\TtsCanary.exe" @refs "$src\Core.cs" "$src\TtsCanary.cs"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$exporterRefs = @(
    "/reference:$exporterFramework\mscorlib.dll",
    "/reference:$exporterFramework\System.dll",
    "/reference:$exporterFramework\System.Core.dll",
    "/reference:$exporterFramework\System.Drawing.dll",
    "/reference:$exporterFramework\System.IO.Compression.dll",
    "/reference:$exporterFramework\System.IO.Compression.FileSystem.dll",
    "/reference:$exporterFramework\System.Net.Http.dll",
    "/reference:$exporterFramework\System.Web.Extensions.dll",
    "/reference:$exporterFramework\System.Windows.Forms.dll"
)
& $exporterDotnet $exporterCsc /nologo /noconfig /nostdlib+ /optimize+ /deterministic+ /target:exe /main:RemNoteQuickClipper.RemNoteExporterCli "/out:$worker\RemNoteQuickClipper.RemNoteExporter.Worker.exe" @exporterRefs "$src\RemNoteExporterRuntime.cs" "$src\RemNoteExporter.cs" "$src\RemNoteExporterWorker.cs" "$src\RemNoteExporterCli.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item -LiteralPath (Join-Path $worker 'RemNoteQuickClipper.RemNoteExporter.Worker.exe') -Destination (Join-Path $dist 'RemNoteExporter.exe') -Force
& $exporterDotnet $exporterCsc /nologo /noconfig /nostdlib+ /optimize+ /deterministic+ /target:exe /main:RemNoteQuickClipper.RemNoteExporterTests "/out:$obj\RemNoteExporterTests.exe" @exporterRefs "$src\RemNoteExporterRuntime.cs" "$src\RemNoteExporter.cs" "$src\RemNoteExporterWorker.cs" "$src\RemNoteExporterTests.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $csc /nologo /optimize+ /target:exe /main:RemNoteQuickClipper.AudioCanary "/out:$obj\AudioCanary.exe" @refs @desktopSources "$src\AudioCanary.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item -LiteralPath (Join-Path $project 'appsettings.json') -Destination (Join-Path $dist 'appsettings.json') -Force
Copy-Item -LiteralPath (Join-Path $project 'appsettings.json') -Destination (Join-Path $worker 'appsettings.json') -Force
& "$obj\CoreTests.exe"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$obj\RemNoteExporterTests.exe"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built: $desktopOutput"
