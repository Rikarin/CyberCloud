@echo off
:: docs/plan/23 § Build — the Windows twin of build.sh. Keep the two in step.
::
::   build.cmd                 the default target, Compile
::   build.cmd Test
::   build.cmd Compile --configuration Release
::   build.cmd --help          every target and parameter
::
:: As in build.sh, this runs the build project directly rather than through the `nuke` global tool,
:: which prompts on the console when it does not recognise the directory.

setlocal

set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set NUKE_TELEMETRY_OPTOUT=1

dotnet tool restore
if errorlevel 1 exit /b %errorlevel%

dotnet run --project "%~dp0build\_build.csproj" --no-launch-profile -- %*
exit /b %errorlevel%
