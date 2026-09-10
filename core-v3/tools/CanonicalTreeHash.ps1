function Get-KcTreeRecordValue {
    param(
        [Parameter(Mandatory)] [object]$Record,
        [Parameter(Mandatory)] [string]$Name
    )

    if ($Record -is [System.Collections.IDictionary]) {
        if (-not $Record.Contains($Name)) { throw "Tree record is missing '$Name'." }
        return $Record[$Name]
    }

    $property = $Record.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "Tree record is missing '$Name'." }
    return $property.Value
}

function ConvertTo-KcCanonicalTreeRecords {
    param([Parameter(Mandatory)] [object[]]$Files)

    $records = [System.Collections.Generic.List[object]]::new()
    $paths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $Files) {
        $path = [string](Get-KcTreeRecordValue -Record $file -Name 'path')
        if ([string]::IsNullOrWhiteSpace($path)) { throw 'Tree record path must be non-empty.' }
        if (-not $paths.Add($path)) { throw "Duplicate tree record path: $path" }

        try {
            $bytes = [Convert]::ToInt64(
                (Get-KcTreeRecordValue -Record $file -Name 'bytes'),
                [Globalization.CultureInfo]::InvariantCulture)
        }
        catch {
            throw "Tree record bytes must be Int64-compatible: $path"
        }
        if ($bytes -lt 0) { throw "Tree record bytes must be non-negative: $path" }

        $sha256 = ([string](Get-KcTreeRecordValue -Record $file -Name 'sha256')).ToUpperInvariant()
        if ($sha256 -notmatch '^[0-9A-F]{64}$') { throw "Tree record sha256 is invalid: $path" }

        $records.Add([pscustomobject][ordered]@{
            path = $path
            bytes = [long]$bytes
            sha256 = $sha256
        })
    }

    $array = $records.ToArray()
    [Array]::Sort($array, [System.Comparison[object]]{
        param($left, $right)
        [StringComparer]::Ordinal.Compare([string]$left.path, [string]$right.path)
    })
    return $array
}

function Get-KcCanonicalTreeMaterial {
    param([Parameter(Mandatory)] [object[]]$Files)

    $builder = [Text.StringBuilder]::new()
    foreach ($record in @(ConvertTo-KcCanonicalTreeRecords -Files $Files)) {
        [void]$builder.Append($record.path).Append("`t").Append(
            $record.bytes.ToString([Globalization.CultureInfo]::InvariantCulture)
        ).Append("`t").Append($record.sha256).Append("`n")
    }
    return $builder.ToString()
}

function Get-KcCanonicalTreeHash {
    param([Parameter(Mandatory)] [object[]]$Files)

    $material = Get-KcCanonicalTreeMaterial -Files $Files
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material)))
}

function Get-KcCanonicalTreeTotalBytes {
    param([Parameter(Mandatory)] [object[]]$Files)

    [long]$total = 0
    foreach ($record in @(ConvertTo-KcCanonicalTreeRecords -Files $Files)) {
        $total += [long]$record.bytes
    }
    return [long]$total
}
