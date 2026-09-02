param(
    [string]$PyInstallerVersion = '6.22.2'
)

$ErrorActionPreference = 'Stop'
$workerRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $workerRoot 'image_ocr_worker.py'
$candidateName = 'KnowledgeClipper.ImageOcr.Worker'
$output = Join-Path $workerRoot ($candidateName + '.exe')
$legacyCompatibilityAlias = Join-Path $workerRoot 'RemNoteQuickClipper.ImageOcr.Worker.exe'
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) ('knowledge-clipper-image-ocr-build-' + [Guid]::NewGuid().ToString('N'))
$workPath = Join-Path $buildRoot 'work'
$specPath = Join-Path $buildRoot 'spec'
$distPath = Join-Path $buildRoot 'dist'
New-Item -ItemType Directory -Path $workPath,$specPath,$distPath -ErrorAction Stop | Out-Null

try {
    if (Test-Path -LiteralPath $output) {
        throw "Refusing to overwrite existing candidate: $output"
    }
    & pipx run --spec ("pyinstaller==" + $PyInstallerVersion) pyinstaller `
        --onefile `
        --console `
        --clean `
        --noconfirm `
        --name $candidateName `
        --distpath $distPath `
        --workpath $workPath `
        --specpath $specPath `
        $source
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed with exit code $LASTEXITCODE" }
    $builtOutput = Join-Path $distPath ($candidateName + '.exe')
    if (-not (Test-Path -LiteralPath $builtOutput -PathType Leaf)) { throw 'Candidate worker EXE was not produced' }

    $describeRaw = & $builtOutput describe --json
    if ($LASTEXITCODE -ne 0) { throw 'Built worker describe failed' }
    $describe = $describeRaw | ConvertFrom-Json
    if ($describe.workerId -ne 'image.ocr.mineru' -or $describe.schemaVersion -ne '1.0.0' -or $describe.workerVersion -ne '1.1.0') {
        throw 'Built worker describe identity mismatch'
    }
    if (($describeRaw -join "`n") -match '(?i)remnote|anki') { throw 'Built worker descriptor is destination-coupled' }

    Move-Item -LiteralPath $builtOutput -Destination $output -ErrorAction Stop
    $legacyDescribe = $null
    if (Test-Path -LiteralPath $legacyCompatibilityAlias -PathType Leaf) {
        $legacyDescribe = (& $legacyCompatibilityAlias describe --json) | ConvertFrom-Json
    }

    [pscustomobject]@{
        productDisplayName = '零碎知识剪藏'
        assemblyRoot = 'KnowledgeClipper'
        candidatePath = 'workers/image-ocr/KnowledgeClipper.ImageOcr.Worker.exe'
        bytes = (Get-Item -LiteralPath $output).Length
        sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
        workerId = $describe.workerId
        workerVersion = $describe.workerVersion
        pyInstallerVersion = $PyInstallerVersion
        legacyCompatibilityAlias = if ($legacyDescribe) {
            [ordered]@{
                path = 'workers/image-ocr/RemNoteQuickClipper.ImageOcr.Worker.exe'
                workerId = $legacyDescribe.workerId
                workerVersion = $legacyDescribe.workerVersion
                sha256 = (Get-FileHash -LiteralPath $legacyCompatibilityAlias -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        } else { $null }
    } | ConvertTo-Json -Depth 4
}
finally {
    if (Test-Path -LiteralPath $buildRoot) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        $resolvedBuildRoot = [IO.Path]::GetFullPath($buildRoot).TrimEnd('\')
        if (-not $resolvedBuildRoot.StartsWith($tempRoot + '\knowledge-clipper-image-ocr-build-', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected build path: $resolvedBuildRoot"
        }
        Remove-Item -LiteralPath $resolvedBuildRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
