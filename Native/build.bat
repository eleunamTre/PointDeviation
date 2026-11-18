@echo off
rem Compila PdRender.dll (aprire «x64 Native Tools Command Prompt for VS» e lanciare da questa cartella)
cl /nologo /O2 /EHsc /LD /MT PdRender.cpp d3d11.lib /Fe:PdRender.dll
if errorlevel 1 exit /b 1
echo PdRender.dll pronta: la compilazione di PointDeviation la copia nella cartella del plugin.
