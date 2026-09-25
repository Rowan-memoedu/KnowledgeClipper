param([string]$DeploymentRoot='D:\Artifacts\KnowledgeClipper\standalone-remnote')
$ErrorActionPreference='Stop'
$target=Join-Path $DeploymentRoot 'host'
$resolved=[IO.Path]::GetFullPath($target)
if(!$resolved.StartsWith('D:\Artifacts\KnowledgeClipper\',[StringComparison]::OrdinalIgnoreCase)){throw 'Deployment must stay under D:\Artifacts\KnowledgeClipper'}
Push-Location $PSScriptRoot
try{
 & npm.cmd ci --cache D:\Caches\npm-cache --no-audit --no-fund
 if($LASTEXITCODE){throw 'Dependency installation failed'}
 & npm.cmd run build
 if($LASTEXITCODE){throw 'Host build failed'}
 New-Item -ItemType Directory -Path $target -Force | Out-Null
 Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dist') -Destination $target -Recurse -Force
 foreach($file in @('package.json','package-lock.json','Start-Host.ps1','Register-Startup.ps1','LICENSE.upstream','NOTICE.md')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $target -Force}
 Push-Location $target
 try{& npm.cmd ci --omit=dev --cache D:\Caches\npm-cache --no-audit --no-fund;if($LASTEXITCODE){throw 'Production dependency installation failed'}}finally{Pop-Location}
}finally{Pop-Location}
Write-Output "Host built at $target; no process or scheduled task changed."
