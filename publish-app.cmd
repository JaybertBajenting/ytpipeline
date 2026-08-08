@echo off
REM Build the React frontend, then package the double-clickable YtPipeline app into app\.
cd /d "%~dp0"

echo === Building frontend (Vite) ===
call npm --prefix web install
if errorlevel 1 goto :error
call npm --prefix web run build
if errorlevel 1 goto :error

echo === Publishing app (single self-contained exe) ===
dotnet publish src\YtPipeline.Web\YtPipeline.Web.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o app
if errorlevel 1 goto :error

echo.
echo Done. Double-click app\YtPipeline.exe (or the Desktop shortcut) to run.
pause
exit /b 0

:error
echo.
echo BUILD FAILED — see output above.
pause
exit /b 1
