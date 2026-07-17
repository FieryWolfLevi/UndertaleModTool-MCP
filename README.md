# UndertaleModMcp

An [MCP (Model Context Protocol)](https://modelcontextprotocol.io/) server that exposes
**Undertale / Deltarune (GameMaker)** data-file manipulation to AI agents such as
[Cline](https://cline.bot/). It lets an agent load a game's `data.win` (or `game.unx`,
`game.ios`, `game.droid`, …), read/decompile GML, inspect and rewrite rooms/tilemaps,
and more — all through typed MCP tools.

> The server speaks MCP over **stdio** (standard input/output), which is the standard
> transport for app-launched / subprocess MCP servers. The MCP client (e.g. Cline)
> launches the server executable itself; no separate HTTP server or background process
> is required.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (the project targets `net10.0`).
- The server depends on two companion libraries in this repo:
  - `UndertaleModLib` — reads/writes GameMaker data files.
  - `Underanalyzer` — GML decompiler.

## Build

```powershell
dotnet build UndertaleModMcp\UndertaleModMcp.csproj -c Debug
```

The build output is `UndertaleModMcp\bin\Debug\net10.0\UndertaleModMcp.dll`
(and `UndertaleModMcp.exe`).

## Connect from Cline

Add the server to your Cline MCP settings (e.g.
`%APPDATA%\Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json`):

```json
{
  "mcpServers": {
    "undertale-mod-tool": {
      "disabled": false,
      "timeout": 60,
      "type": "stdio",
      "command": "dotnet",
      "args": ["...\\UndertaleModMcp\\bin\\Debug\\net10.0\\UndertaleModMcp.dll"]
    }
  }
}
```

Cline spawns `dotnet UndertaleModMcp.dll` and communicates over stdio. Toggle the
`undertale-mod-tool` server off/on in Cline to (re)connect.

## Loading a game

The server starts with **no game loaded**. Call the `load_data_file` tool with the path
to a game data file; all other tools require a loaded file.

```
load_data_file("C:\\Path\\To\\game.win")
```

## Available tools

| Tool                      | Description                                                    |
| ------------------------- | -------------------------------------------------------------- |
| `load_data_file`          | Load a GameMaker data file into memory.                        |
| `save_data_file`          | Save the loaded data file (optionally to a new path).          |
| `get_game_info`           | General info: name, GM version, YYC status, resource counts.   |
| `list_scripts`            | List all script/code-entry names.                              |
| `read_script`             | Decompile a GML code entry to source.                          |
| `write_script`            | Replace/create a GML code entry (compiled back into the data). |
| `list_rooms`              | List all rooms.                                                |
| `read_room_tilemaps`      | Read tile layers of a room as a 2D tile grid.                  |
| `write_room_tilemap`      | Write a 2D tile grid into a room's tile layer.                 |
| `get_all_scripts`         | Decompile every code entry (bulk dump).                        |
| `find_scripts`            | Find code entries whose name contains a substring.             |
| `search_code`             | Search decompiled GML for a substring.                         |
| `get_all_rooms`           | Summary of all rooms (size, layer names).                      |
| `find_rooms`              | Find rooms whose name contains a substring.                    |
| `get_room_info`           | Detailed info about one room.                                  |
| `get_tilemap_layer_names` | Names of a room's tile layers.                                 |
| `list_objects`            | List all game objects.                                         |
| `get_object_info`         | Info about one object (parent, sprite, events).                |
| `list_backgrounds`        | List all backgrounds / tile sets.                              |
| `list_sprites`            | List all sprites.                                              |

## Notes

- This repository was trimmed to just the MCP server and its two required dependencies.
  The original WPF GUI (`UndertaleModTool`) and the test/CLI helper projects were removed;
  the server does not need them.
- YYC-compiled games have no GML code entries, so the script read/write tools will report
  that they are unavailable.

## License

GPLv3 — see [LICENSE.txt](LICENSE.txt).
