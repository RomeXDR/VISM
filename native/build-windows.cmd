@echo off
setlocal
set "webview=%~1\build\native"
set "output=%~2"

set "vswhere=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%vswhere%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "visualStudio=%%i"
if not defined visualStudio (
    echo Install Visual Studio Build Tools with the Desktop development with C++ workload. 1>&2
    exit /b 1
)
call "%visualStudio%\VC\Auxiliary\Build\vcvars64.bat" > nul
if errorlevel 1 exit /b 1
if not exist "%output%\." mkdir "%output%"
cl.exe /nologo /std:c++17 /EHsc /O2 /MT /LD /I"%webview%\include" "%~dp0windows-host.cpp" /Fo"%output%\windows-host.obj" /Fe"%output%\VISM.WindowsHost.dll" /link /IMPLIB:"%output%\VISM.WindowsHost.lib" user32.lib ole32.lib comdlg32.lib uuid.lib
if errorlevel 1 exit /b 1
copy /y "%webview%\x64\WebView2Loader.dll" "%output%\WebView2Loader.dll" > nul
exit /b %errorlevel%
