using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Deceive;

/// <summary>Your owned skins, battle pass progress and missions.</summary>
internal static class ProgressData
{
    private const string SkinLevelType = "e7c63390-eda7-46e0-bb7a-a6abdacd2433";
    private const string ChromaType = "3ad1b2b2-acdb-4524-852f-954a76ddae0a";

    internal sealed class MissionProgress
    {
        public string Id = "";
        public int Progress;
        public bool Complete;
        public DateTime ExpiresUtc;
    }

    internal sealed class Contracts
    {
        /// <summary>Contract ID -> (level reached, XP towards the next level, total XP earned).</summary>
        public Dictionary<string, (int level, int towardsNext, int total)> Progress = new(StringComparer.OrdinalIgnoreCase);
        public List<MissionProgress> Missions = new();
        public DateTime WeeklyRefillUtc;
    }

    /// <summary>The IDs of every skin level and chroma you own.</summary>
    internal static async Task<HashSet<string>> GetOwnedSkinItemsAsync()
    {
        var puuid = Uri.EscapeDataString(await ValorantApi.GetPuuidAsync().ConfigureAwait(false));
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in new[] { SkinLevelType, ChromaType })
        {
            var json = await ValorantApi.GetPdAsync($"store/v1/entitlements/{puuid}/{type}").ConfigureAwait(false);
            foreach (var id in ParseEntitlements(json))
                owned.Add(id);
        }

        return owned;
    }

    internal static IEnumerable<string> ParseEntitlements(JsonNode? json)
    {
        var lists = new List<JsonArray> { json.Arr("Entitlements") };
        foreach (var byType in json.Arr("EntitlementsByTypes"))
            lists.Add(byType.Arr("Entitlements"));
        foreach (var list in lists)
            foreach (var e in list)
                if (e.Str("ItemID") is { Length: > 0 } id)
                    yield return id;
    }

    internal static async Task<Contracts> GetContractsAsync()
    {
        var puuid = Uri.EscapeDataString(await ValorantApi.GetPuuidAsync().ConfigureAwait(false));
        var json = await ValorantApi.GetPdAsync($"contracts/v1/contracts/{puuid}").ConfigureAwait(false);
        return ParseContracts(json);
    }

    internal static Contracts ParseContracts(JsonNode? json)
    {
        var result = new Contracts();
        foreach (var c in json.Arr("Contracts"))
        {
            var id = c.Str("ContractDefinitionID");
            if (id.Length == 0) continue;
            result.Progress[id] = ((int)c.Num("ProgressionLevelReached"), (int)c.Num("ProgressionTowardsNextLevel"),
                (int)(c?["ContractProgression"]).Num("TotalProgressionEarned"));
        }

        foreach (var m in json.Arr("Missions"))
        {
            result.Missions.Add(new MissionProgress
            {
                Id = m.Str("ID"),
                Progress = m.Obj("Objectives").Sum(o => (int)Json.ToLong(o.Value)),
                Complete = m.Bool("Complete"),
                ExpiresUtc = ParseDate(m.Str("ExpirationTime"))
            });
        }

        result.WeeklyRefillUtc = ParseDate(json?["MissionMetadata"].Str("WeeklyRefillTime") ?? "");
        return result;
    }

    private static DateTime ParseDate(string s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : DateTime.MinValue;

    /// <summary>
    /// The current act's battle pass: the newest pass you have progress on. Null when the
    /// community data doesn't have this act's pass yet (it usually catches up within a day or two).
    /// </summary>
    internal static ValorantContent.BattlePass? CurrentPass(ValorantContent.Catalog catalog, Contracts contracts, out bool outdated)
    {
        outdated = false;
        var currentAct = catalog.CurrentActId();
        var pass = catalog.BattlePasses.Values.FirstOrDefault(p => string.Equals(p.ActId, currentAct, StringComparison.OrdinalIgnoreCase));
        if (pass is not null) return pass;
        outdated = true;
        return null;
    }
}
