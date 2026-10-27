using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Deceive;

/// <summary>
/// Names, colors and pictures for skins, agents, maps and ranks, from the community
/// valorant-api.com service. Cached on disk so it's only downloaded now and then.
/// </summary>
internal static class ValorantContent
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly string CacheDir = Path.Combine(Persistence.DataDir, "valorant-content");
    private static readonly SemaphoreSlim LoadLock = new(1, 1);
    private static Catalog? _catalog;

    internal sealed class Skin
    {
        public string Name = "";
        public string Icon = "";
        public Color Rarity = Color.Empty;
        public string RarityIcon = "";
    }

    internal sealed class Agent
    {
        public string Name = "";
        public string Icon = "";
        public string Role = "";
    }

    internal sealed class Tier
    {
        public string Name = "";
        public string Icon = "";
        public Color Color = Color.Empty;
    }

    internal sealed class Season
    {
        public string Id = "";
        public string Name = "";
        public string Parent = "";
        public bool IsAct;
        public DateTime Start;
        public DateTime End;
    }

    internal sealed class Catalog
    {
        public readonly Dictionary<string, Skin> Skins = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Agent> Agents = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Maps = new(StringComparer.OrdinalIgnoreCase); // mapUrl -> name
        public readonly Dictionary<int, Tier> Tiers = new();
        public readonly Dictionary<string, Season> Seasons = new(StringComparer.OrdinalIgnoreCase);

        // Episode 5 Act 1 started on 2022-06-22; that's when the current rank numbers began.
        private static readonly DateTime RankSystemStart = new(2022, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        public string CurrentActId()
        {
            var now = DateTime.UtcNow;
            return Seasons.Values.FirstOrDefault(s => s.IsAct && s.Start <= now && now < s.End)?.Id
                   ?? Seasons.Values.Where(s => s.IsAct && s.Start <= now).OrderByDescending(s => s.Start).FirstOrDefault()?.Id
                   ?? "";
        }

        public bool IsCurrentRankSystem(string seasonId) =>
            Seasons.TryGetValue(seasonId, out var s) && s.Start >= RankSystemStart;

        /// <summary>"E9 A2"-style short name for an act, or "" if unknown.</summary>
        public string ActName(string seasonId)
        {
            if (!Seasons.TryGetValue(seasonId, out var act)) return "";
            if (Seasons.TryGetValue(act.Parent, out var episode) && episode.Name.Length > 0)
                return $"{Short(episode.Name, 'E')} {Short(act.Name, 'A')}";
            return act.Name;
        }

        private static string Short(string name, char letter)
        {
            // "EPISODE 9" -> "E9", "ACT II" -> "A2", "V25" -> "V25"
            var parts = name.Trim().Split(' ');
            var last = parts[parts.Length - 1];
            var roman = last.ToUpperInvariant() switch { "I" => "1", "II" => "2", "III" => "3", "IV" => "4", "V" => "5", "VI" => "6", _ => null };
            if (parts.Length >= 2 && (roman ?? last).All(char.IsDigit))
                return letter + (roman ?? last);
            return name;
        }

        public string MapName(string mapUrl) => Maps.TryGetValue(mapUrl, out var n) ? n : "";

        public Tier? TierInfo(int tier) => tier > 2 && Tiers.TryGetValue(tier, out var t) ? t : null;
    }

    internal static Catalog? Loaded => _catalog;

    /// <summary>Loads the catalog (from disk if fresh, otherwise from valorant-api.com).</summary>
    internal static async Task<Catalog> GetAsync(IEnumerable<string>? mustHaveSkins = null)
    {
        await LoadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var needed = mustHaveSkins?.Where(id => id.Length > 0).ToList() ?? new List<string>();
            if (_catalog is not null && needed.All(_catalog.Skins.ContainsKey))
                return _catalog;

            // A skin we don't know means the cache is from before a new release.
            var forceRefresh = _catalog is not null;
            var catalog = await Task.Run(() => BuildAsync(forceRefresh)).ConfigureAwait(false);
            if (!forceRefresh && !needed.All(catalog.Skins.ContainsKey))
                catalog = await Task.Run(() => BuildAsync(true)).ConfigureAwait(false);
            return _catalog = catalog;
        }
        finally
        {
            LoadLock.Release();
        }
    }

    private static async Task<Catalog> BuildAsync(bool forceRefresh)
    {
        var catalog = new Catalog();

        var skins = await FetchAsync("skins", "https://valorant-api.com/v1/weapons/skins?language=en-US", forceRefresh).ConfigureAwait(false);
        var tiers = await FetchAsync("contenttiers", "https://valorant-api.com/v1/contenttiers?language=en-US", forceRefresh).ConfigureAwait(false);
        var agents = await FetchAsync("agents", "https://valorant-api.com/v1/agents?isPlayableCharacter=true&language=en-US", forceRefresh).ConfigureAwait(false);
        var maps = await FetchAsync("maps", "https://valorant-api.com/v1/maps?language=en-US", forceRefresh).ConfigureAwait(false);
        var ranks = await FetchAsync("competitivetiers", "https://valorant-api.com/v1/competitivetiers?language=en-US", forceRefresh).ConfigureAwait(false);
        var seasons = await FetchAsync("seasons", "https://valorant-api.com/v1/seasons?language=en-US", forceRefresh).ConfigureAwait(false);

        var rarity = new Dictionary<string, (Color color, string icon)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tiers.Arr("data"))
            rarity[t.Str("uuid")] = (ParseRgba(t.Str("highlightColor")), t.Str("displayIcon"));

        foreach (var skin in skins.Arr("data"))
        {
            var name = skin.Str("displayName");
            var tier = rarity.TryGetValue(skin.Str("contentTierUuid"), out var r) ? r : (Color.Empty, "");
            var skinIcon = skin.Str("displayIcon");
            var chromaIcon = skin.Arr("chromas").Select(c => c.Str("fullRender")).FirstOrDefault(s => s.Length > 0) ?? "";

            foreach (var level in skin.Arr("levels"))
            {
                var id = level.Str("uuid");
                if (id.Length == 0) continue;
                var icon = FirstNonEmpty(level.Str("displayIcon"), skinIcon, chromaIcon);
                catalog.Skins[id] = new Skin { Name = name, Icon = icon, Rarity = tier.Item1, RarityIcon = tier.Item2 };
            }

            // Some offers point at the skin itself rather than its first level.
            var skinId = skin.Str("uuid");
            if (skinId.Length > 0 && !catalog.Skins.ContainsKey(skinId))
                catalog.Skins[skinId] = new Skin { Name = name, Icon = FirstNonEmpty(skinIcon, chromaIcon), Rarity = tier.Item1, RarityIcon = tier.Item2 };
        }

        foreach (var a in agents.Arr("data"))
        {
            var id = a.Str("uuid");
            if (id.Length == 0) continue;
            catalog.Agents[id] = new Agent
            {
                Name = a.Str("displayName"),
                Icon = FirstNonEmpty(a.Str("displayIconSmall"), a.Str("displayIcon")),
                Role = a?["role"].Str("displayName") ?? ""
            };
        }

        foreach (var m in maps.Arr("data"))
        {
            var url = m.Str("mapUrl");
            if (url.Length > 0)
                catalog.Maps[url] = m.Str("displayName");
        }

        // The last entry is the current rank system.
        var currentTiers = ranks.Arr("data").LastOrDefault();
        foreach (var t in currentTiers.Arr("tiers"))
        {
            var number = (int)t.Num("tier", -1);
            if (number < 0) continue;
            catalog.Tiers[number] = new Tier
            {
                Name = TitleCase(t.Str("tierName")),
                Icon = FirstNonEmpty(t.Str("smallIcon"), t.Str("largeIcon")),
                Color = ParseRgba(t.Str("color"))
            };
        }

        foreach (var s in seasons.Arr("data"))
        {
            var id = s.Str("uuid");
            if (id.Length == 0) continue;
            catalog.Seasons[id] = new Season
            {
                Id = id,
                Name = s.Str("displayName"),
                Parent = s.Str("parentUuid"),
                IsAct = s.Str("type").IndexOf("Act", StringComparison.OrdinalIgnoreCase) >= 0,
                Start = ParseDate(s.Str("startTime")),
                End = ParseDate(s.Str("endTime"))
            };
        }

        return catalog;
    }

    private static async Task<JsonNode?> FetchAsync(string name, string url, bool forceRefresh)
    {
        var path = Path.Combine(CacheDir, name + ".json");
        try
        {
            if (!forceRefresh && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(2))
            {
                var cached = Json.Parse(File.ReadAllText(path));
                if (cached?["data"] is JsonArray)
                    return cached;
            }
        }
        catch { /* re-download */ }

        try
        {
            var text = await Http.GetStringAsync(url).ConfigureAwait(false);
            var json = Json.Parse(text);
            if (json?["data"] is JsonArray)
            {
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllText(path, text);
                }
                catch { /* cache is optional */ }

                return json;
            }
        }
        catch (Exception e)
        {
            Trace.WriteLine($"valorant-api {name} failed: {e.Message}");
        }

        // Offline: an old cache is better than nothing.
        try { return File.Exists(path) ? Json.Parse(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    private static DateTime ParseDate(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : DateTime.MinValue;

    /// <summary>"RRGGBBAA" (as valorant-api.com sends colors) to a Color.</summary>
    internal static Color ParseRgba(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length < 6) return Color.Empty;
        try
        {
            var r = Convert.ToInt32(hex.Substring(0, 2), 16);
            var g = Convert.ToInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return Color.FromArgb(r, g, b);
        }
        catch
        {
            return Color.Empty;
        }
    }

    private static string TitleCase(string s) =>
        string.Join(" ", s.ToLowerInvariant().Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1)));
}

