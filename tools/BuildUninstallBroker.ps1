param([Parameter(Mandatory)][string]$OutputDirectory, [switch]$Tests)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SafeGeneratedPaths.ps1')
$repository = Split-Path $PSScriptRoot -Parent
$artifactsRoot = Join-Path $repository 'artifacts'
$output = Assert-ContainedPath $OutputDirectory $artifactsRoot
$source = Join-Path $repository 'src/TileWall.UninstallBroker'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Native uninstall broker requires installed Visual Studio C++ x64 build tools (vswhere.exe not found).' }
$installations = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($LASTEXITCODE -ne 0 -or $installations.Count -ne 1) { throw 'Native uninstall broker requires installed Visual Studio C++ x64 build tools.' }
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
if (-not $sdk) { throw 'Native uninstall broker requires an installed Windows 10 SDK with x64 tools and libraries.' }
$cl = Join-Path $vc.FullName 'bin/Hostx64/x64/cl.exe'
$link = Join-Path $vc.FullName 'bin/Hostx64/x64/link.exe'
$rc = Join-Path $kits "bin/$($sdk.Name)/x64/rc.exe"
foreach ($tool in @($cl, $link, $rc)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Missing native build tool: $tool" }
}
$savedInclude = $env:INCLUDE
$savedLib = $env:LIB
$savedNativePath = $env:PATH
try {
    $env:INCLUDE = (@((Join-Path $vc.FullName 'include')) + @('ucrt', 'shared', 'um', 'winrt' | ForEach-Object { Join-Path $sdk.FullName $_ })) -join ';'
    $env:LIB = (@((Join-Path $vc.FullName 'lib/x64')) + @('um', 'ucrt' | ForEach-Object { Join-Path $kits "Lib/$($sdk.Name)/$_/x64" })) -join ';'
    # link /MANIFEST:EMBED invokes the matching SDK resource compiler itself.
    # Do not depend on a developer prompt or a globally installed rc.exe.
    $env:PATH = (Split-Path $rc -Parent) + ';' + $savedNativePath
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Push-Location -LiteralPath $source
    try {
        $inputSource = if ($Tests) { Join-Path $repository 'tools/tests/UninstallBroker.Tests.cpp' } else { 'UninstallBroker.cpp' }
        $imageName = if ($Tests) { 'TileWall.UninstallBroker.Tests.exe' } else { 'TileWall.UninstallBroker.exe' }
        $subsystem = if ($Tests) { '/SUBSYSTEM:CONSOLE' } else { '/SUBSYSTEM:WINDOWS' }
        & $cl /nologo /TP /std:c++20 /EHsc /W4 /WX /O1 /GS /guard:cf /MT /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /c $inputSource "/Fo:$output/UninstallBroker.obj"
        if ($LASTEXITCODE -ne 0) { throw "Uninstall broker compilation failed: $LASTEXITCODE" }
        & $link $subsystem /NOLOGO /WX /MACHINE:X64 /guard:cf /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /MANIFEST:EMBED "/MANIFESTUAC:level='asInvoker' uiAccess='false'" "/OUT:$output/$imageName" "$output/UninstallBroker.obj" kernel32.lib user32.lib shell32.lib ole32.lib oleaut32.lib advapi32.lib bcrypt.lib uuid.lib
        if ($LASTEXITCODE -ne 0) { throw "Uninstall broker linking failed: $LASTEXITCODE" }
    }
    finally { Pop-Location }
}
finally {
    $env:INCLUDE = $savedInclude
    $env:LIB = $savedLib
    $env:PATH = $savedNativePath
}
