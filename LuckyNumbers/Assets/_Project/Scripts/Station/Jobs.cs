using System.Collections.Generic;
using LuckyNumbers.Cargo;
using LuckyNumbers.Model;
using LuckyNumbers.Space;
using Unity.Mathematics;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Dispatch's job board (content/jobs.txtpb): seeded offers from your lucky number and how many jobs you've done,
    /// each on one of the system's planets with its danger, pay and (Retrieve) the relic to bring back. The planet's
    /// site is seeded by universe ^ offer seed (CrashSitePresenter), so a Retrieve target is in that wreck's cargo.
    /// </summary>
    public static class Jobs
    {
        public sealed class Offer
        {
            public JobType Type;
            public string Client = "", Body = "", Planet = "", Target = "", Title = "", Brief = "", Objective = "";
            public int Danger, Pay, Goal;
            public uint Seed;
        }

        /// <summary>How many of the latest relics brought back a Retrieve offer avoids (fewer while there are few).</summary>
        public const int RecentLimit = 3;

        /// <summary>
        /// Dispatch's offers. `recent`: relics brought back lately (oldest first). A Retrieve offer's target is one
        /// you haven't just brought back and no other offer on the board wants; with none like that (there aren't
        /// enough relics yet), that slot is another kind of job instead.
        /// </summary>
        public static List<Offer> Roll(JobConfig config, CargoConfig cargo, uint universe, int round, IReadOnlyList<string> worlds,
                                       IReadOnlyList<string> recent = null)
        {
            var offers = new List<Offer>();
            var types = new List<JobType>();
            foreach (var t in config.Types) if (!t.Disabled && t.Kind != JobKind.Unspecified) types.Add(t);
            if (types.Count == 0 || worlds.Count == 0) return offers;
            var rng = new Random((universe ^ (0x10B5u * (uint)(round + 1)) ^ 0x9E3779B9u) | 1u);
            var first = rng.NextInt(types.Count);   // kinds take turns down the board, so it isn't all one kind
            var avoid = Avoid(cargo, recent);
            for (var i = 0; i < math.max(1, config.Offers); i++)
            {
                var type = types[(first + i) % types.Count];
                var index = rng.NextInt(worlds.Count);
                var o = new Offer
                {
                    Type = type,
                    Body = worlds[index],
                    Planet = BodyNames.Planet(universe, index),
                    Client = config.Clients.Count > 0 ? config.Clients[rng.NextInt(config.Clients.Count)] : "Dispatch",
                    Danger = rng.NextInt(math.max(1, type.DangerMin), math.max(type.DangerMin, type.DangerMax) + 1),
                    Seed = rng.NextUInt() | 1u,
                };
                if (type.Kind == JobKind.Retrieve)
                {
                    // One piece of that wreck's cargo (the same manifest the planet will lay out), not one you just
                    // brought back or another offer already wants.
                    var manifest = Manifest.Generate(cargo, SiteSeed(universe, o.Seed));
                    var fresh = new List<string>();
                    foreach (var r in manifest.Relics) if (!avoid.Contains(r.Name)) fresh.Add(r.Name);
                    if (fresh.Count > 0)
                    {
                        o.Target = fresh[rng.NextInt(fresh.Count)];
                        avoid.Add(o.Target);
                    }
                    else
                    {
                        // Nothing fresh to fetch: the next kind down the board that isn't Retrieve takes the slot.
                        var other = Other(types, first + i);
                        if (other == null) continue;
                        UnityEngine.Debug.Log($"[Jobs] no relic left to retrieve that isn't a repeat: {other.Kind} instead");
                        o.Type = type = other;
                        o.Danger = rng.NextInt(math.max(1, type.DangerMin), math.max(type.DangerMin, type.DangerMax) + 1);
                    }
                }
                o.Pay = type.Pay + type.PayPerDanger * o.Danger;
                // A satellite job's tier (content/dogfight.txtpb): its name and blurb in the text, its bonus on the pay.
                var tier = type.Kind == JobKind.Uplink ? UplinkTier(o.Danger) : null;
                if (tier != null) o.Pay += tier.PayBonus;
                string Tiered(string s) => tier == null ? s : (s ?? "").Replace("{tier}", tier.Name ?? "").Replace("{tier_blurb}", tier.Blurb ?? "");
                o.Goal = type.Target + type.TargetPerDanger * o.Danger;
                o.Title = Fill(Tiered(type.Title), o);
                o.Brief = Fill(Tiered(type.Brief), o);
                o.Objective = Fill(Tiered(type.Objective), o);
                offers.Add(o);
            }
            return offers;
        }

        /// <summary>
        /// The relic names a Retrieve offer skips: the latest RecentLimit brought back, but never every relic there is
        /// (so with a few relics they come round again), and always at least the last one (never twice running).
        /// </summary>
        static HashSet<string> Avoid(CargoConfig cargo, IReadOnlyList<string> recent)
        {
            var avoid = new HashSet<string>();
            if (recent == null || recent.Count == 0) return avoid;
            var relics = cargo?.Relics.Count ?? 0;
            var keep = math.min(recent.Count, math.min(RecentLimit, math.max(1, relics - 1)));
            for (var i = recent.Count - keep; i < recent.Count; i++) avoid.Add(recent[i]);
            return avoid;
        }

        /// <summary>The next enabled kind after `from` (wrapping) that isn't Retrieve; null if there's none.</summary>
        static JobType Other(List<JobType> types, int from)
        {
            for (var k = 1; k <= types.Count; k++)
            {
                var t = types[(from + k) % types.Count];
                if (t.Kind != JobKind.Retrieve) return t;
            }
            return null;
        }

        /// <summary>Remember relics brought back (oldest first), keeping the latest RecentLimit.</summary>
        public static void Remember(IList<string> recent, IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                recent.Remove(name);   // brought back again: it's the latest now
                recent.Add(name);
            }
            while (recent.Count > RecentLimit) recent.RemoveAt(0);
        }

        /// <summary>The planet's site seed for a job (terrain, wreck, cargo): the universe's, varied by the job's.</summary>
        public static uint SiteSeed(uint universe, uint job) => LuckyNumbers.Core.JobSite.Seed(universe, job);

        /// <summary>For the page's board: "kind|title|brief|planet|danger|pay".</summary>
        public static string Encode(Offer o) =>
            $"{o.Type.Kind}|{Clean(o.Title)}|{Clean(o.Brief)}|{Clean(o.Planet)}|{o.Danger}|{o.Pay}";

        public static void Take(PlayerJob job, Offer o)
        {
            job.Kind.Value = o.Type.Kind;
            job.Title.Value = o.Title;
            job.Objective.Value = o.Objective;
            job.Client.Value = o.Client;
            job.Body.Value = o.Body;
            job.Planet.Value = o.Planet;
            job.Danger.Value = o.Danger;
            job.Pay.Value = o.Pay;
            job.Seed.Value = o.Seed;
            job.Target.Value = o.Target;
            job.Count.Value = 0;
            job.Goal.Value = o.Goal;
            job.Done.Value = false;
            job.Failed.Value = false;
            job.Active.Value = true;
        }

        /// <summary>A satellite job's tier for its danger (the highest at or under it); null if there are none.</summary>
        public static UplinkTier UplinkTier(int danger)
        {
            UplinkTier best = null;
            var tiers = GameContent.Dogfight?.UplinkTiers;
            if (tiers != null)
                foreach (var t in tiers)
                    if (t.Danger <= danger && (best == null || t.Danger > best.Danger)) best = t;
            return best;
        }

        public static string Fill(string s, Offer o) => Fill(s, o.Client, o.Planet, o.Target, o.Goal, o.Pay, o.Title);

        public static string Fill(string s, PlayerJob j) =>
            Fill(s, j.Client.Value, j.Planet.Value, j.Target.Value, j.Goal.Value, j.Pay.Value, j.Title.Value);

        static string Fill(string s, string client, string planet, string target, int count, int pay, string title) =>
            Capital(Client(s, client).Replace("{planet}", planet ?? "").Replace("{target}", target ?? "")
                     .Replace("{count}", count.ToString()).Replace("{pay}", pay.ToString("N0")).Replace("{title}", title ?? ""));

        // "the Survey Office wants..." at the start of a line reads "The Survey Office wants...".
        static string Capital(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;

        /// <summary>{client}, capitalised where it starts a sentence ("...on Onrika IV. The Survey Office wants...").</summary>
        static string Client(string s, string client) =>
            (s ?? "").Replace(". {client}", ". " + Capital(client ?? "")).Replace("{client}", client ?? "");

        static string Clean(string s) => (s ?? "").Replace("|", "/");
    }
}
