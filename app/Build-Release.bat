@echo off
cd /d "%~dp0"
echo Building the standalone BO1Ranked.exe (first build downloads the .NET runtime, this can take a few minutes)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist
if errorlevel 1 (
  echo.
  echo BUILD FAILED - copy the errors above.
  pause
  exit /b 1
)
echo.
echo Done: dist\BO1Ranked.exe is the only file to share.
explorer dist
pause
