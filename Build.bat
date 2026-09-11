@echo off
setlocal
set "AKM_SCRIPT_DIR=%~dp0"
set "AKM_HOST=powershell.exe"
where pwsh.exe >nul 2>nul
if not errorlevel 1 set "AKM_HOST=pwsh.exe"
set "AKM_CONTROLLER="
set "AKM_STRICT="
set "AKM_NO_PAUSE="
:arguments
if "%~1"=="" goto build
if /I "%~1"=="--with-controller" (
  set "AKM_CONTROLLER=-WithController"
) else if /I "%~1"=="--strict" (
  set "AKM_STRICT=-Strict"
) else if /I "%~1"=="--no-pause" (
  set "AKM_NO_PAUSE=1"
) else (
  echo Unknown option. Use --with-controller, --strict or --no-pause.
  exit /b 2
)
shift
goto arguments
:build
"%AKM_HOST%" -NoLogo -NoProfile -File "%AKM_SCRIPT_DIR%scripts\Build-Local.ps1" %AKM_CONTROLLER% %AKM_STRICT%
set "AKM_EXIT=%ERRORLEVEL%"
echo.
if "%AKM_EXIT%"=="0" (echo Build complete. Open bin\AnalogKeyMapper.exe.) else (echo Build failed. Read the error above and docs\building.md.)
if not defined AKM_NO_PAUSE pause
exit /b %AKM_EXIT%
