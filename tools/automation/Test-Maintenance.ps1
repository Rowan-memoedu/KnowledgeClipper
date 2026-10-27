[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryPath,
    [Parameter(Mandatory)][string]$RunDirectory,
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
)
. (Join-Path $PSScriptRoot 'Maintenance.Common.ps1')
$run = Assert-RunDirectory $RunDirectory
$reports = Join-Path $run 'reports'
$build = Join-Path $run 'build'
$cache = $run.Replace('D:\Artifacts\GitHubActions\', 'D:\Caches\GitHubActions\') + '\build'
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$passed = [Collections.Generic.List[string]]::new()
$success = $false
$oldChatOutput = $env:KC_CHAT_OUTPUT
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = 'D:\Caches\NuGet\packages'
Push-Location -LiteralPath $RepositoryPath
try {
    Invoke-Checked git @('diff', '--check')
    $python = if ($env:KNOWLEDGECLIPPER_CODEX_PYTHON) { $env:KNOWLEDGECLIPPER_CODEX_PYTHON } else { 'D:\Applications\RemNoteQuickClipperRuntime\codex-python\Scripts\python.exe' }
    Write-Host "Checking the Python interpreter used by offline chat fixtures: $python"
    Invoke-Checked $python @('--version')
    Write-Host 'Building the desktop application into the per-run D: directory...'
    & .\build.ps1 -DotnetPath $DotnetPath -BuildOutput $build -BuildCache $cache
    if ($LASTEXITCODE -ne 0) { throw "Desktop build failed with exit code $LASTEXITCODE" }
    $passed.Add('desktop Release build')
    foreach ($name in @('KnowledgeClipper.Workflow.Tests', 'KnowledgeClipper.Chat.Tests')) {
        Write-Host "Building and executing $name (offline)..."
        Invoke-Checked $DotnetPath @('build', "tests\$name\$name.csproj", '-c', 'Release', '--nologo', "-p:KnowledgeClipperBuildOutput=$build", "-p:KnowledgeClipperBuildCache=$cache")
        $dll = Join-Path $build "$name\Release\net8.0\$name.dll"
        $testArgs = @($dll, $RepositoryPath)
        if ($name -eq 'KnowledgeClipper.Chat.Tests') { $testArgs += (Join-Path $run 'chat-fixtures') }
        Invoke-Checked $DotnetPath $testArgs
        $passed.Add($name)
    }
    foreach ($component in @('remnote-host', 'remnote-plugin')) {
        Invoke-Checked npm.cmd @('ci', '--prefix', $component, '--cache', 'D:\Caches\npm-cache', '--no-audit', '--no-fund')
        Invoke-Checked npm.cmd @('test', '--prefix', $component)
        $passed.Add("$component tests")
    }
    Invoke-Checked npm.cmd @('run', 'typecheck', '--prefix', 'remnote-plugin')
    $passed.Add('remnote-plugin typecheck')
    $success = $true
} finally {
    @{passed=$success;completed=$passed.ToArray();checkedCommit=(Get-CheckedText git @('rev-parse', 'HEAD'))} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reports 'checks.json') -Encoding utf8
    $env:KC_CHAT_OUTPUT = $oldChatOutput
    Pop-Location
}
Write-Host "PASS: $($passed.Count) build/test groups."
