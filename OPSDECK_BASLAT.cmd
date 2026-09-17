@echo off
setlocal
cd /d "%~dp0"
set "DOTNET_ROOT=%~dp0.dotnet"
set "DOTNET_ROOT_X64=%~dp0.dotnet"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "OPSDECK_HOST=%~dp0runtime\OpsDeck.Host.exe"
if not exist "%OPSDECK_HOST%" (
  echo ALAZ OPSDECK active runtime not found.
  echo Expected: %OPSDECK_HOST%
  pause
  exit /b 1
)
start "" "%OPSDECK_HOST%" %*
endlocal
