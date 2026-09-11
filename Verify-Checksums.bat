@echo off
setlocal
set "AKM_SCRIPT_DIR=%~dp0"
set "AKM_HOST=powershell.exe"
where pwsh.exe >nul 2>nul
if not errorlevel 1 set "AKM_HOST=pwsh.exe"
set "AKM_VERIFY_DIR=%AKM_SCRIPT_DIR%"
set "AKM_NO_PAUSE="
:arguments
if "%~1"=="" goto verify
if /I "%~1"=="bin" (
  set "AKM_VERIFY_DIR=%AKM_SCRIPT_DIR%bin"
) else if /I "%~1"=="--no-pause" (
  set "AKM_NO_PAUSE=1"
) else (
  echo Unknown option. Use bin or --no-pause.
  exit /b 2
)
shift
goto arguments
:verify
"%AKM_HOST%" -NoLogo -NoProfile -File "%AKM_SCRIPT_DIR%scripts\Verify-Checksums.ps1" -Directory "%AKM_VERIFY_DIR%\."
set "AKM_EXIT=%ERRORLEVEL%"
echo.
if not defined AKM_NO_PAUSE pause
exit /b %AKM_EXIT%
