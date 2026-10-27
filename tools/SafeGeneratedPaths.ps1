function Get-PathItemOrNull([string]$Path) {
    try { return Get-Item -LiteralPath $Path -Force -ErrorAction Stop }
    catch [System.Management.Automation.ItemNotFoundException] { return $null }
}

function Assert-ContainedPath([string]$Path, [string]$Base, [bool]$AllowBase = $false) {
    $full = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    $baseFull = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Base))
    $separator = [string][System.IO.Path]::DirectorySeparatorChar
    $basePrefix = if ($baseFull.EndsWith($separator)) { $baseFull } else { $baseFull + $separator }
    $comparison = [System.StringComparison]::OrdinalIgnoreCase
    $isBase = $full.Equals($baseFull, $comparison)
    if (($isBase -and -not $AllowBase) -or
        (-not $isBase -and -not $full.StartsWith($basePrefix, $comparison))) {
        throw "Path is outside the intended directory: $full"
    }

    $current = [System.IO.Path]::GetPathRoot($full)
    $parts = $full.Substring($current.Length).Split([char[]]@('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries)
    foreach ($part in $parts) {
        $current = [System.IO.Path]::Combine($current, $part)
        $item = Get-PathItemOrNull $current
        if ($item -and ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse point in generated path: $current"
        }
    }
    return $full
}

function Assert-NoReparseInTree([string]$Path, [string]$Base) {
    $full = Assert-ContainedPath $Path $Base
    $rootItem = Get-PathItemOrNull $full
    if (-not $rootItem -or -not $rootItem.PSIsContainer) { return }
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($full)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($child in [System.IO.Directory]::EnumerateFileSystemEntries($directory)) {
            # Children come only from the checked root or a checked non-reparse directory.
            $item = Get-Item -LiteralPath $child -Force
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "Reparse point inside generated tree: $child"
            }
            if ($item.PSIsContainer) { $pending.Push($child) }
        }
    }
}

function Remove-GeneratedTree([string]$Path, [string]$Base) {
    $full = Assert-ContainedPath $Path $Base
    if (Get-PathItemOrNull $full) {
        Assert-NoReparseInTree $full $Base
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

function Move-GeneratedTree([string]$Path, [string]$Destination, [string]$Base) {
    $sourceFull = Assert-ContainedPath $Path $Base
    $destinationFull = Assert-ContainedPath $Destination $Base
    Assert-NoReparseInTree $sourceFull $Base
    if (Get-PathItemOrNull $destinationFull) { throw "Move destination already exists: $destinationFull" }
    Move-Item -LiteralPath $sourceFull -Destination $destinationFull
}
