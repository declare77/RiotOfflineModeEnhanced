using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Deceive;

/// <summary>An error with a message that can be shown to the user as-is.</summary>
internal sealed class ValorantApiException : Exception
{
    public ValorantApiException(string message, int status = 0) : base(message) => Status = status;
    public int Status { get; }
}

/// <summary>Small helpers for reading System.Text.Json nodes without throwing.</summary>
internal static class Json
{
    public static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch { return null; }
    }

    public static string Str(this JsonNode? node, string key)
    {
        try
        {
            var v = node?[key];
            return v is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";
        }
        catch { return ""; }
    }

    public static long Num(this JsonNode? node, string key, long fallback = 0)
    {
        try { return node?[key] is JsonValue v ? ToLong(v, fallback) : fallback; }
        catch { return fallback; }
    }

    public static double Dbl(this JsonNode? node, string key, double fallback = 0)
    {
        try
        {
            if (node?[key] is not JsonValue v) return fallback;
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            return fallback;
        }
        catch { return fallback; }
    }

    public static bool Bool(this JsonNode? node, string key)
    {
        try { return node?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b; }
        catch { return false; }
    }

    public static JsonArray Arr(this JsonNode? node, string key)
    {
        try { return node?[key] as JsonArray ?? new JsonArray(); }
        catch { return new JsonArray(); }
    }

    public static JsonObject Obj(this JsonNode? node, string key)
    {
        try { return node?[key] as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    public static long ToLong(JsonNode? node, long fallback = 0)
    {
        if (node is not JsonValue v) return fallback;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (long)Math.Round(d);
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
        return fallback;
    }

    public static string AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>
/// Talks to the Riot Client's local API (for your session tokens) and to VALORANT's own web
/// services (store, agent select, ranks). Nothing here touches the game process.
/// </summary>
internal static class ValorantApi
{
    // Base64 of the platform JSON every Riot client sends.
    private const string ClientPlatform =
        "ew0KCSJwbGF0Zm9ybVR5cGUiOiAiUEMiLA0KCSJwbGF0Zm9ybU9TIjogIldpbmRvd3MiLA0KCSJwbGF0Zm9ybU9TVmVyc2lvbiI6ICIxMC4wLjE5MDQyLjEuMjU2LjY0Yml0IiwNCgkicGxhdGZvcm1DaGlwc2V0IjogIlVua25vd24iDQp9";

    internal const string VpCurrency = "85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741";
    internal const string RadianiteCurrency = "e59aa87c-4cbf-517a-5983-6e81511be9b7";
    internal const string KingdomCreditsCurrency = "85ca954a-41f2-ce94-9b45-8ca3dd39a00d";

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim SessionLock = new(1, 1);
    private static Session? _session;
    private static DateTime _sessionAt = DateTime.MinValue;

    private static readonly string RegionCachePath = Path.Combine(Persistence.DataDir, "valorantRegion");

    private static HttpClient CreateHttp()
    {
        // TLS settings are left to Windows (TLS 1.2+ on Windows 10/11), like the rest of the app.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.TryParseAdd("ShooterGame/13 Windows/10.0.19043.1.256.64bit");
        return http;
    }

    internal sealed class Session
    {
        public string Puuid = "";
        public string AccessToken = "";
        public string Entitlement = "";
        public string Region = "";
        public string Shard = "";
        public string ClientVersion = "";

        public string Pd => $"https://pd.{Shard}.a.pvp.net";
        public string Glz => $"https://glz-{Region}-1.{Shard}.a.pvp.net";
    }

    // ------------------------------------------------------------------ local Riot Client API

    private sealed class Lockfile
    {
        public int Port;
        public string Password = "";
    }

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static Lockfile ReadLockfile()
    {
        var path = Path.Combine(LocalAppData, "Riot Games", "Riot Client", "Config", "lockfile");
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd().Trim();
        }
        catch (FileNotFoundException) { throw NotRunning(); }
        catch (DirectoryNotFoundException) { throw NotRunning(); }
        catch (IOException) { throw NotRunning(); }
        catch (UnauthorizedAccessException) { throw NotRunning(); }

        // name:pid:port:password:protocol
        var parts = text.Split(':');
        if (parts.Length < 5 || !int.TryParse(parts[2], out var port))
            throw NotRunning();

        return new Lockfile { Port = port, Password = string.Join(":", parts, 3, parts.Length - 4) };
    }

    private static ValorantApiException NotRunning() =>
        new("The Riot Client isn't running. Start it (or VALORANT) and try again.");

    private static async Task<string> LocalGetAsync(Lockfile lockfile, string path)
    {
        var request = (HttpWebRequest)WebRequest.Create($"https://127.0.0.1:{lockfile.Port}{path}");
        request.Method = "GET";
        request.Accept = "application/json";
        request.Headers[HttpRequestHeader.Authorization] =
            "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + lockfile.Password));
        // The Riot Client's local server uses a self-signed certificate. This only applies to this
        // one request to 127.0.0.1, not to anything else the app does.
        request.ServerCertificateValidationCallback = (_, _, _, _) => true;

        var responseTask = request.GetResponseAsync();
        if (await Task.WhenAny(responseTask, Task.Delay(5000)).ConfigureAwait(false) != responseTask)
        {
            request.Abort();
            _ = responseTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            throw new ValorantApiException("The Riot Client didn't respond. Try again in a moment.");
        }

        try
        {
            using var response = (HttpWebResponse)await responseTask.ConfigureAwait(false);
            using var reader = new StreamReader(response.GetResponseStream()!);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (WebException e) when (e.Response is HttpWebResponse r)
        {
            var status = (int)r.StatusCode;
            r.Dispose();
            throw new ValorantApiException($"The Riot Client returned an error ({status}).", status);
        }
        catch (WebException)
        {
            throw NotRunning();
        }
    }

    // ------------------------------------------------------------------ session

    /// <summary>Forget cached tokens, e.g. after Riot rejected them.</summary>
    internal static void InvalidateSession() => _sessionAt = DateTime.MinValue;

    internal static async Task<Session> GetSessionAsync()
    {
        await SessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Riot tokens last an hour; refresh well before that.
            if (_session is not null && DateTime.UtcNow - _sessionAt < TimeSpan.FromMinutes(4))
                return _session;

            var lockfile = ReadLockfile();
            string tokenJson;
            try
            {
                tokenJson = await LocalGetAsync(lockfile, "/entitlements/v1/token").ConfigureAwait(false);
            }
            catch (ValorantApiException e) when (e.Status is 400 or 401 or 403 or 404)
            {
                throw new ValorantApiException("Sign in to the Riot Client first, then try again.");
            }

            var token = Json.Parse(tokenJson);
            var session = new Session
            {
                Puuid = token.Str("subject"),
                AccessToken = token.Str("accessToken"),
                Entitlement = token.Str("token")
            };
            if (session.Puuid.Length == 0 || session.AccessToken.Length == 0 || session.Entitlement.Length == 0)
                throw new ValorantApiException("Sign in to the Riot Client first, then try again.");

            (session.Region, session.Shard) = await FindRegionAsync(lockfile).ConfigureAwait(false);
            session.ClientVersion = await GetClientVersionAsync().ConfigureAwait(false);

            _session = session;
            _sessionAt = DateTime.UtcNow;
            return session;
        }
        finally
        {
            SessionLock.Release();
        }
    }

    private static (string region, string shard)? _region;

    private static async Task<(string, string)> FindRegionAsync(Lockfile lockfile)
    {
        // 1. The launch arguments of a running VALORANT.
        try
        {
            var sessions = Json.Parse(await LocalGetAsync(lockfile, "/product-session/v1/external-sessions").ConfigureAwait(false)) as JsonObject;
            foreach (var pair in sessions ?? new JsonObject())
            {
                if (!string.Equals(pair.Value.Str("productId"), "valorant", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var arg in pair.Value?["launchConfiguration"].Arr("arguments") ?? new JsonArray())
                {
                    var s = Json.AsString(arg);
                    const string prefix = "-ares-deployment=";
                    if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && FromDeployment(s.Substring(prefix.Length)) is { } found)
                        return Remember(found);
                }
            }
        }
        catch (Exception e)
        {
            Trace.WriteLine("Region from sessions failed: " + e.Message);
        }

        // 2. What we found before.
        if (_region is { } cached)
            return cached;
        try
        {
            if (File.Exists(RegionCachePath))
            {
                var parts = File.ReadAllText(RegionCachePath).Trim().Split(',');
                if (parts.Length == 2 && IsSafeId(parts[0]) && IsSafeId(parts[1]))
                    return (_region = (parts[0], parts[1])).Value;
            }
        }
        catch { /* ignore */ }

        // 3. The game's own log from the last time it ran.
        var log = ReadGameLog();
        if (log is not null)
        {
            var matches = Regex.Matches(log, @"https://glz-([a-z0-9]+)-1\.([a-z0-9]+)\.a\.pvp\.net");
            if (matches.Count > 0)
            {
                var last = matches[matches.Count - 1];
                return Remember((last.Groups[1].Value, last.Groups[2].Value));
            }
        }

        throw new ValorantApiException("Couldn't find your VALORANT region yet. Launch VALORANT once, then try again.");
    }

    private static (string, string)? FromDeployment(string deployment) => deployment.Trim().ToLowerInvariant() switch
    {
        "na" => ("na", "na"),
        "latam" => ("latam", "na"),
        "br" => ("br", "na"),
        "pbe" => ("na", "pbe"),
        "eu" => ("eu", "eu"),
        "ap" => ("ap", "ap"),
        "kr" => ("kr", "kr"),
        _ => null
    };

    private static bool IsSafeId(string s) => s.Length is > 0 and < 16 && s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    private static (string, string) Remember((string, string) region)
    {
        _region = region;
        try { File.WriteAllText(RegionCachePath, region.Item1 + "," + region.Item2); }
        catch { /* ignore */ }
        return region;
    }

    private static string? ReadGameLog()
    {
        try
        {
            var path = Path.Combine(LocalAppData, "VALORANT", "Saved", "Logs", "ShooterGame.log");
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static string? _clientVersion;
    private static DateTime _clientVersionAt = DateTime.MinValue;

    private static async Task<string> GetClientVersionAsync()
    {
        if (_clientVersion is not null && DateTime.UtcNow - _clientVersionAt < TimeSpan.FromMinutes(30))
            return _clientVersion;

        try
        {
            var json = Json.Parse(await Http.GetStringAsync("https://valorant-api.com/v1/version").ConfigureAwait(false));
            var version = json?["data"].Str("riotClientVersion") ?? "";
            if (version.Length > 0)
            {
                _clientVersionAt = DateTime.UtcNow;
                return _clientVersion = version;
            }
        }
        catch (Exception e)
        {
            Trace.WriteLine("Version lookup failed: " + e.Message);
        }

        var log = ReadGameLog();
        var match = log is null ? null : Regex.Match(log, @"CI server version:\s*(release-\S+)");
        if (match is { Success: true })
        {
            _clientVersionAt = DateTime.UtcNow;
            return _clientVersion = match.Groups[1].Value;
        }

        if (_clientVersion is not null)
            return _clientVersion;
        throw new ValorantApiException("Couldn't look up the current VALORANT version. Check your internet connection.");
    }

    // ------------------------------------------------------------------ remote requests

    /// <summary>Sends a request to Riot. Returns null for 404 (e.g. not in agent select).</summary>
    private static async Task<JsonNode?> SendAsync(HttpMethod method, Func<Session, string> url, string? body = null, bool retried = false)
    {
        var session = await GetSessionAsync().ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, url(session));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session.AccessToken);
        request.Headers.TryAddWithoutValidation("X-Riot-Entitlements-JWT", session.Entitlement);
        request.Headers.TryAddWithoutValidation("X-Riot-ClientPlatform", ClientPlatform);
        request.Headers.TryAddWithoutValidation("X-Riot-ClientVersion", session.ClientVersion);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            throw new ValorantApiException("Riot's servers took too long to answer. Try again in a moment.");
        }
        catch (HttpRequestException)
        {
            throw new ValorantApiException("Couldn't reach Riot's servers. Check your internet connection.");
        }

        using (response)
        {
            var text = response.Content is null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
                return Json.Parse(text) ?? throw new ValorantApiException("Riot sent a response this app couldn't read.");

            if (status == 404)
                return null;

            if ((status is 400 or 401 or 403) && !retried)
            {
                // Usually an expired token; get a fresh one once.
                InvalidateSession();
                return await SendAsync(method, url, body, retried: true).ConfigureAwait(false);
            }

            if (status == 429)
                throw new ValorantApiException("Riot is limiting requests right now. Wait a minute and try again.", status);
            if (status >= 500)
                throw new ValorantApiException("Riot's servers are having trouble (maintenance?). Try again later.", status);

            Trace.WriteLine($"Riot request failed {status}: {text}");
            throw new ValorantApiException($"Riot rejected the request ({status}). Try again in a moment.", status);
        }
    }

    // ------------------------------------------------------------------ simple requests

    /// <summary>GET on your shard's pd server (match history, MMR, ...). Null for 404.</summary>
    internal static Task<JsonNode?> GetPdAsync(string path) => SendAsync(HttpMethod.Get, s => $"{s.Pd}/{path}");

    /// <summary>GET on your region's glz server (agent select, live match, party). Null for 404.</summary>
    internal static Task<JsonNode?> GetGlzAsync(string path) => SendAsync(HttpMethod.Get, s => $"{s.Glz}/{path}");

    internal static async Task<string> GetPuuidAsync() => (await GetSessionAsync().ConfigureAwait(false)).Puuid;

    // ------------------------------------------------------------------ store

    internal sealed class StoreOffer
    {
        public string ItemId = "";
        public int Cost;
        public int BaseCost;      // night market only
        public int DiscountPercent; // night market only
        public string ItemTypeId = "";
    }

    internal sealed class BundleOffer
    {
        public string BundleId = "";   // valorant-api.com bundle ID (Riot's DataAssetID)
        public int Cost;               // what you'd pay for the whole bundle, in VP
        public int BaseCost;           // the items' full price added up
        public DateTime EndsUtc;
        public int ItemCount;
        public List<string> ItemIds = new();
    }

    internal const string SkinItemType = "e7c63390-eda7-46e0-bb7a-a6abdacd2433";
    internal const string BuddyItemType = "dd3bf334-87f3-40bd-b043-682a57a8dc3a";
    internal const string SprayItemType = "d5f120f8-ff8c-4aac-92ea-f2b5acbe9475";
    internal const string CardItemType = "3f296c07-64c3-494c-923b-fe692a4fa1bd";
    internal const string TitleItemType = "de7caa6b-adf7-4588-bbd1-143831e786c6";
    internal const string FlexItemType = "03a572de-4234-31ed-d344-ababa488f981";

    internal sealed class Store
    {
        public List<StoreOffer> Daily = new();
        public DateTime DailyEndsUtc;
        public List<StoreOffer>? NightMarket;
        public DateTime NightMarketEndsUtc;
        public int? Vp;
        public int? Radianite;
        public int? KingdomCredits;
        public string Puuid = "";
        public List<BundleOffer> Bundles = new();
        public List<StoreOffer> Accessories = new();
        public DateTime AccessoriesEndsUtc;
    }

    /// <summary>The price in the given currency, or the only price listed.</summary>
    private static int Price(JsonNode? cost, string currency = VpCurrency)
    {
        if (cost is not JsonObject obj || obj.Count == 0) return 0;
        if (obj.TryGetPropertyValue(currency, out var value)) return (int)Json.ToLong(value);
        return obj.Count == 1 ? (int)Json.ToLong(obj.First().Value) : 0;
    }

    private static string RewardItem(JsonNode? offer)
    {
        foreach (var reward in offer.Arr("Rewards"))
        {
            var id = reward.Str("ItemID");
            if (id.Length > 0) return id;
        }

        return offer.Str("OfferID");
    }

    internal static async Task<Store> GetStoreAsync(bool includeWallet = true)
    {
        var json = await SendAsync(HttpMethod.Post, s => $"{s.Pd}/store/v3/storefront/{s.Puuid}", "{}").ConfigureAwait(false)
                   ?? throw new ValorantApiException("Riot didn't return your store. Try again in a moment.");
        var store = ParseStore(json, DateTime.UtcNow);
        store.Puuid = (await GetSessionAsync().ConfigureAwait(false)).Puuid;
        if (!includeWallet)
            return store;

        // The wallet is a nice extra; the store still shows if it fails.
        try
        {
            var wallet = await SendAsync(HttpMethod.Get, s => $"{s.Pd}/store/v1/wallet/{s.Puuid}").ConfigureAwait(false);
            ApplyWallet(store, wallet);
        }
        catch (Exception e)
        {
            Trace.WriteLine("Wallet failed: " + e.Message);
        }

        return store;
    }

    internal static void ApplyWallet(Store store, JsonNode? wallet)
    {
        if (wallet?["Balances"] is not JsonObject balances) return;
        if (balances.TryGetPropertyValue(VpCurrency, out var vp)) store.Vp = (int)Json.ToLong(vp);
        if (balances.TryGetPropertyValue(RadianiteCurrency, out var rp)) store.Radianite = (int)Json.ToLong(rp);
        if (balances.TryGetPropertyValue(KingdomCreditsCurrency, out var kc)) store.KingdomCredits = (int)Json.ToLong(kc);
    }

    internal static Store ParseStore(JsonNode json, DateTime now)
    {
        var store = new Store();

        var panel = json["SkinsPanelLayout"];
        var offers = panel.Arr("SingleItemStoreOffers");
        if (offers.Count > 0)
        {
            foreach (var offer in offers)
                store.Daily.Add(new StoreOffer { ItemId = RewardItem(offer), Cost = Price(offer?["Cost"]) });
        }
        else
        {
            foreach (var id in panel.Arr("SingleItemOffers"))
                store.Daily.Add(new StoreOffer { ItemId = Json.AsString(id) });
        }

        store.DailyEndsUtc = now.AddSeconds(panel.Num("SingleItemOffersRemainingDurationInSeconds"));

        if (json["BonusStore"] is JsonObject bonus)
        {
            store.NightMarket = new List<StoreOffer>();
            foreach (var entry in bonus.Arr("BonusStoreOffers"))
            {
                var offer = entry?["Offer"];
                var baseCost = Price(offer?["Cost"]);
                var cost = Price(entry?["DiscountCosts"]);
                var percent = (int)Math.Round(entry.Dbl("DiscountPercent"));
                if (baseCost > 0 && cost > 0 && percent <= 0)
                    percent = (int)Math.Round(100.0 * (baseCost - cost) / baseCost);
                store.NightMarket.Add(new StoreOffer { ItemId = RewardItem(offer), Cost = cost, BaseCost = baseCost, DiscountPercent = percent });
            }

            store.NightMarketEndsUtc = now.AddSeconds(bonus.Num("BonusStoreRemainingDurationInSeconds"));
            if (store.NightMarket.Count == 0)
                store.NightMarket = null;
        }

        // Featured bundles
        var featured = json["FeaturedBundle"];
        var bundles = featured.Arr("Bundles");
        if (bundles.Count == 0 && featured?["Bundle"] is JsonObject single)
            bundles = new JsonArray(JsonNode.Parse(single.ToJsonString()));
        foreach (var b in bundles)
        {
            var items = b.Arr("Items");
            var baseSum = items.Sum(i => (long)i.Num("BasePrice"));
            var discountedSum = items.Sum(i => (long)i.Num("DiscountedPrice"));
            var total = Price(b?["TotalDiscountedCost"]);
            if (total == 0) total = Price(b?["TotalBaseCost"]);
            if (total == 0) total = (int)(discountedSum > 0 ? discountedSum : baseSum);
            var ends = b.Num("DurationRemainingInSeconds", featured.Num("BundleRemainingDurationInSeconds"));
            store.Bundles.Add(new BundleOffer
            {
                BundleId = b.Str("DataAssetID"),
                Cost = total,
                BaseCost = (int)baseSum,
                EndsUtc = now.AddSeconds(ends),
                ItemCount = items.Count,
                ItemIds = items.Select(i => i?["Item"].Str("ItemID") ?? "").Where(id => id.Length > 0).ToList()
            });
        }

        // Kingdom Credits (accessory) store
        var accessory = json["AccessoryStore"];
        foreach (var entry in accessory.Arr("AccessoryStoreOffers"))
        {
            var offer = entry?["Offer"];
            var reward = offer.Arr("Rewards").FirstOrDefault();
            store.Accessories.Add(new StoreOffer
            {
                ItemId = reward.Str("ItemID") is { Length: > 0 } id ? id : offer.Str("OfferID"),
                ItemTypeId = reward.Str("ItemTypeID"),
                Cost = Price(offer?["Cost"], KingdomCreditsCurrency)
            });
        }

        store.AccessoriesEndsUtc = now.AddSeconds(accessory.Num("AccessoryStoreRemainingDurationInSeconds"));
        return store;
    }

    // ------------------------------------------------------------------ agent select

    internal sealed class Teammate
    {
        public string Puuid = "";
        public bool IsYou;
        public bool Hidden;          // the player hides their name and isn't your friend
        public string Name = "";
        public string Tag = "";
        public string AgentId = "";
        public string SelectionState = ""; // "", "selected" (hovering), "locked"
        public int Level;            // 0 = hidden
        public int PregameTier;
        public string Team = "";     // live match: "Blue"/"Red" (or the player's own ID in deathmatch)
        /// <summary>Live match: weapon ID -> the IDs in that weapon's loadout sockets (skin, level, chroma, buddy).</summary>
        public Dictionary<string, List<string>> Loadout = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class LiveMatch
    {
        public string MatchId = "";
        public string MapId = "";
        public string ModeId = "";
        public string YourTeam = "";
        public List<Teammate> Players = new();
    }

    internal sealed class Pregame
    {
        public string MatchId = "";
        public string MapId = "";
        public string QueueId = "";
        public bool IsRanked;
        public string State = "";
        public List<Teammate> Team = new();
    }

    internal static bool IsValorantRunning()
    {
        try { return Process.GetProcessesByName("VALORANT-Win64-Shipping").Length > 0; }
        catch { return false; }
    }

    /// <summary>Your current agent select, or null when you're not in one.</summary>
    internal static async Task<Pregame?> GetPregameAsync(Func<string, (string name, string tag)?> friendLookup)
    {
        var player = await SendAsync(HttpMethod.Get, s => $"{s.Glz}/pregame/v1/players/{s.Puuid}").ConfigureAwait(false);
        var matchId = player.Str("MatchID");
        if (matchId.Length == 0)
            return null;

        var match = await SendAsync(HttpMethod.Get, s => $"{s.Glz}/pregame/v1/matches/{Uri.EscapeDataString(matchId)}").ConfigureAwait(false);
        if (match is null)
            return null;

        var session = await GetSessionAsync().ConfigureAwait(false);
        var pregame = ParsePregame(match, matchId, session.Puuid, friendLookup);

        await FillNamesAsync(pregame.Team).ConfigureAwait(false);
        return pregame;
    }

    /// <summary>Names for everyone who doesn't hide theirs: one request per new player, not per refresh.</summary>
    private static async Task FillNamesAsync(List<Teammate> players)
    {
        lock (NameCache)
        {
            foreach (var mate in players.Where(t => !t.Hidden && t.Name.Length == 0))
                if (NameCache.TryGetValue(mate.Puuid, out var known))
                    (mate.Name, mate.Tag) = known;
        }

        var lookup = players.Where(t => !t.Hidden && t.Name.Length == 0).Select(t => t.Puuid).ToList();
        if (lookup.Count == 0) return;
        try
        {
            var body = new JsonArray(lookup.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()).ToJsonString();
            var names = await SendAsync(HttpMethod.Put, s => $"{s.Pd}/name-service/v2/players", body).ConfigureAwait(false);
            ApplyNames(players, names);
            lock (NameCache)
            {
                foreach (var mate in players.Where(t => lookup.Contains(t.Puuid) && t.Name.Length > 0))
                    NameCache[mate.Puuid] = (mate.Name, mate.Tag);
            }
        }
        catch (Exception e)
        {
            Trace.WriteLine("Name lookup failed: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ live match

    /// <summary>The match you're in right now (after agent select), or null.</summary>
    internal static async Task<string?> GetLiveMatchIdAsync()
    {
        var player = await SendAsync(HttpMethod.Get, s => $"{s.Glz}/core-game/v1/players/{s.Puuid}").ConfigureAwait(false);
        var id = player.Str("MatchID");
        return id.Length > 0 ? id : null;
    }

    internal static async Task<LiveMatch?> GetLiveMatchAsync(string matchId, Func<string, (string name, string tag)?> friendLookup)
    {
        var match = await SendAsync(HttpMethod.Get, s => $"{s.Glz}/core-game/v1/matches/{Uri.EscapeDataString(matchId)}").ConfigureAwait(false);
        if (match is null) return null;
        var live = ParseLiveMatch(match, await GetPuuidAsync().ConfigureAwait(false), friendLookup);

        try
        {
            var loadouts = await SendAsync(HttpMethod.Get, s => $"{s.Glz}/core-game/v1/matches/{Uri.EscapeDataString(matchId)}/loadouts").ConfigureAwait(false);
            ApplyLoadouts(live, loadouts);
        }
        catch (Exception e)
        {
            Trace.WriteLine("Loadouts failed: " + e.Message);
        }

        await FillNamesAsync(live.Players).ConfigureAwait(false);
        return live;
    }

    internal static LiveMatch ParseLiveMatch(JsonNode match, string yourPuuid, Func<string, (string name, string tag)?> friendLookup)
    {
        var live = new LiveMatch
        {
            MatchId = match.Str("MatchID"),
            MapId = match.Str("MapID"),
            ModeId = match.Str("ModeID")
        };

        foreach (var p in match.Arr("Players"))
        {
            if (p.Bool("IsCoach")) continue;
            var puuid = p.Str("Subject");
            if (puuid.Length == 0) continue;
            var identity = p?["PlayerIdentity"];
            var isYou = string.Equals(puuid, yourPuuid, StringComparison.OrdinalIgnoreCase);
            var mate = new Teammate
            {
                Puuid = puuid,
                IsYou = isYou,
                Team = p.Str("TeamID"),
                AgentId = p.Str("CharacterID"),
                SelectionState = "locked",
                Level = identity.Bool("HideAccountLevel") && !isYou ? 0 : (int)identity.Num("AccountLevel")
            };
            if (identity.Bool("Incognito") && !isYou)
            {
                if (friendLookup(puuid) is { } friend) (mate.Name, mate.Tag) = friend;
                else mate.Hidden = true;
            }

            if (isYou) live.YourTeam = mate.Team;
            live.Players.Add(mate);
        }

        // Your team first (you at the top), then the other team.
        live.Players = live.Players
            .OrderByDescending(t => string.Equals(t.Team, live.YourTeam, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => t.IsYou)
            .ToList();
        return live;
    }

    internal static void ApplyLoadouts(LiveMatch live, JsonNode? json)
    {
        foreach (var entry in json.Arr("Loadouts"))
        {
            var loadout = entry?["Loadout"];
            var mate = live.Players.FirstOrDefault(t => string.Equals(t.Puuid, loadout.Str("Subject"), StringComparison.OrdinalIgnoreCase));
            if (mate is null) continue;
            foreach (var item in loadout.Obj("Items"))
            {
                var ids = new List<string>();
                // The skin socket first, so it wins when resolving names.
                foreach (var socket in item.Value.Obj("Sockets").OrderByDescending(sk => sk.Key.Equals(SkinSocket, StringComparison.OrdinalIgnoreCase)))
                    if (socket.Value?["Item"].Str("ID") is { Length: > 0 } id)
                        ids.Add(id);
                mate.Loadout[item.Key] = ids;
            }
        }
    }

    private const string SkinSocket = "bcef87d6-209b-46c6-8b19-fbe40bd95abc";

    private static readonly Dictionary<string, (string name, string tag)> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A name seen (not hidden) in one of your agent selects or matches, if any.</summary>
    internal static (string name, string tag)? KnownName(string puuid)
    {
        lock (NameCache)
            return NameCache.TryGetValue(puuid, out var n) ? n : null;
    }

    internal static void ApplyNames(Pregame pregame, JsonNode? names) => ApplyNames(pregame.Team, names);

    internal static void ApplyNames(List<Teammate> players, JsonNode? names)
    {
        foreach (var entry in names as JsonArray ?? new JsonArray())
        {
            var mate = players.FirstOrDefault(t => string.Equals(t.Puuid, entry.Str("Subject"), StringComparison.OrdinalIgnoreCase));
            if (mate is null || mate.Hidden) continue;
            mate.Name = entry.Str("GameName");
            mate.Tag = entry.Str("TagLine");
        }
    }

    internal static Pregame ParsePregame(JsonNode match, string matchId, string yourPuuid, Func<string, (string name, string tag)?> friendLookup)
    {
        var pregame = new Pregame
        {
            MatchId = matchId,
            MapId = match.Str("MapID"),
            QueueId = match.Str("QueueID"),
            IsRanked = match.Bool("IsRanked"),
            State = match.Str("PregameState")
        };

        var players = match["AllyTeam"].Arr("Players");
        if (players.Count == 0)
        {
            // Some modes have no AllyTeam; find your team in Teams.
            foreach (var team in match.Arr("Teams"))
            {
                var list = team.Arr("Players");
                if (list.Any(p => string.Equals(p.Str("Subject"), yourPuuid, StringComparison.OrdinalIgnoreCase)))
                {
                    players = list;
                    break;
                }
            }
        }

        foreach (var p in players)
        {
            var identity = p?["PlayerIdentity"];
            var puuid = p.Str("Subject");
            if (puuid.Length == 0) continue;
            var isYou = string.Equals(puuid, yourPuuid, StringComparison.OrdinalIgnoreCase);
            var mate = new Teammate
            {
                Puuid = puuid,
                IsYou = isYou,
                AgentId = p.Str("CharacterID"),
                SelectionState = p.Str("CharacterSelectionState"),
                Level = identity.Bool("HideAccountLevel") && !isYou ? 0 : (int)identity.Num("AccountLevel"),
                PregameTier = (int)p.Num("CompetitiveTier")
            };

            if (identity.Bool("Incognito") && !isYou)
            {
                // Respect "hide my name" unless this person is already on your friends list.
                if (friendLookup(puuid) is { } friend)
                    (mate.Name, mate.Tag) = friend;
                else
                    mate.Hidden = true;
            }

            pregame.Team.Add(mate);
        }

        // You first, then the order Riot gives.
        pregame.Team = pregame.Team.OrderByDescending(t => t.IsYou).ToList();
        return pregame;
    }

    // ------------------------------------------------------------------ ranks

    internal sealed class RankInfo
    {
        public int Tier;            // current act
        public int RankedRating;
        public int Wins;
        public int Games;
        public int PeakTier;
        public string PeakActId = "";
        public bool RankHidden;     // the player turned on "hide my act rank"
    }

    private static readonly Dictionary<string, (RankInfo info, DateTime at)> RankCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RankCacheLock = new();

    internal static RankInfo? CachedRank(string puuid)
    {
        lock (RankCacheLock)
            return RankCache.TryGetValue(puuid, out var hit) && DateTime.UtcNow - hit.at < TimeSpan.FromMinutes(15) ? hit.info : null;
    }

    internal static async Task<RankInfo> GetRankAsync(string puuid, ValorantContent.Catalog catalog)
    {
        if (CachedRank(puuid) is { } cached)
            return cached;

        var json = await SendAsync(HttpMethod.Get, s => $"{s.Pd}/mmr/v1/players/{Uri.EscapeDataString(puuid)}").ConfigureAwait(false);
        var info = ParseRank(json, catalog);
        lock (RankCacheLock)
            RankCache[puuid] = (info, DateTime.UtcNow);
        return info;
    }

    internal static RankInfo ParseRank(JsonNode? json, ValorantContent.Catalog catalog)
    {
        var info = new RankInfo { RankHidden = json.Bool("IsActRankBadgeHidden") };
        var seasons = json?["QueueSkills"]?["competitive"].Obj("SeasonalInfoBySeasonID") ?? new JsonObject();

        var current = catalog.CurrentActId();
        foreach (var pair in seasons)
        {
            var season = pair.Value;
            var id = season.Str("SeasonID") is { Length: > 0 } sid ? sid : pair.Key;

            if (string.Equals(id, current, StringComparison.OrdinalIgnoreCase))
            {
                info.Tier = (int)season.Num("CompetitiveTier");
                info.RankedRating = (int)season.Num("RankedRating");
                info.Wins = (int)season.Num("NumberOfWinsWithPlacements", season.Num("NumberOfWins"));
                info.Games = (int)season.Num("NumberOfGames");
            }

            // Tier numbers changed in Episode 5 (Ascendant was added), so only compare acts since then.
            if (!catalog.IsCurrentRankSystem(id))
                continue;

            var best = (int)season.Num("CompetitiveTier");
            foreach (var tierWins in season.Obj("WinsByTier"))
            {
                if (int.TryParse(tierWins.Key, out var tier) && Json.ToLong(tierWins.Value) > 0)
                    best = Math.Max(best, tier);
            }

            if (best > info.PeakTier)
            {
                info.PeakTier = best;
                info.PeakActId = id;
            }
        }

        if (info.Tier > info.PeakTier)
        {
            info.PeakTier = info.Tier;
            info.PeakActId = current;
        }

        return info;
    }
}
