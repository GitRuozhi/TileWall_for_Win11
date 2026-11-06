param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SafeGeneratedPaths.ps1')
$repository = Split-Path $PSScriptRoot -Parent
$artifactsRoot = Join-Path $repository 'artifacts'
$output = Assert-ContainedPath $OutputDirectory $artifactsRoot
$source = Join-Path $repository 'src/TileWall.ExplorerExtension'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'MSIX Explorer extension requires installed Visual Studio C++ x64 build tools (vswhere.exe not found).' }
$installations = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($LASTEXITCODE -ne 0 -or $installations.Count -ne 1) { throw 'MSIX Explorer extension requires installed Visual Studio C++ x64 build tools.' }
$vcRoot = Join-Path $installations[0] 'VC/Tools/MSVC'
$vc = Get-ChildItem -LiteralPath $vcRoot -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $vc) { throw 'No installed MSVC toolset was found.' }
$kits = Get-ItemPropertyValue -LiteralPath 'HKLM:/SOFTWARE/Microsoft/Windows Kits/Installed Roots' -Name KitsRoot10
$sdk = Get-ChildItem -LiteralPath (Join-Path $kits 'Include') -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+\.0$' -and
        (Test-Path -LiteralPath (Join-Path $kits "bin/$($_.Name)/x64/rc.exe") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $kits "Lib/$($_.Name)/um/x64/ole32.lib") -PathType Leaf) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw 'MSIX Explorer extension requires an installed Windows 10 SDK with x64 tools and libraries.' }
$cl = Join-Path $vc.FullName 'bin/Hostx64/x64/cl.exe'
$link = Join-Path $vc.FullName 'bin/Hostx64/x64/link.exe'
$rc = Join-Path $kits "bin/$($sdk.Name)/x64/rc.exe"
foreach ($tool in @($cl, $link, $rc)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Missing native build tool: $tool" }
}
$savedInclude = $env:INCLUDE
$savedLib = $env:LIB
try {
    $env:INCLUDE = (@((Join-Path $vc.FullName 'include')) + @('ucrt', 'shared', 'um', 'winrt' | ForEach-Object { Join-Path $sdk.FullName $_ })) -join ';'
    $env:LIB = (@((Join-Path $vc.FullName 'lib/x64')) + @('um', 'ucrt' | ForEach-Object { Join-Path $kits "Lib/$($sdk.Name)/$_/x64" })) -join ';'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Push-Location -LiteralPath $source
    try {
        & $cl /nologo /TC /std:c11 /W4 /WX /O1 /GS /guard:cf /MT /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /c ExplorerCommand.c "/Fo:$output/ExplorerCommand.obj"
        if ($LASTEXITCODE -ne 0) { throw "Explorer extension compilation failed: $LASTEXITCODE" }
        & $rc /nologo /c65001 "/fo$output/ExplorerCommand.res" ExplorerCommand.rc
        if ($LASTEXITCODE -ne 0) { throw "Explorer extension resource compilation failed: $LASTEXITCODE" }
        & $link /DLL /NOLOGO /WX /MACHINE:X64 /guard:cf /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /DEF:ExplorerCommand.def "/OUT:$output/TileWall.ExplorerExtension.dll" "$output/ExplorerCommand.obj" "$output/ExplorerCommand.res" kernel32.lib user32.lib shell32.lib ole32.lib uuid.lib
        if ($LASTEXITCODE -ne 0) { throw "Explorer extension linking failed: $LASTEXITCODE" }
    }
    finally { Pop-Location }
}
finally { $env:INCLUDE = $savedInclude; $env:LIB = $savedLib }
