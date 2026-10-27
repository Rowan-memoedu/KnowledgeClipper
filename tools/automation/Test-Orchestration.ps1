[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Maintenance.Common.ps1')
$entry = Join-Path $PSScriptRoot 'Invoke-Maintenance.ps1'
$id = 'fixture-' + [guid]::NewGuid().ToString('N')
$root = "D:\Artifacts\GitHubActions\KnowledgeClipper\work\$id"
$run = "D:\Artifacts\GitHubActions\KnowledgeClipper\runs\$id"
$repo = Join-Path $root 'checkout'
$remote = Join-Path $root 'remote.git'
$auth = Join-Path $root 'fake-auth'
$fake = Join-Path $root 'fake-codex.ps1'
New-Item -ItemType Directory -Path $repo,$auth -Force | Out-Null
@'
if ($args -contains 'status') { Write-Output 'Logged in using ChatGPT'; exit 0 }
$promptText = @($input) -join "`n"
[IO.File]::WriteAllText((Join-Path $env:MAINTENANCE_RUN 'received-prompt.txt'), $promptText)
$outputIndex = [Array]::IndexOf($args, '--output-last-message')
[IO.File]::WriteAllText($args[$outputIndex + 1], 'Fixture completed without changes.')
if ($env:MAINTENANCE_FIXTURE_FAIL -eq 'true') { '{"type":"turn.failed"}'; exit 9 }
if ($env:MAINTENANCE_FIXTURE_FAIL -eq 'partial') { '{"type":"turn.started"}'; exit 0 }
'{"type":"turn.completed"}'
exit 0
'@ | Set-Content -LiteralPath $fake -Encoding utf8
'{}' | Set-Content -LiteralPath (Join-Path $auth 'auth.json') -Encoding utf8
$variables = @('GITHUB_ACTIONS','GITHUB_WORKSPACE','GITHUB_REPOSITORY','GITHUB_REF','GITHUB_RUN_ID','CODEX_BIN','DOTNET_EXE','CODEX_HOME','MAINTENANCE_TASK','MAINTENANCE_RUN','GH_TOKEN','MAINTENANCE_FIXTURE_FAIL','OPENAI_API_KEY','CODEX_API_KEY')
$previous = @{}
foreach ($key in $variables) { $previous[$key] = [Environment]::GetEnvironmentVariable($key) }
$checks = [Collections.Generic.List[string]]::new()
function Reject-Operation([scriptblock]$Operation, [string]$Expected, [string]$Name) {
    try { & $Operation } catch { if ($_.ToString() -match $Expected) { $checks.Add($Name); return }; throw }
    throw "Expected rejection: $Name"
}
Push-Location -LiteralPath $repo
try {
    Invoke-Checked git @('init','--bare','--initial-branch=main',$remote)
    Invoke-Checked git @('init','--initial-branch=main')
    Invoke-Checked git @('config','user.name','Automation fixture')
    Invoke-Checked git @('config','user.email','fixture@example.invalid')
    'fixture' | Set-Content -LiteralPath 'README.md'
    Invoke-Checked git @('add','README.md')
    Invoke-Checked git @('commit','-m','fixture baseline')
    Invoke-Checked git @('remote','add','origin',$remote)
    Invoke-Checked git @('push','-u','origin','main')
    $env:GITHUB_ACTIONS='true'; $env:GITHUB_WORKSPACE=$repo
    $env:GITHUB_REPOSITORY='Rowan-memoedu/KnowledgeClipper'; $env:GITHUB_REF='refs/heads/main'
    $env:GITHUB_RUN_ID=$id; $env:CODEX_BIN=$fake; $env:DOTNET_EXE=(Get-Command pwsh).Source
    $env:CODEX_HOME=$auth; $env:MAINTENANCE_RUN=$run; $env:GH_TOKEN='non-secret-fixture'
    $env:MAINTENANCE_TASK='测试引号 " and $(throw "must stay literal") `n <xml> 与中文'
    & $entry -Phase Prepare
    $baseline = Get-CheckedText git @('rev-parse','HEAD')
    & $entry -Phase Agent
    $received = Get-Content -Raw -LiteralPath (Join-Path $run 'received-prompt.txt')
    if (-not $received.Contains($env:MAINTENANCE_TASK)) { throw 'Task input was changed or interpreted.' }
    $checks.Add('task text including shell syntax reaches stdin literally')
    $env:MAINTENANCE_FIXTURE_FAIL='true'
    Reject-Operation { & $entry -Phase Agent } 'did not finish successfully' 'failed agent is rejected'
    $env:MAINTENANCE_FIXTURE_FAIL='partial'
    Reject-Operation { & $entry -Phase Agent } 'did not finish successfully' 'partial agent output is rejected even with exit zero'
    $env:MAINTENANCE_FIXTURE_FAIL='false'
    '{"passed":false}' | Set-Content -LiteralPath (Join-Path $run 'reports\checks.json')
    Reject-Operation { & $entry -Phase Publish } 'checks have not passed' 'failed checks cannot publish'
    if ((Get-CheckedText git @('rev-parse','HEAD')) -ne $baseline) { throw 'Failure path created a commit.' }
    '{"passed":true}' | Set-Content -LiteralPath (Join-Path $run 'reports\checks.json')
    & $entry -Phase Publish
    $result = Get-Content -Raw -LiteralPath (Join-Path $run 'reports\result.json') | ConvertFrom-Json
    if ($result.outcome -ne 'no changes' -or $result.pullRequest) { throw 'No-change run attempted a PR.' }
    $checks.Add('no-change run produces no commit or PR')
    New-Item -ItemType Directory -Path '.codex' | Out-Null
    '{}' | Set-Content -LiteralPath '.codex\auth.json'
    Invoke-Checked git @('add','--','.codex/auth.json')
    Reject-Operation { & $entry -Phase Publish } 'credential-like' 'already-staged credential file cannot bypass publication checks'
    Invoke-Checked git @('restore','--staged','--','.codex/auth.json')
    'exit 7' | Set-Content -LiteralPath 'build.ps1'
    $testEntry = Join-Path $PSScriptRoot 'Test-Maintenance.ps1'
    Reject-Operation { & $testEntry -RepositoryPath $repo -RunDirectory (Join-Path $run 'failed-build') -DotnetPath $env:DOTNET_EXE } 'Desktop build failed' 'desktop script nonzero exit cannot count as passed'
    $failedCheck = Get-Content -Raw -LiteralPath (Join-Path $run 'failed-build\reports\checks.json') | ConvertFrom-Json
    if ($failedCheck.passed -or $failedCheck.completed.Count -ne 0) { throw 'Failed build was recorded as passing.' }
    Reject-Operation { Assert-PublishablePaths @('src/code.cs','.codex/auth.json') } 'credential-like' 'credential file cannot be staged for publication'
    $env:GITHUB_WORKSPACE='D:\Projects\RemNote Quick Clipper'
    Reject-Operation { & $entry -Phase Prepare } 'outside the dedicated runner checkout' 'ordinary development directory cannot be reset'
    @{passed=$true;checks=$checks.ToArray()} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'orchestration-tests.json') -Encoding utf8
    Write-Host "PASS: $($checks.Count) orchestration regression checks. Evidence: $run"
} finally {
    Pop-Location
    foreach ($key in $variables) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
}
