using System.Text.Json;
using Microsoft.Win32;

namespace PhoneAudioConnector;

/// <summary>Remembers ticked devices and log options. Stored in %AppData%\PhoneAudioConnector.</summary>
internal sealed class AppSettings
{
    public List<string> SelectedIds { get; set; } = new();
    public bool Verbose { get; set; }
    public LogCat LogCategories { get; set; } = LogCat.All;
    public bool LogToFile { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PhoneAudioConnector", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable file: start fresh.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
            Logger.Debug(LogCat.App, $"Settings saved: {SelectedIds.Count} selected, verbose={Verbose}, " +
                                     $"categories={LogCategories}, toFile={LogToFile}");
        }
        catch (Exception ex)
        {
            Logger.Info($"Could not save settings: {ex.Message}");
        }
    }
}

/// <summary>"Start with Windows" via the current user's Run registry key.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PhoneAudioConnector";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue(ValueName) as string;
        Logger.Debug(LogCat.App, $"Autostart registry value: {value ?? "(none)"}");
        return value is not null;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var command = $"\"{Environment.ProcessPath}\" --minimized";
            key.SetValue(ValueName, command);
            Logger.Debug(LogCat.App, $"Autostart on: {command}");
        }
        else
        {
            key.DeleteValue(ValueName, false);
            Logger.Debug(LogCat.App, "Autostart off");
        }
    }
}