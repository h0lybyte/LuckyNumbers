using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using NUnit.Framework;
using UnityEngine;

namespace LuckyNumbers.Tests
{
    /// <summary>
    /// Saves from shipped versions keep loading (Core/SaveFormat, docs/saves.md). Each fixture is a slot as that version
    /// wrote it: never edit one, add another for a new version.
    /// </summary>
    public sealed class SaveCompatTests
    {
        // 0.4.1 (version 1): upgrades, the dice drive, the universe, recent targets; pack items without their value.
        const string V1 = @"{""version"":1,""played"":1794000000000,""scene"":""Station"",""seed"":4242,""star"":0.31,""worlds"":0.5,""luck"":0.72,""moons"":-1.0,
""faces"":[6,3,5,1,4,2],""character"":{""callsign"":""Vega"",""classId"":""ace"",""originId"":""belt"",""seed"":4242,""moved"":[1,-1,0,0,0,0],""level"":3,""xp"":410,""locked"":true},
""pack"":[{""id"":""rifle"",""x"":0,""y"":0,""rot"":0,""equipped"":4,""count"":1},{""id"":""medpatch"",""x"":2,""y"":0,""rot"":0,""equipped"":0,""count"":3},
{""id"":""pliny_roman_vase"",""x"":4,""y"":1,""rot"":1,""equipped"":0,""count"":1},{""id"":""rune_shard"",""x"":6,""y"":2,""rot"":0,""equipped"":0,""count"":1}],
""skin"":""cubik"",""fuel"":640.5,""credits"":1250,""trips"":4,
""job"":{""active"":true,""done"":false,""failed"":false,""kind"":5,""danger"":2,""pay"":900,""count"":1,""goal"":4,""completed"":2,""title"":""Hold the line"",""objective"":""Plant the beacon"",""client"":""Ops"",""body"":""..."",""planet"":""Cinder IV"",""target"":"""",""seed"":77,""recent"":[""Cinder IV""]},
""upgrades"":[""thrusters|2"",""tanks|1"",""dice_drive|1""],""dice"":123456,""universe"":""emberdeep""}";

        // The oldest saves (before upgrades, the dice drive and universes): pack items carry copies of their content.
        const string Oldest = @"{""version"":1,""played"":1790000000000,""scene"":""Planet"",""seed"":7,""star"":-1.0,""worlds"":-1.0,""luck"":-1.0,""moons"":-1.0,""faces"":[],
""character"":{""callsign"":""Rook"",""classId"":""medic"",""originId"":""station"",""seed"":7,""moved"":[0,0,0,0,0,0],""level"":1,""xp"":20,""locked"":true},
""pack"":[{""id"":""sidearm"",""name"":""Sidearm"",""description"":""A pistol."",""value"":120,""x"":0,""y"":0,""rot"":0,""equipped"":4,""count"":1}],
""skin"":""idolknight"",""fuel"":1000.0,""credits"":300,""trips"":1,
""job"":{""active"":false,""done"":false,""failed"":false,""kind"":0,""danger"":0,""pay"":0,""count"":0,""goal"":0,""completed"":0,""title"":"""",""objective"":"""",""client"":"""",""body"":"""",""planet"":"""",""target"":"""",""seed"":0}}";

        [Test]
        public void ShippedSavesLoadWhole()
        {
            var notes = new List<string>();
            var f = SaveFormat.Read(V1, notes, out var error);
            Assert.IsNotNull(f, error);
            Assert.AreEqual(SaveFormat.Version, f.version);
            Assert.AreEqual("Vega", f.character.callsign);
            Assert.AreEqual(4, f.pack.Count, string.Join("; ", notes));
            Assert.AreEqual(1250, f.credits, "nothing paid back: every item is still there");
            Assert.AreEqual(new[] { "thrusters|2", "tanks|1", "dice_drive|1" }, f.upgrades, string.Join("; ", notes));
            Assert.AreEqual("cubik", f.skin);
            Assert.AreEqual("emberdeep", f.universe);
            Assert.AreEqual(5, f.job.kind);
            Assert.AreEqual(new[] { "version 1 -> 2" }, notes.ToArray(), "only the version step: no content was lost");

            notes.Clear();
            var old = SaveFormat.Read(Oldest, notes, out error);
            Assert.IsNotNull(old, error);
            Assert.AreEqual("sidearm", old.pack[0].id);
            Assert.AreEqual(120, old.pack[0].value, "the oldest saves' copy of the value is read back");
            Assert.IsTrue(string.IsNullOrEmpty(old.universe), "no universe: the default");
            Assert.IsNull(old.upgrades);
            Assert.AreEqual(new[] { "version 1 -> 2" }, notes.ToArray());
        }

        [Test]
        public void WriteThenReadIsTheSame()
        {
            var f = SaveFormat.Read(V1, null, out _);
            var again = SaveFormat.Read(SaveFormat.Write(f), new List<string>(), out var error);
            Assert.IsNotNull(again, error);
            Assert.AreEqual(JsonUtility.ToJson(f), JsonUtility.ToJson(again));
        }

        /// <summary>Every id content/shipped_ids.json lists (each ever shipped) still resolves (ln.py gen checks it too).</summary>
        [Test]
        public void EveryShippedIdResolves()
        {
            var path = Path.Combine(Application.dataPath, "..", "..", "content", "shipped_ids.json");
            var json = File.ReadAllText(path);
            var kinds = new Dictionary<string, System.Func<string, string>>
            {
                ["items"] = ContentIds.Item, ["upgrades"] = ContentIds.Upgrade, ["skins"] = ContentIds.Skin,
                ["universes"] = ContentIds.Universe, ["classes"] = ContentIds.Class, ["origins"] = ContentIds.Origin,
            };
            foreach (var (kind, resolve) in kinds)
            {
                var block = Regex.Match(json, "\"" + kind + "\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                Assert.IsTrue(block.Success, $"shipped_ids.json has {kind}");
                var ids = Regex.Matches(block.Groups[1].Value, "\"([^\"]+)\"");
                Assert.Greater(ids.Count, 0, kind);
                foreach (Match m in ids) Assert.IsNotNull(resolve(m.Groups[1].Value), $"{kind}: '{m.Groups[1].Value}' no longer resolves");
            }
        }

        [Test]
        public void RenamedIdsFollowOldIds()
        {
            var item = GameContent.Items.Items[0];
            var upgrade = GameContent.Upgrades.Upgrades[0];
            var skin = GameContent.Ships.Ships[0];
            try
            {
                item.OldIds.Add("test_old_item");
                upgrade.OldIds.Add("test_old_upgrade");
                skin.OldSkins.Add("test_old_skin");
                var f = SaveFormat.Read(V1, null, out _);
                f.pack[0].id = "test_old_item";
                f.upgrades = new[] { "test_old_upgrade|1" };
                f.skin = "test_old_skin";
                var notes = new List<string>();
                SaveFormat.Reconcile(f, notes);
                Assert.AreEqual(item.Id, f.pack[0].id);
                Assert.AreEqual(new[] { $"{upgrade.Id}|1" }, f.upgrades);
                Assert.AreEqual(skin.Skin, f.skin);
                Assert.AreEqual(3, notes.Count, string.Join("; ", notes));
            }
            finally
            {
                item.OldIds.Remove("test_old_item");
                upgrade.OldIds.Remove("test_old_upgrade");
                skin.OldSkins.Remove("test_old_skin");
            }
        }

        [Test]
        public void RemovedContentFallsBack()
        {
            var f = SaveFormat.Read(V1, null, out _);
            f.pack.Add(new SavedItem { id = "no_such_item", value = 40, count = 2 });
            f.pack.Add(new SavedItem { id = "no_such_free_item", count = 1 });
            f.upgrades = new[] { "no_such_upgrade|2", "thrusters|99" };
            f.skin = "no_such_skin";
            f.universe = "no_such_universe";
            f.character.classId = "no_such_class";
            f.job.kind = 99;
            var notes = new List<string>();
            SaveFormat.Reconcile(f, notes);

            Assert.AreEqual(4, f.pack.Count, "the two unknown items are gone");
            Assert.AreEqual(1250 + 80, f.credits, "paid back at what they were worth");
            var max = ShipUpgrades.Find("thrusters").Costs.Count;
            Assert.AreEqual(new[] { $"thrusters|{max}" }, f.upgrades, "unknown dropped, level clamped");
            Assert.AreEqual("", f.skin);
            Assert.AreEqual("", f.universe);
            Assert.AreEqual("no_such_class", f.character.classId, "kept: CharacterPresenter falls back to the default");
            Assert.IsNull(f.job);
        }

        [Test]
        public void UnreadableAndNewerSaves()
        {
            Assert.IsNull(SaveFormat.Read("{not json", null, out var error));
            Assert.IsNotNull(error);
            Assert.IsNull(SaveFormat.Read("{\"version\":1}", null, out _), "no character: not a save");
            Assert.IsNull(SaveFormat.Read("", null, out _));

            var newer = V1.Replace("\"version\":1", "\"version\":99");
            var notes = new List<string>();
            var f = SaveFormat.Read(newer, notes, out error);
            Assert.IsNotNull(f, error);
            Assert.AreEqual(99, f.version, "not stepped down");
            Assert.AreEqual(4, f.pack.Count);
        }
    }
}
