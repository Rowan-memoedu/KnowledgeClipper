param([string]$ManifestPath,[switch]$LaunchOfflineSelfTest,[switch]$SelfTest)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

function Test-RunnerRelease([string]$Path,[scriptblock]$HashReader,[scriptblock]$Launcher,[bool]$Launch) {
    $manifest=Get-Content -Raw -LiteralPath $Path -ErrorAction Stop|ConvertFrom-Json -Depth 30 -ErrorAction Stop
    if($manifest.schemaVersion-ne'knowledge-clipper-actual-runner-release.v5'-or$manifest.runtime.model-ne'self-contained'-or$manifest.runtime.rid-ne'win-x64'-or$manifest.runtime.targetFramework-ne'net8.0-windows'-or$manifest.runtime.singleFile-ne$false){throw 'runner-preflight-runtime-model-invalid'}
    $root=Split-Path -Parent ([IO.Path]::GetFullPath($Path));$prefix=$root.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [long]$bytes=0
    foreach($file in $manifest.files){$relative=[string]$file.path;if([IO.Path]::IsPathRooted($relative)-or$relative.Contains('..',[StringComparison]::Ordinal)){throw 'runner-preflight-path-invalid'};$normalized=$relative.Replace('/',[IO.Path]::DirectorySeparatorChar);$full=[IO.Path]::GetFullPath((Join-Path $root $normalized));if(-not$full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)-or-not$seen.Add($relative)){throw 'runner-preflight-path-invalid'};if(-not(Test-Path -LiteralPath $full -PathType Leaf)){throw 'runner-preflight-file-missing'};$item=Get-Item -LiteralPath $full -ErrorAction Stop;if($item.Length-ne[long]$file.bytes){throw 'runner-preflight-bytes-drift'};$hash=&$HashReader $full;if(([string]$hash).ToUpperInvariant()-ne([string]$file.sha256).ToUpperInvariant()){throw 'runner-preflight-hash-drift'};$bytes+=$item.Length}
    if($seen.Count-ne[int]$manifest.payload.files-or$bytes-ne[long]$manifest.payload.bytes){throw 'runner-preflight-payload-drift'}
    foreach($required in @([string]$manifest.entrypoint)+@($manifest.requiredRuntimeFiles|ForEach-Object{[string]$_})){if(-not$seen.Contains($required)){throw 'runner-preflight-runtime-closure-missing'}}
    if($Launch){$entry=[IO.Path]::GetFullPath((Join-Path $root ([string]$manifest.entrypoint).Replace('/',[IO.Path]::DirectorySeparatorChar)));&$Launcher $entry}
    return [ordered]@{status='PASS';files=$seen.Count;bytes=$bytes;runtimeModel='self-contained-win-x64';runnerStarts=if($Launch){1}else{0}}
}

function New-Fixture([string]$Root,[string[]]$Required,[hashtable]$Content,[string]$PathOverride) {
    [IO.Directory]::CreateDirectory($Root)|Out-Null;$rows=@();foreach($name in $Content.Keys){$path=Join-Path $Root $name;[IO.Directory]::CreateDirectory((Split-Path -Parent $path))|Out-Null;[IO.File]::WriteAllText($path,[string]$Content[$name],[Text.UTF8Encoding]::new($false));$item=Get-Item $path;$rows+=[ordered]@{path=$name.Replace('\','/');bytes=$item.Length;sha256=(Get-FileHash -Algorithm SHA256 $path).Hash}}
    [long]$fixtureBytes=0;foreach($row in $rows){$fixtureBytes+=[long]$row.bytes};$manifest=[ordered]@{schemaVersion='knowledge-clipper-actual-runner-release.v5';runtime=[ordered]@{model='self-contained';rid='win-x64';targetFramework='net8.0-windows';singleFile=$false};entrypoint=if($PathOverride){$PathOverride}else{'app/runner.exe'};requiredRuntimeFiles=$Required;payload=[ordered]@{files=$rows.Count;bytes=$fixtureBytes};files=$rows};$path=Join-Path $Root 'manifest.json';[IO.File]::WriteAllText($path,(ConvertTo-Json $manifest -Depth 20),[Text.UTF8Encoding]::new($false));return $path
}

if($SelfTest){$temp=Join-Path ([IO.Path]::GetTempPath()) ('kc-runner-v5-preflight-'+[guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($temp)|Out-Null;try{$starts=0;$launcher={$script:starts++};$hashReader={param($p)(Get-FileHash -Algorithm SHA256 -LiteralPath $p -ErrorAction Stop).Hash};$valid=@{'app\runner.exe'='exe';'app\runner.runtimeconfig.json'='runtime';'app\runner.deps.json'='deps';'app\hostpolicy.dll'='host'};$required=@('app/runner.runtimeconfig.json','app/runner.deps.json','app/hostpolicy.dll');$validPath=New-Fixture (Join-Path $temp 'valid') $required $valid $null;[void](Test-RunnerRelease $validPath $hashReader $launcher $true);if($starts-ne1){throw 'valid-runner-not-started-once'}
    $cases=@();foreach($missing in @('app\runner.runtimeconfig.json','app\runner.deps.json')){$content=$valid.Clone();$content.Remove($missing);$cases+=,(New-Fixture (Join-Path $temp ('missing-'+[IO.Path]::GetFileName($missing))) $required $content $null)}
    $hashPath=New-Fixture (Join-Path $temp 'hash') $required $valid $null;$hashDoc=Get-Content -Raw $hashPath|ConvertFrom-Json -Depth 20;$hashDoc.files[0].sha256='0'*64;[IO.File]::WriteAllText($hashPath,(ConvertTo-Json $hashDoc -Depth 20),[Text.UTF8Encoding]::new($false));$cases+=,$hashPath
    $cases+=,(New-Fixture (Join-Path $temp 'path') $required $valid '../runner.exe')
    $nonTerminating=New-Fixture (Join-Path $temp 'hash-error') $required $valid $null
    foreach($case in $cases){try{[void](Test-RunnerRelease $case $hashReader $launcher $true);throw 'negative-fixture-accepted'}catch{if($_.Exception.Message-eq'negative-fixture-accepted'){throw}}}
    try{[void](Test-RunnerRelease $nonTerminating {param($p) Write-Error 'injected-hash-reader-error';return '0'*64} $launcher $true);throw 'nonterminating-error-accepted'}catch{if($_.Exception.Message-eq'nonterminating-error-accepted'){throw}}
    if($starts-ne1){throw 'negative-fixture-started-runner'}
    [ordered]@{status='PASS';validStarts=1;negativeFixtures=5;negativeRunnerStarts=0;strictMode='Latest';errorActionPreference='Stop';externalConnections=0;businessTasks=0}|ConvertTo-Json -Compress;exit 0
}finally{if(Test-Path $temp){Remove-Item -LiteralPath $temp -Recurse -Force}}}

if([string]::IsNullOrWhiteSpace($ManifestPath)){throw 'manifest-path-required'}
$result=Test-RunnerRelease ([IO.Path]::GetFullPath($ManifestPath)) {param($p)(Get-FileHash -Algorithm SHA256 -LiteralPath $p -ErrorAction Stop).Hash} {param($entry)$process=Start-Process -FilePath $entry -ArgumentList '--self-test' -PassThru -Wait -WindowStyle Hidden;if($process.ExitCode-ne0){throw 'runner-offline-self-test-failed'}} ([bool]$LaunchOfflineSelfTest)
$result|ConvertTo-Json -Compress
