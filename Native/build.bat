@echo off
rem Compila PdRender.dll. Basta un doppio clic: lo script trova da solo Visual Studio con la parte C++.
setlocal
cd /d "%~dp0"
where cl >nul 2>nul
if not errorlevel 1 goto compile

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo Visual Studio Installer non trovato.
  goto fail
)
set "VSDIR="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (
  echo Manca la parte C++ di Visual Studio: in Visual Studio Installer › Modifica spunta
  echo «Sviluppo di applicazioni desktop con C++».
  goto fail
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 (
  echo Impostazione del compilatore non riuscita.
  goto fail
)

:compile
cl /nologo /O2 /EHsc /LD /MT PdRender.cpp d3d11.lib d3dcompiler.lib user32.lib /Fe:PdRender.dll
if errorlevel 1 goto fail
echo.
echo PdRender.dll pronta: ora compila PointDeviation in Visual Studio, la copia nella cartella del plugin.
pause
exit /b 0

:fail
echo.
echo Compilazione NON riuscita: manda questa finestra (o una foto) a chi segue il progetto.
pause
exit /b 1
