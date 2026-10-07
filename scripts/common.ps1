# Shared by setup.ps1, play.ps1 and build.ps1. Everything lives inside the GrannyCraft folder.
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root = Split-Path $PSScriptRoot -Parent
$Version = "0.1.0"
$Repo = "cabcrab/Granny-Minecraft-Passthrough"
$Game = Join-Path $Root "game\Granny Legacy"        # a copy of Granny Legacy with BepInEx + the plugin
$Launcher = Join-Path $Root "launcher"              # Prism Launcher, portable
$Instance = Join-Path $Launcher "instances\GrannyCraft"
$JavaDir = Join-Path $Root "java"
$Bin = Join-Path $Root "bin"
$Downloads = Join-Path $Root "downloads"
$ModJar = "grannycraft-$Version.jar"

# Everything is downloaded from its official source and checked against these hashes.
$Deps = @{
    BepInEx   = @{ File = "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.752.zip"; Sha256 = "F9D128E162269579B67E91A4923764F3D54FA8C8162A238CE6657D704084334C"
                   Url = "https://builds.bepinex.dev/projects/bepinex_be/752/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.752%2Bdd0655f.zip" }
    Prism     = @{ File = "PrismLauncher-Windows-MSVC-Portable-11.1.1.zip"; Sha256 = "AB35A770FB06D89D2CCC098079DB5DB329FB4E68F42B72BABD8B095EFDE3D2D7"
                   Url = "https://github.com/PrismLauncher/PrismLauncher/releases/download/11.1.1/PrismLauncher-Windows-MSVC-Portable-11.1.1.zip" }
    Java      = @{ File = "OpenJDK25U-jre_x64_windows_hotspot_25.0.4.1_1.zip"; Sha256 = "4C95451CEA98556DEF2C54F7782933F52A26D4A36BD85E1D59F0364464828B07"
                   Url = "https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.4.1%2B1/OpenJDK25U-jre_x64_windows_hotspot_25.0.4.1_1.zip" }
    FabricApi = @{ File = "fabric-api-0.161.0+26.3.jar"; Sha256 = "86F16178A3CECC887A85A4CFE9A79D92FA7341D8F39B5951A4D6AD800AB657A6"
                   Url = "https://cdn.modrinth.com/data/P7dR8mSH/versions/bNnaTiuM/fabric-api-0.161.0%2B26.3.jar" }
}

function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

function Get-Dep($name) {
    $d = $Deps[$name]
    $path = Join-Path $Downloads $d.File
    New-Item -ItemType Directory -Force $Downloads | Out-Null
    if ((Test-Path $path) -and (Get-FileHash $path -Algorithm SHA256).Hash -eq $d.Sha256) { return $path }
    Write-Host "   downloading $($d.File)..."
    Invoke-WebRequest $d.Url -OutFile $path -UseBasicParsing
    if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $d.Sha256) {
        Remove-Item $path -Force
        throw "$($d.File): the download is damaged or changed (wrong SHA-256). Try again later."
    }
    return $path
}

function Expand-Zip($zip, $to) {
    New-Item -ItemType Directory -Force $to | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($e in $archive.Entries) {
            $target = Join-Path $to $e.FullName
            if ($e.FullName.EndsWith("/")) { New-Item -ItemType Directory -Force $target | Out-Null; continue }
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $target, $true)
        }
    } finally { $archive.Dispose() }
}

function Find-Javaw {
    if (-not (Test-Path $JavaDir)) { return $null }
    Get-ChildItem $JavaDir -Recurse -Filter javaw.exe | Select-Object -First 1 -ExpandProperty FullName
}

# Prism stores its settings in Qt INI files ([General] section). Paths use forward slashes there.
function Set-Ini($file, $key, $value) {
    $lines = @()
    if (Test-Path $file) { $lines = @(Get-Content $file) }
    if (-not ($lines | Where-Object { $_ -eq "[General]" })) { $lines = @("[General]") + $lines }
    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -like "$key=*") { $lines[$i] = "$key=$value"; $found = $true }
    }
    if (-not $found) {
        $at = [Array]::IndexOf($lines, "[General]")
        $rest = @()
        if ($at + 1 -lt $lines.Count) { $rest = @($lines[($at + 1)..($lines.Count - 1)]) }
        $lines = @($lines[0..$at]) + @("$key=$value") + $rest
    }
    [IO.File]::WriteAllLines($file, [string[]]$lines)
}

# The folder may have been moved since setup: point Prism at our Java again.
function Update-JavaPaths {
    $javaw = Find-Javaw
    if (-not $javaw) { throw "Java is missing: run Setup.bat again." }
    $j = $javaw.Replace("\", "/")
    Set-Ini (Join-Path $Launcher "prismlauncher.cfg") "JavaPath" $j
    Set-Ini (Join-Path $Instance "instance.cfg") "OverrideJavaLocation" "true"
    Set-Ini (Join-Path $Instance "instance.cfg") "JavaPath" $j
}

function Test-Installed {
    (Test-Path (Join-Path $Game "Granny Legacy.exe")) -and (Test-Path (Join-Path $Game "BepInEx\plugins\GrannyCraft\GrannyCraft.dll")) -and
    (Test-Path (Join-Path $Launcher "prismlauncher.exe")) -and (Test-Path (Join-Path $Instance "minecraft\mods\$ModJar")) -and (Find-Javaw)
}
