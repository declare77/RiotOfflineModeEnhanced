using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Threading.Timer;

namespace Deceive;

/// <summary>
/// User-editable settings stored in stored in the app-settings.json next to the exelt;AppNamestored in the app-settings.json next to the exegt;-settings.json next to the exe.
/// The file is watched, so saving it applies changes while the app is running.
/// </summary>
internal class AppSettings
{
    internal const string DefaultCardId = "9fb348bc-41a0-91ad-8a3e-818035c4e561"; // "VALORANT Card"
    internal const string DefaultTitleId = "e3ca05a4-4e44-9afe-3791-7d96ca8f71fa"; // "V1.0"

    [JsonPropertyName("friendName")] public string FriendName { get; set; } = AppInfo.Name;
    [JsonPropertyName("friendGroup")] public string FriendGroup { get; set; } = AppInfo.Name;
    [JsonPropertyName("playerCardId")] public string PlayerCardId { get; set; } = DefaultCardId;
    [JsonPropertyName("playerTitleId")] public string PlayerTitleId { get; set; } = DefaultTitleId;
    [JsonPropertyName("accountLevel")] public int AccountLevel { get; set; } = 999;
    [JsonPropertyName("visibleTo")] public List<string> VisibleTo { get; set; } = new();
    [JsonPropertyName("keepOnTop")] public bool KeepOnTop { get; set; } = false;
    [JsonPropertyName("agentSelectAlert")] public bool AgentSelectAlert { get; set; } = true;
    [JsonPropertyName("agentSelectSound")] public bool AgentSelectSound { get; set; } = true;
    [JsonPropertyName("wishlist")] public List<string> Wishlist { get; set; } = new();

    // ---------------------------------------------------------------------

    private static readonly object Lock = new();
    private static FileSystemWatcher? _watcher;
    private static Timer? _debounce;
    private static DateTime _ignoreWatcherUntil = DateTime.MinValue;

    internal static AppSettings Current { get; private set; } = new();
    internal static string FilePath { get; } = ResolvePath();
    internal static string? LastError { get; private set; }

    /// <summary>Raised (on a background thread) after the file was changed on disk and reloaded.</summary>
    internal static event EventHandler? Reloaded;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    private static string ResolvePath()
    {
        var nextToExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{AppInfo.Name}-settings.json");
        try
        {
            if (File.Exists(nextToExe))
                return nextToExe;
            // Make sure we can actually write there (e.g. not inside Program Files).
            File.WriteAllText(nextToExe, DefaultFileContents());
            return nextToExe;
        }
        catch
        {
            return Path.Combine(Persistence.DataDir, $"{AppInfo.Name}-settings.json");
        }
    }

    internal static void Initialize()
    {
        Load();

        try
        {
            _debounce = new Timer(_ =>
            {
                // Our own Save() also touches the file; don't treat that as an outside edit.
                if (DateTime.UtcNow < _ignoreWatcherUntil)
                    return;
                Load();
                Reloaded?.Invoke(null, EventArgs.Empty);
            });

            _watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            FileSystemEventHandler onChange = (_, _) => _debounce.Change(400, Timeout.Infinite);
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (_, _) => _debounce.Change(400, Timeout.Infinite);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e)
        {
            Trace.WriteLine("Could not watch settings file: " + e);
        }
    }

