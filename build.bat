@echo off
setlocal

dotnet build MissionPlanner.slnx -c Release -m:1 --nologo
if errorlevel 1 exit /b %errorlevel%

dotnet test MissionPlannerTests\Avalonia\MissionPlanner.Tests\MissionPlanner.Tests.csproj ^
  -c Release -m:1 --no-build --nologo
exit /b %errorlevel%
