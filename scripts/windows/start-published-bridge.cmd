@echo off
setlocal
set DOTNET_EXE=C:\Program Files\dotnet\dotnet.exe
set APPHOST=%~dp0\..\..\publish\OpcBridge.App.exe
set REPO_ROOT=%~dp0\..\..
if not exist "%REPO_ROOT%\publish\OpcBridge.App.dll" (
    set REPO_ROOT=C:\Users\xlibr\Documents\OpcBridge
)
cd /d "%REPO_ROOT%\publish"
REM Prefer the self-contained apphost (carries its own runtime); fall back to dotnet + dll.
if exist "OpcBridge.App.exe" (
    "OpcBridge.App.exe" 1>> "%REPO_ROOT%\publish\bridge-task-stdout.log" 2>> "%REPO_ROOT%\publish\bridge-task-stderr.log"
) else (
    "%DOTNET_EXE%" OpcBridge.App.dll 1>> "%REPO_ROOT%\publish\bridge-task-stdout.log" 2>> "%REPO_ROOT%\publish\bridge-task-stderr.log"
)
