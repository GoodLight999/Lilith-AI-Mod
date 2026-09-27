param(
    [string]$GameDir = $env:LILITH_GAME_DIR,
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"

function Resolve-LilithGameDir {
    param([string]$Candidate)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($Candidate)) {
        $candidates += $Candidate
    }

    $candidates += @(
        "C:\Program Files (x86)\Steam\steamapps\common\The NOexistenceN of Lilith",
        "C:\Program Files\Steam\steamapps\common\The NOexistenceN of Lilith"
    )

    foreach ($path in $candidates | Select-Object -Unique) {
        if (Test-Path (Join-Path $path "Lilith.exe")) {
            return (Resolve-Path $path).Path
        }
    }

    throw "Could not find Lilith.exe. Pass -GameDir or set LILITH_GAME_DIR."
}

$GameDir = Resolve-LilithGameDir $GameDir
$InteropDir = Join-Path $GameDir "BepInEx\interop"
$BepInExCore = Join-Path $GameDir "BepInEx\core"

$required = @(
    (Join-Path $InteropDir "Assembly-CSharp.dll"),
    (Join-Path $InteropDir "UnityEngine.dll"),
    (Join-Path $InteropDir "Unity.TextMeshPro.dll"),
    (Join-Path $BepInExCore "BepInEx.Core.dll"),
    (Join-Path $BepInExCore "BepInEx.Unity.IL2CPP.dll"),
    (Join-Path $BepInExCore "Il2CppInterop.Runtime.dll"),
    (Join-Path $BepInExCore "0Harmony.dll")
)

$missing = $required | Where-Object { -not (Test-Path $_) }
if ($missing.Count -gt 0) {
    Write-Host "Missing generated/runtime assemblies:" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host "  $_" }
    throw "Install BepInEx for Lilith and launch the game once so BepInEx\interop is generated, then rerun this script."
}

Write-Host "Game: $GameDir"
dotnet build "src\LilithTextInjector\LilithTextInjector.csproj" -c Release -p:LilithGameDir="$GameDir"
if ($LASTEXITCODE -ne 0) { throw "LilithTextInjector build failed." }

dotnet publish "src\LilithVoiceHost\LilithVoiceHost.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "LilithVoiceHost publish failed." }

dotnet publish "src\LilithModInstaller\LilithModInstaller.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed." }

$injectorOutput = Join-Path $PSScriptRoot "..\src\LilithTextInjector\bin\Release\net6.0"
$voiceOutput = Join-Path $PSScriptRoot "..\src\LilithVoiceHost\bin\Release\net8.0\win-x64\publish"
$installerOutput = Join-Path $PSScriptRoot "..\src\LilithModInstaller\bin\Release\net8.0-windows\win-x64\publish"

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "Injector:  $injectorOutput"
Write-Host "VoiceHost: $voiceOutput"
Write-Host "Installer: $installerOutput"

if (-not $Deploy) {
    Write-Host "Use -Deploy to copy the development binaries into the installed game."
    exit 0
}

$pluginDir = Join-Path $GameDir "BepInEx\plugins"
$voiceRuntime = Join-Path $GameDir "BepInEx\data\LilithTextInjector\voice-runtime"
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
New-Item -ItemType Directory -Force -Path $voiceRuntime | Out-Null

$pluginFiles = @(
    "LilithTextInjector.dll",
    "NAudio.dll",
    "NAudio.Core.dll",
    "NAudio.Wasapi.dll"
)

foreach ($name in $pluginFiles) {
    $source = Join-Path $injectorOutput $name
    if (Test-Path $source) {
        Copy-Item $source (Join-Path $pluginDir $name) -Force
    }
}

$voiceHostExe = Join-Path $voiceOutput "LilithVoiceHost.exe"
if (-not (Test-Path $voiceHostExe)) {
    throw "Published LilithVoiceHost.exe was not found: $voiceHostExe"
}
Copy-Item $voiceHostExe (Join-Path $voiceRuntime "LilithVoiceHost.exe") -Force

Write-Host "Development binaries deployed to the game." -ForegroundColor Green
Write-Host "Start Lilith normally through Steam and inspect BepInEx\LogOutput.log if startup fails."
