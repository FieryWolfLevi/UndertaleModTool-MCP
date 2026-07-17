using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace UndertaleModTool
{
    /// <summary>
    /// View model backing the "MCP configuration" tab. Manages a child process running the
    /// <c>UndertaleModMcp</c> server in HTTP mode, and reports its status, port and available tools.
    /// </summary>
    public class McpConfigViewModel : INotifyPropertyChanged
    {
        private readonly MainWindow _mainWindow = Application.Current.MainWindow as MainWindow;
        private Process _serverProcess;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
        private CancellationTokenSource _pollCts;

        private bool _isRunning;
        private string _statusText = "Stopped";
        private string _loadedFile = "(none)";
        private int _port = Settings.Instance?.McpPort ?? 8401;
        private bool _autoStart = Settings.Instance?.McpAutoStart ?? false;
        private string _log = "";

        public event PropertyChangedEventHandler PropertyChanged;

        public bool IsRunning
        {
            get => _isRunning;
            private set { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotRunning)); }
        }

        public bool IsNotRunning => !_isRunning;

        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        public string LoadedFile
        {
            get => _loadedFile;
            private set { _loadedFile = value; OnPropertyChanged(); }
        }

        public int Port
        {
            get => _port;
            set { _port = value; OnPropertyChanged(); }
        }

        public bool AutoStart
        {
            get => _autoStart;
            set { _autoStart = value; OnPropertyChanged(); if (Settings.Instance is not null) { Settings.Instance.McpAutoStart = value; Settings.Save(); } }
        }

        public string Log
        {
            get => _log;
            private set { _log = value; OnPropertyChanged(); }
        }

        public ObservableCollection<McpToolInfo> Tools { get; } = new();

        public McpConfigViewModel()
        {
            if (Settings.Instance is not null)
                _port = Settings.Instance.McpPort;
        }

        /// <summary>Starts the MCP HTTP server as a child process and begins polling its status.</summary>
        public void Start()
        {
            if (IsRunning)
                return;

            string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            // Prefer the built MCP server assembly next to this app; fall back to a sibling build folder.
            string mcpExe = Path.Combine(exeDir, "UndertaleModMcp.exe");
            if (!File.Exists(mcpExe))
                mcpExe = Path.Combine(exeDir, "UndertaleModMcp.dll");
            if (!File.Exists(mcpExe))
            {
                // Try the sibling project output (development build)
                string sibling = Path.GetFullPath(Path.Combine(exeDir, "..", "UndertaleModMcp", "bin", "Debug", "net10.0", "UndertaleModMcp.dll"));
                if (File.Exists(sibling))
                    mcpExe = sibling;
            }

            if (!File.Exists(mcpExe))
            {
                Log += $"[error] Could not find UndertaleModMcp at '{mcpExe}'.\n";
                _mainWindow?.ShowError("Could not find the UndertaleModMcp server executable. Build the UndertaleModMcp project first.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = mcpExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? mcpExe : "dotnet",
                Arguments = mcpExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? $"--http --port {Port}" : $"\"{mcpExe}\" --http --port {Port}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _serverProcess.OutputDataReceived += (s, e) => { if (e.Data is not null) AppendLog("[server] " + e.Data); };
                _serverProcess.ErrorDataReceived += (s, e) => { if (e.Data is not null) AppendLog("[server-error] " + e.Data); };
                _serverProcess.Exited += (s, e) => _mainWindow?.Dispatcher.Invoke(StopInternal);
                _serverProcess.Start();
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Log += $"[error] Failed to start server: {ex.Message}\n";
                _mainWindow?.ShowError("Failed to start MCP server: " + ex.Message);
                return;
            }

            if (Settings.Instance is not null)
            {
                Settings.Instance.McpPort = Port;
                Settings.Save();
            }

            IsRunning = true;
            StatusText = $"Starting on port {Port}...";
            AppendLog($"Started MCP server (PID {_serverProcess.Id}) on port {Port}.\n");

            _pollCts = new CancellationTokenSource();
            _ = Task.Run(() => PollLoop(_pollCts.Token));
        }

        /// <summary>Stops the MCP HTTP server child process.</summary>
        public void Stop()
        {
            StopInternal();
        }

        private void StopInternal()
        {
            if (!IsRunning && _serverProcess is null)
                return;

            _pollCts?.Cancel();
            _pollCts = null;

            try
            {
                if (_serverProcess is not null && !_serverProcess.HasExited)
                {
                    _serverProcess.Kill();
                    _serverProcess.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[error] stopping server: {ex.Message}\n");
            }
            finally
            {
                _serverProcess?.Dispose();
                _serverProcess = null;
            }

            IsRunning = false;
            StatusText = "Stopped";
            LoadedFile = "(none)";
            Tools.Clear();
            AppendLog("MCP server stopped.\n");
        }

        private async Task PollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var resp = await _http.GetAsync($"http://localhost:{Port}/status", token);
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(token), cancellationToken: token);
                        var root = doc.RootElement;
                        _mainWindow?.Dispatcher.Invoke(() =>
                        {
                            StatusText = "Running";
                            LoadedFile = root.TryGetProperty("loadedFile", out var lf) && lf.ValueKind == JsonValueKind.String ? lf.GetString() : "(none)";
                            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
                            {
                                Tools.Clear();
                                foreach (var t in tools.EnumerateArray())
                                {
                                    if (t.ValueKind == JsonValueKind.String)
                                        Tools.Add(new McpToolInfo { Name = t.GetString() });
                                }
                            }
                        });
                    }
                    else
                    {
                        _mainWindow?.Dispatcher.Invoke(() => StatusText = $"HTTP {resp.StatusCode}");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception)
                {
                    _mainWindow?.Dispatcher.Invoke(() => StatusText = "Not responding");
                }

                try { await Task.Delay(1500, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void AppendLog(string text)
        {
            _mainWindow?.Dispatcher.Invoke(() =>
            {
                Log += text;
                // Keep the log from growing without bound
                if (Log.Length > 8000)
                    Log = Log.Substring(Log.Length - 8000);
            });
        }

        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Simple descriptor for a tool exposed by the MCP server.</summary>
    public class McpToolInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
    }
}