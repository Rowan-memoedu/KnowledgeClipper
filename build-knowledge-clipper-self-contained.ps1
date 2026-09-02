param(
    [string]$BuildId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $project 'build-desktop-self-contained.ps1') -BuildId $BuildId
exit $LASTEXITCODE
