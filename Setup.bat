@echo off
rem Installs Granny Minecraft Passthrough: a copy of Granny Legacy with BepInEx + plugin, and the Minecraft side
rem (a version for the launcher you already use, or a portable Prism Launcher). Options: -MinecraftDir "path", -Prism
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\setup.ps1" %*
echo.
pause
