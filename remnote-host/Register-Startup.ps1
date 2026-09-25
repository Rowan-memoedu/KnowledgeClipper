param([string]$DeploymentRoot='D:\Artifacts\KnowledgeClipper\standalone-remnote',[string]$PwshPath=(Get-Command pwsh).Source)
$ErrorActionPreference='Stop'
$script=Join-Path $DeploymentRoot 'host\Start-Host.ps1'
if(!(Test-Path -LiteralPath $script)){throw 'Deploy host before registering startup'}
$taskName='KnowledgeClipper RemNote SDK'
$existing=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if($existing -and ($existing.Description -ne 'KnowledgeClipper standalone RemNote SDK host' -or @($existing.Actions).Count -ne 1 -or $existing.Actions[0].Arguments -notlike '*standalone-remnote*host*Start-Host.ps1*')){throw 'Existing task is not owned by KnowledgeClipper; refusing replacement'}
$user=[System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action=New-ScheduledTaskAction -Execute $PwshPath -Argument ('-NoLogo -NoProfile -WindowStyle Hidden -File "'+$script+'" -DeploymentRoot "'+$DeploymentRoot+'"')
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
$principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Description 'KnowledgeClipper standalone RemNote SDK host' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Select-Object TaskName,State
