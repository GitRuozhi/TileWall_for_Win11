param([Parameter(Mandatory)][string]$OutputDirectory, [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64')

$ErrorActionPreference = 'Stop'
$Architecture = $Architecture.ToLowerInvariant()
$hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($hostArchitecture -notin @('x64', 'arm64')) { throw 'Native builds require an x64 or ARM64 host.' }
$hostTools = if ($hostArchitecture -eq 'arm64') { 'Hostarm64' } else { 'Hostx64' }
$component = if ($Architecture -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
. (Join-Path $PSScriptRoot 'SafeGeneratedPaths.ps1')
$repository = Split-Path $PSScriptRoot -Parent
$artifactsRoot = Join-Path $repository 'artifacts'
$output = Assert-ContainedPath $OutputDirectory $artifactsRoot
$source = Join-Path $repository 'src/TileWall.ExplorerExtension'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw "MSIX Explorer extension requires installed Visual Studio C++ $Architecture build tools (vswhere.exe not found)." }
$installations = @(& $vswhere -latest -products '*' -requires $component -property installationPath)
if ($LASTEXITCODE -ne 0 -or $installations.Count -ne 1) { throw "MSIX Explorer extension requires installed Visual Studio C++ $Architecture build tools." }
$vcRoot = Join-Path $installations[0] 'VC/Tools/MSVC'
$vc = Get-ChildItem -LiteralPath $vcRoot -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and
        (Test-Path -LiteralPath (Join-Path $_.FullName "bin/$hostTools/$Architecture/cl.exe") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName "bin/$hostTools/$Architecture/link.exe") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName "lib/$Architecture/libcmt.lib") -PathType Leaf) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $vc) { throw "No installed MSVC toolset has the $hostArchitecture host tools and $Architecture target libraries." }
$kits = Get-ItemPropertyValue -LiteralPath 'HKLM:/SOFTWARE/Microsoft/Windows Kits/Installed Roots' -Name KitsRoot10
$sdk = Get-ChildItem -LiteralPath (Join-Path $kits 'Include') -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+\.0$' -and
        (Test-Path -LiteralPath (Join-Path $kits "bin/$($_.Name)/$hostArchitecture/rc.exe") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $kits "Lib/$($_.Name)/um/$Architecture/ole32.lib") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $kits "Lib/$($_.Name)/ucrt/$Architecture/libucrt.lib") -PathType Leaf) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw 'MSIX Explorer extension requires an installed Windows 10 SDK with matching host tools and target libraries.' }
$cl = Join-Path $vc.FullName "bin/$hostTools/$Architecture/cl.exe"
$link = Join-Path $vc.FullName "bin/$hostTools/$Architecture/link.exe"
$rc = Join-Path $kits "bin/$($sdk.Name)/$hostArchitecture/rc.exe"
foreach ($tool in @($cl, $link, $rc)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Missing native build tool: $tool" }
}
$savedInclude = $env:INCLUDE
$savedLib = $env:LIB
try {
    $env:INCLUDE = (@((Join-Path $vc.FullName 'include')) + @('ucrt', 'shared', 'um', 'winrt' | ForEach-Object { Join-Path $sdk.FullName $_ })) -join ';'
    $env:LIB = (@((Join-Path $vc.FullName "lib/$Architecture")) + @('um', 'ucrt' | ForEach-Object { Join-Path $kits "Lib/$($sdk.Name)/$_/$Architecture" })) -join ';'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Push-Location -LiteralPath $source
    try {
        & $cl /nologo /TC /std:c11 /W4 /WX /O1 /GS /guard:cf /MT /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /c ExplorerCommand.c "/Fo:$output/ExplorerCommand.obj"
        if ($LASTEXITCODE -ne 0) { throw "Explorer extension compilation failed: $LASTEXITCODE" }
        & $rc /nologo /c65001 "/fo$output/ExplorerCommand.res" ExplorerCommand.rc
        if ($LASTEXITCODE -ne 0) { throw "Explorer extension resource compilation failed: $LASTEXITCODE" }
        & $link /DLL /NOLOGO /WX "/MACHINE:$Architecture" /guard:cf /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /DEF:ExplorerCommand.def "/OUT:$output/TileWall.ExplorerExtension.dll" "$output/ExplorerCommand.obj" "$output/ExplorerCommand.res" kernel32.lib user32.lib shell32.lib ole32.lib uuid.lib
        if ($LASTEXITCODE -ne 0) { throw "Explorer extension linking failed: $LASTEXITCODE" }
    }
    finally { Pop-Location }
}
finally { $env:INCLUDE = $savedInclude; $env:LIB = $savedLib }
