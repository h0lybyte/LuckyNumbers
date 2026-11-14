using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using UnityEngine;

namespace LuckyNumbers.Core
{
    /// <summary>One save slot's record (JSON, SaveFormat). A field an older save lacks reads as its default.</summary>
    [Serializable]
    public sealed class SaveFile
    {
        public int version;
        public long played;
        public string scene;
        // The universe: the lucky number, its dice roles (0..1, -1 none) and the faces.
        public uint seed;
        public float star, worlds, luck, moons;
        public int[] faces;
        public CharacterSave character;
        public List<SavedItem> pack;
        public string skin;
        public float fuel;
        public int credits, trips;
        public JobSave job;
        public string[] upgrades;   // PlayerShip.upgrades, "id|level" (absent in older saves)
        public uint dice;           // the dice drive's roll
        public string universe;     // UniverseState.id (absent in older saves: the default, home)
    }

    [Serializable]
    public sealed class JobSave
    {
        public bool active, done, failed;
        public int kind, danger, pay, count, goal, completed;
        public string title, objective, client, body, planet, target;
        public uint seed;
        public string[] recent;   // PlayerJob.RecentTargets (absent in older saves)
    }

    /// <summary>
    /// Save slots across releases (docs/saves.md). A save is read in three steps: parse (JSON: a field it lacks reads
    /// as its default, one the game no longer has is skipped), migrate (Steps: one per version, for a change of
    /// meaning rather than a new field), then reconcile with today's content: every content id it holds (items,
    /// upgrades, skin, universe, class, origin) is mapped to the live one through old_ids (ContentIds), and one that
    /// no longer exists is dropped (an item is paid back at what it was worth when saved). Plain code, so the EditMode
    /// tests run old saves through it (SaveCompatTests).
    /// </summary>
    public static class SaveFormat
    {
        /// <summary>
        /// Written into each save. 1: the first. 2: each pack item keeps its value (the refund if it's ever removed).
        /// </summary>
        public const int Version = 2;

        /// <summary>
        /// Steps[n] takes a version n+1 save to n+2, in place. Add one (and bump Version) when a field changes what it
        /// means; a new field needs none.
        /// </summary>
        static readonly Action<SaveFile>[] Steps =
        {
            f => { },   // 1 -> 2: SavedItem.value added (0 in a v1 save: no refund for an item removed later)
        };

        public static string Write(SaveFile file)
        {
            file.version = Version;
            return JsonUtility.ToJson(file);
        }

        /// <summary>
        /// The save in `json`, brought up to Version and to today's content (`notes`: what changed on the way), or null
        /// (`error` says why: empty, unreadable, or no character).
        /// </summary>
        public static SaveFile Read(string json, List<string> notes, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(json)) { error = "empty"; return null; }
            SaveFile file;
            try { file = JsonUtility.FromJson<SaveFile>(json); }
            catch (Exception e) { error = e.Message; return null; }
            // JsonUtility fills a missing object with an empty one: a character with neither name nor seed is none.
            if (file == null || file.version <= 0 || file.character == null ||
                (string.IsNullOrEmpty(file.character.callsign) && file.character.seed == 0))
            {
                error = "not a save";
                return null;
            }
            Migrate(file, notes);
            Reconcile(file, notes);
            return file;
        }

        public static void Migrate(SaveFile file, List<string> notes)
        {
            if (file.version > Version)
            {
                // A newer build wrote it (an older page cached): read what this one knows.
                notes?.Add($"saved by a newer version ({file.version})");
                return;
            }
            for (var v = file.version; v < Version; v++)
            {
                Steps[v - 1](file);
                notes?.Add($"version {v} -> {v + 1}");
            }
            file.version = Version;
        }

