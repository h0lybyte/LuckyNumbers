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

        public static List<Offer> Roll(JobConfig config, CargoConfig cargo, uint universe, int round, IReadOnlyList<string> worlds)
        {
            var offers = new List<Offer>();
            var types = new List<JobType>();
            foreach (var t in config.Types) if (!t.Disabled && t.Kind != JobKind.Unspecified) types.Add(t);
            if (types.Count == 0 || worlds.Count == 0) return offers;
            var rng = new Random((universe ^ (0x10B5u * (uint)(round + 1)) ^ 0x9E3779B9u) | 1u);
            var first = rng.NextInt(types.Count);   // kinds take turns down the board, so it isn't all one kind
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
                o.Pay = type.Pay + type.PayPerDanger * o.Danger;
                o.Goal = type.Target + type.TargetPerDanger * o.Danger;
                if (type.Kind == JobKind.Retrieve)
                {
                    // One piece of that wreck's cargo (the same manifest the planet will lay out).
                    var manifest = Manifest.Generate(cargo, SiteSeed(universe, o.Seed));
                    if (manifest.Relics.Count > 0) o.Target = manifest.Relics[rng.NextInt(manifest.Relics.Count)].Name;
                }
                o.Title = Fill(type.Title, o);
                o.Brief = Fill(type.Brief, o);
                o.Objective = Fill(type.Objective, o);
                offers.Add(o);
            }
            return offers;
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
            job.Active.Value = true;
        }

        public static string Fill(string s, Offer o) => Fill(s, o.Client, o.Planet, o.Target, o.Goal, o.Pay, o.Title);

        public static string Fill(string s, PlayerJob j) =>
            Fill(s, j.Client.Value, j.Planet.Value, j.Target.Value, j.Goal.Value, j.Pay.Value, j.Title.Value);

        static string Fill(string s, string client, string planet, string target, int count, int pay, string title) =>
            Capital((s ?? "").Replace("{client}", client ?? "").Replace("{planet}", planet ?? "").Replace("{target}", target ?? "")
                     .Replace("{count}", count.ToString()).Replace("{pay}", pay.ToString("N0")).Replace("{title}", title ?? ""));

        // "the Survey Office wants..." at the start of a line reads "The Survey Office wants...".
        static string Capital(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;

        static string Clean(string s) => (s ?? "").Replace("|", "/");
    }
}
