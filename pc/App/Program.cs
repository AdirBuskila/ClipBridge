using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClipBridge.Core;
using LinkState = ClipBridge.Core.LinkState;

namespace ClipBridge;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--test-bluetooth")) return TestBluetooth();
        RunTray();
        return 0;
    }

    private static int TestBluetooth()
    {
        var path = Path.Combine(Settings.Dir, "bluetooth-test.txt");
        var lines = new List<string>();
        int exit = 1;
        try
        {
            var code = Settings.Load().GetCode();
            if (code == null)
            {
                lines.Add("FAILED: not paired yet");
            }
            else
            {
                var result = BluetoothLink.Test(Proto.DeriveKey(code), lines.Add);
                lines.Add(result);
                if (result.StartsWith("OK")) exit = 0;
            }
        }
        catch (Exception e)
        {
            lines.Add("FAILED: " + e.GetBaseException().Message);
        }
        Directory.CreateDirectory(Settings.Dir);
        File.WriteAllLines(path, lines);
        return exit;
    }

    private static void RunTray()
    {
        using var single = new Mutex(true, @"Local\ClipBridge-PC", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        // TrayApp posts link updates back to this thread, before any window exists.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var app = new TrayApp();
        Application.Run();
    }
}

/// <summary>Tray icon and menu. Green means copies go both ways between PC and tablet.</summary>
internal sealed class TrayApp : IDisposable
{
    private readonly Settings _settings = Settings.Load();
    private readonly NotifyIcon _tray = new();
    private readonly ClipboardWatcher _watcher = new();
    private readonly TabletLink _link;
    private readonly SynchronizationContext _ui = SynchronizationContext.Current!;
    private readonly ToolStripMenuItem _status = new("Starting...") { Enabled = false };
    private readonly ToolStripMenuItem _last = new("Nothing sent yet") { Enabled = false };
    private readonly ToolStripMenuItem _received = new("Nothing received yet") { Enabled = false };
    private readonly ToolStripMenuItem _pause = new("Pause");
    private readonly ToolStripMenuItem _autostart = new("Start with Windows");
    private readonly Dictionary<Color, Icon> _icons = new();
    private readonly string _logPath = Path.Combine(Settings.Dir, "log.txt");
    private readonly object _logLock = new();

    private LinkState _state = LinkState.Searching;
    private string? _stateDetail;
    private string? _lastSaved;

