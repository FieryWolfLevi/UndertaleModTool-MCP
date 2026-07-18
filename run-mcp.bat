@echo off
setlocal
set BINDIR=D:\Projects\undertale-mod-tool-MCP\UndertaleModMcp\bin\Debug\net10.0
set EXE=%BINDIR%\UndertaleModMcp.exe
set DLL=%BINDIR%\UndertaleModMcp.dll
set LOG=%TEMP%\undertale-mcp-build.log

REM ---------------------------------------------------------------------------
REM IMPORTANT: Do NOT run "dotnet build" on every launch.
REM A previous server instance can keep UndertaleModLib.dll locked, which makes
REM "dotnet build" fail at launch and the MCP client reports "Connection closed"
REM before any JSON-RPC handshake. Build is a separate, one-time step (see below).
REM Here we just RUN the already-built assembly.
REM ---------------------------------------------------------------------------

if not exist "%EXE%" (
  if not exist "%DLL%" (
    echo UndertaleModMcp build output not found. Run: dotnet build D:\Projects\undertale-mod-tool-MCP\UndertaleModMcp\UndertaleModMcp.csproj -c Debug 1>&2
    exit /b 1
  )
  REM Fall back to running via dotnet if only the dll is present.
  dotnet "%DLL%"
  exit /b %errorlevel%
)

REM Run the MCP server over stdio. Its stdout must stay clean for the JSON-RPC
REM protocol, so nothing here writes to stdout.
"%EXE%"