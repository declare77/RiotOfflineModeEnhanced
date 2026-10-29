using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Deceive;

/// <summary>
/// Remembers every daily store and Night Market the app has seen, one file per account,
/// in %APPDATA%\Deceive\store-history-*.json.
/// </summary>
internal static class StoreHistory
{
    internal sealed class Day
    {
        public DateTime EndsUtc;               // when that day's store reset
        public List<string> Daily = new();     // skin IDs
        public List<string> Night = new();     // skin IDs (empty when no Night Market)
    }

    private static readonly object Lock = new();
    private const int Keep = 400;

    private static string PathFor(string puuid) =>
        Path.Combine(Persistence.DataDir, $"store-history-{new string(puuid.Where(char.IsLetterOrDigit).ToArray())}.json");

    internal static List<Day> Load(string puuid)
    {
        lock (Lock)
        {
            var days = new List<Day>();
            try
            {
                var path = PathFor(puuid);
                if (!File.Exists(path)) return days;
                foreach (var d in Json.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray())
                {
                    if (!DateTime.TryParse(d.Str("ends"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ends))
                        continue;
                    days.Add(new Day
                    {
                        EndsUtc = DateTime.SpecifyKind(ends, DateTimeKind.Utc),
                        Daily = d.Arr("daily").Select(Json.AsString).Where(s => s.Length > 0).ToList(),
                        Night = d.Arr("night").Select(Json.AsString).Where(s => s.Length > 0).ToList()
                    });
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("Store history unreadable: " + e.Message);
            }

            return days.OrderByDescending(d => d.EndsUtc).ToList();
        }
    }

    /// <summary>Adds (or updates) the entry for this store's day.</summary>
    internal static void Record(ValorantApi.Store store, ValorantContent.Catalog catalog)
    {
        if (store.Puuid.Length == 0 || store.Daily.Count == 0) return;
        lock (Lock)
        {
            var days = Load(store.Puuid);
            // Round to the hour: the same day fetched twice has a reset time a few seconds apart.
            var ends = new DateTime(store.DailyEndsUtc.Ticks - store.DailyEndsUtc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc)
                .AddHours(store.DailyEndsUtc.Minute >= 30 ? 1 : 0);
            days.RemoveAll(d => Math.Abs((d.EndsUtc - ends).TotalMinutes) < 90);
            days.Add(new Day
            {
                EndsUtc = ends,
                Daily = store.Daily.Select(o => SkinId(o.ItemId, catalog)).ToList(),
                Night = store.NightMarket?.Select(o => SkinId(o.ItemId, catalog)).ToList() ?? new List<string>()
            });

            var keep = days.OrderByDescending(d => d.EndsUtc).Take(Keep).ToList();
            var json = new JsonArray(keep.Select(d => (JsonNode)new JsonObject
            {
                ["ends"] = d.EndsUtc.ToString("o"),
                ["daily"] = new JsonArray(d.Daily.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                ["night"] = new JsonArray(d.Night.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
            }).ToArray());

            try
            {
                var path = PathFor(store.Puuid);
                File.WriteAllText(path + ".tmp", json.ToJsonString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e)
            {
                Trace.WriteLine("Couldn't save store history: " + e.Message);
            }
        }
    }

    /// <summary>The skin's own ID for a store item (store items are usually a skin's first level).</summary>
    internal static string SkinId(string itemId, ValorantContent.Catalog catalog) =>
        catalog.Skins.TryGetValue(itemId, out var skin) && skin.Id.Length > 0 ? skin.Id.ToLowerInvariant() : itemId.ToLowerInvariant();
}

/// <summary>
/// Checks your store once per daily reset while you have something on your wishlist, and shows a
/// Windows notification if a wishlisted skin is in the daily store or the Night Market.
/// That's one request to Riot a day.
/// </summary>
internal static class StoreWatcher
{
    private static MainController? _controller;
    private static Timer? _timer;
    private static DateTime _checkedUntil = DateTime.MinValue; // the reset time of the last store we saw
    private static int _running;
    private static readonly HashSet<string> Notified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a fresh store was fetched (by the watcher or the Store tab).</summary>
    internal static event EventHandler? StoreSeen;

    internal static void Start(MainController controller)
    {
        _controller = controller;
        // First check a couple of minutes after launch, when you're signed in; then every 10 minutes
        // (which only asks Riot again after the store has reset).
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
    }

    private static async Task CheckAsync()
    {
        if (AppSettings.Current.Wishlist.Count == 0) return;
        if (DateTime.UtcNow < _checkedUntil) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var store = await ValorantApi.GetStoreAsync(includeWallet: false).ConfigureAwait(false);
            var catalog = await ValorantContent.GetAsync(store.Daily.Select(o => o.ItemId)).ConfigureAwait(false);
            Observe(store, catalog, notify: true);
        }
        catch (Exception e)
        {
            // Not signed in, offline, etc. Try again at the next tick.
            Trace.WriteLine("Store check skipped: " + e.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// Records a store that was just fetched and, when asked, notifies about wishlist matches.
    /// The Store tab calls this with notify: false (you're already looking at it).
    /// </summary>
    internal static void Observe(ValorantApi.Store store, ValorantContent.Catalog catalog, bool notify)
    {
        if (store.DailyEndsUtc > _checkedUntil)
            _checkedUntil = store.DailyEndsUtc.AddSeconds(30);

        StoreHistory.Record(store, catalog);

        var wishlist = new HashSet<string>(AppSettings.Current.Wishlist, StringComparer.OrdinalIgnoreCase);
        var hits = new List<(string name, bool night)>();
        foreach (var (offer, night) in store.Daily.Select(o => (o, false)).Concat((store.NightMarket ?? new List<ValorantApi.StoreOffer>()).Select(o => (o, true))))
        {
            var id = StoreHistory.SkinId(offer.ItemId, catalog);
            if (!wishlist.Contains(id)) continue;
            var key = $"{(night ? store.NightMarketEndsUtc : store.DailyEndsUtc):yyyyMMddHH}:{id}";
            if (!Notified.Add(key)) continue;
            hits.Add((catalog.Skins.TryGetValue(id, out var skin) ? skin.Name : "A wishlist skin", night));
        }

        StoreSeen?.Invoke(null, EventArgs.Empty);

        if (!notify || hits.Count == 0) return;
        var title = hits.Count == 1 ? "Wishlist skin in your store" : $"{hits.Count} wishlist skins in your store";
        var text = string.Join(", ", hits.Select(h => h.name + (h.night ? " (Night Market)" : "")));
        _controller?.ShowTrayNotice(title, text);
    }
}
