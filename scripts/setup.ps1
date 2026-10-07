# Granny Minecraft Passthrough setup.
#   game\Granny Legacy   a COPY of your Granny Legacy + BepInEx + the GrannyCraft plugin (your Steam install stays untouched)
# Minecraft, one of two ways:
#   your own launcher    adds the version "Granny-Minecraft-Passthrough" (Fabric 26.3) to your .minecraft folder; its
#                        mods load from this folder's minecraft\mods only, so your other versions and mods are not affected
#   -Prism               a portable Prism Launcher + Java 25 inside this folder (for people without a launcher)
param([string]$GrannyPath, [string]$MinecraftDir, [switch]$Prism)
. "$PSScriptRoot\common.ps1"

function Find-GrannyInSteam {
    $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libraries = @($steam.Replace("/", "\"))
    $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        foreach ($m in (Select-String $vdf -Pattern '"path"\s+"(.+)"')) { $libraries += $m.Matches[0].Groups[1].Value.Replace("\\", "\") }
    }
    foreach ($library in $libraries) {
        $common = Join-Path $library "steamapps\common"
        if (-not (Test-Path $common)) { continue }
        foreach ($dir in (Get-ChildItem $common -Directory)) {
            if (Test-Path (Join-Path $dir.FullName "Granny Legacy.exe")) { return $dir.FullName }
        }
    }
    return $null
}

function Ask-Folder($description) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = $description
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dialog.SelectedPath }
    return $null
}

Write-Host "Granny Minecraft Passthrough $Version setup" -ForegroundColor Green

# 0. How Minecraft will run
$state = Read-State
if ($Prism) { $mode = "prism" }
elseif ($MinecraftDir) { $mode = "launcher" }
elseif ($state) { $mode = $state.mode; if ($state.minecraftDir) { $MinecraftDir = $state.minecraftDir } }
else {
    $default = Join-Path $env:APPDATA ".minecraft"
    Write-Host ""
    Write-Host "How do you want to start Minecraft?"
    if (Test-Path (Join-Path $default "versions")) { Write-Host "  1 = with the Minecraft launcher you already use (found: $default)  [recommended]" }
    else { Write-Host "  1 = with the Minecraft launcher you already use (you will pick its .minecraft folder)" }
    Write-Host "  2 = with a separate portable launcher (Prism) inside this folder, signing in with your Microsoft account"
    $choice = Read-Host "Type 1 or 2 and press Enter"
    if ($choice.Trim() -eq "2") { $mode = "prism" } else { $mode = "launcher" }
}
if ($mode -eq "launcher") {
    if (-not $MinecraftDir) {
        $MinecraftDir = Join-Path $env:APPDATA ".minecraft"
        if (-not (Test-Path (Join-Path $MinecraftDir "versions"))) {
            Write-Host "   No Minecraft found in $MinecraftDir : please select your .minecraft folder."
            $MinecraftDir = Ask-Folder 'Select your Minecraft folder (".minecraft", the one that contains "versions")'
        }
    }
    if (-not $MinecraftDir -or -not (Test-Path (Join-Path $MinecraftDir "versions"))) {
        throw 'That is not a Minecraft folder (no "versions" inside). Start your launcher once, or run Setup.bat again and pick option 2.'
    }
    $MinecraftDir = (Resolve-Path $MinecraftDir).Path
}

# 1. GrannyCraft binaries (in bin\ in the release zip; downloaded from the release when this is a source checkout)
Step "GrannyCraft files"
$needed = @("GrannyCraft.dll", "GrannyCraftNative.dll", $ModJar)
if ($needed | Where-Object { -not (Test-Path (Join-Path $Bin $_)) }) {
    $zip = Join-Path $Downloads "Granny-Minecraft-Passthrough-$Version.zip"
    New-Item -ItemType Directory -Force $Downloads | Out-Null
    Write-Host "   bin\ is missing: downloading the release from GitHub..."
    Invoke-WebRequest "https://github.com/$Repo/releases/download/v$Version/Granny-Minecraft-Passthrough-$Version.zip" -OutFile $zip -UseBasicParsing
    $tmp = Join-Path $Downloads "release"
    Expand-Zip $zip $tmp
    New-Item -ItemType Directory -Force $Bin | Out-Null
    $released = Get-ChildItem $tmp -Recurse -Directory -Filter bin | Select-Object -First 1
    Copy-Item (Join-Path $released.FullName "*") $Bin -Force
    Remove-Item $tmp -Recurse -Force
}
Write-Host "   ok"

# 2. A copy of Granny Legacy
Step "Granny Legacy (copy)"
if (Test-Path (Join-Path $Game "Granny Legacy.exe")) {
    Write-Host "   already copied: $Game"
} else {
    if (-not $GrannyPath) { $GrannyPath = Find-GrannyInSteam; if ($GrannyPath) { Write-Host "   found in Steam: $GrannyPath" } }
    if (-not $GrannyPath) {
        Write-Host "   Granny Legacy not found in your Steam libraries: please select its folder."
        $GrannyPath = Ask-Folder 'Select your Granny Legacy folder (the one that contains "Granny Legacy.exe")'
    }
    if (-not $GrannyPath -or -not (Test-Path (Join-Path $GrannyPath "Granny Legacy.exe"))) {
        throw 'No "Granny Legacy.exe" there. Run Setup.bat again and pick the Granny Legacy folder (Steam: right click the game > Manage > Browse local files).'
    }
    $GrannyPath = (Resolve-Path $GrannyPath).Path
    if ($GrannyPath.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) { throw "Pick your original Granny Legacy folder, not one inside this folder." }
    Write-Host "   copying (about 1.4 GB, the original is only read)..."
    New-Item -ItemType Directory -Force $Game | Out-Null
    # any mod loader already in the original is left out: a clean BepInEx goes in below
    robocopy $GrannyPath $Game /E /NFL /NDL /NJH /NJS /NP /XD (Join-Path $GrannyPath "BepInEx") (Join-Path $GrannyPath "dotnet") `
        /XF winhttp.dll doorstop_config.ini .doorstop_version changelog.txt | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying Granny Legacy failed (robocopy $LASTEXITCODE)." }
    Write-Host "   ok"
}

# 3. BepInEx 6 (IL2CPP) into the copy
Step "BepInEx 6.0.0-be.752"
if (-not (Test-Path (Join-Path $Game "BepInEx\core\BepInEx.Core.dll"))) {
    Expand-Zip (Get-Dep "BepInEx") $Game
}
Write-Host "   ok"

# 4. The GrannyCraft plugin
Step "GrannyCraft plugin"
$plugin = Join-Path $Game "BepInEx\plugins\GrannyCraft"
New-Item -ItemType Directory -Force $plugin | Out-Null
Copy-Item (Join-Path $Bin "GrannyCraft.dll"), (Join-Path $Bin "GrannyCraftNative.dll") $plugin -Force
Write-Host "   ok"

function Install-Mods($mods) {
    New-Item -ItemType Directory -Force $mods | Out-Null
    Get-ChildItem $mods -Filter "grannycraft-*.jar" | Remove-Item -Force
    Get-ChildItem $mods -Filter "fabric-api-*.jar" | Remove-Item -Force
    Copy-Item (Join-Path $Bin $ModJar) $mods -Force
    Copy-Item (Get-Dep "FabricApi") $mods -Force
}

if ($mode -eq "launcher") {
    # 5. A version in your own launcher
    Step "Minecraft version for your launcher"
    Install-Mods $McMods
    Write-MinecraftVersion $MinecraftDir
    Write-Host "   added: $MinecraftDir\versions\$VersionId"
    if (Add-LauncherProfile $MinecraftDir) { Write-Host '   added the installation "Granny Minecraft Passthrough" (official launcher)' }
    Write-Host "   ok"
} else {
    # 5. Java 25
    Step "Java 25 (Eclipse Temurin)"
    if (-not (Find-Javaw)) { Expand-Zip (Get-Dep "Java") $JavaDir }
    Write-Host "   ok"

    # 6. Prism Launcher (portable: its accounts, settings and Minecraft files all stay in launcher\)
    Step "Prism Launcher 11.1.1 (portable)"
    if (-not (Test-Path (Join-Path $Launcher "prismlauncher.exe"))) { Expand-Zip (Get-Dep "Prism") $Launcher }
    Write-Host "   ok"

    # 7. The GrannyCraft instance: Minecraft 26.3 + Fabric Loader 0.19.5 + Fabric API + GrannyCraft
    Step "Minecraft instance"
    New-Item -ItemType Directory -Force $Instance | Out-Null
    $pack = Join-Path $Instance "mmc-pack.json"
    if (-not (Test-Path $pack)) {
        Write-Utf8 $pack @'
{
    "components": [
        { "uid": "net.minecraft", "version": "26.3", "important": true },
        { "uid": "net.fabricmc.intermediary", "version": "26.3", "dependencyOnly": true },
        { "uid": "net.fabricmc.fabric-loader", "version": "0.19.5" }
    ],
    "formatVersion": 1
}
'@
    }
    $cfg = Join-Path $Instance "instance.cfg"
    Set-Ini $cfg "InstanceType" "OneSix"
    Set-Ini $cfg "name" "GrannyCraft"
    Set-Ini $cfg "iconKey" "default"
    Install-Mods (Join-Path $Instance "minecraft\mods")
    Update-JavaPaths
    Write-Host "   ok"
}

$saved = [ordered]@{ mode = $mode; minecraftDir = $MinecraftDir; version = $Version }
Write-Utf8 $StateFile (New-Object PSObject -Property $saved | ConvertTo-Json)

Write-Host ""
Write-Host "Setup complete. Start the game with Play.bat" -ForegroundColor Green
if ($mode -eq "launcher") {
    Write-Host "Play.bat then asks you to start Minecraft from your launcher with the version $VersionId"
    Write-Host '(official Minecraft Launcher: the installation "Granny Minecraft Passthrough").'
} else {
    Write-Host "(the first time, Prism asks you to sign in with the Microsoft account that owns Minecraft Java Edition)"
}
