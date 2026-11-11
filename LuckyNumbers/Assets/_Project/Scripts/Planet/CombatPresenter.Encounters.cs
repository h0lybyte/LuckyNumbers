using System.Collections.Generic;
using LuckyNumbers.Model;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Dice-only encounters (content/encounters.txtpb): standoffs with some of the site's monsters, and finds (caches,
    /// a flight recorder, remains) near the wreck. Each is d20 + a skill (the character sheet) against a DC, natural
    /// 20 / 1 always succeed / fail.
    ///
    /// A standoff stops time on a card (EncounterState): pick a choice (EncounterChoice), see the roll, "continue"
    /// applies the outcome and lets time run again. A find just happens as you walk up: it rolls, what it held goes
    /// into your pack (or the failure bites), and a toast (ItemHandled) shows the roll and the result.
    /// </summary>
    public sealed partial class CombatPresenter
    {
        sealed class Find
        {
            public EncounterFind Type;
            public Transform View;
            public Vector3 Position;
            public bool Done;
        }

        sealed class Card
        {
            public Monster Monster;
            public List<(string id, string label, string skill, int bonus, int dc, EncounterOption option)> Options = new();
            public (string id, bool success, EncounterOption option)? Rolled;
        }

        readonly EncounterState _encounter;
        readonly ISubscriber<EncounterChoice> _choices;
        readonly List<Find> _finds = new();
        Card _card;
        string _choice;
        int _danger = 1;

        static EncounterConfig E => GameContent.Encounters;
        float StandoffDistance => E != null && E.StandoffDistance > 0f ? E.StandoffDistance : 12f;
        int SurpriseBonus => E?.SurpriseBonus ?? 0;

        void StartEncounters()
        {
            _encounter.Open.Value = false;
            _subscriptions.Add(_choices.Subscribe(e => _choice = e.Option));
        }

        void SpawnEncounters(int danger, Vector3 wreck)
        {
            _danger = danger;
            if (E == null) return;
            // Standoffs: the first few monsters wait for you instead of hunting.
            var standoffs = math.min(_monsters.Count, E.Standoffs + (int)math.floor(E.StandoffsPerDanger * danger));
            for (var i = 0; i < standoffs; i++) _monsters[i].Mind |= Mind.Standoff;
            // Finds: placeholder blocks in a ring round the wreck.
            for (var i = 0; i < E.Finds && E.FindTypes.Count > 0; i++)
            {
                var type = PickFind();
                var at = wreck + Quaternion.Euler(0f, _rng.NextFloat(360f), 0f) * Vector3.forward *
                         _rng.NextFloat(E.FindMin, math.max(E.FindMin + 1f, E.FindMax));
                var size = new Vector3(type.Size.X > 0f ? type.Size.X : 0.6f, type.Size.Y > 0f ? type.Size.Y : 0.4f, type.Size.Z > 0f ? type.Size.Z : 0.6f);
                at.y = _ground.SurfaceAt(at) + size.y * 0.5f;
                var block = MonsterView.Block($"PLACEHOLDER_find_{type.Id}", size, new Color(type.Color.R, type.Color.G, type.Color.B), _bodyMaterial);
                block.SetPositionAndRotation(at, Quaternion.Euler(0f, _rng.NextFloat(360f), 0f));
                _finds.Add(new Find { Type = type, View = block, Position = at });
            }
            Debug.Log($"[Encounter] {standoffs} standoff(s), {_finds.Count} find(s)");
        }

        EncounterFind PickFind()
        {
            var total = 0f;
            foreach (var f in E.FindTypes) total += math.max(0.01f, f.Weight);
            var roll = _rng.NextFloat(total);
            foreach (var f in E.FindTypes)
            {
                roll -= math.max(0.01f, f.Weight);
                if (roll <= 0f) return f;
            }
            return E.FindTypes[0];
        }

        /// <summary>The card, if one is open (true: everything else waits); else open any find you've walked up to.</summary>
        bool TickEncounters()
        {
            if (_card != null)
            {
                var choice = _choice;
                _choice = null;
                if (choice != null) Choose(choice);
                return _card != null;
            }
            _choice = null;
            if (!_firstPerson.Active || E == null) return false;
            var me = _player.Body.transform.position;
            foreach (var f in _finds)
            {
                if (f.Done) continue;
                var d = Vector2.Distance(new Vector2(f.Position.x, f.Position.z), new Vector2(me.x, me.z));
                if (d < math.max(1f, E.FindDistance)) OpenFind(f);
            }
            return false;
        }

        void OpenStandoff(Monster m)
        {
            var card = new Card { Monster = m };
            foreach (var o in E.Options) card.Options.Add(Option(o.Id, o.Label, o.Skill, o.Dc, o.DcPerDanger, o));
            Open(card, "standoff", Fill(E.StandoffTitle, m), Fill(E.StandoffText, m));
            Debug.Log($"[Encounter] standoff: {m.Type.Name}");
        }

        /// <summary>
        /// Walked up to a find: roll its check (no card, no choice), then loot it or take the failure, and say how it
        /// went in a toast. Same roll as the card's: d20 + the skill vs its DC, natural 20 / 1 always succeed / fail.
        /// </summary>
        void OpenFind(Find f)
        {
            var (_, _, skill, bonus, dc, _) = Option("open", "", f.Type.Skill, f.Type.Dc, 0f, null);
            bool success;
            string roll;
            if (string.IsNullOrEmpty(skill))
            {
                success = true;
                roll = "";
            }
            else
            {
                var d20 = _rng.NextInt(1, 21);
                var total = d20 + bonus;
                success = d20 == 20 || (d20 != 1 && total >= dc);
                var call = d20 == 20 ? "natural 20" : d20 == 1 ? "natural 1" : success ? "success" : "failure";
                roll = $"{skill} d20 {d20} {(bonus >= 0 ? "+" : "-")} {Mathf.Abs(bonus)} = {total} vs DC {dc}: {call}. ";
                Debug.Log($"[Encounter] {f.Type.Name}: {skill} d20 {d20} + {bonus} = {total} vs DC {dc}: {(success ? "success" : "failure")}");
            }
            var full = Loot(f, success);
            var text = success ? f.Type.SuccessText : f.Type.FailureText;
            if (full.Count > 0) text += $" No room for {string.Join(", ", full)}.";
            _handled.Publish(new ItemHandled(f.Type.Name, success ? "opened" : "failed to open", roll + text));
        }

        (string, string, string, int, int, EncounterOption) Option(string id, string label, string skill, int dc, float perDanger, EncounterOption option)
        {
            if (string.IsNullOrEmpty(skill)) return (id, label, "", 0, 0, option);
            var (name, bonus) = Skill(skill);
            return (id, label, name, bonus, dc + (int)math.floor(perDanger * _danger), option);
        }

        void Open(Card card, string kind, string title, string text)
        {
            _card = card;
            _firstPerson.Lock(ControlLock.Encounter);   // time stands still: no walking about behind the card
            _encounter.Kind.Value = kind;
            _encounter.Title.Value = title;
            _encounter.Text.Value = text;
            _encounter.Options.Clear();
            foreach (var o in card.Options) _encounter.Options.Add($"{o.id}|{o.label}|{o.skill}|{o.bonus}|{o.dc}");
            _encounter.Rolled.Value = false;
            _encounter.Result.Value = "";
            _encounter.Open.Value = true;
        }

        void Choose(string id)
        {
            var card = _card;
            if (card.Rolled != null)
            {
                if (id == "continue") Close();
                return;
            }
            var pick = card.Options.Find(o => o.id == id);
            if (pick.id == null) return;
            // The roll (no skill: no roll, it just happens).
            bool success;
            if (string.IsNullOrEmpty(pick.skill))
            {
                success = true;
                _encounter.D20.Value = 0;
            }
            else
            {
                var d20 = _rng.NextInt(1, 21);
                var total = d20 + pick.bonus;
                success = d20 == 20 || (d20 != 1 && total >= pick.dc);
                _encounter.D20.Value = d20;
                _encounter.Total.Value = total;
                Debug.Log($"[Encounter] {pick.label}: {pick.skill} d20 {d20} + {pick.bonus} = {total} vs DC {pick.dc}: {(success ? "success" : "failure")}");
            }
            _encounter.Skill.Value = pick.skill;
            _encounter.Bonus.Value = pick.bonus;
            _encounter.Dc.Value = pick.dc;
            _encounter.Success.Value = success;
            card.Rolled = (pick.id, success, pick.option);
            var o = pick.option;
            _encounter.Result.Value = Fill(success ? o?.SuccessText : o?.FailureText, card.Monster);
            _encounter.Rolled.Value = true;
        }

        /// <summary>Close the card, doing what the roll decided.</summary>
        void Close()
        {
            var card = _card;
            _card = null;
            _encounter.Open.Value = false;
            _firstPerson.Unlock(ControlLock.Encounter);
            if (card.Rolled is not { } r) return;
            Resolve(card.Monster, r.success ? r.option.Success : r.option.Failure, r.option, r.success);
        }

        void Resolve(Monster m, EncounterOutcome outcome, EncounterOption option, bool success)
        {
            m.Mind &= ~Mind.Standoff;
            switch (outcome)
            {
                case EncounterOutcome.Ignored:
                    m.Mind |= Mind.Ignored;
                    break;
                case EncounterOutcome.Flees:
                    m.Fleeing = 0f;
                    if (m.Type.Xp > 1) _xp.Publish(new CharacterXpGained(m.Type.Xp / 2, $"{m.Type.Name} (scared off)"));
                    break;
                case EncounterOutcome.Calm:
                    m.CalmUntil = Time.time + math.max(1f, option.CalmSeconds);
                    break;
                case EncounterOutcome.Ambushed:
                    m.Mind |= Mind.Aggro;
                    m.Cooldown = 0f;
                    Bite(m);
                    break;
                default:   // Fight
                    m.Mind = (m.Mind & ~Mind.Surprise) | Mind.Aggro | (success ? Mind.Surprise : Mind.None);
                    m.Cooldown = math.max(m.Cooldown, 0.8f);   // a moment to raise your weapon
                    break;
            }
            Debug.Log($"[Encounter] {m.Type.Name}: {outcome}");
        }

        /// <summary>A find's outcome: on a success what it held into your pack (returns what didn't fit), else its damage.</summary>
        List<string> Loot(Find f, bool success)
        {
            var full = new List<string>();
            f.Done = true;
            if (f.View != null) Object.Destroy(f.View.gameObject);
            if (success)
            {
                foreach (var id in f.Type.Items)
                {
                    if (_pack.TryAdd(id)) continue;
                    var name = GameContent.Items?.Items.Find(d => d.Id == id)?.Name ?? id;
                    _pack.Say($"No room for the {name}");
                    full.Add(name);
                }
                if (f.Type.Credits > 0) _voyage.Credits.Value += f.Type.Credits;
                if (f.Type.Xp > 0) _xp.Publish(new CharacterXpGained(f.Type.Xp, f.Type.Name));
                Debug.Log($"[Encounter] looted {f.Type.Name}: {string.Join(", ", f.Type.Items)} +{f.Type.Credits} cr");
            }
            else if (!string.IsNullOrEmpty(f.Type.FailureDamage)) TakeDamage(Dice.Roll(f.Type.FailureDamage, ref _rng), f.Type.Name);
            return full;
        }

        /// <summary>A skill's name and bonus off the character sheet ("id|name|abbr|bonus|trained").</summary>
        (string name, int bonus) Skill(string id)
        {
            foreach (var s in _sheet.Skills)
            {
                var parts = s.Split('|');
                if (parts.Length >= 4 && parts[0] == id) return (parts[1], int.TryParse(parts[3], out var b) ? b : 0);
            }
            return (id, 0);
        }

        /// <summary>Dev (the page's inspector): a standoff monster 8 m ahead, or a find at your feet.</summary>
        void DevEncounter(string what)
        {
            if (E == null) return;
            EnsureBolts();
            var body = _player.Body.transform;
            var ahead = Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized;
            if (what == "standoff" && C.Monsters.Count > 0)
            {
                Spawn(Pick(_danger), body.position + ahead * 8f, false);
                _monsters[^1].Mind |= Mind.Standoff;
            }
            else if (what == "find" && E.FindTypes.Count > 0)
            {
                var type = PickFind();
                var at = body.position + ahead * 1.2f;
                at.y = _ground.SurfaceAt(at) + 0.25f;
                var block = MonsterView.Block($"PLACEHOLDER_find_{type.Id}", new Vector3(0.6f, 0.4f, 0.6f), new Color(type.Color.R, type.Color.G, type.Color.B), _bodyMaterial);
                block.position = at;
                _finds.Add(new Find { Type = type, View = block, Position = at });
            }
            Debug.Log($"[Encounter] dev: {what}");
        }

        static string Fill(string s, Monster m) => (s ?? "").Replace("{monster}", m.Type.Name);
    }
}
