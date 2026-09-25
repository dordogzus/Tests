@echo off
setlocal
cd /d "%~dp0"

if "%~1"=="" (
  echo.
  echo ============================================================
  echo   More Players v3.0.0 - Drag and Drop Installer
  echo ============================================================
  echo.
  echo Drag one of these onto this BAT file:
  echo   - Approximately Up game EXE
  echo   - Approximately Up game folder
  echo   - its BepInEx folder
  echo.
  echo Or drag it now into this window and press Enter:
  set /p "DROP=> "
  if not defined DROP goto :fail
  set "DROP=%DROP:"=%"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Mod.ps1" "%DROP%"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Mod.ps1" "%~1"
)

set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" (
  echo Installation finished successfully.
) else (
  echo Installation failed. Read the message above.
)
echo.
pause
exit /b %RC%

:fail
echo No game path was supplied.
pause
exit /b 1
