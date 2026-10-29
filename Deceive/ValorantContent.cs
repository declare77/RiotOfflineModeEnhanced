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
        public string Id = "";         // the skin's own ID (what the wishlist stores)
        public string FirstLevelId = "";
        public string Name = "";
        public string Weapon = "";
        public string Icon = "";
        public Color Rarity = Color.Empty;
        public string RarityIcon = "";
        public string RarityName = "";  // Select, Deluxe, Premium, Exclusive, Ultra
        public int RarityRank;          // 1 (Select) .. 5 (Ultra), 0 for none
    }

    /// <summary>A buddy, spray, player card, title, flex or bundle.</summary>
    internal sealed class Item
    {
        public string Name = "";
        public string Icon = "";
        public string Kind = ""; // "Buddy", "Spray", "Card", "Title", "Flex", "Bundle"
    }

    internal sealed class PassLevel
    {
        public int Xp;              // XP needed to go from the previous level to this one
        public string RewardType = "";
        public string RewardId = "";
        public int Amount;
        public bool Highlighted;
    }

    internal sealed class BattlePass
    {
        public string Id = "";      // contract ID
        public string Name = "";
        public string ActId = "";
        public List<PassLevel> Levels = new();
    }

    internal sealed class Mission
    {
        public string Id = "";
        public string Title = "";
        public string Type = "";    // Weekly, Daily, ...
        public int Xp;
        public int ProgressToComplete;
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
        /// <summary>By skin, level and chroma ID; all of a skin's IDs point at the same object.</summary>
        public readonly Dictionary<string, Skin> Skins = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Each skin once (for search), excluding standard and random-favorite skins.</summary>
        public readonly List<Skin> SkinList = new();
        public readonly Dictionary<string, Item> Items = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Weapons = new(StringComparer.OrdinalIgnoreCase); // id -> name
        public readonly Dictionary<string, BattlePass> BattlePasses = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Mission> Missions = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Item> Bundles = new(StringComparer.OrdinalIgnoreCase);
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
    private static DateTime _lastForcedRefresh = DateTime.MinValue;

    /// <summary>
    /// Loads the catalog. A skin or agent it doesn't know means the cached copy is from before a
    /// new release (like a new agent), so it downloads a fresh copy — at most every 15 minutes, so an
    /// ID valorant-api.com hasn't added yet doesn't cause a download on every check.
    /// </summary>
    internal static async Task<Catalog> GetAsync(IEnumerable<string>? mustHaveSkins = null, IEnumerable<string>? mustHaveAgents = null)
    {
        await LoadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var skins = mustHaveSkins?.Where(id => id.Length > 0).ToList() ?? new List<string>();
            var agents = mustHaveAgents?.Where(id => id.Length > 0).ToList() ?? new List<string>();
            bool Complete(Catalog c) => skins.All(c.Skins.ContainsKey) && agents.All(c.Agents.ContainsKey);

            var catalog = _catalog ?? await Task.Run(() => BuildAsync(false)).ConfigureAwait(false);
            if (!Complete(catalog) && DateTime.UtcNow - _lastForcedRefresh > TimeSpan.FromMinutes(15))
            {
                _lastForcedRefresh = DateTime.UtcNow;
                var fresh = await Task.Run(() => BuildAsync(true)).ConfigureAwait(false);
                // Keep the old copy if the download failed and came back emptier.
                if (fresh.Agents.Count >= catalog.Agents.Count && fresh.Skins.Count >= catalog.Skins.Count)
                    catalog = fresh;
            }

            return _catalog = catalog;
        }
        finally
        {
            LoadLock.Release();
        }
    }

    /// <summary>Downloads everything again (e.g. when a new act's battle pass is missing).</summary>
    internal static async Task<Catalog> RefreshAsync()
    {
        await LoadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return _catalog = await Task.Run(() => BuildAsync(true)).ConfigureAwait(false);
        }
        finally
        {
            LoadLock.Release();
        }
    }

    private static async Task<Catalog> BuildAsync(bool forceRefresh)
    {
        var catalog = new Catalog();

        var tiers = await FetchAsync("contenttiers", "https://valorant-api.com/v1/contenttiers?language=en-US", forceRefresh).ConfigureAwait(false);
        var agents = await FetchAsync("agents", "https://valorant-api.com/v1/agents?isPlayableCharacter=true&language=en-US", forceRefresh).ConfigureAwait(false);
        var maps = await FetchAsync("maps", "https://valorant-api.com/v1/maps?language=en-US", forceRefresh).ConfigureAwait(false);
        var ranks = await FetchAsync("competitivetiers", "https://valorant-api.com/v1/competitivetiers?language=en-US", forceRefresh).ConfigureAwait(false);
        var seasons = await FetchAsync("seasons", "https://valorant-api.com/v1/seasons?language=en-US", forceRefresh).ConfigureAwait(false);

        var rarity = new Dictionary<string, (Color color, string icon, string name, int rank)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tiers.Arr("data"))
            rarity[t.Str("uuid")] = (ParseRgba(t.Str("highlightColor")), t.Str("displayIcon"), t.Str("devName"), (int)t.Num("rank") + 1);

        // Weapon names, from the full weapons list (skins only know their weapon's ID).
        var weapons = await FetchAsync("weapons", "https://valorant-api.com/v1/weapons?language=en-US", forceRefresh).ConfigureAwait(false);
        foreach (var weapon in weapons.Arr("data"))
        {
            var weaponName = weapon.Str("displayName");
            if (weapon.Str("uuid") is { Length: > 0 } weaponId)
                catalog.Weapons[weaponId] = weaponName;
            foreach (var skin in weapon.Arr("skins"))
                AddSkin(catalog, skin, weaponName, rarity);
        }

        await AddItemsAsync(catalog, forceRefresh).ConfigureAwait(false);

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

    private static void AddSkin(Catalog catalog, JsonNode? skin, string weapon, Dictionary<string, (Color color, string icon, string name, int rank)> rarity)
    {
        var skinId = skin.Str("uuid");
        if (skinId.Length == 0) return;
        var tier = rarity.TryGetValue(skin.Str("contentTierUuid"), out var r) ? r : (Color.Empty, "", "", 0);
        var levels = skin.Arr("levels");
        var firstLevel = levels.FirstOrDefault();
        var chromaIcon = skin.Arr("chromas").Select(c => c.Str("fullRender")).FirstOrDefault(s => s.Length > 0) ?? "";
        var item = new Skin
        {
            Id = skinId,
            FirstLevelId = firstLevel.Str("uuid"),
            Name = skin.Str("displayName"),
            Weapon = weapon,
            Icon = FirstNonEmpty(firstLevel.Str("displayIcon"), skin.Str("displayIcon"), chromaIcon),
            Rarity = tier.Item1,
            RarityIcon = tier.Item2,
            RarityName = tier.Item3,
            RarityRank = tier.Item4
        };

        catalog.Skins[skinId] = item;
        foreach (var level in levels)
            if (level.Str("uuid") is { Length: > 0 } id) catalog.Skins[id] = item;
        foreach (var chroma in skin.Arr("chromas"))
            if (chroma.Str("uuid") is { Length: > 0 } id && !catalog.Skins.ContainsKey(id)) catalog.Skins[id] = item;

        // Standard and "Random Favorite" skins have no rarity; leave them out of search.
        if (skin.Str("contentTierUuid").Length > 0)
            catalog.SkinList.Add(item);
    }

    private static async Task AddItemsAsync(Catalog catalog, bool forceRefresh)
    {
        var buddies = await FetchAsync("buddies", "https://valorant-api.com/v1/buddies?language=en-US", forceRefresh).ConfigureAwait(false);
        foreach (var b in buddies.Arr("data"))
        {
            var item = new Item { Name = b.Str("displayName"), Icon = b.Str("displayIcon"), Kind = "Buddy" };
            catalog.Items[b.Str("uuid")] = item;
            foreach (var level in b.Arr("levels"))
                if (level.Str("uuid") is { Length: > 0 } id) catalog.Items[id] = item;
        }

        var sprays = await FetchAsync("sprays", "https://valorant-api.com/v1/sprays?language=en-US", forceRefresh).ConfigureAwait(false);
        foreach (var sp in sprays.Arr("data"))
        {
            var item = new Item { Name = sp.Str("displayName"), Icon = FirstNonEmpty(sp.Str("fullTransparentIcon"), sp.Str("displayIcon")), Kind = "Spray" };
            catalog.Items[sp.Str("uuid")] = item;
            foreach (var level in sp.Arr("levels"))
                if (level.Str("uuid") is { Length: > 0 } id) catalog.Items[id] = item;
        }

        var cards = await FetchAsync("playercards", "https://valorant-api.com/v1/playercards?language=en-US", forceRefresh).ConfigureAwait(false);
        foreach (var c in cards.Arr("data"))
            catalog.Items[c.Str("uuid")] = new Item { Name = c.Str("displayName"), Icon = FirstNonEmpty(c.Str("smallArt"), c.Str("displayIcon")), Kind = "Card" };

        var titles = await FetchAsync("playertitles", "https://valorant-api.com/v1/playertitles?language=en-US", forceRefresh).ConfigureAwait(false);
        foreach (var t in titles.Arr("data"))
            catalog.Items[t.Str("uuid")] = new Item { Name = t.Str("titleText") is { Length: > 0 } text ? text : t.Str("displayName"), Kind = "Title" };

        var flex = await FetchAsync("flex", "https://valorant-api.com/v1/flex?language=en-US", forceRefresh).ConfigureAwait(false);
        foreach (var f in flex.Arr("data"))
            catalog.Items[f.Str("uuid")] = new Item { Name = f.Str("displayName"), Icon = f.Str("displayIcon"), Kind = "Flex" };

        var contracts = await FetchAsync("contracts", "https://valorant-api.com/v1/contracts?language=en-US", forceRefresh).ConfigureAwait(false);
        foreach (var c in contracts.Arr("data"))
        {
            var content = c?["content"];
            if (!string.Equals(content.Str("relationType"), "Season", StringComparison.OrdinalIgnoreCase)) continue;
            var pass = new BattlePass { Id = c.Str("uuid"), Name = c.Str("displayName"), ActId = content.Str("relationUuid") };
            foreach (var chapter in content.Arr("chapters"))
                foreach (var level in chapter.Arr("levels"))
                {
                    var reward = level?["reward"];
                    pass.Levels.Add(new PassLevel
                    {
                        Xp = (int)level.Num("xp"),
                        RewardType = reward.Str("type"),
                        RewardId = reward.Str("uuid"),
                        Amount = (int)reward.Num("amount"),
                        Highlighted = reward.Bool("isHighlighted")
                    });
                }
            if (pass.Id.Length > 0) catalog.BattlePasses[pass.Id] = pass;
        }

        var missions = await FetchAsync("missions", "https://valorant-api.com/v1/missions?language=en-US", forceRefresh).ConfigureAwait(false);
        foreach (var m in missions.Arr("data"))
        {
            var id = m.Str("uuid");
            if (id.Length == 0) continue;
            var type = m.Str("type");
            catalog.Missions[id] = new Mission
            {
                Id = id,
                Title = m.Str("title") is { Length: > 0 } t ? t : m.Str("displayName"),
                Type = type.Contains("::") ? type.Substring(type.LastIndexOf(':') + 1) : type,
                Xp = (int)m.Num("xpGrant"),
                ProgressToComplete = (int)m.Num("progressToComplete")
            };
        }

        var bundles = await FetchAsync("bundles", "https://valorant-api.com/v1/bundles?language=en-US", forceRefresh).ConfigureAwait(false);
        foreach (var b in bundles.Arr("data"))
            catalog.Bundles[b.Str("uuid")] = new Item
            {
                Name = b.Str("displayName"),
                Icon = FirstNonEmpty(b.Str("displayIcon"), b.Str("displayIcon2")),
                Kind = "Bundle"
            };
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
