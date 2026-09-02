$ErrorActionPreference = 'Stop'

$tests = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = (Resolve-Path (Join-Path $tests '..\..')).Path
$tool = Join-Path $project 'tools\Get-DesktopSourceSnapshot.ps1'
$fixtureRoot = Join-Path $project 'obj\desktop-source-snapshot-fixture'
if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

$names = @('Z.txt', 'a.txt', 'ä.txt')
foreach ($name in $names) {
    [IO.File]::WriteAllText((Join-Path $fixtureRoot $name), "fixture:$name", [Text.UTF8Encoding]::new($false))
}

$declaredEntries = @()
foreach ($name in @('ä.txt', 'a.txt', 'Z.txt')) {
    $path = Join-Path $fixtureRoot $name
    $declaredEntries += [ordered]@{
        path = $name
        bytes = (Get-Item -LiteralPath $path).Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$fixtureReceipt = Join-Path $fixtureRoot 'source-receipt.json'
[ordered]@{ sourceSnapshot = [ordered]@{ entries = $declaredEntries } } |
    ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $fixtureReceipt -Encoding utf8NoBOM

$explicitOrdinalOrder = @('Z.txt', 'a.txt', 'ä.txt')
$builder = [Text.StringBuilder]::new()
foreach ($name in $explicitOrdinalOrder) {
    $entry = $declaredEntries | Where-Object { $_.path -ceq $name } | Select-Object -First 1
    [void]$builder.Append($name).Append([char]0).Append(([long]$entry.bytes).ToString([Globalization.CultureInfo]::InvariantCulture)).Append([char]0).Append($entry.sha256).Append("`n")
}
$expectedTree = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($builder.ToString()))).ToLowerInvariant()
$snapshot = & $tool -WorkspaceRoot $fixtureRoot -SourceReceiptPath $fixtureReceipt -ExpectedTreeSha256 $expectedTree | ConvertFrom-Json -Depth 10

if ($snapshot.treeSha256 -cne $expectedTree) { throw 'Ordinal source snapshot tree did not match independent fixed-order computation.' }
if ((@($snapshot.entries.path) -join '|') -cne ($explicitOrdinalOrder -join '|')) { throw 'Source snapshot entries are not in StringComparer.Ordinal order.' }
[long]$expectedBytes = 0
foreach ($entry in $declaredEntries) { $expectedBytes += [long]$entry.bytes }
if ($snapshot.files -ne 3 -or $snapshot.bytes -ne $expectedBytes) { throw 'Source snapshot aggregate mismatch.' }

$failedClosed = $false
try {
    & $tool -WorkspaceRoot $fixtureRoot -SourceReceiptPath $fixtureReceipt -ExpectedTreeSha256 ('0' * 64) | Out-Null
} catch {
    $failedClosed = $_.Exception.Message -like 'Source snapshot tree mismatch:*'
}
if (-not $failedClosed) { throw 'Source snapshot expected-tree mismatch did not fail closed.' }

Write-Output "PASS desktop source snapshot ordinal tree files=$($snapshot.files) tree=$expectedTree"
