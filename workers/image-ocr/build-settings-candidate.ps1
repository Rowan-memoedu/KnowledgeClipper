param(
    [string]$PyInstallerVersion = '6.22.2'
)

$ErrorActionPreference = 'Stop'
$workerRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $workerRoot 'image_ocr_worker.py'
$version = '1.2.0'
$candidateName = 'KnowledgeClipper.ImageOcr.Worker'
$candidateRoot = Join-Path $workerRoot ('candidates\' + $version)
$output = Join-Path $candidateRoot ($candidateName + '.exe')
$receiptPath = Join-Path $candidateRoot 'build-receipt.json'
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) ('knowledge-clipper-image-ocr-settings-build-' + [Guid]::NewGuid().ToString('N'))
$workPath = Join-Path $buildRoot 'work'
$specPath = Join-Path $buildRoot 'spec'
$distPath = Join-Path $buildRoot 'dist'

if (Test-Path -LiteralPath $output -PathType Leaf) { throw "Refusing to overwrite existing versioned candidate: $output" }
if (Test-Path -LiteralPath $receiptPath -PathType Leaf) { throw "Refusing to overwrite existing build receipt: $receiptPath" }
New-Item -ItemType Directory -Path $candidateRoot,$workPath,$specPath,$distPath -ErrorAction Stop | Out-Null

try {
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
    if (-not (Test-Path -LiteralPath $builtOutput -PathType Leaf)) { throw 'Versioned candidate EXE was not produced' }
    $describeRaw = & $builtOutput describe --json
    if ($LASTEXITCODE -ne 0) { throw 'Versioned candidate describe failed' }
    $describe = $describeRaw | ConvertFrom-Json
    if ($describe.workerId -ne 'image.ocr.mineru' -or $describe.schemaVersion -ne '1.0.0' -or $describe.workerVersion -ne $version) {
        throw 'Versioned candidate identity mismatch'
    }
    Move-Item -LiteralPath $builtOutput -Destination $output -ErrorAction Stop
    $receipt = [ordered]@{
        schemaVersion = 'knowledge-clipper-image-ocr-build-receipt.v1'
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        workerId = $describe.workerId
        workerVersion = $describe.workerVersion
        contractVersion = $describe.contractVersion
        candidatePath = 'workers/image-ocr/candidates/1.2.0/KnowledgeClipper.ImageOcr.Worker.exe'
        bytes = (Get-Item -LiteralPath $output).Length
        sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
        pyInstallerVersion = $PyInstallerVersion
        replacesAuthorityCandidate = $false
        registryOnboarding = 'NO-GO-until-new-authority-checkpoint'
        externalServiceCalled = $false
    }
    $json = $receipt | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($receiptPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $json
}
finally {
    if (Test-Path -LiteralPath $buildRoot) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        $resolvedBuildRoot = [IO.Path]::GetFullPath($buildRoot).TrimEnd('\')
        if (-not $resolvedBuildRoot.StartsWith($tempRoot + '\knowledge-clipper-image-ocr-settings-build-', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected build path: $resolvedBuildRoot"
        }
        Remove-Item -LiteralPath $resolvedBuildRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
