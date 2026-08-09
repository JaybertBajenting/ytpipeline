@echo off
REM Build the React frontend, then package the double-clickable YtPipeline app into app\.
cd /d "%~dp0"

REM A running app\YtPipeline.exe holds an exclusive lock on itself, so publishing over it dies
REM with "GenerateBundle task failed / Access to the path ... is denied". Close it first rather
REM than making that the user's problem.
echo === Closing YtPipeline if it is running ===
taskkill /IM YtPipeline.exe /F >nul 2>&1
if not errorlevel 1 (
  echo   closed a running instance
  REM Windows releases the file handle a moment after the process dies. `ping` is used as the
  REM delay rather than `timeout`, which aborts with "Input redirection is not supported"
  REM whenever stdin isn't a console.
  ping -n 3 127.0.0.1 >nul
) else (
  echo   not running
)

echo === Building frontend (Vite) ===
call npm --prefix web install
if errorlevel 1 goto :error
call npm --prefix web run build
if errorlevel 1 goto :error

REM dotnet publish copies into app\ without pruning, so old hashed bundles pile up in
REM app\wwwroot and it stops being obvious which build the packaged app is actually serving.
REM Clearing it first means app\wwwroot always holds exactly one frontend.
echo === Clearing previously published frontend ===
if exist "app\wwwroot" rmdir /s /q "app\wwwroot"

echo === Publishing app (single self-contained exe) ===
dotnet publish src\YtPipeline.Web\YtPipeline.Web.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o app
if errorlevel 1 goto :error

echo.
echo Done. Double-click app\YtPipeline.exe (or the Desktop shortcut) to run.
pause
exit /b 0

:error
echo.
REM Plain ASCII only: cmd reads this file in the OEM codepage, so an em-dash here prints
REM as mojibake ("BUILD FAILED ΓÇö ...").
echo BUILD FAILED - see output above.
pause
exit /b 1
