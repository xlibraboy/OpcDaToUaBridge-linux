@echo off
set REPO_ROOT=%~dp0\..\..
pushd "%REPO_ROOT%\publish"
start "" /b "%REPO_ROOT%\publish\OpcBridge.App.exe" 1>> "%REPO_ROOT%\publish\bridge-task-stdout.log" 2>> "%REPO_ROOT%\publish\bridge-task-stderr.log"
popd
