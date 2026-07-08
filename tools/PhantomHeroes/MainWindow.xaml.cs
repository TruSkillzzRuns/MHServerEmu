using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhantomHeroes;

public partial class MainWindow : Window
{
    private Detection? _last;
    private bool _running;

    public MainWindow()
    {
        InitializeComponent();
        Log("Phantom Heroes ready.");
        Log("Point at your MHServerEmu source folder and click Detect.");
        LogSummary.Text = "Idle";
    }

    // ================================================================ UI events

    private void SourceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Clear stale detection when the path changes.
        _last = null;
        UpdateButtons();
        ClearDetectionUi();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        // WPF ships FolderBrowserDialog via System.Windows.Forms, but we want
        // to keep the project WPF-only. Use OpenFileDialog on the folder trick
        // via System.Windows.Forms is common; we'll use the modern shell folder
        // picker via Ookii? — no, keep zero deps. Fall back to a minimal folder
        // prompt: OpenFileDialog with a "select this folder" hack.
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select any file inside your MHServerEmu source folder",
            CheckFileExists = false,
            CheckPathExists = true,
            ValidateNames = false,
            FileName = "select-this-folder"
        };
        if (dlg.ShowDialog(this) == true)
        {
            string? dir = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrWhiteSpace(dir))
                SourceBox.Text = dir;
        }
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        string path = SourceBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            Log("Enter a source path first.", LogLevel.Warn);
            return;
        }
        if (!Directory.Exists(path))
        {
            Log($"Path does not exist: {path}", LogLevel.Error);
            return;
        }

        SetBusy(true, "Scanning…");
        try
        {
            var det = await Task.Run(() => new Detection(path));
            _last = det;
            RenderDetection(det);
            Log($"Detection complete — version={det.DetectedVersion}, ok={det.Ok}, alreadyInstalled153={det.AlreadyInstalled153}",
                det.Ok ? LogLevel.Success : LogLevel.Warn);
            Log(det.Diagnosis, det.Ok ? LogLevel.Success : LogLevel.Error);
        }
        catch (Exception ex)
        {
            Log($"Detection failed: {ex.Message}", LogLevel.Error);
        }
        SetBusy(false, "Ready");
        UpdateButtons();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _last == null) return;
        if (!_last.Ok) { Log("Detection is not OK. Fix the missing files first.", LogLevel.Error); return; }

        var opts = new Program.Options
        {
            Command = "install",
            SourcePath = _last.SourceRoot,
            Version = _last.DetectedVersion,
            DryRun = DryRunToggle.IsChecked == true
        };

        SetBusy(true, opts.DryRun ? "Dry-run installing…" : "Installing…");
        try
        {
            IPatcher patcher = opts.Version switch
            {
                "153" => new Patcher153(_last, opts),
                "152" => new Patcher152(_last, opts),
                _     => throw new Exception($"No patcher for version {opts.Version}.")
            };

            bool ok = await Task.Run(() => WithConsoleCaptured(() => patcher.Install()));
            Log(ok
                ? (opts.DryRun ? "Dry run finished — no files changed." : "Install complete. Rebuild:  dotnet build src/MHServerEmu/MHServerEmu.csproj -c Release")
                : "Install did not complete cleanly. See messages above.",
                ok ? LogLevel.Success : LogLevel.Error);

            if (ok && !opts.DryRun)
            {
                // Refresh detection so the "already installed" flag flips.
                var det = await Task.Run(() => new Detection(opts.SourcePath));
                _last = det;
                RenderDetection(det);

                // Rebuild the server if the checkbox is on.
                if (RebuildToggle.IsChecked == true)
                {
                    SetBusy(true, "Building server…");
                    Log("Running: dotnet build MHServerEmu.sln -c Release", LogLevel.Info);
                    bool built = await Task.Run(() => RunDotnetBuild(opts.SourcePath));
                    Log(built
                        ? "Server build succeeded. Start the server to try phantom heroes in-game."
                        : "Server build FAILED. See lines above for the compiler error(s). Uninstall to roll back, or fix + rebuild manually.",
                        built ? LogLevel.Success : LogLevel.Error);
                    SetBusy(false, "Ready");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Install failed: {ex.Message}", LogLevel.Error);
        }
        SetBusy(false, "Ready");
        UpdateButtons();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _last == null) return;
        var confirm = MessageBox.Show(this,
            "Uninstall Phantom Heroes from this source tree?\n\nAll .phbak backups will be restored over the current files, and the phantom source files will be deleted.",
            "Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var opts = new Program.Options
        {
            Command = "uninstall",
            SourcePath = _last.SourceRoot,
            DryRun = DryRunToggle.IsChecked == true
        };

        SetBusy(true, "Uninstalling…");
        try
        {
            await Task.Run(() => WithConsoleCaptured(() => { Program.RunUninstall(opts); return true; }));
            Log(opts.DryRun ? "Dry run finished." : "Uninstall complete.", LogLevel.Success);
            var det = await Task.Run(() => new Detection(opts.SourcePath));
            _last = det;
            RenderDetection(det);
        }
        catch (Exception ex)
        {
            Log($"Uninstall failed: {ex.Message}", LogLevel.Error);
        }
        SetBusy(false, "Ready");
        UpdateButtons();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
    }

    // ================================================================ Runtime HTTP

    private static readonly System.Net.Http.HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private async void Runtime_Spawn_Click(object sender, RoutedEventArgs e)
    {
        int count = int.TryParse(RuntimeCountBox.Text, out var c) ? c : 5;
        int level = int.TryParse(RuntimeLevelBox.Text, out var l) ? l : 60;
        string url = ServerUrlBox.Text.TrimEnd('/') + "/webapi/phantom/spawn";
        RuntimeStatusText.Text = "spawning...";
        try
        {
            var body = new System.Net.Http.StringContent($"{{\"count\":{count},\"level\":{level}}}", System.Text.Encoding.UTF8, "application/json");
            var resp = await s_http.PostAsync(url, body);
            string txt = await resp.Content.ReadAsStringAsync();
            RuntimeStatusText.Text = $"{(int)resp.StatusCode} — {txt}";
            Log($"POST {url}  →  {(int)resp.StatusCode}  {txt}", resp.IsSuccessStatusCode ? LogLevel.Success : LogLevel.Warn);
        }
        catch (Exception ex) { RuntimeStatusText.Text = "err: " + ex.Message; Log("Runtime spawn: " + ex.Message, LogLevel.Error); }
    }

    private async void Runtime_Clear_Click(object sender, RoutedEventArgs e)
    {
        string url = ServerUrlBox.Text.TrimEnd('/') + "/webapi/phantom/clear";
        RuntimeStatusText.Text = "clearing...";
        try
        {
            var resp = await s_http.PostAsync(url, new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            string txt = await resp.Content.ReadAsStringAsync();
            RuntimeStatusText.Text = $"{(int)resp.StatusCode} — {txt}";
            Log($"POST {url}  →  {(int)resp.StatusCode}  {txt}", resp.IsSuccessStatusCode ? LogLevel.Success : LogLevel.Warn);
        }
        catch (Exception ex) { RuntimeStatusText.Text = "err: " + ex.Message; Log("Runtime clear: " + ex.Message, LogLevel.Error); }
    }

    private async void Runtime_Status_Click(object sender, RoutedEventArgs e)
    {
        string url = ServerUrlBox.Text.TrimEnd('/') + "/webapi/phantom/status";
        RuntimeStatusText.Text = "querying...";
        try
        {
            var resp = await s_http.GetAsync(url);
            string txt = await resp.Content.ReadAsStringAsync();
            RuntimeStatusText.Text = $"{(int)resp.StatusCode} — {txt}";
            Log($"GET  {url}  →  {(int)resp.StatusCode}  {txt}", resp.IsSuccessStatusCode ? LogLevel.Success : LogLevel.Warn);
        }
        catch (Exception ex) { RuntimeStatusText.Text = "err: " + ex.Message; Log("Runtime status: " + ex.Message, LogLevel.Error); }
    }

    // ================================================================ Rendering

    private void ClearDetectionUi()
    {
        DetectedVersionText.Text = "—";
        Signal153.Text = "—";
        Signal152.Text = "—";
        FileChecklist.ItemsSource = null;
        SetBadge("Awaiting scan", warn: true);
    }

    private void RenderDetection(Detection det)
    {
        // Display strings describe the server code generation. Client version is
        // independent and picked by the running server's Config — both generations
        // can target the 1.52 or 1.53 game client.
        DetectedVersionText.Text = det.DetectedVersion switch
        {
            "153" => "Modern (phantom-Player)",
            "152" => "Legacy (render-override)",
            _     => "Unknown"
        };
        Signal153.Text = det.AvatarHasIsMovementAuthoritative ? "found" : "not found";
        Signal152.Text = det.AgentHasClientPrototypeRefOverride ? "found" : "not found";

        if (det.DetectedVersion == "unknown" || !det.Ok)
            SetBadge("Not ready", warn: true);
        else if (det.AlreadyInstalled153)
            SetBadge("Installed", success: true);
        else
            SetBadge("Ready to install", success: true);

        var rows = new List<FileRow>
        {
            Row("src/",                                     det.HasSrcDir),
            Row("MHServerEmu.Games.csproj",                 det.HasGamesProject),
            Row("MHServerEmu.csproj",                       det.HasHostProject),
            Row("Avatar.cs",                                det.HasAvatarCs),
            Row("Player.cs",                                det.HasPlayerCs),
            Row("Entity.cs",                                det.HasEntityCs),
            Row("Agent.cs",                                 det.HasAgentCs),
            Row("GameServiceMailbox.cs",                    det.HasGameSvcMailbox),
        };
        FileChecklist.ItemsSource = rows;
    }

    private FileRow Row(string name, bool ok) => new(name, ok);

    private void SetBadge(string text, bool success = false, bool warn = false)
    {
        StatusBadgeText.Text = text;
        var color = success ? (Color)FindResource("Success")
                  : warn    ? (Color)FindResource("Warn")
                            : (Color)FindResource("Danger");
        StatusBadgeText.Foreground = new SolidColorBrush(color);
        var bg = Color.FromArgb(0x33, color.R, color.G, color.B);
        StatusBadge.Background = new SolidColorBrush(bg);
    }

    // ================================================================ Log & busy

    private enum LogLevel { Info, Success, Warn, Error }

    private void Log(string msg, LogLevel level = LogLevel.Info)
    {
        string prefix = level switch
        {
            LogLevel.Success => "[ ok ] ",
            LogLevel.Warn    => "[warn] ",
            LogLevel.Error   => "[err ] ",
            _                => "       "
        };
        string ts = DateTime.Now.ToString("HH:mm:ss");
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"{ts}  {prefix}{msg}\n");
            LogScroll.ScrollToEnd();
        });
    }

    private void SetBusy(bool busy, string summary)
    {
        _running = busy;
        DetectButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy && (_last?.AlreadyInstalled153 == true);
        InstallButton.IsEnabled = !busy && _last != null && _last.Ok && _last.DetectedVersion != "unknown";
        LogSummary.Text = summary;
    }

    private void UpdateButtons()
    {
        UninstallButton.IsEnabled = !_running && (_last?.AlreadyInstalled153 == true);
        InstallButton.IsEnabled = !_running && _last != null && _last.Ok && _last.DetectedVersion != "unknown";
        if (_last != null)
            ChipVersion.Text = _last.DetectedVersion switch
            {
                "153" => "Modern detected",
                "152" => "Legacy detected",
                _     => "Generation unknown"
            };
    }

    // ================================================================ Console capture

    /// <summary>
    /// The Patcher* classes write to Console.Out. Redirect that into the UI log
    /// while a patcher runs, so users see the same detailed report as CLI mode.
    /// </summary>
    private bool WithConsoleCaptured(Func<bool> action)
    {
        var original = Console.Out;
        var writer = new UiLogWriter(this);
        Console.SetOut(writer);
        Console.SetError(writer);
        try { return action(); }
        finally
        {
            Console.SetOut(original);
            Console.SetError(original);
        }
    }

    // ================================================================ dotnet build

    private bool RunDotnetBuild(string sourceRoot)
    {
        string sln = System.IO.Path.Combine(sourceRoot, "MHServerEmu.sln");
        if (!System.IO.File.Exists(sln))
        {
            Log($"MHServerEmu.sln not found at {sln} — cannot build.", LogLevel.Error);
            return false;
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{sln}\" -c Release -nologo",
            WorkingDirectory = sourceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        try
        {
            using var p = System.Diagnostics.Process.Start(psi)
                          ?? throw new System.InvalidOperationException("Failed to start dotnet");
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Log("  " + e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) Log("  " + e.Data, LogLevel.Warn); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Log("dotnet CLI not found on PATH. Install the .NET 8 SDK from https://dotnet.microsoft.com and retry, "
              + "or uncheck 'Rebuild server after install' and build manually.", LogLevel.Error);
            return false;
        }
        catch (System.Exception ex)
        {
            Log($"Build launcher failed: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    private sealed class UiLogWriter : System.IO.TextWriter
    {
        private readonly MainWindow _owner;
        private readonly StringBuilder _line = new();
        public override Encoding Encoding => Encoding.UTF8;
        public UiLogWriter(MainWindow owner) { _owner = owner; }
        public override void Write(char value)
        {
            if (value == '\r') return;
            if (value == '\n')
            {
                if (_line.Length > 0) _owner.Log(_line.ToString());
                _line.Clear();
                return;
            }
            _line.Append(value);
        }
        public override void Write(string? value) { if (value != null) foreach (var c in value) Write(c); }
    }

    // ================================================================ File row model

    public sealed class FileRow
    {
        public string Name { get; }
        public bool Ok { get; }
        public string Symbol => Ok ? "●" : "○";
        public Brush Brush => Ok
            ? (Brush)Application.Current.FindResource("SuccessBrush")
            : (Brush)Application.Current.FindResource("DangerBrush");
        public FileRow(string name, bool ok) { Name = name; Ok = ok; }
    }
}
