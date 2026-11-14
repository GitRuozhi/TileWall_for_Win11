param([Parameter(Mandatory)][string]$OutputDirectory, [switch]$Tests, [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64')

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
$source = Join-Path $repository 'src/TileWall.UninstallBroker'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw "Native uninstall broker requires installed Visual Studio C++ $Architecture build tools (vswhere.exe not found)." }
$installations = @(& $vswhere -latest -products '*' -requires $component -property installationPath)
if ($LASTEXITCODE -ne 0 -or $installations.Count -ne 1) { throw "Native uninstall broker requires installed Visual Studio C++ $Architecture build tools." }
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
if (-not $sdk) { throw 'Native uninstall broker requires an installed Windows 10 SDK with matching host tools and target libraries.' }
$cl = Join-Path $vc.FullName "bin/$hostTools/$Architecture/cl.exe"
$link = Join-Path $vc.FullName "bin/$hostTools/$Architecture/link.exe"
$rc = Join-Path $kits "bin/$($sdk.Name)/$hostArchitecture/rc.exe"
foreach ($tool in @($cl, $link, $rc)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Missing native build tool: $tool" }
}
$savedInclude = $env:INCLUDE
$savedLib = $env:LIB
$savedNativePath = $env:PATH
try {
    $env:INCLUDE = (@((Join-Path $vc.FullName 'include')) + @('ucrt', 'shared', 'um', 'winrt' | ForEach-Object { Join-Path $sdk.FullName $_ })) -join ';'
    $env:LIB = (@((Join-Path $vc.FullName "lib/$Architecture")) + @('um', 'ucrt' | ForEach-Object { Join-Path $kits "Lib/$($sdk.Name)/$_/$Architecture" })) -join ';'
    # link /MANIFEST:EMBED invokes the matching SDK resource compiler itself.
    # Do not depend on a developer prompt or a globally installed rc.exe.
    $env:PATH = (Split-Path $rc -Parent) + ';' + $savedNativePath
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Push-Location -LiteralPath $source
    try {
        $inputSource = if ($Tests) { Join-Path $repository 'tests/packaging/UninstallBroker.Tests.cpp' } else { 'UninstallBroker.cpp' }
        $imageName = if ($Tests) { 'TileWall.UninstallBroker.Tests.exe' } else { 'TileWall.UninstallBroker.exe' }
        $subsystem = if ($Tests) { '/SUBSYSTEM:CONSOLE' } else { '/SUBSYSTEM:WINDOWS' }
        & $cl /nologo /TP /std:c++20 /EHsc /W4 /WX /O1 /GS /guard:cf /MT /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /c $inputSource "/Fo:$output/UninstallBroker.obj"
        if ($LASTEXITCODE -ne 0) { throw "Uninstall broker compilation failed: $LASTEXITCODE" }
        & $link $subsystem /NOLOGO /WX "/MACHINE:$Architecture" /guard:cf /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /MANIFEST:EMBED "/MANIFESTUAC:level='asInvoker' uiAccess='false'" "/OUT:$output/$imageName" "$output/UninstallBroker.obj" kernel32.lib user32.lib shell32.lib ole32.lib oleaut32.lib advapi32.lib bcrypt.lib uuid.lib
        if ($LASTEXITCODE -ne 0) { throw "Uninstall broker linking failed: $LASTEXITCODE" }
    }
    finally { Pop-Location }
}
finally {
    $env:INCLUDE = $savedInclude
    $env:LIB = $savedLib
    $env:PATH = $savedNativePath
}
