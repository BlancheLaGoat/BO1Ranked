@echo off
cd /d "%~dp0"
echo Starting BO1 Ranked from source...
dotnet run -c Release
if errorlevel 1 pause
