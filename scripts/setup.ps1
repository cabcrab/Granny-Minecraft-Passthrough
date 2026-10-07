# GrannyCraft setup. Everything is installed INSIDE this folder; nothing outside it is changed:
#   game\Granny Legacy   a COPY of your Granny Legacy + BepInEx + the GrannyCraft plugin (your Steam install stays untouched)
#   launcher             Prism Launcher (portable) with a "GrannyCraft" instance: Minecraft 26.3 + Fabric + GrannyCraft
#   java                 Java 25 for that Minecraft
#   downloads            the downloaded files (can be deleted after setup)
param([string]$GrannyPath)
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

function Ask-GrannyFolder {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select your Granny Legacy folder (the one that contains "Granny Legacy.exe")'
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dialog.SelectedPath }
    return $null
}

Write-Host "Granny Minecraft Passthrough $Version setup - everything goes into $Root" -ForegroundColor Green

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
    if (-not $GrannyPath) { Write-Host "   Granny Legacy not found in your Steam libraries: please select its folder."; $GrannyPath = Ask-GrannyFolder }
    if (-not $GrannyPath -or -not (Test-Path (Join-Path $GrannyPath "Granny Legacy.exe"))) {
        throw 'No "Granny Legacy.exe" there. Run Setup.bat again and pick the Granny Legacy folder (Steam: right click the game > Manage > Browse local files).'
    }
    $GrannyPath = (Resolve-Path $GrannyPath).Path
    if ($GrannyPath.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) { throw "Pick your original Granny Legacy folder, not one inside GrannyCraft." }
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
$mods = Join-Path $Instance "minecraft\mods"
New-Item -ItemType Directory -Force $mods | Out-Null
$pack = Join-Path $Instance "mmc-pack.json"
if (-not (Test-Path $pack)) {
    [IO.File]::WriteAllText($pack, @'
{
    "components": [
        { "uid": "net.minecraft", "version": "26.3", "important": true },
        { "uid": "net.fabricmc.intermediary", "version": "26.3", "dependencyOnly": true },
        { "uid": "net.fabricmc.fabric-loader", "version": "0.19.5" }
    ],
    "formatVersion": 1
}
'@)
}
$cfg = Join-Path $Instance "instance.cfg"
Set-Ini $cfg "InstanceType" "OneSix"
Set-Ini $cfg "name" "GrannyCraft"
Set-Ini $cfg "iconKey" "default"
Get-ChildItem $mods -Filter "grannycraft-*.jar" | Remove-Item -Force
Get-ChildItem $mods -Filter "fabric-api-*.jar" | Remove-Item -Force
Copy-Item (Join-Path $Bin $ModJar) $mods -Force
Copy-Item (Get-Dep "FabricApi") $mods -Force
Update-JavaPaths
Write-Host "   ok"

Write-Host ""
Write-Host "Setup complete. Start the game with Play.bat" -ForegroundColor Green
Write-Host "(the first time, Prism asks you to sign in with the Microsoft account that owns Minecraft Java Edition)"