/// <summary>Downloads pictures once and keeps them in memory.</summary>
internal static class ImageCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, Image> Images = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Loading = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> FailedAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    /// <summary>Raised on a background thread when a picture finishes downloading.</summary>
    internal static event EventHandler? Loaded;

    /// <summary>The picture if it's ready; otherwise starts downloading it and returns null.</summary>
    internal static Image? Get(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;

        lock (Lock)
        {
            if (Images.TryGetValue(url, out var image))
                return image;
            // After a failed download, wait a minute before trying that picture again.
            if (FailedAt.TryGetValue(url, out var failed) && DateTime.UtcNow - failed < TimeSpan.FromMinutes(1))
                return null;
            if (!Loading.Add(url))
                return null;
        }

        _ = Task.Run(async () =>
        {
            Image? result = null;
            try
            {
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes);
                using var decoded = Image.FromStream(stream);
                result = new Bitmap(decoded); // detach from the stream
            }
            catch (Exception e)
            {
                Trace.WriteLine("Image failed: " + e.Message);
            }

            lock (Lock)
            {
                if (result is not null)
                {
                    Images[url] = result;
                    FailedAt.Remove(url);
                }
                else
                {
                    FailedAt[url] = DateTime.UtcNow;
                }

                Loading.Remove(url);
            }

            Loaded?.Invoke(null, EventArgs.Empty);
        });
        return null;
    }
}
