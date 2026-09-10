Set-StrictMode -Version Latest;$ErrorActionPreference='Stop'
function Normalize([object]$r){$p=[string]$r.path;$s=[string]$r.sha256;$b=[Convert]::ToInt64($r.bytes,[Globalization.CultureInfo]::InvariantCulture);if(!$p-or$b-lt0-or$s-notmatch'^[a-fA-F0-9]{64}$'){throw 'invalid'};[pscustomobject]@{path=$p;bytes=[int64]$b;sha256=$s.ToUpperInvariant()}}
function Sum([object[]]$rows){[int64]$total=0;foreach($row in @($rows)){[int64]$value=(Normalize $row).bytes;if($value-gt([int64]::MaxValue-$total)){throw 'overflow'};$total+=$value};$total}
$sha='a'*64
$ordered=[ordered]@{path='a';bytes=[int64]7;sha256=$sha}
$custom=[pscustomobject]@{path='b';bytes='8';sha256=$sha}
$table=@{path='c';bytes=[int64]9007199254740991;sha256=$sha}
if((Sum @())-ne0){throw 'empty'};if((Sum @($ordered))-ne7){throw 'single'};if((Sum @($ordered,$custom,$table))-ne9007199254741006){throw 'large-int64'}
foreach($row in @($ordered,$custom,$table)){if((Normalize $row).bytes-isnot[int64]){throw 'type'}}
$paths=@('zeta','知识/卡片.json','Alpha');[Array]::Sort($paths,[StringComparer]::Ordinal)
if(($paths-join'|')-cne'Alpha|zeta|知识/卡片.json'){throw 'ordinal-nonascii'}
[ordered]@{status='PASS';types=@('OrderedDictionary','PSCustomObject','Hashtable');empty=0;single=7;largeInt64=9007199254741006;ordinalNonAscii=$paths}|ConvertTo-Json
