<#
.SYNOPSIS
    Builds the native IGCS shim and the Dalamud plugin.

.DESCRIPTION
    The shim must be built first: the plugin project copies IgcsBridge.dll into its
    output, and packages it into latest.zip, only if it already exists on disk.

.PARAMETER Deploy
    Also copy the built plugin into Dalamud's devPlugins folder, so it can be loaded
    in-game without going through a plugin repository.

.PARAMETER SkipTests
    Skip the IGCS ABI harness. The harness verifies the add-on boundary without needing
    the game, so there is rarely a good reason to skip it.

.PARAMETER PlatformToolset
    MSVC toolset for the native shim. Detected from the installed Visual Studio when
    omitted, which is what lets the same script build on a developer machine and on a CI
    runner with a different Visual Studio.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Deploy,

    [switch]$SkipTests,

    [string]$PlatformToolset
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$nativeProject = Join-Path $root 'src\native\IgcsBridge\IgcsBridge.vcxproj'
$pluginProject = Join-Path $root 'src\CameraToolsXIV\CameraToolsXIV.csproj'

function Find-VisualStudio {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw "vswhere.exe not found. Install Visual Studio Build Tools with the C++ workload."
    }

    $installPath = & $vswhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath

    if (-not $installPath) {
        throw "No Visual Studio installation with the C++ toolset was found."
    }

    $msbuild = Join-Path $installPath 'MSBuild\Current\Bin\MSBuild.exe'
    if (-not (Test-Path $msbuild)) {
        throw "MSBuild.exe not found under $installPath."
    }

    return [pscustomobject]@{ MSBuild = $msbuild; InstallPath = $installPath }
}

# The vcxproj pins a toolset because MSBuild's own default resolves to v100 and fails, but a pinned
# one is only correct on the machine it was pinned for: this repo is developed against VS 18 (v145)
# while GitHub's windows-latest runners ship VS 2022 (v143). Ask the installation which toolsets it
# actually has and take the newest, so the same script builds in both places.
function Find-PlatformToolset {
    param([string]$InstallPath)

    $toolsets = Get-ChildItem -Path (Join-Path $InstallPath 'MSBuild\Microsoft\VC') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'Platforms\x64\PlatformToolsets' } |
        Where-Object { Test-Path $_ } |
        Get-ChildItem -Directory |
        Select-Object -ExpandProperty Name |
        Where-Object { $_ -match '^v\d+$' } |
        Sort-Object { [int]$_.Substring(1) } -Descending

    if (-not $toolsets) {
        throw "No x64 platform toolset found under $InstallPath. Install the C++ build tools."
    }

    return @($toolsets)[0]
}

Write-Host "==> Building IgcsBridge ($Configuration)" -ForegroundColor Cyan
$vs = Find-VisualStudio
if (-not $PlatformToolset) {
    $PlatformToolset = Find-PlatformToolset -InstallPath $vs.InstallPath
}
Write-Host "Using platform toolset $PlatformToolset"

& $vs.MSBuild $nativeProject /p:Configuration=$Configuration /p:Platform=x64 /p:PlatformToolset=$PlatformToolset /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Native build failed." }

Write-Host "==> Building CameraToolsXIV ($Configuration)" -ForegroundColor Cyan
& dotnet build $pluginProject -c $Configuration -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }

$output = Join-Path $root "src\CameraToolsXIV\bin\$Configuration"

# The shim is what ReShade add-ons actually discover, so a build that silently dropped
# it would look fine and then fail to connect in-game for no visible reason.
$shim = Join-Path $output 'IgcsBridge.dll'
if (-not (Test-Path $shim)) {
    throw "IgcsBridge.dll is missing from the plugin output at $output."
}

Write-Host "Built to $output" -ForegroundColor Green

if (-not $SkipTests) {
    Write-Host "==> Verifying the IGCS ABI" -ForegroundColor Cyan
    $harness = Join-Path $root 'tests\IgcsBridgeHarness\IgcsBridgeHarness.csproj'

    & dotnet build $harness -c $Configuration -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "Harness build failed." }

    $harnessExe = Join-Path $root "tests\IgcsBridgeHarness\bin\$Configuration\net10.0\IgcsBridgeHarness.exe"
    & $harnessExe $shim
    if ($LASTEXITCODE -ne 0) { throw "IGCS ABI harness failed." }
}

if ($Deploy) {
    $devPlugins = Join-Path $env:AppData 'XIVLauncher\devPlugins\CameraToolsXIV'
    Write-Host "==> Deploying to $devPlugins" -ForegroundColor Cyan

    if (-not (Test-Path $devPlugins)) {
        New-Item -ItemType Directory -Path $devPlugins -Force | Out-Null
    }

    foreach ($name in @('CameraToolsXIV.dll', 'CameraToolsXIV.json', 'CameraToolsXIV.deps.json', 'IgcsBridge.dll')) {
        Copy-Item (Join-Path $output $name) -Destination $devPlugins -Force
    }

    Write-Host "Deployed. Reload the plugin from Dalamud's dev plugin list." -ForegroundColor Green
}
