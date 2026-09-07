@echo off
setlocal EnableExtensions EnableDelayedExpansion
title BenevoLink ImpactOS V14 - XAMPP MySQL App
cd /d "%~dp0"
set "APP_URL=http://localhost:5088"
set "PROJECT=src\BenevoLink.App\BenevoLink.App.csproj"

echo.
echo ============================================================
echo  BenevoLink ImpactOS V14 - Fixed Premium Layout + Working Actions
echo ============================================================
echo.
echo This launcher uses your existing database exactly here:
echo   Server=127.0.0.1;Port=3306;Database=benevolink;User ID=root;Password=;
echo.
echo [1/5] Checking XAMPP MySQL port 3306...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$c=Test-NetConnection -ComputerName 127.0.0.1 -Port 3306 -WarningAction SilentlyContinue; if(-not $c.TcpTestSucceeded){ exit 2 }"
if errorlevel 2 (
  echo.
  echo [FAILED] XAMPP MySQL is not reachable on 127.0.0.1:3306.
  echo Open XAMPP Control Panel and click Start beside MySQL, then run this file again.
  pause
  exit /b 2
)

echo [2/5] Cleaning old build cache...
for /d /r %%d in (bin,obj) do if exist "%%d" rd /s /q "%%d" >nul 2>nul

echo [3/5] Restoring NuGet packages...
dotnet restore "%PROJECT%"
if errorlevel 1 goto fail

echo [4/5] Building BenevoLink...
dotnet build "%PROJECT%" --no-restore
if errorlevel 1 goto fail

echo [5/5] Starting app window...
start "BenevoLink App Window" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\OpenWhenReady.ps1" -Url "%APP_URL%"
dotnet run --project "%PROJECT%" --no-build --urls "%APP_URL%"
exit /b 0

:fail
echo.
echo [FAILED] Build failed. Send the red error lines to ChatGPT.
pause
exit /b 1
