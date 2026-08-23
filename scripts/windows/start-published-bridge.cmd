@echo off
setlocal
set DOTNET_EXE=C:\Program Files (x86)\dotnet\dotnet.exe
set REPO_ROOT=%~dp0\..\..
if not exist "%REPO_ROOT%\publish\OpcBridge.App.dll" (
    set REPO_ROOT=C:\Users\xlibr\Documents\OpcBridge
)
cd /d "%REPO_ROOT%\publish"
"%DOTNET_EXE%" OpcBridge.App.dll 1>> "%REPO_ROOT%\publish\bridge-task-stdout.log" 2>> "%REPO_ROOT%\publish\bridge-task-stderr.log"
