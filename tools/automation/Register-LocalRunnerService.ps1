# Run once from an elevated PowerShell after installing and verifying the official runner archive.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = 'D:\AppData\GitHubActions\KnowledgeClipper'
$receipt = 'D:\Artifacts\GitHubActions\KnowledgeClipper\setup\service-registration.json'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Official Windows runner service registration requires an elevated PowerShell.' }
$exitCode = 1
try {
    foreach ($path in @("$root\runner\config.cmd", "$root\tools\pwsh\pwsh.exe", "$root\tools\codex\codex.exe")) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing verified runtime: $path" }
    }
    if (Test-Path -LiteralPath "$root\runner\.runner") { throw 'Runner is already configured; inspect it instead of replacing its identity.' }
    # Runner.Listener officially loads .env before starting. This lets Actions use its native pwsh shell.
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    "PATH=$root\tools\pwsh;$root\tools\codex;$machinePath" | Set-Content -LiteralPath "$root\runner\.env" -Encoding utf8
    $gh = 'C:\Program Files\GitHub CLI\gh.exe'
    $repo = & $gh api repos/Rowan-memoedu/KnowledgeClipper | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $repo.private -or -not $repo.permissions.admin) { throw 'A private repository and current GitHub admin permission are required.' }
    # GitHub exposes one combined switch for Actions PR creation/approval. The workflow only creates PRs.
    & $gh api --method PUT repos/Rowan-memoedu/KnowledgeClipper/actions/permissions/workflow -f default_workflow_permissions=read -F can_approve_pull_request_reviews=true | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable PR creation for this repository.' }
    $env:ACTIONS_RUNNER_INPUT_TOKEN = & $gh api --method POST repos/Rowan-memoedu/KnowledgeClipper/actions/runners/registration-token --jq .token
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($env:ACTIONS_RUNNER_INPUT_TOKEN)) { throw 'Could not obtain the short-lived runner registration token.' }
    Push-Location -LiteralPath "$root\runner"
    try {
        & .\config.cmd --unattended --url https://github.com/Rowan-memoedu/KnowledgeClipper --name knowledgeclipper-windows-local --labels knowledgeclipper --work D:\Artifacts\GitHubActions\KnowledgeClipper\work --runasservice
        if ($LASTEXITCODE -ne 0) { throw 'Official runner service configuration failed.' }
    } finally { Pop-Location }
    $name = (Get-Content -Raw -LiteralPath "$root\runner\.service").Trim()
    $service = Get-CimInstance Win32_Service | Where-Object Name -EQ $name
    if (-not $service -or $service.State -ne 'Running') { throw 'The configured runner service is not running.' }
    @{success=$true;service=$service.Name;state=$service.State;startMode=$service.StartMode;account=$service.StartName;processId=$service.ProcessId;path=$service.PathName} |
        ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding utf8
    $exitCode = 0
} catch {
    @{success=$false;error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding utf8
    Write-Error $_ -ErrorAction Continue
} finally {
    Remove-Item Env:ACTIONS_RUNNER_INPUT_TOKEN -ErrorAction SilentlyContinue
}
exit $exitCode
