using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol.Transport;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using Underanalyzer.Decompiler;

namespace UndertaleModMcp;

/// <summary>
/// An official MCP (Model Context Protocol) server that exposes GameMaker
/// (Undertale/Deltarune) data file manipulation to AI agents.
///
/// It speaks MCP over standard input/output (stdio), which is the standard
/// transport for app-launched / subprocess MCP servers (e.g. Cline launches
/// this executable directly via its MCP configuration).
///
/// All tools require a loaded data file (load_data_file) first.
/// </summary>
public static class Program
{
    // The currently loaded game data file. All tools operate on this.
    internal static UndertaleData _data;
    internal static string _dataFilePath;

    // Hard cap on any single tool response. Large responses (e.g. SearchCode
    // over the whole game, or GetAllScripts) can exceed the MCP client's stdio
    // message-size limit and cause the client to abruptly close the connection
    // ("Connection closed"). Truncating keeps the JSON-RPC stream alive.
    internal const int MaxResponseChars = 120_000;

    public static int Main(string[] args)
    {
        // Install global handlers so that ANY failure (including a broken stdio
        // pipe / transport error that the SDK surfaces outside the request path)
        // is logged to mcp_crash.txt instead of killing the process silently
        // with a bare "Connection closed".
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            WriteCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, e) =>
            WriteCrash("TaskScheduler.UnobservedTaskException", e.Exception);
        Console.CancelKeyPress += (s, e) =>
        {
            // Ctrl-C / client disconnect: don't let it tear down mid-write.
            e.Cancel = true;
        };

