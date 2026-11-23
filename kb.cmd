@echo off
rem Windows wrapper for the kalshi-bot CLI. Builds incrementally, then runs it. Relative paths resolve from your current folder.
setlocal
set "ROOT=%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set "KB_LOG=%TEMP%\kalshi-bot-build.log"
dotnet build "%ROOT%src\KalshiBot.Cli\KalshiBot.Cli.csproj" -c Release -nologo > "%KB_LOG%" 2>&1
if errorlevel 1 (
  type "%KB_LOG%"
  exit /b 1
)
dotnet "%ROOT%src\KalshiBot.Cli\bin\Release\net10.0\kb.dll" %*
exit /b %ERRORLEVEL%
