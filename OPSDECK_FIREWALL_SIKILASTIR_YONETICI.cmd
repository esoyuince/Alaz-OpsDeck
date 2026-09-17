@echo off
setlocal
set "DECK_IP=%~1"
if "%DECK_IP%"=="" set "DECK_IP=192.168.1.50"
net session >nul 2>&1
if not "%errorlevel%"=="0" (
  echo Windows yonetici onayi istenecek...
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList '%DECK_IP%'"
  exit /b
)
echo OpsDeck firewall daraltiliyor: deck=%DECK_IP%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\configure_opsdeck_firewall.ps1" -ProgramPath "%~dp0runtime\OpsDeck.Host.exe" -DeckIp "%DECK_IP%" -Apply
if errorlevel 1 echo FIREWALL UYGULANAMADI
if not errorlevel 1 echo FIREWALL UYGULANDI VE DOGRULANDI
pause
endlocal
