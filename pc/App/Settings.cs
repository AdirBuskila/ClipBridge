using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ClipBridge;

/// <summary>Saved in %APPDATA%\ClipBridge. The pairing code is encrypted for this Windows user.</summary>
internal sealed class Settings
{
    public string? ProtectedCode { get; set; }
    public string? ManualHost { get; set; }
    public string? LastHost { get; set; }
    public bool FirstRunDone { get; set; }

    /// <summary>CLIPBRIDGE_DATA_DIR lets the end-to-end test run without touching real settings.</summary>
    public static string Dir { get; } =
        Environment.GetEnvironmentVariable("CLIPBRIDGE_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBridge");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, FilePath, true);
    }

    public string? GetCode()
    {
        if (ProtectedCode == null) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(ProtectedCode), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public void SetCode(string code)
    {
        ProtectedCode = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(code), null, DataProtectionScope.CurrentUser));
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("ClipBridge") != null;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("ClipBridge", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("ClipBridge", false);
        }
    }
}
