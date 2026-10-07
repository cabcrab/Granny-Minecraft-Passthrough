@echo off
rem Builds GrannyCraftNative.dll (needs Visual Studio or its Build Tools with the C++ workload).
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (
  echo Visual Studio C++ build tools not found.
  exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cd /d %~dp0
cl /nologo /O2 /EHsc /MT /LD /std:c++17 GrannyCraftNative.cpp /Fe:GrannyCraftNative.dll /link /DLL
