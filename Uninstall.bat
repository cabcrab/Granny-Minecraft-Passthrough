@echo off
rem Removes the Minecraft version this mod added to your .minecraft. Then just delete this folder.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1" %*
echo.
pause