    public TrayApp()
    {
        _link = new TabletLink { BluetoothConnect = key => BluetoothLink.Connect(key, Log) };
        Directory.CreateDirectory(Settings.Dir);
        TrimLog();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(_last);
        menu.Items.Add(_received);
        menu.Items.Add(new ToolStripSeparator());
        _pause.CheckOnClick = true;
        _pause.CheckedChanged += (_, _) =>
        {
            _link.SetPaused(_pause.Checked);
        };
        menu.Items.Add(_pause);
        menu.Items.Add("Enter pairing code...", null, (_, _) => AskCode());
        menu.Items.Add("Set tablet address...", null, (_, _) => AskHost());
        _autostart.CheckOnClick = true;
        _autostart.CheckedChanged += (_, _) => Settings.StartWithWindows = _autostart.Checked;
        menu.Items.Add(_autostart);
        menu.Items.Add("Open log", null, (_, _) =>
            Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true }));
        menu.Items.Add("Help", null, (_, _) =>
            Process.Start(new ProcessStartInfo("https://github.com/AdirBuskila/ClipBridge#troubleshooting") { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;

        _watcher.Log += Log;
        _watcher.Copied += (type, payload) =>
        {
            _link.Send(type, payload);
            Log($"copied {(type == Proto.TypePng ? "image" : "text")}, {payload.Length} bytes");
        };

        _link.Log += Log;
        _link.StateChanged += (s, d) => _ui.Post(_ => ShowState(s, d), null);
        _link.HostFound += host => _ui.Post(_ => RememberHost(host), null);
        _link.ClipDelivered += _ => _ui.Post(_ => _last.Text = "Last sent: " + DateTime.Now.ToString("HH:mm:ss"), null);
        // Synchronous, so the tablet is only acknowledged once the clip is on the clipboard.
        _link.ClipReceived += (type, payload) => _ui.Send(_ =>
        {
            _watcher.Write(type, payload);
            _received.Text = "Last received: " + DateTime.Now.ToString("HH:mm:ss");
            Log($"received {(type == Proto.TypePng ? "image" : "text")} from tablet, {payload.Length} bytes");
        }, null);

        if (!_settings.FirstRunDone)
        {
            Settings.StartWithWindows = true;
            _settings.FirstRunDone = true;
            _settings.Save();
        }
        _autostart.Checked = Settings.StartWithWindows;

        ShowState(LinkState.Searching, null);
        _link.Configure(_settings.GetCode(), _settings.ManualHost, _settings.LastHost);
        _link.Start();

        Application.ApplicationExit += (_, _) => Dispose();

        if (_settings.GetCode() == null)
        {
            _ui.Post(_ => AskCode(), null);
        }
    }

    private void RememberHost(string host)
    {
        if (host == _lastSaved) return;
        _lastSaved = host;
        _settings.LastHost = host;
        _settings.Save();
    }

    private void ShowState(LinkState s, string? detail)
    {
        var previous = _state;
        _state = s;
        _stateDetail = detail ?? _stateDetail;
        var (text, color) = s switch
        {
            LinkState.Connected => ($"Connected to tablet ({_stateDetail})", Color.FromArgb(46, 160, 67)),
            LinkState.Searching => ("Looking for tablet...", Color.FromArgb(230, 150, 0)),
            LinkState.WrongCode => ("Wrong pairing code", Color.FromArgb(214, 40, 40)),
            LinkState.NoCode => ("Not paired yet", Color.FromArgb(214, 40, 40)),
            LinkState.Paused => ("Paused", Color.Gray),
            _ => ("", Color.Gray),
        };
        _status.Text = text;
        _tray.Text = Truncate("ClipBridge: " + text, 63);
        _tray.Icon = IconFor(color);
        if (s == LinkState.WrongCode && previous != LinkState.WrongCode)
        {
            _tray.ShowBalloonTip(5000, "ClipBridge", "The pairing code doesn't match the tablet. Right-click the tray icon to enter it again.", ToolTipIcon.Warning);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private Icon IconFor(Color color)
    {
        if (_icons.TryGetValue(color, out var cached)) return cached;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var board = new SolidBrush(color);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(board, 4, 5, 24, 26);
            g.FillRectangle(board, 11, 1, 10, 8);
            g.FillRectangle(white, 8, 12, 16, 3);
            g.FillRectangle(white, 8, 18, 16, 3);
            g.FillRectangle(white, 8, 24, 10, 3);
        }
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        _icons[color] = icon;
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private void AskCode()
    {
        var code = Prompt.Show(
            "ClipBridge: pair with your tablet",
            "Open ClipBridge on the tablet and type the code it shows:",
            "",
            Proto.IsValidCode,
            "The code has 8 letters and numbers.");
        if (code == null) return;
        _settings.SetCode(Proto.NormalizeCode(code));
        _settings.Save();
        _link.Configure(_settings.GetCode(), _settings.ManualHost, _settings.LastHost);
    }

    private void AskHost()
    {
        var host = Prompt.Show(
            "ClipBridge: tablet address",
            "Usually not needed. Use this only if the tablet isn't found automatically.\nType the address shown in the tablet app, or leave empty for automatic:",
            _settings.ManualHost ?? "",
            _ => true,
            "");
        if (host == null) return;
        _settings.ManualHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        _settings.Save();
        _link.Configure(_settings.GetCode(), _settings.ManualHost, _settings.LastHost);
    }

    private void Log(string message)
    {
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Never let logging interrupt delivery.
        }
    }

    private void TrimLog()
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (info.Exists && info.Length > 1024 * 1024) info.Delete();
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _watcher.Dispose();
        _link.Dispose();
    }
}

internal static class Prompt
{
    public static string? Show(string title, string message, string initial, Func<string, bool> valid, string invalidMessage)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(460, 170),
            TopMost = true,
            ShowInTaskbar = true,
            Font = new Font("Segoe UI", 10f),
        };
        var label = new Label { Text = message, Left = 16, Top = 14, Width = 428, Height = 62 };
        var box = new TextBox { Text = initial, Left = 16, Top = 80, Width = 428 };
        var ok = new Button { Text = "OK", Left = 268, Top = 122, Width = 84, Height = 32, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 360, Top = 122, Width = 84, Height = 32, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([label, box, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.FormClosing += (_, e) =>
        {
            if (form.DialogResult == DialogResult.OK && !valid(box.Text))
            {
                MessageBox.Show(form, invalidMessage, "ClipBridge");
                e.Cancel = true;
            }
        };
        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}
