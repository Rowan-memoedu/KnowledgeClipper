[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-CoreV260ValidationReceipts.ps1') -Raw
$publishPattern = @'
(?ms)& \$dotnet publish \(Join-Path \$ProjectRoot 'orchestrator/KnowledgeClipper\.Orchestrator\.csproj'\).*?if \(\$LASTEXITCODE -ne 0\) \{ throw 'Core runtime publish failed\.' \}\r?\n
'@
$templateWithoutPublish = [regex]::Replace($template, $publishPattern, '')
if ($templateWithoutPublish -eq $template) { throw 'Validation template publish block is missing.' }
$template = $templateWithoutPublish
$replacements = [ordered]@{
    "[string]`$RuntimeVersion = '2.6.0'" = "[string]`$RuntimeVersion = '2.7.0'"
    "if (Test-Path -LiteralPath `$runtimeRoot) { throw `"Append-only runtime root exists: `$runtimeRoot`" }" = "if (-not (Test-Path -LiteralPath `$runtimeRoot -PathType Container)) { throw `"Core 2.7.0 runtime root is missing: `$runtimeRoot`" }"
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Validation template marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}
$temporary = Join-Path $PSScriptRoot ('.New-CoreV270ValidationReceipts.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -OutputDirectory $OutputDirectory -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Core v2.7.0 validation failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }
