$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$testProject = Join-Path $PSScriptRoot 'ContentAdapterCompatibility\ContentAdapterCompatibility.csproj'
if (-not (Test-Path -LiteralPath $dotnet)) { throw "Missing fixed .NET runtime: $dotnet" }
& $dotnet build $testProject --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Content adapter compatibility build failed: $LASTEXITCODE" }
& $dotnet run --project $testProject --configuration Release --no-build -- $project
if ($LASTEXITCODE -ne 0) { throw "Content adapter compatibility acceptance failed: $LASTEXITCODE" }