        /// <summary>Every content id in the save mapped to today's (old_ids), or dropped if it's gone.</summary>
        public static void Reconcile(SaveFile file, List<string> notes)
        {
            var c = file.character;
            if (c != null)
            {
                c.classId = Map("class", c.classId, ContentIds.Class, notes, keep: true);
                c.originId = Map("origin", c.originId, ContentIds.Origin, notes, keep: true);
            }

            if (file.pack != null)
            {
                for (var i = file.pack.Count - 1; i >= 0; i--)
                {
                    var item = file.pack[i];
                    var id = ContentIds.Item(item?.id);
                    if (id != null)
                    {
                        if (id != item.id) notes?.Add($"item '{item.id}' is now '{id}'");
                        item.id = id;
                        continue;
                    }
                    file.pack.RemoveAt(i);
                    var refund = item == null ? 0 : Math.Max(0, item.value) * Math.Max(1, item.count);
                    file.credits += refund;
                    notes?.Add($"item '{item?.id}' no longer exists: removed{(refund > 0 ? $", {refund} cr paid back" : "")}");
                }
            }

            file.skin = Map("skin", file.skin, ContentIds.Skin, notes, keep: false);
            file.universe = Map("universe", file.universe, ContentIds.Universe, notes, keep: false);

            if (file.upgrades != null)
            {
                var kept = new List<string>(file.upgrades.Length);
                foreach (var entry in file.upgrades)
                {
                    if (string.IsNullOrEmpty(entry)) continue;
                    var bar = entry.IndexOf('|');
                    var id = bar < 0 ? entry : entry.Substring(0, bar);
                    var level = bar < 0 ? 1 : int.TryParse(entry.Substring(bar + 1), out var n) ? n : 1;
                    var live = ContentIds.Upgrade(id);
                    if (live == null) { notes?.Add($"upgrade '{id}' no longer exists: removed"); continue; }
                    var max = ShipUpgrades.Find(live)?.Costs.Count ?? level;
                    if (level > max) { notes?.Add($"upgrade '{live}' level {level} -> {max}"); level = max; }
                    if (live != id) notes?.Add($"upgrade '{id}' is now '{live}'");
                    if (level > 0) kept.Add($"{live}|{level}");
                }
                file.upgrades = kept.ToArray();
            }

            if (file.job != null && file.job.active && !Enum.IsDefined(typeof(JobKind), file.job.kind))
            {
                notes?.Add($"job kind {file.job.kind} no longer exists: dropped");
                file.job = null;
            }
        }

        /// <summary>
        /// The live id for `id` (unchanged if it's empty); one that's gone becomes "" (the default) unless `keep` (the
        /// restorer has its own fallback and logs it).
        /// </summary>
        static string Map(string what, string id, Func<string, string> resolve, List<string> notes, bool keep)
        {
            if (string.IsNullOrEmpty(id)) return id;
            var live = resolve(id);
            if (live == null)
            {
                notes?.Add($"{what} '{id}' no longer exists{(keep ? "" : ": the default")}");
                return keep ? id : "";
            }
            if (live != id) notes?.Add($"{what} '{id}' is now '{live}'");
            return live;
        }
    }

    /// <summary>
    /// Content ids as a save holds them: the live id for a live id or one of its old_ids, null if neither
    /// (tools/ln.py save_ids does the same; content/shipped_ids.json lists every id ever shipped).
    /// </summary>
    public static class ContentIds
    {
        public static string Item(string id) => Find(GameContent.Items?.Items, id, d => d.Id, d => d.OldIds);
        public static string Upgrade(string id) => Find(GameContent.Upgrades?.Upgrades, id, d => d.Id, d => d.OldIds);
        public static string Skin(string id) => Find(GameContent.Ships?.Ships, id, d => d.Skin, d => d.OldSkins);
        public static string Universe(string id) => Find(GameContent.Universes?.Universes, id, d => d.Id, d => d.OldIds);
        public static string Class(string id) => Find(GameContent.Character?.Classes, id, d => d.Id, d => d.OldIds);
        public static string Origin(string id) => Find(GameContent.Character?.Origins, id, d => d.Id, d => d.OldIds);

        static string Find<T>(List<T> defs, string id, Func<T, string> live, Func<T, List<string>> old)
        {
            if (defs == null || string.IsNullOrEmpty(id)) return null;
            foreach (var d in defs) if (live(d) == id) return id;
            foreach (var d in defs) if (old(d).Contains(id)) return live(d);
            return null;
        }
    }
}
