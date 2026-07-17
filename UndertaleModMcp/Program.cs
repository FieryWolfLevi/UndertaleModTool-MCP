using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using Underanalyzer.Decompiler;

namespace UndertaleModMcp;

/// <summary>
/// A minimal Model Context Protocol (MCP) server that exposes GameMaker (Undertale/Deltarune)
/// data file manipulation to AI agents.
///
/// It can run in two modes:
///   - stdio (default): speaks JSON-RPC 2.0 over standard input/output (for agents that launch the process directly).
///   - http: listens on a TCP port and serves MCP over HTTP (POST /mcp for JSON-RPC, GET /status for a status page),
///     so that external MCP clients can connect to a running instance (e.g. from the main GUI's config tab).
///
/// It allows agents to load a data file (data.win / .unx / .ios / .droid / ...),
/// list / read / write GML scripts & code entries, list rooms, and read / write room tilemaps.
/// </summary>
public static class Program
{
    // The currently loaded game data file. All tools operate on this.
    private static UndertaleData _data;
    private static string _dataFilePath;

    public static int Main(string[] args)
    {
        // Determine the data file path. Can be supplied as the first argument,
        // or via the UMT_DATA_FILE environment variable, otherwise the agent
        // must use the "load_data_file" tool.
        string initialFile = args.Length > 0 && !args[0].StartsWith("-") ? args[0] : Environment.GetEnvironmentVariable("UMT_DATA_FILE");
        if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
        {
            TryLoad(initialFile, out _);
        }

        // HTTP mode?
        bool httpMode = args.Contains("--http") || args.Contains("-h");
        int port = 8401; // default MCP port
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                port = p;
        }

        if (httpMode)
        {
            RunHttpServer(port).GetAwaiter().GetResult();
            return 0;
        }

