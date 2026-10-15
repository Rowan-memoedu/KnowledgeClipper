[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][string]$RootDocumentId,
      [string]$DataRoot = 'D:\AppData\KnowledgeClipper')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$source = [IO.Path]::GetFullPath($SourceRoot)
$target = Join-Path $DataRoot 'modes\chat'
$mediaRoot = Join-Path $DataRoot 'chat-media\examples'
$original = Get-Content -LiteralPath (Join-Path $source 'question-types.json') -Raw | ConvertFrom-Json
$entries = [Collections.Generic.List[object]]::new()
$files = [Collections.Generic.List[object]]::new()
$media = @{}
foreach ($type in $original.types) {
    $examplePath = [IO.Path]::GetFullPath((Join-Path $source $type.examplesFile))
    $assets = [Collections.Generic.List[object]]::new()
    $example = Get-Content -LiteralPath $examplePath -Raw
    $example = [regex]::Replace($example, '!\[([^\]]*)\]\(([^)]+)\)', [Text.RegularExpressions.MatchEvaluator]{ param($match)
        $path = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($examplePath)) $match.Groups[2].Value))
        if (-not $path.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '示例图片超出来源目录。' }
        $bytes = [IO.File]::ReadAllBytes($path)
        $originalBytes = $bytes
        if ($bytes.Length -lt 24) { throw "示例图片无效：$path" }
        if ($bytes[0] -ne 137 -or $bytes[1] -ne 80) {
            $picture = [Drawing.Image]::FromFile($path)
            $stream = [IO.MemoryStream]::new()
            try { $picture.Save($stream,[Drawing.Imaging.ImageFormat]::Png); $bytes=$stream.ToArray() }
            finally { $stream.Dispose(); $picture.Dispose() }
        }
        $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $destination = Join-Path $mediaRoot ($sha + '.png')
        $media[$sha] = @{ source=$path; destination=$destination; bytes=$bytes; original=$originalBytes }
        $assets.Add(@{path=$destination;sha256=$sha;role='question';index=$assets.Count+1})
        return '![' + $match.Groups[1].Value + '](asset:' + $sha + ')'
    })
    $files.Add(@{path="few-shots/$($type.id).md";text=$example})
    $files.Add(@{path="few-shots/$($type.id).assets.json";text=(ConvertTo-Json -InputObject @($assets.ToArray()) -Depth 8)})
    $entries.Add(@{id=$type.id;label=$type.label;promptFile="types/$($type.id).md";examplesFile="few-shots/$($type.id).md";
      exampleAssetsFile="few-shots/$($type.id).assets.json";profiles=@();modelHint=$type.defaultProfile;
      documentId=$original.remnoteImport.typeDocuments.($type.label)})
}
$registry = @{types=@($entries.ToArray());rootId=$RootDocumentId;baseId=$original.remnoteImport.baseDocumentRemId}
$files.Add(@{path='question-types.json';text=($registry | ConvertTo-Json -Depth 10)})
# Preflight every destination; an upgrade never replaces an existing personal file.
foreach ($item in $files) {
    if (Test-Path -LiteralPath (Join-Path $target $item.path)) { throw "个人配置已经存在，未覆盖：$($item.path)" }
}
New-Item -ItemType Directory -Path $mediaRoot -Force | Out-Null
foreach ($item in $media.Values) {
    if (-not (Test-Path -LiteralPath $item.destination)) { [IO.File]::WriteAllBytes($item.destination,$item.bytes) }
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($item.bytes)) -ne (Get-FileHash -LiteralPath $item.destination).Hash) { throw '示例图片字节核对失败。' }
    $originals = Join-Path $mediaRoot 'originals'
    New-Item -ItemType Directory -Path $originals -Force | Out-Null
    $originalName = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($item.original)).ToLowerInvariant()+[IO.Path]::GetExtension($item.source)
    $originalPath = Join-Path $originals $originalName
    if (-not (Test-Path -LiteralPath $originalPath)) { Copy-Item -LiteralPath $item.source -Destination $originalPath }
}
foreach ($item in $files) {
    $path = Join-Path $target $item.path
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force | Out-Null
    [IO.File]::WriteAllText($path,$item.text,[Text.UTF8Encoding]::new($false))
}
[pscustomobject]@{types=$entries.Count;files=$files.Count;images=$media.Count;target=$target;existingFilesOverwritten=0} | ConvertTo-Json
