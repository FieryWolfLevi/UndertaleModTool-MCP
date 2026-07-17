@echo off
setlocal
set PROJ=D:\Projects\undertale-mod-tool-MCP\UndertaleModMcp\UndertaleModMcp.csproj
set OUT=D:\Projects\undertale-mod-tool-MCP\UndertaleModMcp\bin\Debug\net10.0\UndertaleModMcp.exe
set LOG=%TEMP%\undertale-mcp-build.log

REM Build the project silently. Incremental builds are fast once up to date.
dotnet build "%PROJ%" -c Debug > "%LOG%" 2>&1
if errorlevel 1 (
  echo UndertaleModMcp BUILD FAILED - see %LOG% 1>&2
  exit /b 1
)

REM Run the MCP server. Its stdout must stay clean for the stdio JSON-RPC protocol,
REM so build logs above are redirected to %LOG% and never reach this stream.
"%OUT%"