        try
        {
            // Determine the data file path. Can be supplied as the first argument,
            // or via the UMT_DATA_FILE environment variable.
            string initialFile = args.Length > 0 && !args[0].StartsWith("-") ? args[0] : Environment.GetEnvironmentVariable("UMT_DATA_FILE");
            if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
            {
                TryLoad(initialFile, out _);
            }

            // Always serve over stdio (the standard MCP subprocess transport).
            RunStdioServer().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e)
        {
            // Write to a file (NEVER stdout - that would corrupt the MCP
            // JSON-RPC stream and cause the client to report "Connection closed").
            WriteCrash("Main catch", e);
            return 1;
        }
    }

    /// <summary>
    /// Writes a crash report to mcp_crash.txt next to the executable. Never
    /// writes to stdout (that would corrupt the JSON-RPC stream).
    /// </summary>
    private static void WriteCrash(string where, Exception e)
    {
        try
        {
            string dir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName) ?? ".";
            File.AppendAllText(Path.Combine(dir, "mcp_crash.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}:\n{e}\n\n");
        }
        catch { }
    }

    /// <summary>
    /// Caps a tool's text response so it can never grow large enough to make the
    /// MCP client close the stdio connection. Appends a notice when truncated.
    /// </summary>
    internal static string CapResponse(string text)
    {
        if (text == null) return string.Empty;
        if (text.Length <= MaxResponseChars) return text;
        return text.Substring(0, MaxResponseChars)
            + $"\n\n[... response truncated at {MaxResponseChars} chars to avoid exceeding the MCP stdio message-size limit. Use more specific queries (FindScripts/SearchCode with a smaller maxResults) to read the rest. ...]";
    }

    #region MCP server hosting

    /// <summary>
    /// Runs the MCP server over stdio using the official MCP SDK's documented
    /// Host + WithStdioServerTransport() pattern (preview.4).
    /// </summary>
    private static async Task RunStdioServer()
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
        await builder.Build().RunAsync();
    }

    #endregion

    #region Tool wrappers (official MCP SDK)

    /// <summary>
    /// MCP tool surface. Each method is discovered by the SDK and exposed to
    /// clients. Business logic lives in the static helpers below.
    /// </summary>
    [McpServerToolType]
    public class McpTools
    {
        [McpServerTool, Description("Load a GameMaker data file (e.g. data.win, game.unx, game.ios, game.droid) into memory so its assets can be read and modified. Must be called before any other tool that operates on game data.")]
        public string LoadDataFile(string path) => ToolLoadDataFile(path);

        [McpServerTool, Description("Save the currently loaded data file to disk. Optionally specify an output path; otherwise overwrites the originally loaded file.")]
        public string SaveDataFile(string path = null) => ToolSaveDataFile(path);

        [McpServerTool, Description("Return basic information about the currently loaded game (name, GameMaker version, YYC status, counts of resources).")]
        public string GetGameInfo() => ToolGetGameInfo();

        [McpServerTool, Description("List all script assets and standalone code entries (GML) in the game, returning their names. Scripts are gml_Script_* entries; this also lists other code entries such as object events and global scripts.")]
        public string ListScripts() => ToolListScripts();

        [McpServerTool, Description("Read and decompile a GML code entry (script, object event, or global script) by its name into human-readable GML source.")]
        public string ReadScript(string name) => CapResponse(ToolReadScript(name));

        [McpServerTool, Description("Replace (or create) a GML code entry by name with the provided GML source. The GML is compiled and written back into the loaded data file. Supports script names, object event names, and global script names. Call save_data_file afterwards to persist.")]
        public string WriteScript(string name, string gml) => ToolWriteScript(name, gml);

        [McpServerTool, Description("List all rooms in the game, returning their names and index.")]
        public string ListRooms() => ToolListRooms();

        [McpServerTool, Description("Read the tilemap layers of a room. Returns each tile layer's name, dimensions (in tiles), the background/tileset it uses, and a 2D grid (rows x columns) of tile indices. A tile index of 0 typically means empty.")]
        public string ReadRoomTilemaps(string room) => CapResponse(ToolReadRoomTilemaps(room));

        [McpServerTool, Description("Write a 2D grid (rows x columns) of tile indices into a specific tile layer of a room. The grid replaces the layer's existing tile data. Tileset/background assignment is not changed. Call save_data_file afterwards to persist.")]
        public string WriteRoomTilemap(string room, string layer, int[][] tileData) => ToolWriteRoomTilemap(room, layer, tileData);

        [McpServerTool, Description("Decompile and return the GML source of ALL code entries (scripts, object events, global scripts) in the game as a single concatenated text block, separated by headers. Useful for bulk analysis or search. Can be large.")]
        public string GetAllScripts(bool includeAnonymous = false) => CapResponse(ToolGetAllScripts(includeAnonymous));

        [McpServerTool, Description("Return the names of all code entries whose name contains the given substring (case-insensitive). Use this to discover script/event names before reading or writing them.")]
        public string FindScripts(string contains) => ToolFindScripts(contains);

        [McpServerTool, Description("Search the decompiled GML of all code entries for a substring (case-insensitive). Returns each matching code entry name and the matching line(s). Useful for finding where a variable, function, or string is used.")]
        public string SearchCode(string query, int maxResults = 50) => CapResponse(ToolSearchCode(query, maxResults));

        [McpServerTool, Description("Return a summary of ALL rooms: name, index, size, and the names of their tile layers and background layers. Use this to discover room/layer names before reading or writing tilemaps.")]
        public string GetAllRooms() => ToolGetAllRooms();

        [McpServerTool, Description("Return the names/indices of all rooms whose name contains the given substring (case-insensitive).")]
        public string FindRooms(string contains) => ToolFindRooms(contains);

        [McpServerTool, Description("Return detailed information about a single room: size, instances, tiles, and a list of its layers (with type and, for tile layers, the tileset and grid dimensions).")]
        public string GetRoomInfo(string room) => ToolGetRoomInfo(room);

        [McpServerTool, Description("Return the names of all TILE layers in a room (the layers that hold tilemaps and can be written with write_room_tilemap).")]
        public string GetTilemapLayerNames(string room) => ToolGetTilemapLayerNames(room);

        [McpServerTool, Description("List all game objects (name and index).")]
        public string ListObjects() => ToolListObjects();

        [McpServerTool, Description("Return information about a single game object: its name, parent, sprite, and the events it defines (as code entry names).")]
        public string GetObjectInfo(string objectName) => ToolGetObjectInfo(objectName);

        [McpServerTool, Description("List all backgrounds / tile sets (name and index).")]
        public string ListBackgrounds() => ToolListBackgrounds();

        [McpServerTool, Description("List all sprites (name and index).")]
        public string ListSprites() => ToolListSprites();

        [McpServerTool, Description("List every placed instance (game object) in a room: its object name, x, y, scaleX, scaleY, and the object definition's solid/visible flags. Use this to inspect collision geometry (where obj_solidparent walls are) and find mis-placed interactables / seams pre-Toriel. Call after load_data_file.")]
        public string GetRoomObjects(string room) => CapResponse(ToolGetRoomObjects(room));

        [McpServerTool, Description("Set the base Sprite of a single game object by name (or numeric index). The sprite is resolved by name from the sprite list. Use to repoint an NPC to a different appearance, e.g. spr_sans_d. Call save_data_file afterwards to persist.")]
        public string SetObjectSprite(string objectName, string spriteName) => ToolSetObjectSprite(objectName, spriteName);

        [McpServerTool, Description("Set the text of a single string-table entry by its key (the name used by scr_gettext, e.g. 'obj_papyrus1_407' or 'SCR_TEXT_1807'). This is how in-game dialogue is stored. Call save_data_file afterwards to persist.")]
        public string SetString(string key, string value) => ToolSetString(key, value);

        [McpServerTool, Description("Bulk-set string-table entries from a JSON object mapping key -> value (e.g. {\"obj_papyrus1_407\":\"heh.\",\"SCR_TEXT_1807\":\"wanna hear about my brother?\"}). This rewrites in-game dialogue. Call save_data_file afterwards to persist.")]
        public string SetStrings(string json) => ToolSetStrings(json);
    }

    #endregion

    #region Tool implementations

    private static string ToolLoadDataFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new Exception("Missing 'path' argument.");
        if (!File.Exists(path))
            throw new Exception("File does not exist: " + path);

        if (!TryLoad(path, out string error))
            throw new Exception(error);

        return $"Loaded data file '{_dataFilePath}'. Project name: {_data.GeneralInfo?.Name}";
    }

    private static string ToolSaveDataFile(string path)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(path))
            path = _dataFilePath;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        UndertaleIO.Write(fs, _data);
        _dataFilePath = path;

        return $"Saved data file to '{path}'.";
    }

    private static string ToolGetGameInfo()
    {
        EnsureLoaded();
        var info = new JObject
        {
            ["name"] = _data.GeneralInfo?.Name?.Content,
            ["isGMS2"] = _data.IsGameMaker2(),
            ["isYYC"] = _data.IsYYC(),
            ["bytecodeVersion"] = _data.GeneralInfo?.BytecodeVersion,
            ["config"] = _data.GeneralInfo?.Config?.Content,
            ["sounds"] = _data.Sounds.Count,
            ["sprites"] = _data.Sprites.Count,
            ["backgrounds"] = _data.Backgrounds.Count,
            ["scripts"] = _data.Scripts.Count,
            ["shaders"] = _data.Shaders.Count,
            ["fonts"] = _data.Fonts.Count,
            ["timelines"] = _data.Timelines.Count,
            ["gameObjects"] = _data.GameObjects.Count,
            ["rooms"] = _data.Rooms.Count,
            ["extensions"] = _data.Extensions.Count,
            ["texturePageItems"] = _data.TexturePageItems.Count,
            ["codeEntries"] = _data.Code.Count,
            ["strings"] = _data.Strings.Count,
            ["embeddedTextures"] = _data.EmbeddedTextures.Count
        };
        return info.ToString(Formatting.Indented);
    }

    private static string ToolListScripts()
    {
        EnsureLoaded();
        if (_data.IsYYC())
            return "Game is YYC-compiled; no GML code entries are available.";

        var names = new JArray();
        foreach (var code in _data.Code)
        {
            if (code.ParentEntry is not null)
                continue; // skip anonymous-function child entries
            names.Add(code.Name?.Content);
        }
        var result = new JObject
        {
            ["count"] = names.Count,
            ["codeEntries"] = names
        };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolReadScript(string name)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(name))
            throw new Exception("Missing 'name' argument.");

        UndertaleCode code = _data.Code.ByName(name);
        if (code == null)
            throw new Exception($"No code entry named '{name}'.");
        if (code.ParentEntry is not null)
            return $"// This code entry is a reference to an anonymous function within \"{code.ParentEntry.Name.Content}\", decompile that instead.";

        string text = new DecompileContext(new GlobalDecompileContext(_data), code, _data.ToolInfo.DecompilerSettings).DecompileToString();
        return text;
    }

    private static string ToolWriteScript(string name, string gml)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(name))
            throw new Exception("Missing 'name' argument.");
        if (gml == null)
            throw new Exception("Missing 'gml' argument.");

        if (_data.IsYYC())
            throw new Exception("Cannot write scripts into a YYC-compiled game; code is embedded in the executable.");

        var group = new CodeImportGroup(_data)
        {
            MainThreadAction = static f => f()
        };
        group.QueueReplace(name, gml);
        CompileResult result = group.Import();

        if (!result.Successful)
            throw new Exception("Code import failed:\n" + result.PrintAllErrors(false));

        return $"Successfully compiled and wrote code entry '{name}'.";
    }

    private static string ToolListRooms()
    {
        EnsureLoaded();
        var rooms = new JArray();
        for (int i = 0; i < _data.Rooms.Count; i++)
        {
            UndertaleRoom room = _data.Rooms[i];
            rooms.Add(new JObject
            {
                ["index"] = i,
                ["name"] = room.Name?.Content,
                ["width"] = room.Width,
                ["height"] = room.Height
            });
        }
        var result = new JObject
        {
            ["count"] = rooms.Count,
            ["rooms"] = rooms
        };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolReadRoomTilemaps(string roomName)
    {
        EnsureLoaded();
        UndertaleRoom room = FindRoom(roomName);

        var layersOut = new JArray();

        foreach (var layer in room.Layers)
        {
            if (layer.LayerType != UndertaleRoom.LayerType.Tiles)
                continue;
            var tilesData = layer.TilesData;
            if (tilesData?.TileData == null)
                continue;

            var grid = new JArray();
            for (int y = 0; y < tilesData.TileData.Length; y++)
            {
                var row = new JArray();
                var rowArr = tilesData.TileData[y];
                for (int x = 0; x < rowArr.Length; x++)
                    row.Add(rowArr[x]);
                grid.Add(row);
            }

            layersOut.Add(new JObject
            {
                ["layerName"] = layer.LayerName?.Content,
                ["layerType"] = "Tiles",
                ["tileset"] = tilesData.Background?.Name?.Content,
                ["tilesX"] = tilesData.TilesX,
                ["tilesY"] = tilesData.TilesY,
                ["tileData"] = grid
            });
        }

        if (room.Tiles.Count > 0)
        {
            var legacy = new JArray();
            foreach (var tile in room.Tiles)
            {
                legacy.Add(new JObject
                {
                    ["x"] = tile.X,
                    ["y"] = tile.Y,
                    ["sourceX"] = tile.SourceX,
                    ["sourceY"] = tile.SourceY,
                    ["width"] = tile.Width,
                    ["height"] = tile.Height,
                    ["tileset"] = tile.BackgroundDefinition?.Name?.Content,
                    ["depth"] = tile.TileDepth,
                    ["instanceId"] = tile.InstanceID
                });
            }
            layersOut.Add(new JObject
            {
                ["layerType"] = "LegacyTiles",
                ["tiles"] = legacy
            });
        }

        var result = new JObject
        {
            ["room"] = room.Name?.Content,
            ["layers"] = layersOut
        };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolWriteRoomTilemap(string roomName, string layerName, int[][] tileDataToken)
    {
        EnsureLoaded();
        if (tileDataToken == null)
            throw new Exception("Missing or invalid 'tileData' argument (expected a 2D array of integers).");

        UndertaleRoom room = FindRoom(roomName);

        UndertaleRoom.Layer targetLayer = null;
        foreach (var layer in room.Layers)
        {
            if (layer.LayerType == UndertaleRoom.LayerType.Tiles &&
                string.Equals(layer.LayerName?.Content, layerName, StringComparison.Ordinal))
            {
                targetLayer = layer;
                break;
            }
        }
        if (targetLayer == null)
            throw new Exception($"No tile layer named '{layerName}' found in room '{roomName}'.");

        var tilesData = targetLayer.TilesData;
        if (tilesData == null)
            throw new Exception("Target layer has no tile data.");

        uint rows = tilesData.TilesY;
        uint cols = tilesData.TilesX;

        if (tileDataToken.Length != (int)rows)
            throw new Exception($"tileData has {tileDataToken.Length} rows but the layer expects {rows} rows (TilesY).");
        for (int y = 0; y < tileDataToken.Length; y++)
        {
            var row = tileDataToken[y];
            if (row == null || row.Length != (int)cols)
                throw new Exception($"Row {y} has {(row?.Length ?? -1)} columns but the layer expects {cols} columns (TilesX).");
        }

        var newData = new uint[rows][];
        for (int y = 0; y < rows; y++)
        {
            newData[y] = new uint[cols];
            for (int x = 0; x < cols; x++)
                newData[y][x] = (uint)tileDataToken[y][x];
        }

        tilesData.TileData = newData;
        tilesData.TileDataUpdated();

        return $"Wrote {rows}x{cols} tile grid into layer '{layerName}' of room '{roomName}'.";
    }

    private static string ToolGetAllScripts(bool includeAnonymous)
    {
        EnsureLoaded();
        if (_data.IsYYC())
            return "Game is YYC-compiled; no GML code entries are available.";

        var sb = new StringBuilder();
        int count = 0;
        foreach (var code in _data.Code)
        {
            if (code.ParentEntry is not null && !includeAnonymous)
                continue;

            sb.AppendLine("/* ===================== " + code.Name?.Content + " ===================== */");
            try
            {
                sb.AppendLine(new DecompileContext(new GlobalDecompileContext(_data), code, _data.ToolInfo.DecompilerSettings).DecompileToString());
            }
            catch (Exception e)
            {
                sb.AppendLine("/* DECOMPILE FAILED: " + e.Message + " */");
            }
            sb.AppendLine();
            count++;
        }
        return $"Dumped {count} code entries:\n\n" + sb;
    }

    private static string ToolFindScripts(string contains)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(contains))
            throw new Exception("Missing 'contains' argument.");

        var matches = new JArray();
        foreach (var code in _data.Code)
        {
            if (code.ParentEntry is not null)
                continue;
            string name = code.Name?.Content ?? "";
            if (name.Contains(contains, StringComparison.OrdinalIgnoreCase))
                matches.Add(name);
        }
        var result = new JObject
        {
            ["query"] = contains,
            ["count"] = matches.Count,
            ["codeEntries"] = matches
        };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolSearchCode(string query, int maxResults)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(query))
            throw new Exception("Missing 'query' argument.");

        if (_data.IsYYC())
            return "Game is YYC-compiled; no GML code entries are available.";

        var sb = new StringBuilder();
        int matches = 0;
        foreach (var code in _data.Code)
        {
            if (code.ParentEntry is not null)
                continue;
            if (matches >= maxResults)
                break;

            string source;
            try
            {
                source = new DecompileContext(new GlobalDecompileContext(_data), code, _data.ToolInfo.DecompilerSettings).DecompileToString();
            }
            catch
            {
                continue;
            }

            var lines = source.Split('\n');
            var hitLines = new JArray();
            bool any = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    any = true;
                    hitLines.Add($"{i + 1}: {lines[i].Trim()}");
                }
            }
            if (any)
            {
                matches++;
                sb.AppendLine("=== " + code.Name?.Content + " ===");
                foreach (var l in hitLines)
                    sb.AppendLine(l.ToString());
                sb.AppendLine();
            }
        }
        return $"Found {matches} code entries matching \"{query}\":\n\n" + sb;
    }

    private static string ToolGetAllRooms()
    {
        EnsureLoaded();
        var rooms = new JArray();
        for (int i = 0; i < _data.Rooms.Count; i++)
        {
            UndertaleRoom room = _data.Rooms[i];
            var tileLayers = new JArray();
            var bgLayers = new JArray();
            foreach (var layer in room.Layers)
            {
                if (layer.LayerType == UndertaleRoom.LayerType.Tiles)
                    tileLayers.Add(layer.LayerName?.Content);
                else if (layer.LayerType == UndertaleRoom.LayerType.Background)
                    bgLayers.Add(layer.LayerName?.Content);
            }
            rooms.Add(new JObject
            {
                ["index"] = i,
                ["name"] = room.Name?.Content,
                ["width"] = room.Width,
                ["height"] = room.Height,
                ["tileLayers"] = tileLayers,
                ["backgroundLayers"] = bgLayers
            });
        }
        var result = new JObject
        {
            ["count"] = rooms.Count,
            ["rooms"] = rooms
        };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolFindRooms(string contains)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(contains))
            throw new Exception("Missing 'contains' argument.");

        var matches = new JArray();
        for (int i = 0; i < _data.Rooms.Count; i++)
        {
            string name = _data.Rooms[i].Name?.Content ?? "";
            if (name.Contains(contains, StringComparison.OrdinalIgnoreCase))
                matches.Add(new JObject { ["index"] = i, ["name"] = name });
        }
        var result = new JObject
        {
            ["query"] = contains,
            ["count"] = matches.Count,
            ["rooms"] = matches
        };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolGetRoomInfo(string roomName)
    {
        EnsureLoaded();
        UndertaleRoom room = FindRoom(roomName);

        var layers = new JArray();
        foreach (var layer in room.Layers)
        {
            var lj = new JObject { ["name"] = layer.LayerName?.Content, ["type"] = layer.LayerType.ToString() };
            if (layer.LayerType == UndertaleRoom.LayerType.Tiles && layer.TilesData != null)
            {
                lj["tileset"] = layer.TilesData.Background?.Name?.Content;
                lj["tilesX"] = layer.TilesData.TilesX;
                lj["tilesY"] = layer.TilesData.TilesY;
            }
            layers.Add(lj);
        }

        var result = new JObject
        {
            ["name"] = room.Name?.Content,
            ["width"] = room.Width,
            ["height"] = room.Height,
            ["instances"] = room.GameObjects.Count,
            ["legacyTiles"] = room.Tiles.Count,
            ["layers"] = layers
        };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolGetTilemapLayerNames(string roomName)
    {
        EnsureLoaded();
        UndertaleRoom room = FindRoom(roomName);

        var names = new JArray();
        foreach (var layer in room.Layers)
        {
            if (layer.LayerType == UndertaleRoom.LayerType.Tiles)
                names.Add(layer.LayerName?.Content);
        }
        var result = new JObject
        {
            ["room"] = room.Name?.Content,
            ["count"] = names.Count,
            ["tileLayers"] = names
        };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolListObjects()
    {
        EnsureLoaded();
        var objs = new JArray();
        for (int i = 0; i < _data.GameObjects.Count; i++)
            objs.Add(new JObject { ["index"] = i, ["name"] = _data.GameObjects[i].Name?.Content });
        var result = new JObject { ["count"] = objs.Count, ["objects"] = objs };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolGetObjectInfo(string objName)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(objName))
            throw new Exception("Missing 'objectName' argument.");

        UndertaleGameObject obj = _data.GameObjects.ByName(objName);
        if (obj == null && int.TryParse(objName, out int idx) && idx >= 0 && idx < _data.GameObjects.Count)
            obj = _data.GameObjects[idx];
        if (obj == null)
            throw new Exception($"No object named '{objName}'.");

        var events = new JArray();
        foreach (var eventList in obj.Events)
            foreach (var ev in eventList)
                foreach (var action in ev.Actions)
                    if (action.CodeId?.Name?.Content is string c)
                        events.Add(c);

        var result = new JObject
        {
            ["name"] = obj.Name?.Content,
            ["parent"] = obj.ParentId?.Name?.Content,
            ["sprite"] = obj.Sprite?.Name?.Content,
            ["visible"] = obj.Visible,
            ["solid"] = obj.Solid,
            ["eventCodeEntries"] = events
        };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolListBackgrounds()
    {
        EnsureLoaded();
        var items = new JArray();
        for (int i = 0; i < _data.Backgrounds.Count; i++)
            items.Add(new JObject { ["index"] = i, ["name"] = _data.Backgrounds[i].Name?.Content });
        var result = new JObject { ["count"] = items.Count, ["backgrounds"] = items };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolListSprites()
    {
        EnsureLoaded();
        var items = new JArray();
        for (int i = 0; i < _data.Sprites.Count; i++)
            items.Add(new JObject { ["index"] = i, ["name"] = _data.Sprites[i].Name?.Content });
        var result = new JObject { ["count"] = items.Count, ["sprites"] = items };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolSetObjectSprite(string objectName, string spriteName)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(objectName))
            throw new Exception("Missing 'objectName' argument.");
        if (string.IsNullOrWhiteSpace(spriteName))
            throw new Exception("Missing 'spriteName' argument.");

        UndertaleGameObject obj = _data.GameObjects.ByName(objectName);
        if (obj == null && int.TryParse(objectName, out int oi) && oi >= 0 && oi < _data.GameObjects.Count)
            obj = _data.GameObjects[oi];
        if (obj == null)
            throw new Exception($"No object named '{objectName}'.");

        UndertaleSprite spr = FindSprite(spriteName);
        if (spr == null)
            throw new Exception($"No sprite named '{spriteName}'.");

        obj.Sprite = spr;
        return $"Set sprite of '{obj.Name?.Content}' to '{spriteName}'.";
    }

    private static string ToolSetSpritesForObjects(string objectNames, string spriteName)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(objectNames))
            throw new Exception("Missing 'objectNames' argument.");
        if (string.IsNullOrWhiteSpace(spriteName))
            throw new Exception("Missing 'spriteName' argument.");

        UndertaleSprite spr = FindSprite(spriteName);
        if (spr == null)
            throw new Exception($"No sprite named '{spriteName}'.");

        var names = objectNames.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);
        int ok = 0, fail = 0;
        var failed = new StringBuilder();
        foreach (var nm in names)
        {
            UndertaleGameObject obj = _data.GameObjects.ByName(nm);
            if (obj == null && int.TryParse(nm, out int oi) && oi >= 0 && oi < _data.GameObjects.Count)
                obj = _data.GameObjects[oi];
            if (obj == null)
            {
                fail++;
                failed.AppendLine(nm);
                continue;
            }
            obj.Sprite = spr;
            ok++;
        }
        string result = $"Repointed {ok} object(s) to '{spriteName}'.";
        if (fail > 0)
            result += $" Could not find {fail} object(s):\n{failed}";
        return result;
    }

    private static string ToolGetRoomObjects(string roomName)
    {
        EnsureLoaded();
        UndertaleRoom room = FindRoom(roomName);

        var instances = new JArray();
        foreach (var go in room.GameObjects)
        {
            var objDef = go.ObjectDefinition;
            instances.Add(new JObject
            {
                ["object"] = objDef?.Name?.Content,
                ["x"] = go.X,
                ["y"] = go.Y,
                ["scaleX"] = go.ScaleX,
                ["scaleY"] = go.ScaleY,
                ["solid"] = objDef?.Solid ?? false,
                ["visible"] = objDef?.Visible ?? false,
                ["instanceId"] = go.InstanceID
            });
        }
        var result = new JObject
        {
            ["room"] = room.Name?.Content,
            ["roomWidth"] = room.Width,
            ["roomHeight"] = room.Height,
            ["count"] = instances.Count,
            ["instances"] = instances
        };
        return result.ToString(Formatting.Indented);
    }

    private static string ToolSetString(string key, string value)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(key))
            throw new Exception("Missing 'key' argument.");
        if (value == null)
            throw new Exception("Missing 'value' argument.");

        UndertaleString s = FindString(key);
        if (s == null)
            throw new Exception($"No string-table entry named '{key}'.");
        s.Content = value;
        return $"Set string '{key}' to: {value}";
    }

    private static string ToolSetStrings(string json)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(json))
            throw new Exception("Missing 'json' argument.");

        JObject map;
        try
        {
            map = JObject.Parse(json);
        }
        catch (Exception e)
        {
            throw new Exception("Invalid JSON object: " + e.Message);
        }

        int ok = 0, fail = 0;
        var failed = new StringBuilder();
        foreach (var prop in map.Properties())
        {
            UndertaleString s = FindString(prop.Name);
            if (s == null)
            {
                fail++;
                failed.AppendLine(prop.Name);
                continue;
            }
            s.Content = prop.Value?.ToString();
            ok++;
        }
        string result = $"Set {ok} string(s).";
        if (fail > 0)
            result += $" Could not find {fail} key(s):\n{failed}";
        return result;
    }

    #endregion

    #region Helpers

    private static bool TryLoad(string path, out string error)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            _data = UndertaleIO.Read(fs);
            _dataFilePath = path;
            error = null;
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private static void EnsureLoaded()
    {
        if (_data == null)
            throw new Exception("No data file loaded. Call 'load_data_file' first.");
    }

    private static UndertaleRoom FindRoom(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            throw new Exception("Missing 'room' argument.");

        UndertaleRoom room = _data.Rooms.ByName(roomName);
        if (room == null)
        {
            if (int.TryParse(roomName, out int idx) && idx >= 0 && idx < _data.Rooms.Count)
                room = _data.Rooms[idx];
        }
        if (room == null)
            throw new Exception($"No room named '{roomName}'.");
        return room;
    }

    private static UndertaleSprite FindSprite(string spriteName)
    {
        if (string.IsNullOrWhiteSpace(spriteName))
            return null;
        if (int.TryParse(spriteName, out int idx) && idx >= 0 && idx < _data.Sprites.Count)
            return _data.Sprites[idx];
        foreach (var s in _data.Sprites)
            if (string.Equals(s.Name?.Content, spriteName, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    private static UndertaleString FindString(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        // Undertale's string table stores entries as interleaved key/value pairs:
        // Strings[2k].Content is the scr_gettext key, Strings[2k+1].Content is the
        // dialogue text. scr_gettext(key) returns the entry immediately after the
        // matching key. So we find the key entry and return the NEXT entry (the value).
        for (int i = 0; i < _data.Strings.Count; i++)
        {
            if (string.Equals(_data.Strings[i].Content, key, StringComparison.OrdinalIgnoreCase))
                return _data.Strings[Math.Min(i + 1, _data.Strings.Count - 1)];
        }
        return null;
    }

    #endregion
}