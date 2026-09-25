param([string]$DeploymentRoot='D:\Artifacts\KnowledgeClipper\standalone-remnote',[string]$NodePath='C:\Program Files\nodejs\node.exe')
$ErrorActionPreference='Stop'
$hostRoot=Join-Path $DeploymentRoot 'host'
foreach($required in @($NodePath,(Join-Path $hostRoot 'dist\main.js'),(Join-Path $DeploymentRoot 'host-config.json'))){if(!(Test-Path -LiteralPath $required -PathType Leaf)){throw "Missing required file: $required"}}
$logs='D:\AppData\KnowledgeClipper\remnote-host-logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$process=Start-Process -FilePath $NodePath -ArgumentList @('"'+(Join-Path $hostRoot 'dist\main.js')+'"','"'+(Join-Path $DeploymentRoot 'host-config.json')+'"') -WorkingDirectory $hostRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logs "$stamp.stdout.log") -RedirectStandardError (Join-Path $logs "$stamp.stderr.log") -PassThru -Wait
exit $process.ExitCode
