@echo off
cd /d C:\Users\xlibr\Documents\OpcDaToUaBridge\publish
REM Read the runtime HTTP port from appsettings.json (the bridge auto-assigns
REM a non-default port when 8080 is already in use on the host).
set HTTP_PORT=8080
for /f "usebackq tokens=2 delims=:,}" %%a in (`powershell -NoProfile -Command "(Get-Content appsettings.json -Raw | ConvertFrom-Json).Bridge.HttpPort" 2^>nul`) do set HTTP_PORT=%%a
if not defined HTTP_PORT set HTTP_PORT=8080
REM If something is already listening on the runtime port, do nothing (prevents duplicate processes).
powershell -NoProfile -Command "if((Test-NetConnection -ComputerName 127.0.0.1 -Port %HTTP_PORT% -WarningAction SilentlyContinue).TcpTestSucceeded){exit 1}" >nul 2>&1
if errorlevel 1 goto :eof
start "" "C:\Program Files (x86)\dotnet\dotnet.exe" OpcBridge.App.dll
