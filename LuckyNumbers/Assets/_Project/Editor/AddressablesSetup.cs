using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEngine;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Addressables from code, like the scenes: settings, groups and addresses come from <see cref="Rules"/>, never
    /// from clicking in the Groups window (a hand edit is overwritten on the next Apply). Then AssetKeys.g.cs is
    /// written from the entries, so code only names assets that exist.
    ///   Menu: LuckyNumbers/Assets/Apply Addressables. Headless: tools/ln.py assets.
    /// WebBuild runs Apply and <see cref="CheckDuplicates"/> before every build; Addressables builds its content
    /// with the player (BuildWithPlayer) and ships it in StreamingAssets/aa next to the game.
    /// </summary>
    public static class AddressablesSetup
    {
        /// <summary>
        /// One group per lifetime (what loads and unloads together), filled from one folder: every asset matching
        /// `filter` there gets address prefix + kebab-case file name. Order = group order in the window.
        /// Labels: the scenes (GameScene names) whose preload should fetch this group (SceneFlow -> AssetPrefetch).
        /// </summary>
        readonly struct Rule
        {
            public readonly string Group, Folder, Filter, Prefix;
            public readonly string[] Labels;
            public Rule(string group, string folder, string filter, string prefix, params string[] labels)
            {
                Group = group; Folder = folder; Filter = filter; Prefix = prefix; Labels = labels;
            }
        }

        static readonly Rule[] Rules =
        {
            // SceneBuilder.BakeWeapons; loaded by WeaponRack in the scenes with hands (fetched while the one before plays).
            new("Weapons", "Assets/_Project/Prefabs/Weapons", "t:Prefab", "weapon/", "Planet", "Station"),
            // SPIKE: delete with the AddressablesSmoke folder.
            // Labelled Station so Title's preload (its next scene) downloads it: the smoke test exercises prefetch.
            new("Spikes", "Assets/_Project/Spikes/AddressablesSmoke", "t:Prefab", "spike/", "Station"),
        };

        const string KeysPath = "Assets/_Project/Scripts/Core/Assets/AssetKeys.g.cs";

        [MenuItem("LuckyNumbers/Assets/Apply Addressables")]
        public static void ApplyMenu()
        {
            if (Apply()) Debug.Log($"[Addressables] {KeysPath} changed; Unity recompiles");
        }

        /// <summary>tools/ln.py assets (batch mode).</summary>
        public static void ApplyBatch() => Apply();

        /// <summary>Settings, groups and entries to match <see cref="Rules"/>; true if AssetKeys.g.cs changed.</summary>
        public static bool Apply()
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            settings.BuildRemoteCatalog = false;   // everything ships with the game (itch); a CDN is a profile change later
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.BuildWithPlayer;

            var ruled = new HashSet<string>();
            foreach (var rule in Rules)
            {
                var group = Group(settings, rule.Group);
                ruled.Add(group.Name);
                var want = new Dictionary<string, string>();   // guid -> address
                if (AssetDatabase.IsValidFolder(rule.Folder))
                    foreach (var guid in AssetDatabase.FindAssets(rule.Filter, new[] { rule.Folder }))
                        want[guid] = rule.Prefix + Kebab(Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid)));
                foreach (var stale in group.entries.Where(e => !want.ContainsKey(e.guid)).Select(e => e.guid).ToList())
                    settings.RemoveAssetEntry(stale, false);
                foreach (var (guid, address) in want)
                {
                    var entry = settings.CreateOrMoveEntry(guid, group, false, false);
                    if (entry.address != address) entry.SetAddress(address, false);
                    foreach (var label in entry.labels.Where(l => !rule.Labels.Contains(l)).ToList())
                        entry.SetLabel(label, false, false, false);
                    foreach (var label in rule.Labels)
                    {
                        settings.AddLabel(label, false);
                        entry.SetLabel(label, true, false, false);
                    }
                }
            }
            // A group no rule names anymore (its folder's rule was deleted): its entries go with it.
            foreach (var group in settings.groups.Where(g => g != null && !g.IsDefaultGroup() && !ruled.Contains(g.Name) &&
                                                             g.HasSchema<BundledAssetGroupSchema>() && g.entries.Count > 0).ToList())
                foreach (var stale in group.entries.Select(e => e.guid).ToList())
                    settings.RemoveAssetEntry(stale, false);

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return WriteKeys(settings);
        }

        static AddressableAssetGroup Group(AddressableAssetSettings settings, string name)
        {
            var group = settings.FindGroup(name) ??
                        settings.CreateGroup(name, false, false, false, null, typeof(BundledAssetGroupSchema));
            var bundled = group.GetSchema<BundledAssetGroupSchema>() ?? group.AddSchema<BundledAssetGroupSchema>();
            bundled.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            bundled.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            bundled.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;   // one bundle per lifetime
            // LZ4: itch serves bundles without Content-Encoding, and LZ4 loads without a full decompress (LZMA doesn't).
            bundled.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            EditorUtility.SetDirty(bundled);
            return group;
        }

        /// <summary>
        /// Fail the build if a scene in it depends on an addressable asset: that asset would ship twice (once in the
        /// player data, once in its bundle) and load as two separate copies. Scenes must hold keys, not references.
        /// </summary>
        public static void CheckDuplicates(IEnumerable<string> scenes)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;
            var inScenes = new HashSet<string>(AssetDatabase.GetDependencies(scenes.ToArray(), true));
            var twice = settings.groups.Where(g => g != null).SelectMany(g => g.entries)
                                .Where(e => inScenes.Contains(e.AssetPath)).Select(e => $"{e.address} ({e.AssetPath})").ToList();
            if (twice.Count > 0)
                throw new BuildFailedException("Addressable assets also referenced by scenes (would ship twice); " +
                                               "load them through AssetScope instead:\n  " + string.Join("\n  ", twice));
        }

        /// <summary>AssetKeys.g.cs from every ruled entry; true if it changed.</summary>
        static bool WriteKeys(AddressableAssetSettings settings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated> by Editor/AddressablesSetup.cs (LuckyNumbers/Assets/Apply Addressables, tools/ln.py assets).");
            sb.AppendLine("// Don't edit: change AddressablesSetup.Rules or the assets, then Apply.");
            sb.AppendLine("namespace LuckyNumbers.Core");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Every addressable asset, by address (see AssetScope).</summary>");
            sb.AppendLine("    public static class AssetKeys");
            sb.AppendLine("    {");
            var entries = Rules.Select(r => settings.FindGroup(r.Group)).Where(g => g != null)
                               .SelectMany(g => g.entries).OrderBy(e => e.address, StringComparer.Ordinal);
            var names = new HashSet<string>();
            foreach (var e in entries)
            {
                var name = Pascal(e.address);
                if (!names.Add(name)) throw new BuildFailedException($"[Addressables] two addresses make key {name}: rename one");
                var type = e.MainAssetType ?? typeof(UnityEngine.Object);
                sb.AppendLine($"        public static readonly AssetKey<global::{type.FullName}> {name} = new(\"{e.address}\");");
            }
            // Per rule, by file name: for assets content names (an item's `model`), AssetKeys.Weapon["rifle_block"].
            foreach (var rule in Rules)
            {
                var group = settings.FindGroup(rule.Group);
                if (group == null || group.entries.Count == 0) continue;
                var ofRule = group.entries.OrderBy(e => e.address, StringComparer.Ordinal).ToList();
                var type = ofRule[0].MainAssetType ?? typeof(UnityEngine.Object);
                if (ofRule.Any(e => e.MainAssetType != ofRule[0].MainAssetType)) continue;   // mixed types: constants only
                var dict = Pascal(rule.Prefix.TrimEnd('/'));
                if (!names.Add(dict)) throw new BuildFailedException($"[Addressables] lookup {dict} clashes with a key: rename one");
                sb.AppendLine();
                sb.AppendLine($"        /// <summary>{rule.Group}, by file name.</summary>");
                sb.AppendLine($"        public static readonly System.Collections.Generic.IReadOnlyDictionary<string, AssetKey<global::{type.FullName}>> {dict} =");
                sb.AppendLine($"            new System.Collections.Generic.Dictionary<string, AssetKey<global::{type.FullName}>>");
                sb.AppendLine("            {");
                foreach (var e in ofRule)
                    sb.AppendLine($"                [\"{Path.GetFileNameWithoutExtension(e.AssetPath)}\"] = {Pascal(e.address)},");
                sb.AppendLine("            };");
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");
            var text = sb.ToString().Replace("\r\n", "\n");
            if (File.Exists(KeysPath) && File.ReadAllText(KeysPath) == text) return false;
            File.WriteAllText(KeysPath, text);
            AssetDatabase.ImportAsset(KeysPath);
            return true;
        }

        /// <summary>"SmokeCube" / "smoke_cube" -> "smoke-cube".</summary>
        static string Kebab(string name)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (c is '_' or ' ' or '.') { sb.Append('-'); continue; }
                if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>"spike/smoke-cube" -> "SpikeSmokeCube".</summary>
        static string Pascal(string address)
        {
            var sb = new StringBuilder();
            foreach (var part in address.Split('/', '-'))
                if (part.Length > 0) sb.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }
    }
}
