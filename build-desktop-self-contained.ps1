param(
    [string]$BuildId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$csproj = Join-Path $project 'desktop\RemNoteQuickClipper.Desktop.csproj'
$output = Join-Path $project (Join-Path 'dist\desktop-win-x64' $BuildId)
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "Fixed .NET SDK missing: $dotnet" }
if (Test-Path -LiteralPath $output) { throw "Immutable candidate output already exists: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
& $dotnet publish $csproj --configuration Release --runtime win-x64 --self-contained true --output $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$sdkVersion = (& $dotnet --version).Trim()
$executable = Join-Path $output 'KnowledgeClipper.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Published executable missing: $executable" }
[string[]]$fileNames = @(Get-ChildItem -LiteralPath $output -File | ForEach-Object Name)
[Array]::Sort[string]($fileNames, [Comparison[string]]{ param($left, $right) [StringComparer]::Ordinal.Compare($left, $right) })
$files = $fileNames | ForEach-Object {
    $item = Get-Item -LiteralPath (Join-Path $output $_)
    [pscustomobject][ordered]@{ path = $item.Name; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$treeBuilder = [Text.StringBuilder]::new()
foreach ($file in $files) { [void]$treeBuilder.Append($file.path).Append("`n").Append($file.sha256).Append("`n").Append($file.bytes).Append("`n") }
$treeMaterial = $treeBuilder.ToString()
$sha = [System.Security.Cryptography.SHA256]::Create()
try { $treeSha256 = [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($treeMaterial))).ToLowerInvariant() }
finally { $sha.Dispose() }
$receipt = [ordered]@{
    schemaVersion = 'desktop-build-receipt.v1'
    buildId = $BuildId
    createdAt = (Get-Date).ToUniversalTime().ToString('o')
    sdkPath = $dotnet
    sdkVersion = $sdkVersion
    targetFramework = 'net8.0-windows'
    runtimeIdentifier = 'win-x64'
    selfContained = $true
    receiptExcludedFromTreeHash = $true
    treeHashAlgorithm = 'sha256(utf8 concat ordinal(path + LF + sha256 + LF + bytes + LF))'
    treeSha256 = $treeSha256
    fileCount = @($files).Count
    totalBytes = [long](($files | ForEach-Object { [long]$_.bytes } | Measure-Object -Sum).Sum)
    executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    applicationDllSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'KnowledgeClipper.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    files = @($files)
}
$receiptPath = Join-Path $output 'desktop-build-receipt.v1.json'
$temporaryReceipt = $receiptPath + '.tmp-' + [Guid]::NewGuid().ToString('N')
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporaryReceipt -Encoding utf8NoBOM
Move-Item -LiteralPath $temporaryReceipt -Destination $receiptPath
Write-Host "Published immutable desktop candidate: $output"
Write-Host "SDK: $sdkVersion"
