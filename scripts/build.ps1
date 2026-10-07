# Builds GrannyCraft from source into bin\ (only needed if you change the code; the release zip already has bin\).
# Needs: JDK 25 (JAVA_HOME), .NET SDK 6 or newer, Visual Studio Build Tools with C++, and Setup.bat + one Play.bat run
# done before (the plugin compiles against the files BepInEx generates from the game on its first start).
#   scripts\build.ps1 -Install   also copies the new files into game\ and the Minecraft instance
param([switch]$Install)
. "$PSScriptRoot\common.ps1"

# build caches stay inside the folder too
$env:GRADLE_USER_HOME = Join-Path $Root ".cache\gradle"
$env:NUGET_PACKAGES = Join-Path $Root ".cache\nuget"
$env:DOTNET_CLI_HOME = Join-Path $Root ".cache\dotnet"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_ROLL_FORWARD = "Major"

if (-not (Test-Path (Join-Path $Game "BepInEx\interop\Assembly-CSharp.dll"))) {
    throw "game\Granny Legacy\BepInEx\interop is missing: run Setup.bat, then start the game once with Play.bat."
}
New-Item -ItemType Directory -Force $Bin | Out-Null

Step "native compositor"
cmd /c "`"$Root\granny-native\build.bat`""
if ($LASTEXITCODE -ne 0) { throw "native build failed" }
Copy-Item "$Root\granny-native\GrannyCraftNative.dll" $Bin -Force

Step "Granny plugin"
dotnet build "$Root\granny-plugin\GrannyCraft.csproj" -c Release -o "$Root\.cache\plugin-out" "-p:Game=$Game"
if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }
Copy-Item "$Root\.cache\plugin-out\GrannyCraft.dll" $Bin -Force

Step "Minecraft mod"
Push-Location "$Root\minecraft-mod"
try { .\gradlew.bat build --no-daemon; if ($LASTEXITCODE -ne 0) { throw "mod build failed" } } finally { Pop-Location }
Copy-Item "$Root\minecraft-mod\build\libs\$ModJar" $Bin -Force

if ($Install) {
    Copy-Item (Join-Path $Bin "GrannyCraft.dll"), (Join-Path $Bin "GrannyCraftNative.dll") (Join-Path $Game "BepInEx\plugins\GrannyCraft") -Force
    $state = Read-State
    if ($state -and $state.mode -eq "prism") { $mods = Join-Path $Instance "minecraft\mods" } else { $mods = $McMods }
    Get-ChildItem $mods -Filter "grannycraft-*.jar" | Remove-Item -Force
    Copy-Item (Join-Path $Bin $ModJar) $mods -Force
}
Write-Host "built into $Bin" -ForegroundColor Green
