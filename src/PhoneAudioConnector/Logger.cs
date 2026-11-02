using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneAudioConnector;

/// <summary>Areas that can be logged in verbose mode. Stored in settings as a bit mask.</summary>
[Flags]
internal enum LogCat
{
    None = 0,
    Devices = 1, // device watcher: found / updated / removed / enumeration
    Connection = 2, // create, start, open, close, dispose of each link
    Retry = 4, // the 10-second retry timer
    App = 8, // startup, settings, "Start with Windows"
    Ui = 16, // clicks, ticks, theme changes, window show/hide
    All = Devices | Connection | Retry | App | Ui
}

/// <summary>
///     App-wide log. Info lines always show; Debug lines only when Verbose is on and their category is enabled.
///     Can also append to %AppData%\PhoneAudioConnector\log.txt. Thread-safe.
///     Bluetooth addresses and device names are masked in every line, so logs can be shared safely.
/// </summary>
internal static partial class Logger
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MinNameLength = 3; // shorter names would match ordinary log text
    private static readonly object FileLock = new();

    // Real device name -> log tag. Kept sorted longest first, so "John's iPhone 15" is replaced before "John's iPhone".
    private static readonly ConcurrentDictionary<string, string> NameTags = new();
    private static volatile KeyValuePair<string, string>[] _sortedTags = [];

    public static volatile bool Verbose;
    public static volatile bool ToFile;
    private static int _categories = (int)LogCat.All;

    public static LogCat Categories
    {
        get => (LogCat)Volatile.Read(ref _categories);
        set => Volatile.Write(ref _categories, (int)value);
    }

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneAudioConnector");

    public static string FilePath => Path.Combine(Folder, "log.txt");

    /// <summary>Raised for every emitted line, on the calling thread.</summary>
    public static event Action<string>? Line;

    public static bool IsOn(LogCat cat)
    {
        return Verbose && (Categories & cat) != 0;
    }

    public static void Info(string msg)
    {
        Emit("INFO", msg);
    }

    public static void Debug(LogCat cat, string msg)
    {
        if (IsOn(cat)) Emit(cat.ToString().ToUpperInvariant(), msg);
    }

    private static void Emit(string tag, string msg)
    {
        // Thread id only in verbose mode; it helps when tracing overlapping async work.
        var thread = Verbose ? $" T{Environment.CurrentManagedThreadId,-3}" : "";
        var line = $"[{DateTime.Now:HH:mm:ss.fff}]{thread} {tag,-10} {MaskAddresses(MaskNames(msg))}";

        Line?.Invoke(line);
        if (ToFile) AppendToFile(line);
    }

    /// <summary>
    ///     Adds a device name to hide in logs and returns its tag: "Device-" plus the last 4 hex digits
    ///     of the Bluetooth address in <paramref name="deviceId" />, for example "Device-E5F6".
    ///     If the id has no address, a hash of the name is used instead. Either way the tag stays the same between runs.
    /// </summary>
    public static string RegisterName(string name, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < MinNameLength) return name;
        if (NameTags.TryGetValue(name, out var existing)) return existing;

        lock (NameTags)
        {
            if (NameTags.TryGetValue(name, out existing)) return existing;

            var tag = "Device-" + (MacEnd(deviceId) ?? HashEnd(name));
            NameTags[name] = tag;
            _sortedTags = NameTags.OrderByDescending(kv => kv.Key.Length).ToArray();
            return tag;
        }
    }

    /// <summary>The log tag for a device name, or the name itself if it isn't masked.</summary>
    public static string TagFor(string name)
    {
        return NameTags.GetValueOrDefault(name, name);
    }

    /// <summary>Last 4 hex digits of the Bluetooth address in a device id, or null if there is none.</summary>
    private static string? MacEnd(string deviceId)
    {
        // Some ids hold two addresses ("local-remote"); the device's own address is the last one.
        var bare = BareMac().Matches(deviceId);
        if (bare.Count > 0) return bare[^1].Value[8..].ToUpperInvariant();

        var separated = SeparatedMac().Matches(deviceId);
        if (separated.Count > 0)
        {
            var mac = separated[^1].Value;
            return (mac[12..14] + mac[15..17]).ToUpperInvariant();
        }

        return null;
    }

    private static string HashEnd(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return $"{hash[0]:X2}{hash[1]:X2}";
    }

    private static string MaskNames(string text)
    {
        foreach (var (name, tag) in _sortedTags)
            text = text.Replace(name, tag, StringComparison.Ordinal);
        return text;
    }

    /// <summary>
    ///     Hides Bluetooth MAC addresses, keeping the last 4 hex digits so devices can still be told apart.
    ///     Handles "AA:BB:CC:DD:EE:FF", "AA-BB-CC-DD-EE-FF" and the bare "AABBCCDDEEFF" form used in device ids.
    /// </summary>
    public static string MaskAddresses(string text)
    {
        text = SeparatedMac().Replace(text, m =>
        {
            var sep = m.Value[2];
            return $"XX{sep}XX{sep}XX{sep}XX{sep}XX{sep}{m.Value[14..]}";
        });
        return BareMac().Replace(text, m => "XXXXXXXX" + m.Value[8..]);
    }

    [GeneratedRegex(@"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}([:-])){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])")]
    private static partial Regex SeparatedMac();

    // 12 hex digits in a row. Not preceded by '-' so the last group of a GUID is left alone.
    [GeneratedRegex(@"(?<![0-9A-Fa-f-])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])")]
    private static partial Regex BareMac();

    private static void AppendToFile(string line)
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Folder);

                // Keep one old file so the log can't grow forever.
                var info = new FileInfo(FilePath);
                if (info is { Exists: true, Length: > MaxFileBytes })
                    File.Move(FilePath, Path.Combine(Folder, "log.old.txt"), true);

                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }
}