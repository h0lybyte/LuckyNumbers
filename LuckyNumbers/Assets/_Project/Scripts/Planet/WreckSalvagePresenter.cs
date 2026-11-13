using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Intro;
using LuckyNumbers.Model;
using MessagePipe;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// A Salvage job's wreck (content/cargo.txtpb salvage_*): walk up to its hull and E flags it for processing, which
    /// meets the job's goal (paid back at the station); the lines send you back to your ship. Relics lying out still
    /// sell on top. Other jobs: the wreck's just scenery (a Retrieve wants its one relic; a Clear has no wreck).
    /// Dev: "salvage" starts a practice Salvage job here if none is active (RelicsPresenter walks you to the wreck).
    /// </summary>
    [Presenter]
    public sealed class WreckSalvagePresenter : IInteractable, IStartable, IDisposable
    {
        static readonly object Flag = new(), Flagged = new();   // what's on offer: E flags it / it's flagged (said only)

        readonly ICrashSite _site;
        readonly PlanetView _view;
        readonly PlayerJob _job;
        readonly ISubscriber<DevWarpRequested> _warps;
        readonly CancellationTokenSource _cts = new();
        IDisposable _subscription;
        bool _flagged, _measured;
        Bounds _hull;

        static CargoConfig Config => GameContent.Cargo;

        [Inject]
        public WreckSalvagePresenter(ICrashSite site, PlanetView view, PlayerJob job, ISubscriber<DevWarpRequested> warps)
        {
            _site = site;
            _view = view;
            _job = job;
            _warps = warps;
        }

        public void Start() => _subscription = _warps.Subscribe(e => { if (e.To == "salvage") Practice(); });

        bool OnSalvageJob => _job.Active.Value && _job.Kind.Value == JobKind.Salvage;

        public bool Offer(Vector3 feet, out Interaction offer)
        {
            offer = default;
            if (!OnSalvageJob || !_site.Built || !_site.HasWreck || string.IsNullOrEmpty(Config.SalvagePrompt)) return false;
            var wreck = _view.Wreck;
            if (wreck == null || !wreck.Fitted) return false;
            if (!_measured) { _hull = wreck.Hull; _measured = true; }   // it lies still: its bounds once, with its hull in
            // From the hull, not the middle: a long freighter can be reached from any side.
            var flat = new Vector3(feet.x, _hull.center.y, feet.z);
            var distance = Vector3.Distance(_hull.ClosestPoint(flat), flat);
            if (distance > (Config.SalvageDistance > 0f ? Config.SalvageDistance : 6f)) return false;
            offer = _flagged ? new Interaction(Flagged, InteractionPriority.Info, distance, usable: false)
                             : new Interaction(Flag, InteractionPriority.Job, distance);
            return true;
        }

        public string Prompt(object key) => key == Flag ? Config.SalvagePrompt : Config.SalvagedPrompt;

        public void Interact(object key)
        {
            if (key != Flag || _flagged) return;
            _flagged = true;
            if (!_job.Done.Value) _job.Done.Value = true;   // a relic picked up may have met it already
            Debug.Log($"[Salvage] wreck flagged for processing: {_job.Title.Value} done");
            SayAsync(Config.SalvageLines, _cts.Token).Forget();
        }

        async UniTaskVoid SayAsync(List<Dialogue> lines, CancellationToken ct)
        {
            try
            {
                foreach (var line in lines) await DialogueLine.Play(line, _view.Dialogue, ct);
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>Dev: a practice Salvage job (none active, e.g. ?ln-planet), so the wreck can be flagged.</summary>
        void Practice()
        {
            if (_job.Active.Value || !_site.HasWreck) return;
            _job.Kind.Value = JobKind.Salvage;
            _job.Title.Value = "Practice salvage";
            _job.Objective.Value = "Flag the wreck for salvage (E beside it), then fly back to the station";
            _job.Client.Value = "Dispatch";
            _job.Pay.Value = 0;
            _job.Target.Value = "";
            _job.Done.Value = _job.Failed.Value = false;
            _job.Active.Value = true;
            Debug.Log("[Salvage] dev: practice salvage job");
        }

        public void Dispose()
        {
            _subscription?.Dispose();
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
