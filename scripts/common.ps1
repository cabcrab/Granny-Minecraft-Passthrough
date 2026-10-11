# Shared by setup.ps1, play.ps1, uninstall.ps1 and build.ps1.
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root = Split-Path $PSScriptRoot -Parent
$Version = "1.0.0"
$Repo = "cabcrab/Granny-Minecraft-Passthrough"
$Game = Join-Path $Root "game\Granny Legacy"        # a copy of Granny Legacy with BepInEx + the plugin
$Bin = Join-Path $Root "bin"
$Downloads = Join-Path $Root "downloads"
$StateFile = Join-Path $Root "setup-state.json"
$ModJar = "grannycraft-$Version.jar"
# "your own launcher" mode: a Minecraft version that loads the mods from this folder only
$VersionId = "Granny-Minecraft-Passthrough"
$ProfileKey = "granny-minecraft-passthrough"
$McGame = Join-Path $Root "minecraft"               # its game folder (worlds, options) in the official launcher
$McMods = Join-Path $McGame "mods"
# "portable Prism" mode
$Launcher = Join-Path $Root "launcher"
$Instance = Join-Path $Launcher "instances\GrannyCraft"
$JavaDir = Join-Path $Root "java"

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

function Write-Utf8($path, $text) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false)) }

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

function Read-State {
    if (Test-Path $StateFile) { return Get-Content $StateFile -Raw | ConvertFrom-Json }
    return $null
}

# ---------- "your own launcher" mode ----------

# versions\Granny-Minecraft-Passthrough in the player's .minecraft: Fabric Loader 0.19.5 for 26.3 (the official Fabric
# profile, shipped in scripts\) that loads mods ONLY from this folder (fabric.modsFolder), never from .minecraft\mods.
function Write-MinecraftVersion($mcDir) {
    $fabric = Get-Content (Join-Path $PSScriptRoot "fabric-loader-0.19.5-26.3.json") -Raw | ConvertFrom-Json
    $fabric.id = $VersionId
    $fabric.arguments.jvm = @($fabric.arguments.jvm) + @("-Dfabric.modsFolder=$McMods")
    $dir = Join-Path $mcDir "versions\$VersionId"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Write-Utf8 (Join-Path $dir "$VersionId.json") ($fabric | ConvertTo-Json -Depth 20)
    $jar = Join-Path $dir "$VersionId.jar"
    if (-not (Test-Path $jar)) { Write-Utf8 $jar "" }   # launchers expect a jar next to the json (Fabric's installer does the same)
}

# An installation in the official launcher's list, playing that version with this folder's minecraft\ as game folder.
function Add-LauncherProfile($mcDir) {
    $file = Join-Path $mcDir "launcher_profiles.json"
    if (-not (Test-Path $file)) { return $false }
    $backup = "$file.before-granny-passthrough"
    if (-not (Test-Path $backup)) { Copy-Item $file $backup }
    $json = Get-Content $file -Raw | ConvertFrom-Json
    if (-not $json.profiles) { $json | Add-Member -NotePropertyName profiles -NotePropertyValue ([pscustomobject]@{}) -Force }
    $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    $entry = [pscustomobject]@{ name = "Granny Minecraft Passthrough"; type = "custom"; lastVersionId = $VersionId
                                gameDir = $McGame; icon = "Furnace"; created = $now; lastUsed = $now }
    $json.profiles | Add-Member -NotePropertyName $ProfileKey -NotePropertyValue $entry -Force
    Write-Utf8 $file ($json | ConvertTo-Json -Depth 20)
    return $true
}

function Remove-LauncherProfile($mcDir) {
    $file = Join-Path $mcDir "launcher_profiles.json"
    if (-not (Test-Path $file)) { return }
    $json = Get-Content $file -Raw | ConvertFrom-Json
    if ($json.profiles -and $json.profiles.PSObject.Properties[$ProfileKey]) {
        $json.profiles.PSObject.Properties.Remove($ProfileKey)
        Write-Utf8 $file ($json | ConvertTo-Json -Depth 20)
    }
}

# ---------- "portable Prism" mode ----------

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

function Test-Installed($state) {
    if (-not $state) { return $false }
    if (-not ((Test-Path (Join-Path $Game "Granny Legacy.exe")) -and (Test-Path (Join-Path $Game "BepInEx\plugins\GrannyCraft\GrannyCraft.dll")))) { return $false }
    if ($state.mode -eq "prism") {
        return (Test-Path (Join-Path $Launcher "prismlauncher.exe")) -and (Test-Path (Join-Path $Instance "minecraft\mods\$ModJar")) -and [bool](Find-Javaw)
    }
    return (Test-Path (Join-Path $McMods $ModJar)) -and (Test-Path (Join-Path $state.minecraftDir "versions\$VersionId\$VersionId.json"))
}
