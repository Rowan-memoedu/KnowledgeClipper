param(
    [switch]$Force,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Join-Path $PSScriptRoot 'anki_exporter.py'
$versionFile = Join-Path $PSScriptRoot 'KnowledgeClipper.AnkiExporter.Worker.version.txt'
$targetDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $projectRoot 'workers\anki'
}
else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
$target = Join-Path $targetDirectory 'KnowledgeClipper.AnkiExporter.Worker.exe'

if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "Missing KnowledgeClipper Windows version metadata: $versionFile"
}

if ((Test-Path -LiteralPath $target) -and -not $Force) {
    throw "Refusing to overwrite existing KnowledgeClipper worker EXE without -Force: $target"
}

$pyInstallerVersion = (python -m PyInstaller --version).Trim()
if ($LASTEXITCODE -ne 0 -or $pyInstallerVersion -ne '6.21.0') {
    throw "PyInstaller 6.21.0 is required; found '$pyInstallerVersion'"
}

$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporary = Join-Path $temporaryParent ('knowledge-clipper-anki-exporter-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null

try {
    python -m PyInstaller `
        --onefile `
        --console `
        --clean `
        --noconfirm `
        --noupx `
        --name 'KnowledgeClipper.AnkiExporter.Worker' `
        --version-file $versionFile `
        --distpath $targetDirectory `
        --workpath (Join-Path $temporary 'work') `
        --specpath (Join-Path $temporary 'spec') `
        $source
    if ($LASTEXITCODE -ne 0) {
        throw "PyInstaller failed with exit code $LASTEXITCODE"
    }
    if (-not (Test-Path -LiteralPath $target)) {
        throw "PyInstaller did not create the KnowledgeClipper worker EXE: $target"
    }
    Get-FileHash -Algorithm SHA256 -LiteralPath $target | Select-Object Path, Hash
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
    if (-not $resolvedTemporary.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean build path outside the system temp directory: $resolvedTemporary"
    }
    if (Test-Path -LiteralPath $resolvedTemporary) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}