    /// <summary>Reloads settings from disk. Keeps the previous settings if the file is invalid.</summary>
    internal static void Load()
    {
        lock (Lock)
        {
            try
            {
                if (!File.Exists(FilePath))
                    File.WriteAllText(FilePath, DefaultFileContents());

                string text = ReadShared(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(text, ReadOptions) ?? new AppSettings();
                loaded.Sanitize();
                Current = loaded;
                LastError = null;
                Trace.WriteLine("Loaded settings from " + FilePath);
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Trace.WriteLine("Failed to load settings: " + e);
            }
        }
    }

    private static string ReadShared(string path)
    {
        // Editors may still hold the file open right after saving; retry briefly.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>A copy that can be modified and passed to <see cref="Save"/>.</summary>
    internal AppSettings Clone() => new()
    {
        FriendName = FriendName,
        FriendGroup = FriendGroup,
        PlayerCardId = PlayerCardId,
        PlayerTitleId = PlayerTitleId,
        AccountLevel = AccountLevel,
        VisibleTo = VisibleTo.ToList(),
        KeepOnTop = KeepOnTop,
        AgentSelectAlert = AgentSelectAlert,
        AgentSelectSound = AgentSelectSound,
        Wishlist = Wishlist.ToList()
    };

    /// <summary>Makes these the current settings and writes them to the file (used by the app window).</summary>
    internal static void Save(AppSettings settings)
    {
        lock (Lock)
        {
            settings.Sanitize();
            Current = settings;
            LastError = null;
            try
            {
                _ignoreWatcherUntil = DateTime.UtcNow.AddSeconds(1.5);
                File.WriteAllText(FilePath, ToFileContents(settings));
            }
            catch (Exception e)
            {
                Trace.WriteLine("Failed to save settings: " + e);
            }
        }
    }

    private void Sanitize()
    {
        FriendName = string.IsNullOrWhiteSpace(FriendName) ? AppInfo.Name : FriendName.Trim();
        FriendGroup = string.IsNullOrWhiteSpace(FriendGroup) ? FriendName : FriendGroup.Trim();
        if (!Guid.TryParse(PlayerCardId, out _)) PlayerCardId = DefaultCardId;
        if (!Guid.TryParse(PlayerTitleId, out _)) PlayerTitleId = DefaultTitleId;
        if (AccountLevel < 1) AccountLevel = 1;

        VisibleTo = (VisibleTo ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Wishlist = (Wishlist ?? new List<string>())
            .Where(v => Guid.TryParse(v, out _))
            .Select(v => v.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    internal static void OpenInEditor()
    {
        try
        {
            if (!File.Exists(FilePath))
                File.WriteAllText(FilePath, DefaultFileContents());
            Process.Start("notepad.exe", "\"" + FilePath + "\"");
        }
        catch (Exception e)
        {
            MessageBox.Show("Could not open the settings file:\n" + FilePath + "\n\n" + e.Message,
                StartupHandler.DeceiveTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string DefaultFileContents() => ToFileContents(new AppSettings());

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string J(string value) => JsonSerializer.Serialize(value, WriteOptions);

    private static string ToFileContents(AppSettings s)
    {
        var wishlist = s.Wishlist.Count == 0
            ? "[\n  ]"
            : "[\n" + string.Join(",\n", s.Wishlist.Select(v => "    " + J(v))) + "\n  ]";

        var visible = s.VisibleTo.Count == 0
            ? "[\n  ]"
            : "[\n" + string.Join(",\n", s.VisibleTo.Select(v => "    " + J(v))) + "\n  ]";

        return
            $"// {AppInfo.Name} settings. You can also change most of these from the {AppInfo.Name} window\n" +
            $"// (tray icon > Open {AppInfo.Name}). Saving this file applies changes right away,\n" +
            "// except friendName/friendGroup, which apply next time you launch.\n" +
            "{\n" +
            $"  // Name and friends-list group of the fake friend that controls {AppInfo.Name}.\n" +
            $"  \"friendName\": {J(s.FriendName)},\n" +
            $"  \"friendGroup\": {J(s.FriendGroup)},\n" +
            "\n" +
            "  // VALORANT player card, title and level shown on the fake friend.\n" +
            "  // IDs can be looked up at https://valorant-api.com/v1/playercards and /v1/playertitles\n" +
            $"  \"playerCardId\": {J(s.PlayerCardId)},\n" +
            $"  \"playerTitleId\": {J(s.PlayerTitleId)},\n" +
            $"  \"accountLevel\": {s.AccountLevel},\n" +
            "\n" +
            "  // Friends who still see your real status while you appear Offline or Mobile.\n" +
            "  // Use their Riot ID, e.g. \"PlayerName#NA1\". Leave out the #tag to match any tag.\n" +
            $"  \"visibleTo\": {visible},\n" +
            "\n" +
            $"  // Keep the {AppInfo.Name} window above other windows.\n" +
            $"  \"keepOnTop\": {(s.KeepOnTop ? "true" : "false")},\n" +
            "\n" +
            "  // VALORANT: flash the window and/or play a sound when agent select starts.\n" +
            $"  \"agentSelectAlert\": {(s.AgentSelectAlert ? "true" : "false")},\n" +
            $"  \"agentSelectSound\": {(s.AgentSelectSound ? "true" : "false")},\n" +
            "\n" +
            "  // VALORANT skins you want (skin IDs). Easiest to change from the Store tab > Wishlist.\n" +
            $"  \"wishlist\": {wishlist}\n" +
            "}\n";
    }
}
