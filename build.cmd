@echo off
rem Builds dist\Echostep.exe: one portable, self-contained EXE (needs the .NET 10 SDK to build, nothing to run).
dotnet publish "%~dp0src\Echostep\Echostep.csproj" -c Release -o "%~dp0dist" || exit /b 1
echo.
echo Built %~dp0dist\Echostep.exe
