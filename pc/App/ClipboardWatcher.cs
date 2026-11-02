using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClipBridge.Core;

namespace ClipBridge;

/// <summary>Raises Copied whenever text or an image lands on the Windows clipboard.</summary>
internal sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public event Action<byte, byte[]>? Copied;
    public event Action<string>? Log;

    private readonly System.Windows.Forms.Timer _settle = new() { Interval = 120 };
    private string? _lastHash;
    private long _lastHashAt;

    public ClipboardWatcher()
    {
        CreateHandle(new CreateParams());
        if (!AddClipboardFormatListener(Handle))
            throw new InvalidOperationException("Could not watch the clipboard: " + Marshal.GetLastWin32Error());
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Read();
        };
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            // Apps often post several updates for one copy, and hold the clipboard
            // open while doing it, so wait until it settles.
            _settle.Stop();
            _settle.Start();
        }
        base.WndProc(ref m);
    }

    private void Read()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                ReadOnce();
                return;
            }
            catch (ExternalException)
            {
                // Another app has the clipboard open.
                Thread.Sleep(50 + attempt * 50);
            }
            catch (Exception e)
            {
                Log?.Invoke("could not read clipboard: " + e.Message);
                return;
            }
        }
        Log?.Invoke("clipboard stayed locked by another app");
    }

    private void ReadOnce()
    {
        var data = Clipboard.GetDataObject();
        if (data == null) return;

        // Password managers set these so clipboard tools leave their content alone.
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") ||
            data.GetDataPresent("CanIncludeInClipboardHistory") && IsZero(data, "CanIncludeInClipboardHistory"))
        {
            Log?.Invoke("skipped clip marked private by the app that copied it");
            return;
        }

        // Text first: Excel and others put both, and the text is what you want there.
        // Lightshot puts only a bitmap.
        if (Clipboard.ContainsText())
        {
            Emit(Proto.TypeText, Encoding.UTF8.GetBytes(Clipboard.GetText()));
            return;
        }
        if (Clipboard.ContainsImage())
        {
            using var image = Clipboard.GetImage();
            if (image == null) return;
            using var ms = new MemoryStream();
            image.Save(ms, ImageFormat.Png);
            Emit(Proto.TypePng, ms.ToArray());
        }
    }

    private static bool IsZero(IDataObject data, string format)
    {
        try
        {
            return data.GetData(format) is MemoryStream s && s.Length >= 4 && s.ReadByte() == 0;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private void Emit(byte type, byte[] payload)
    {
        if (payload.Length == 0) return;
        if (payload.Length > 100 * 1024 * 1024)
        {
            Log?.Invoke($"skipped clip: {payload.Length / (1024 * 1024)} MB is over the 100 MB limit");
            return;
        }

        // The same copy can be announced twice, moments apart.
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        long now = Environment.TickCount64;
        if (hash == _lastHash && now - _lastHashAt < 2000) return;
        _lastHash = hash;
        _lastHashAt = now;

        Copied?.Invoke(type, payload);
    }

    public void Dispose()
    {
        _settle.Dispose();
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}
