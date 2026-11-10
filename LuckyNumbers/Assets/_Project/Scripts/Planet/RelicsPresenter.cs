using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Cargo;
using LuckyNumbers.Core;
using LuckyNumbers.Intro;
using LuckyNumbers.Model;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// The freighter's relics at the crash site (content/cargo.txtpb `relics`, the same manifest the MAYDAY read out):
    /// laid out between the wreck and the landing pad, glowing. Walk up and E picks one up into the pack (it tells you
    /// whose it was, and earns XP); one you already carry (relics are unique) isn't laid out again. Using a relic is an
    /// ordinary pack use (Core/InventoryPresenter rolls its d20; R uses the ready one); this does the effects only the
    /// crash site can (ItemEffectRequested: slowed, a beam over the nearest relic or all of them), says what the roll
    /// did, and shows how many lie out and a revealed one's bearing (RelicSite).
    /// It's also where the pack drops things (IDropTarget): a dropped item lies at your feet as a placeholder block
    /// (PLACEHOLDER_&lt;item id&gt;, sized from its grid shape, until it has a model) and E picks it back up.
    /// </summary>
    [Presenter]
    public sealed class RelicsPresenter : IDropTarget, IStartable, ITickable, IDisposable
    {
        sealed class Dropped
        {
            public DroppedItem Item;
            public Transform Shape;
            public Vector3 Rest;
        }

        sealed class Lying
        {
            public Relic Relic;
            public Transform Shape, Beam;
            public Vector3 Rest;
            public bool Taken, Target;   // Target: the job's (Retrieve), its beam always on
        }

        readonly ICrashSite _site;
        readonly IFighter _fighter;
        readonly PlayerJob _job;
        readonly PlanetView _view;
        readonly PlayerView _player;
        readonly GroundView _ground;
        readonly IFirstPerson _firstPerson;
        readonly IInventory _pack;
        readonly PlayerInventory _packState;
        readonly ISubscriber<ItemEffectRequested> _itemEffects;
        readonly IPublisher<CharacterXpGained> _xp;
        readonly RelicSite _relicSite;
        readonly ISubscriber<RelicRolled> _rolled;
        readonly ISubscriber<InputActionPerformed> _actions;
        readonly ISubscriber<DevWarpRequested> _warps;
        readonly List<IDisposable> _subscriptions = new();
        readonly List<Lying> _lying = new();
        readonly List<Dropped> _dropped = new();
        readonly CancellationTokenSource _cts = new();
        static readonly int ColorA = Shader.PropertyToID("_ColorA");
        static readonly int ColorB = Shader.PropertyToID("_ColorB");

        CargoConfig Config => GameContent.Cargo;
        bool _spawned, _pickup;
        float _slowUntil, _revealUntil;
        bool _revealAll;
        string _prompt = "";

        [Inject]
        public RelicsPresenter(ICrashSite site, PlanetView view, PlayerView player, GroundView ground, IFirstPerson firstPerson,
                               IPublisher<CharacterXpGained> xp, IInventory pack, PlayerInventory packState,
                               ISubscriber<ItemEffectRequested> itemEffects, RelicSite relicSite, ISubscriber<RelicRolled> rolled,
                               ISubscriber<InputActionPerformed> actions, ISubscriber<DevWarpRequested> warps, IFighter fighter, PlayerJob job)
        {
            _job = job;
            _fighter = fighter;
            _warps = warps;
            _site = site;
            _view = view;
            _player = player;
            _ground = ground;
            _firstPerson = firstPerson;
            _pack = pack;
            _packState = packState;
            _itemEffects = itemEffects;
            _xp = xp;
            _relicSite = relicSite;
            _rolled = rolled;
            _actions = actions;
        }

        public void Start()
        {
            _relicSite.Lying.Value = 0;
            _relicSite.Reveal.Value = "";
            _subscriptions.Add(_actions.Subscribe(e =>
            {
                if (e.Action == InputAction.Interact) _pickup = true;
            }));
            _subscriptions.Add(_warps.Subscribe(e => Warp(e.To)));
            // Relic rolls and pack consumables (ping beacon, flare): the effects only the crash site can do.
            _subscriptions.Add(_itemEffects.Subscribe(e => Apply(e.Effect, e.Amount, e.Seconds)));
            // What a roll did, said out loud here.
            _subscriptions.Add(_rolled.Subscribe(e =>
                DialogueLine.Play(new Dialogue { Speaker = $"{e.Relic}: {e.Total} - {e.Title}", Text = e.Text, DurationSeconds = 4f, CharsPerSecond = 50f },
                                  _view.Dialogue, _cts.Token).Forget()));
            _pack.DropTarget = this;
        }

        public void Tick()
        {
            var dt = Time.deltaTime;
            // Once the wreck is down and the landing pad placed (the named artifacts lie on its way); no pad: once on foot.
            var padReady = _view.LandingPad != null && _view.LandingPad.gameObject.activeSelf;
            if (!_spawned && _site.Built && _view.Wreck != null && _view.Wreck.Pivot.gameObject.activeInHierarchy &&
                (padReady || _firstPerson.Active)) Spawn();
            if (!_spawned) return;

            // Lying relics turn slowly and bob, so they read as something to pick up.
            foreach (var l in _lying)
            {
                if (l.Taken) continue;
                l.Shape.position = l.Rest + Vector3.up * (0.08f * math.sin(Time.time * 1.6f + l.Relic.Seed % 7));
                l.Shape.rotation = Quaternion.Euler(0f, Time.time * 40f + l.Relic.Seed % 360, 0f);
            }

            foreach (var d in _dropped)
                d.Shape.SetPositionAndRotation(d.Rest + Vector3.up * (0.05f * math.sin(Time.time * 2f + d.Item.Def.Id.Length)),
                                               Quaternion.Euler(0f, Time.time * 30f, 0f));

            var onFoot = _firstPerson.Active;
            var me = _player.Body.transform.position;
            Lying near = null;
            Dropped nearDrop = null;
            if (onFoot)
            {
                var best = Config.PickupDistance > 0f ? Config.PickupDistance : 2.2f;
                foreach (var l in _lying)
                {
                    if (l.Taken) continue;
                    var d = Vector2.Distance(new Vector2(l.Rest.x, l.Rest.z), new Vector2(me.x, me.z));
                    if (d < best) { best = d; near = l; }
                }
                foreach (var dr in _dropped)
                {
                    var d = Vector2.Distance(new Vector2(dr.Rest.x, dr.Rest.z), new Vector2(me.x, me.z));
                    if (d < best) { best = d; nearDrop = dr; near = null; }
                }
            }
            // E: a relic within reach first; else your landed ship, if you're beside it (flies you back out).
            var board = onFoot && near == null && nearDrop == null ? _fighter.BoardPrompt(me) : "";
            if (_pickup && near != null) Take(near);
            else if (_pickup && nearDrop != null) PickUp(nearDrop);
            else if (_pickup && board != "") _fighter.Board();
            _pickup = false;

            // Effects that last.
            var load = _packState.Speed.Value > 0f ? _packState.Speed.Value : 1f;   // a heavy pack slows you
            _firstPerson.SpeedScale = (Time.time < _slowUntil ? _slowScale : 1f) * load;
            UpdateReveal(me);

            var ready = _packState.ReadyName.Value;
            var prompt = nearDrop != null ? Fill(Config.PickupPrompt, Label(nearDrop.Item))
                       : near != null ? Fill(Config.PickupPrompt, near.Relic.Name)
                       : board != "" ? board
                       : !string.IsNullOrEmpty(ready) && onFoot && _packState.Cooldown.Value <= 0f ? Fill(Config.UsePrompt, ready) : "";
            if (prompt != _prompt)
            {
                _prompt = prompt;
                _view.Hint(prompt, prompt == "" ? 0f : 1f);
            }
        }

        float _slowScale = 1f;

        /// <summary>Lay the manifest's relics out between the wreck and the pad, on the ground (not ones you carry).</summary>
        void Spawn()
        {
            _spawned = true;
            var manifest = Manifest.Generate(Config, _site.Seed);
            var rng = new Random((_site.Seed ^ 0x5E1C5u) | 1u);
            var wreck = _view.Wreck.Pivot.position;
            // Clear of the hull whatever the ship's size: at least the wreck's own footprint (from its renderers) + 4 m.
            var min = math.max(Config.ScatterMin > 0f ? Config.ScatterMin : 10f, WreckReach(wreck) + 4f);
            var max = math.max(min + 1f, Config.ScatterMax);
            var root = new GameObject("Relics").transform;
            // The named artifacts lie on your way in: between the wreck and the landing pad (if there is one).
            var pad = _view.LandingPad != null ? _view.LandingPad.position : wreck + Vector3.forward * 40f;
            var towardPad = new Vector3(pad.x - wreck.x, 0f, pad.z - wreck.z);
            var padAngle = towardPad.sqrMagnitude > 1f ? Mathf.Atan2(towardPad.x, towardPad.z) * Mathf.Rad2Deg : 0f;
            var named = 0;
            foreach (var relic in manifest.Relics)
            {
                if (_pack.Has(relic.Def.Id)) continue;   // unique: already in your pack (brought back from an earlier landing)
                var own = _view.ArtifactModel(relic.Def.Model);   // the relic's own model, if it has one
                var template = own != null ? own : _view.AmuletTemplate;
                if (template == null) continue;
                var distance = named == 0 ? min : rng.NextFloat(min, max);
                var angle = padAngle + 14f * named++;
                var at = wreck + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * distance;
                at.y = _ground.SurfaceAt(at) + 0.35f;   // on the mesh you walk on
                var shape = UnityEngine.Object.Instantiate(template, root);
                shape.gameObject.SetActive(true);
                shape.position = at;
                if (own == null) Colour(shape, relic);   // own models keep their materials
                Transform beam = null;
                if (_view.RelicBeamTemplate != null)
                {
                    beam = UnityEngine.Object.Instantiate(_view.RelicBeamTemplate, root);
                    beam.position = at + Vector3.up * beam.localScale.y;   // a cylinder is 2 high: base at the relic
                    beam.gameObject.SetActive(false);
                }
                var target = _job.Active.Value && _job.Kind.Value == JobKind.Retrieve && relic.Name == _job.Target.Value;
                _lying.Add(new Lying { Relic = relic, Shape = shape, Beam = beam, Rest = at, Target = target });
                if (target && beam != null) beam.gameObject.SetActive(true);
                if (target) Debug.Log($"[Relics] job target {relic.Name}, {Vector3.Distance(at, wreck):0} m from the wreck");
            }
            _relicSite.Lying.Value = _lying.Count;
            Debug.Log($"[Relics] {_lying.Count} relics round the wreck ({manifest.Relics.Count} aboard)");
        }

        /// <summary>How far the wreck reaches out from its pivot on the ground plane (its renderers' bounds).</summary>
        float WreckReach(Vector3 pivot)
        {
            var reach = 0f;
            foreach (var r in _view.Wreck.Pivot.GetComponentsInChildren<Renderer>())
            {
                var b = r.bounds;
                for (var x = -1; x <= 1; x += 2)
                for (var z = -1; z <= 1; z += 2)
                {
                    var corner = new Vector2(b.center.x + x * b.extents.x - pivot.x, b.center.z + z * b.extents.z - pivot.z);
                    reach = math.max(reach, corner.magnitude);
                }
            }
            return reach;
        }

        static void Colour(Transform shape, Relic relic)
        {
            var a = relic.Def.Color.A > 0f ? relic.Def.Color : new Rgba(0.7f, 0.6f, 0.9f, 1f);   // unset: alpha 0
            var b = relic.Def.ColorB.A > 0f ? relic.Def.ColorB : a;
            var block = new MaterialPropertyBlock();
            block.SetColor(ColorA, new Color(a.R, a.G, a.B, 1f));
            block.SetColor(ColorB, new Color(b.R, b.G, b.B, 1f));
            foreach (var r in shape.GetComponentsInChildren<Renderer>()) r.SetPropertyBlock(block);
        }

        void Take(Lying l)
        {
            // Into the pack first: no room (or you already have it), it stays where it lies.
            var def = l.Relic.Def;
            if (!_pack.TryAdd(def.Id))
            {
                var full = _pack.Has(def.Id) ? $"You already have the {def.Name}"
                         : (GameContent.Items.FullPrompt ?? "No room for {name}").Replace("{name}", def.Name);
                DialogueLine.Play(new Dialogue { Speaker = "Pack", Text = full, DurationSeconds = 2.5f, CharsPerSecond = 60f },
                                  _view.Dialogue, _cts.Token).Forget();
                return;
            }
            l.Taken = true;
            l.Shape.gameObject.SetActive(false);
            if (l.Beam != null) l.Beam.gameObject.SetActive(false);
            _relicSite.Lying.Value = _lying.FindAll(x => !x.Taken).Count;
            Debug.Log($"[Relics] picked up {def.Name} ({def.Value} cr)");
            // The job: a Retrieve's target, or any relic for a Salvage, meets its goal (paid back at the station).
            if (_job.Active.Value && !_job.Done.Value &&
                (l.Target || _job.Kind.Value == JobKind.Salvage))
            {
                _job.Done.Value = true;
                Debug.Log($"[Relics] job goal met: {_job.Title.Value}");
            }
            var lore = def.Lore;
            var xp = lore != null ? GameContent.Character.XpPerArtifact : GameContent.Character.XpPerRelic;
            if (xp > 0) _xp.Publish(new CharacterXpGained(xp, def.Name));
            var line = lore != null && !string.IsNullOrEmpty(lore.Text)
                ? new Dialogue { Speaker = $"{def.Name} - {lore.Figure}, {lore.Era}", Text = lore.Text, DurationSeconds = 5f, CharsPerSecond = 45f }
                : new Dialogue { Speaker = "Relic", Text = $"A {def.Name}. Worth {def.Value:N0} credits.", DurationSeconds = 3f, CharsPerSecond = 45f };
            DialogueLine.Play(line, _view.Dialogue, _cts.Token).Forget();
        }

        /// <summary>The effects only the crash site can do (heals happen in the pack).</summary>
        void Apply(RelicEffect effect, float amount, float seconds)
        {
            switch (effect)
            {
                case RelicEffect.Slow:
                    _slowScale = math.clamp(amount, 0.1f, 1f);
                    _slowUntil = Time.time + seconds;
                    break;
                case RelicEffect.RevealNearest:
                    _revealAll = false;
                    _revealUntil = Time.time + seconds;
                    break;
                case RelicEffect.RevealAll:
                    _revealAll = true;
                    _revealUntil = Time.time + seconds;
                    break;
            }
        }

        /// <summary>Beams over the revealed relics (the nearest, or all), and its bearing for the HUD.</summary>
        void UpdateReveal(Vector3 me)
        {
            var on = Time.time < _revealUntil;
            Lying nearest = null;
            var best = float.MaxValue;
            foreach (var l in _lying)
            {
                if (l.Taken) continue;
                var d = Vector3.Distance(l.Rest, me);
                if (d < best) { best = d; nearest = l; }
            }
            foreach (var l in _lying)
                if (l.Beam != null) l.Beam.gameObject.SetActive(!l.Taken && (l.Target || on && (_revealAll || l == nearest)));
            var text = "";
            // Nothing left lying out: the pearl points back to the wreck instead.
            var at = nearest != null ? nearest.Rest : _view.Wreck != null ? _view.Wreck.Pivot.position : me;
            var label = nearest != null ? nearest.Relic.Name : "The wreck";
            if (on && (nearest != null || _view.Wreck != null))
            {
                var to = at - me;
                var bearing = (Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg + 360f) % 360f;
                string[] compass = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
                text = $"{label}: {new Vector2(to.x, to.z).magnitude:0} m {compass[Mathf.RoundToInt(bearing / 45f) % 8]}";
            }
            if (_relicSite.Reveal.Value != text) _relicSite.Reveal.Value = text;
        }

        /// <summary>Dev: on foot beside the nearest named relic ("artifact"), any relic, the wreck, or your ship.</summary>
        void Warp(string to)
        {
            if (!_spawned || !_firstPerson.Active) return;
            var me = _player.Body.transform.position;
            Vector3? target = null;
            var best = float.MaxValue;
            foreach (var l in _lying)
            {
                if (l.Taken || (to == "artifact" && l.Relic.Def.Lore == null)) continue;
                var d = Vector3.Distance(l.Rest, me);
                if (d < best) { best = d; target = l.Rest; }
            }
            if (to == "wreck" || target == null) target = _view.Wreck.Pivot.position + Vector3.right * 14f;
            var at = target.Value + Vector3.back * 1.2f;
            if (to == "ship" && _fighter.BoardingSpot is Vector3 spot) { at = spot; target = spot + (spot - me).normalized; }
            at.y = _ground.SurfaceAt(at) + 0.15f;
            var body = _player.Body;
            var face = target.Value - at;
            var yaw = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
            body.enabled = false;
            body.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
            body.enabled = true;
            _firstPerson.Look(yaw, 35f);   // facing it, looking down at it
            Debug.Log($"[Relics] dev warp to {to}");
        }

        static string Fill(string text, string name) => (text ?? "").Replace("{name}", name);

        // ---- dropped items --------------------------------------------------------------------------------------

        static string Label(DroppedItem item) => item.Count > 1 ? $"{item.Name} x{item.Count}" : item.Name;

        public bool CanDrop => _firstPerson.Active;

        /// <summary>IDropTarget: a placeholder block at your feet, a step ahead, on the ground.</summary>
        public void Drop(DroppedItem item)
        {
            var body = _player.Body.transform;
            var ahead = Vector3.ProjectOnPlane(body.forward, Vector3.up);
            if (ahead.sqrMagnitude < 0.01f) ahead = Vector3.forward;
            var at = body.position + ahead.normalized * 1.3f + Quaternion.Euler(0f, _dropped.Count * 47f, 0f) * Vector3.right * 0.3f;
            // Its footprint: the grid shape's size, a quarter metre a cell.
            var rows = string.IsNullOrEmpty(item.Def.Shape) ? new[] { "#" } : item.Def.Shape.Split('/');
            var w = 1;
            foreach (var r in rows) w = Math.Max(w, r.Length);
            var size = new Vector3(w * 0.25f, 0.18f, rows.Length * 0.25f);
            at.y = _ground.SurfaceAt(at) + size.y * 0.5f + 0.05f;

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = $"PLACEHOLDER_{item.Def.Id}";   // no model yet: replace with the item's own (docs/items.md)
            UnityEngine.Object.Destroy(cube.GetComponent<Collider>());   // nothing to trip on
            cube.transform.localScale = size;
            cube.transform.position = at;
            var c = item.Def.Color;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", new Color(c.R, c.G, c.B, 1f));
            block.SetColor("_Color", new Color(c.R, c.G, c.B, 1f));
            cube.GetComponent<Renderer>().SetPropertyBlock(block);
            _dropped.Add(new Dropped { Item = item, Shape = cube.transform, Rest = at });
            Debug.Log($"[Relics] dropped {Label(item)} ({cube.name})");
        }

        void PickUp(Dropped d)
        {
            if (!_pack.TryAdd(d.Item))
            {
                var full = (GameContent.Items.FullPrompt ?? "No room for {name}").Replace("{name}", d.Item.Name);
                DialogueLine.Play(new Dialogue { Speaker = "Pack", Text = full, DurationSeconds = 2.5f, CharsPerSecond = 60f },
                                  _view.Dialogue, _cts.Token).Forget();
                return;
            }
            _dropped.Remove(d);
            UnityEngine.Object.Destroy(d.Shape.gameObject);
            Debug.Log($"[Relics] picked up {Label(d.Item)}");
        }

        public void Dispose()
        {
            if (_pack.DropTarget == this) _pack.DropTarget = null;
            _firstPerson.SpeedScale = 1f;
            _cts.Cancel();
            _cts.Dispose();
            foreach (var s in _subscriptions) s.Dispose();
        }
    }
}
