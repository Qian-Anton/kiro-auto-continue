# Build and run the exe's unit tests (tests\cs), with the portable .NET SDK in .tools\dotnet if there is one,
# otherwise the dotnet on PATH. Exit code: the number of failed checks (or the build's).
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\cs\run.ps1
# ASCII only.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' } else { $env:DOTNET_CLI_HOME = Join-Path $repo '.tools' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$proj = Join-Path $PSScriptRoot 'KiroAutoContinue.Tests.csproj'
& $dotnet build $proj -c Debug -p:UseSharedCompilation=false -nodeReuse:false -consoleloggerparameters:NoSummary -v:q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $PSScriptRoot 'bin\Debug\net48\KiroAutoContinue.Tests.exe')
exit $LASTEXITCODE