        RunStdioLoop();
        return 0;
    }

    #region JSON-RPC / MCP plumbing

    private static void RunStdioLoop()
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var writer = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };

        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            HandleJsonRpcLine(line, writer);
        }
    }

    /// <summary>
    /// Parses and handles a single JSON-RPC line, writing any response to <paramref name="writer"/>.
    /// </summary>
    private static void HandleJsonRpcLine(string line, TextWriter writer)
    {
        JObject request;
        try
        {
            request = JObject.Parse(line);
        }
        catch (Exception e)
        {
            WriteError(writer, null, -32700, "Parse error: " + e.Message);
            return;
        }

        string jsonRpc = request["jsonrpc"]?.ToString();
        string method = request["method"]?.ToString();
        JToken id = request["id"];
        JToken @params = request["params"];

        if (jsonRpc != "2.0" || method == null)
        {
            WriteError(writer, id, -32600, "Invalid Request");
            return;
        }

        try
        {
            HandleRequest(writer, method, @params, id);
        }
        catch (Exception e)
        {
            WriteError(writer, id, -32603, "Internal error: " + e);
        }
    }

    private static void WriteResult(TextWriter writer, JToken id, object result)
    {
        var response = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = JToken.FromObject(result)
        };
        writer.WriteLine(response.ToString(Formatting.None));
    }

    private static void WriteError(TextWriter writer, JToken id, int code, string message)
    {
        var response = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        writer.WriteLine(response.ToString(Formatting.None));
    }

    private static void HandleRequest(TextWriter writer, string method, JToken @params, JToken id)
    {
        switch (method)
        {
            case "initialize":
                WriteResult(writer, id, new JObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JObject
                    {
                        ["tools"] = new JObject()
                    },
                    ["serverInfo"] = new JObject
                    {
                        ["name"] = "undertale-mod-tool",
                        ["version"] = "0.9.1.2"
                    }
                });
                break;

            case "notifications/initialized":
            case "initialized":
                // No response required for notifications; for "initialized" request respond empty.
                if (id != null && id.Type != JTokenType.Null)
                    WriteResult(writer, id, new JObject());
                break;

            case "ping":
                WriteResult(writer, id, new JObject());
                break;

            case "tools/list":
                WriteResult(writer, id, new JObject { ["tools"] = JToken.FromObject(GetToolDefinitions()) });
                break;

            case "tools/call":
                HandleToolCall(writer, @params, id);
                break;

            default:
                WriteError(writer, id, -32601, "Method not found: " + method);
                break;
        }
    }

    #endregion

    #region HTTP server mode

    /// <summary>
    /// Runs the MCP server over HTTP, listening on the given port.
    /// Handles POST /mcp (JSON-RPC), GET /status (status + tool list), and GET / (status HTML).
    /// </summary>
    private static async Task RunHttpServer(int port)
    {
        string prefix = $"http://localhost:{port}/";
        HttpListener listener = new();
        listener.Prefixes.Add(prefix);
        listener.Start();

        Console.WriteLine($"MCP HTTP server listening on {prefix}");

        // Best-effort URL reservation hint for the user.
        try { listener.AuthenticationSchemes = AuthenticationSchemes.Anonymous; } catch { }

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("HTTP listener error: " + e.Message);
                break;
            }

            _ = Task.Run(() => HandleHttpContext(context));
        }
    }

    private static async Task HandleHttpContext(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        byte[] body;
        string contentType = "application/json";

        try
        {
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            if (request.HttpMethod == "GET" && (request.Url.AbsolutePath == "/status" || request.Url.AbsolutePath == "/"))
            {
                bool isHtml = request.Url.AbsolutePath == "/";
                string text = isHtml ? BuildStatusHtml() : BuildStatusJson();
                body = Encoding.UTF8.GetBytes(text);
                contentType = isHtml ? "text/html" : "application/json";
            }
            else if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/mcp")
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
                string line = await reader.ReadToEndAsync();
                using var writer = new StringWriter();
                HandleJsonRpcLine(line, writer);
                body = Encoding.UTF8.GetBytes(writer.ToString());
            }
            else
            {
                response.StatusCode = 404;
                context.Response.Close();
                return;
            }
        }
        catch (Exception e)
        {
            body = Encoding.UTF8.GetBytes("{\"error\":\"" + e.Message.Replace("\"", "'") + "\"}");
            response.StatusCode = 500;
        }

        response.ContentType = contentType;
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = body.Length;
        using var outStream = response.OutputStream;
        await outStream.WriteAsync(body, 0, body.Length);
        outStream.Close();
    }

    private static string BuildStatusJson()
    {
        var obj = new JObject
        {
            ["name"] = "undertale-mod-tool",
            ["version"] = "0.9.1.2",
            ["status"] = "running",
            ["loadedFile"] = _dataFilePath,
            ["hasDataLoaded"] = _data != null,
            ["tools"] = JToken.FromObject(GetToolDefinitions().Select(t => ((dynamic)t).name).ToArray())
        };
        return obj.ToString(Formatting.Indented);
    }

    private static string BuildStatusHtml()
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>UndertaleModTool MCP</title></head><body>");
        sb.Append("<h1>UndertaleModTool MCP Server</h1>");
        sb.Append($"<p>Status: <b>running</b></p>");
        sb.Append($"<p>Loaded file: <b>{(_dataFilePath ?? "(none)")}</b></p>");
        sb.Append("<h2>Tools</h2><ul>");
        foreach (var tool in GetToolDefinitions())
        {
            dynamic t = tool;
            sb.Append($"<li><b>{t.name}</b> &mdash; {t.description}</li>");
        }
        sb.Append("</ul>");
        sb.Append("<p>Endpoint: <code>POST /mcp</code> (JSON-RPC 2.0). Status: <code>GET /status</code>.</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    #endregion

    #region Tool definitions

    private static List<object> GetToolDefinitions()
    {
        return new List<object>
        {
            new
            {
                name = "load_data_file",
                description = "Load a GameMaker data file (e.g. data.win, game.unx, game.ios, game.droid) into memory so its assets can be read and modified. Must be called before any other tool that operates on game data.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string", description = "Filesystem path to the data file to load." }
                    },
                    required = new[] { "path" }
                }
            },
            new
            {
                name = "save_data_file",
                description = "Save the currently loaded data file to disk. Optionally specify an output path; otherwise overwrites the originally loaded file.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string", description = "Optional output path. If omitted, saves over the loaded file." }
                    },
                    required = new string[] { }
                }
            },
            new
            {
                name = "get_game_info",
                description = "Return basic information about the currently loaded game (name, GameMaker version, YYC status, counts of resources).",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } }
            },
            new
            {
                name = "list_scripts",
                description = "List all script assets and standalone code entries (GML) in the game, returning their names. Scripts are gml_Script_* entries; this also lists other code entries such as object events and global scripts.",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } }
            },
            new
            {
                name = "read_script",
                description = "Read and decompile a GML code entry (script, object event, or global script) by its name into human-readable GML source.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Name of the code entry, e.g. 'gml_Script_init_map' or 'gml_Object_obj_player_Step_0'." }
                    },
                    required = new[] { "name" }
                }
            },
            new
            {
                name = "write_script",
                description = "Replace (or create) a GML code entry by name with the provided GML source. The GML is compiled and written back into the loaded data file. Supports script names, object event names, and global script names. Call save_data_file afterwards to persist.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Name of the code entry to write, e.g. 'gml_Script_my_script'." },
                        gml = new { type = "string", description = "GML source code to compile and store." }
                    },
                    required = new[] { "name", "gml" }
                }
            },
            new
            {
                name = "list_rooms",
                description = "List all rooms in the game, returning their names and index.",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } }
            },
            new
            {
                name = "read_room_tilemaps",
                description = "Read the tilemap layers of a room. Returns each tile layer's name, dimensions (in tiles), the background/tileset it uses, and a 2D grid (rows x columns) of tile indices. A tile index of 0 typically means empty.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        room = new { type = "string", description = "Name of the room to read." }
                    },
                    required = new[] { "room" }
                }
            },
            new
            {
                name = "write_room_tilemap",
                description = "Write a 2D grid (rows x columns) of tile indices into a specific tile layer of a room. The grid replaces the layer's existing tile data. Tileset/background assignment is not changed. Call save_data_file afterwards to persist.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        room = new { type = "string", description = "Name of the room." },
                        layer = new { type = "string", description = "Name of the tile layer to write to." },
                        tileData = new
                        {
                            type = "array",
                            description = "2D array (array of rows, each row is an array of unsigned integer tile indices) of size TilesY x TilesX.",
                            items = new { type = "array", items = new { type = "integer" } }
                        }
                    },
                    required = new[] { "room", "layer", "tileData" }
                }
            },
            new
            {
                name = "get_all_scripts",
                description = "Decompile and return the GML source of ALL code entries (scripts, object events, global scripts) in the game as a single concatenated text block, separated by headers. Useful for bulk analysis or search. Can be large.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        includeAnonymous = new { type = "boolean", description = "If true, also include anonymous-function child code entries. Default false." }
                    },
                    required = new string[] { }
                }
            },
            new
            {
                name = "find_scripts",
                description = "Return the names of all code entries whose name contains the given substring (case-insensitive). Use this to discover script/event names before reading or writing them.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        contains = new { type = "string", description = "Substring to search for in code entry names, e.g. 'obj_player' or 'scr_'." }
                    },
                    required = new[] { "contains" }
                }
            },
            new
            {
                name = "search_code",
                description = "Search the decompiled GML of all code entries for a substring (case-insensitive). Returns each matching code entry name and the matching line(s). Useful for finding where a variable, function, or string is used.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "Text to search for within the decompiled GML." },
                        maxResults = new { type = "integer", description = "Maximum number of code entries to return (default 50)." }
                    },
                    required = new[] { "query" }
                }
            },
            new
            {
                name = "get_all_rooms",
                description = "Return a summary of ALL rooms: name, index, size, and the names of their tile layers and background layers. Use this to discover room/layer names before reading or writing tilemaps.",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } }
            },
            new
            {
                name = "find_rooms",
                description = "Return the names/indices of all rooms whose name contains the given substring (case-insensitive).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        contains = new { type = "string", description = "Substring to search for in room names." }
                    },
                    required = new[] { "contains" }
                }
            },
            new
            {
                name = "get_room_info",
                description = "Return detailed information about a single room: size, creation code, instances, tiles, and a list of its layers (with type and, for tile layers, the tileset and grid dimensions).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        room = new { type = "string", description = "Name (or index) of the room." }
                    },
                    required = new[] { "room" }
                }
            },
            new
            {
                name = "get_tilemap_layer_names",
                description = "Return the names of all TILE layers in a room (the layers that hold tilemaps and can be written with write_room_tilemap).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        room = new { type = "string", description = "Name (or index) of the room." }
                    },
                    required = new[] { "room" }
                }
            },
            new
            {
                name = "list_objects",
                description = "List all game objects (name and index).",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } }
            },
            new
            {
                name = "get_object_info",
                description = "Return information about a single game object: its name, parent, sprite, and the events it defines (as code entry names).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        objectName = new { type = "string", description = "Name (or index) of the object." }
                    },
                    required = new[] { "objectName" }
                }
            },
            new
            {
                name = "list_backgrounds",
                description = "List all backgrounds / tile sets (name and index).",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } }
            },
            new
            {
                name = "list_sprites",
                description = "List all sprites (name and index).",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } }
            }
        };
    }

    #endregion

    #region Tool dispatch

    private static void HandleToolCall(TextWriter writer, JToken @params, JToken id)
    {
        string name = @params?["name"]?.ToString();
        JObject args = @params?["arguments"] as JObject ?? new JObject();

        object result;
        try
        {
            result = name switch
            {
                "load_data_file" => ToolLoadDataFile(args),
                "save_data_file" => ToolSaveDataFile(args),
                "get_game_info" => ToolGetGameInfo(),
                "list_scripts" => ToolListScripts(),
                "read_script" => ToolReadScript(args),
                "write_script" => ToolWriteScript(args),
                "list_rooms" => ToolListRooms(),
                "read_room_tilemaps" => ToolReadRoomTilemaps(args),
                "write_room_tilemap" => ToolWriteRoomTilemap(args),
                "get_all_scripts" => ToolGetAllScripts(args),
                "find_scripts" => ToolFindScripts(args),
                "search_code" => ToolSearchCode(args),
                "get_all_rooms" => ToolGetAllRooms(),
                "find_rooms" => ToolFindRooms(args),
                "get_room_info" => ToolGetRoomInfo(args),
                "get_tilemap_layer_names" => ToolGetTilemapLayerNames(args),
                "list_objects" => ToolListObjects(),
                "get_object_info" => ToolGetObjectInfo(args),
                "list_backgrounds" => ToolListBackgrounds(),
                "list_sprites" => ToolListSprites(),
                _ => throw new Exception("Unknown tool: " + name)
            };
        }
        catch (Exception e)
        {
            result = new JObject
            {
                ["content"] = new JArray(new JObject
                {
                    ["type"] = "text",
                    ["text"] = "Error: " + e.Message
                }),
                ["isError"] = true
            };
        }

        WriteResult(writer, id, result);
    }

    private static JObject TextResult(string text)
    {
        return new JObject
        {
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = false
        };
    }

    #endregion

    #region Tools

    private static JObject ToolLoadDataFile(JObject args)
    {
        string path = args["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(path))
            throw new Exception("Missing 'path' argument.");
        if (!File.Exists(path))
            throw new Exception("File does not exist: " + path);

        if (!TryLoad(path, out string error))
            throw new Exception(error);

        return TextResult($"Loaded data file '{_dataFilePath}'. Project name: {_data.GeneralInfo?.Name}");
    }

    private static JObject ToolSaveDataFile(JObject args)
    {
        EnsureLoaded();
        string path = args["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(path))
            path = _dataFilePath;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        UndertaleIO.Write(fs, _data);
        _dataFilePath = path;

        return TextResult($"Saved data file to '{path}'.");
    }

    private static JObject ToolGetGameInfo()
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
        return TextResult(info.ToString(Formatting.Indented));
    }

    private static JObject ToolListScripts()
    {
        EnsureLoaded();
        if (_data.IsYYC())
            return TextResult("Game is YYC-compiled; no GML code entries are available.");

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
        return TextResult(result.ToString(Formatting.Indented));
    }

    private static JObject ToolReadScript(JObject args)
    {
        EnsureLoaded();
        string name = args["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name))
            throw new Exception("Missing 'name' argument.");

        UndertaleCode code = _data.Code.ByName(name);
        if (code == null)
            throw new Exception($"No code entry named '{name}'.");
        if (code.ParentEntry is not null)
            return TextResult($"// This code entry is a reference to an anonymous function within \"{code.ParentEntry.Name.Content}\", decompile that instead.");

        string text = new DecompileContext(new GlobalDecompileContext(_data), code, _data.ToolInfo.DecompilerSettings).DecompileToString();
        return TextResult(text);
    }

    private static JObject ToolWriteScript(JObject args)
    {
        EnsureLoaded();
        string name = args["name"]?.ToString();
        string gml = args["gml"]?.ToString();
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

        return TextResult($"Successfully compiled and wrote code entry '{name}'.");
    }

    private static JObject ToolListRooms()
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
        return TextResult(result.ToString(Formatting.Indented));
    }

    private static JObject ToolReadRoomTilemaps(JObject args)
    {
        EnsureLoaded();
        string roomName = args["room"]?.ToString();
        UndertaleRoom room = FindRoom(roomName);

        var layersOut = new JArray();

        // GMS2+ style: room.Layers
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

        // GMS1 style: room.Tiles (legacy tiles) - report as individual placed tiles, no grid.
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
        return TextResult(result.ToString(Formatting.Indented));
    }

    private static JObject ToolWriteRoomTilemap(JObject args)
    {
        EnsureLoaded();
        string roomName = args["room"]?.ToString();
        string layerName = args["layer"]?.ToString();
        JArray tileDataToken = args["tileData"] as JArray;
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

        if (tileDataToken.Count != (int)rows)
            throw new Exception($"tileData has {tileDataToken.Count} rows but the layer expects {rows} rows (TilesY).");
        for (int y = 0; y < tileDataToken.Count; y++)
        {
            var row = tileDataToken[y] as JArray;
            if (row == null || row.Count != (int)cols)
                throw new Exception($"Row {y} has {(row?.Count ?? -1)} columns but the layer expects {cols} columns (TilesX).");
        }

        var newData = new uint[rows][];
        for (int y = 0; y < rows; y++)
        {
            var row = (JArray)tileDataToken[y];
            newData[y] = new uint[cols];
            for (int x = 0; x < cols; x++)
                newData[y][x] = (uint)row[x].Value<long>();
        }

        tilesData.TileData = newData;
        tilesData.TileDataUpdated();

        return TextResult($"Wrote {rows}x{cols} tile grid into layer '{layerName}' of room '{roomName}'.");
    }

    private static JObject ToolGetAllScripts(JObject args)
    {
        EnsureLoaded();
        if (_data.IsYYC())
            return TextResult("Game is YYC-compiled; no GML code entries are available.");

        bool includeAnonymous = args["includeAnonymous"]?.Value<bool>() ?? false;

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
        return TextResult($"Dumped {count} code entries:\n\n" + sb);
    }

    private static JObject ToolFindScripts(JObject args)
    {
        EnsureLoaded();
        string contains = args["contains"]?.ToString();
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
        return TextResult(result.ToString(Formatting.Indented));
    }

    private static JObject ToolSearchCode(JObject args)
    {
        EnsureLoaded();
        string query = args["query"]?.ToString();
        if (string.IsNullOrWhiteSpace(query))
            throw new Exception("Missing 'query' argument.");
        int maxResults = args["maxResults"]?.Value<int>() ?? 50;

        if (_data.IsYYC())
            return TextResult("Game is YYC-compiled; no GML code entries are available.");

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
        return TextResult($"Found {matches} code entr" + (matches == 1 ? "y" : "ies") + " matching \"{query}\":\n\n" + sb);
    }

    private static JObject ToolGetAllRooms()
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
        return TextResult(result.ToString(Formatting.Indented));
    }

    private static JObject ToolFindRooms(JObject args)
    {
        EnsureLoaded();
        string contains = args["contains"]?.ToString();
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
        return TextResult(result.ToString(Formatting.Indented));
    }

    private static JObject ToolGetRoomInfo(JObject args)
    {
        EnsureLoaded();
        UndertaleRoom room = FindRoom(args["room"]?.ToString());

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
            ["creationCode"] = room.CreationCodeId?.Name?.Content,
            ["instances"] = room.GameObjects.Count,
            ["legacyTiles"] = room.Tiles.Count,
            ["layers"] = layers
        };
        return TextResult(result.ToString(Formatting.Indented));
    }

    private static JObject ToolGetTilemapLayerNames(JObject args)
    {
        EnsureLoaded();
        UndertaleRoom room = FindRoom(args["room"]?.ToString());

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
        return TextResult(result.ToString(Formatting.Indented));
    }

    private static JObject ToolListObjects()
    {
        EnsureLoaded();
        var objs = new JArray();
        for (int i = 0; i < _data.GameObjects.Count; i++)
            objs.Add(new JObject { ["index"] = i, ["name"] = _data.GameObjects[i].Name?.Content });
        var result = new JObject { ["count"] = objs.Count, ["objects"] = objs };
        return TextResult(result.ToString(Formatting.Indented));
    }

    private static JObject ToolGetObjectInfo(JObject args)
    {
        EnsureLoaded();
        string objName = args["objectName"]?.ToString();
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
        return TextResult(result.ToString(Formatting.Indented));
    }

    private static JObject ToolListBackgrounds()
    {
        EnsureLoaded();
        var items = new JArray();
        for (int i = 0; i < _data.Backgrounds.Count; i++)
            items.Add(new JObject { ["index"] = i, ["name"] = _data.Backgrounds[i].Name?.Content });
        var result = new JObject { ["count"] = items.Count, ["backgrounds"] = items };
        return TextResult(result.ToString(Formatting.Indented));
    }

    private static JObject ToolListSprites()
    {
        EnsureLoaded();
        var items = new JArray();
        for (int i = 0; i < _data.Sprites.Count; i++)
            items.Add(new JObject { ["index"] = i, ["name"] = _data.Sprites[i].Name?.Content });
        var result = new JObject { ["count"] = items.Count, ["sprites"] = items };
        return TextResult(result.ToString(Formatting.Indented));
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
            // Allow lookup by index
            if (int.TryParse(roomName, out int idx) && idx >= 0 && idx < _data.Rooms.Count)
                room = _data.Rooms[idx];
        }
        if (room == null)
            throw new Exception($"No room named '{roomName}'.");
        return room;
    }

    #endregion
}