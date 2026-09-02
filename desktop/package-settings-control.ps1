param([Parameter(Mandatory=$true)][string]$BuildId)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$distRoot = Join-Path $project 'dist\desktop-win-x64'
$output = Join-Path $distRoot $BuildId
if (Test-Path -LiteralPath $output) { throw "Append-only output exists: $output" }

& $dotnet publish (Join-Path $project 'desktop\RemNoteQuickClipper.Desktop.csproj') -c Release --no-restore -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$resolved = [IO.Path]::GetFullPath($output)
$prefix = [IO.Path]::GetFullPath($distRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe desktop package root.' }
foreach ($name in @('KnowledgeClipper.Orchestrator.exe','KnowledgeClipper.Orchestrator.deps.json','KnowledgeClipper.Orchestrator.runtimeconfig.json','KnowledgeClipper.Orchestrator.pdb')) {
    $path = Join-Path $resolved $name
    if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
}

$byName = @{}
[string[]]$names = @(Get-ChildItem -LiteralPath $resolved -File -Recurse | Where-Object Name -ne 'desktop-build-receipt.v1.json' | ForEach-Object { $relative=[IO.Path]::GetRelativePath($resolved,$_.FullName).Replace('\','/'); $byName[$relative] = $_; $relative })
[Array]::Sort[string]($names, [StringComparer]::Ordinal)
$files = @($names | ForEach-Object { $byName[$_] })
$entries = @(); [long]$bytes = 0; $material = ''
foreach ($file in $files) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative=[IO.Path]::GetRelativePath($resolved,$file.FullName).Replace('\','/')
    $entries += [ordered]@{ path=$relative; bytes=$file.Length; sha256=$hash }
    $bytes += $file.Length
    $material += $relative + "`n" + $hash + "`n" + $file.Length + "`n"
}
$sha = [Security.Cryptography.SHA256]::Create()
try { $tree = [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant() }
finally { $sha.Dispose() }
$receipt = [ordered]@{
    schemaVersion='desktop-build-receipt.v1'; buildId=$BuildId; createdAt=(Get-Date).ToUniversalTime().ToString('o')
    productName='零碎知识剪藏'; assemblyName='KnowledgeClipper'; entrypoint='KnowledgeClipper.exe'
    runtimeIdentifier='win-x64'; selfContained=$true; sdkVersion='8.0.424'
    fileCount=$entries.Count; totalBytes=$bytes; treeSha256=$tree
    executableSha256=($entries | Where-Object path -eq 'KnowledgeClipper.exe').sha256
    applicationDllSha256=($entries | Where-Object path -eq 'KnowledgeClipper.dll').sha256
    files=$entries
}
$receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $resolved 'desktop-build-receipt.v1.json') -Encoding utf8NoBOM
Write-Output "BUILD_ID=$BuildId"
Write-Output "PACKAGE=$resolved"
Write-Output "FILES=$($entries.Count) BYTES=$bytes TREE=$tree"
