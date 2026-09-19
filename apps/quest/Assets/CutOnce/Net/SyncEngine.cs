using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CutOnce.Core;

namespace CutOnce.Net
{
    /// <summary>
    /// Keeps the headset's BuildStateStore and the server in step, and keeps working when the server is gone.
    /// Start order: the server's current run → the journal from last time → the plan bundled in the app.
    /// A tap is applied locally first (so the hologram answers at once), saved to the journal, then delivered;
    /// undelivered taps are retried on every Flush. Main thread only (Unity's context brings awaits back to it).
    /// </summary>
    public sealed class SyncEngine
    {
        public const string LocalRunId = "asm_local";

        readonly ApiClient _api;
        readonly BuildStateStore _store;
        readonly Journal _journal;
        readonly Func<string> _bundledPlanJson;
        AssemblyDto _assembly;
        string _planJson;
        bool _flushing, _starting;

        public bool Online { get; private set; }
        /// <summary>True when the run in the store is one the server knows, so taps can be delivered.</summary>
        public bool HasServerRun => _assembly != null && _assembly.assembly_id != LocalRunId;
        public int Waiting => _store.IsLoaded ? _store.Provisional.Count() : 0;
        public string StatusLine => Online ? (Waiting == 0 ? "Live" : $"Live · sending {Waiting}") : (Waiting == 0 ? "Offline" : $"Offline · {Waiting} waiting");

        /// <summary>The store now holds a different run or plan: rebuild the hologram.</summary>
        public event Action RunLoaded;
        public event Action<string, int> PlanReady;
        public event Action<DirectorCommandDto> DirectorCommand;
        /// <summary>build_inventory and build_ideas, on the main thread. Build mode listens; the sync engine keeps no build state.</summary>
        public event Action<WsMessageDto> BuildMessage;

        public SyncEngine(ApiClient api, BuildStateStore store, Journal journal, Func<string> bundledPlanJson)
        { _api = api; _store = store; _journal = journal; _bundledPlanJson = bundledPlanJson; }

        public async Task Start()
        {
            if (_starting) return;
            _starting = true;
            try
            {
                var saved = _journal.Load();
                var assembly = await _api.GetCurrentAssembly();
                if (assembly != null)
                {
                    var (plan, json) = await _api.GetPlan(assembly.plan_id, assembly.plan_revision);
                    var page = plan == null ? null : await _api.GetEvents(assembly.assembly_id);
                    if (page != null)
                    {
                        var pending = saved?.assembly?.assembly_id == assembly.assembly_id ? saved.pending : new List<BuildEventDto>();
                        Load(assembly, plan, json, page.events.Concat(pending));
                        Online = true;
                        await Flush();
                        return;
                    }
                }

                Online = false;
                if (_store.IsLoaded) return;                                  // keep what is on screen; a retry will come
                if (saved?.plan_json != null && saved.assembly != null)
                    Load(saved.assembly, CoreJson.Parse<PlanDto>(saved.plan_json), saved.plan_json, saved.events.Concat(saved.pending));
                else if (_bundledPlanJson != null)                             // the app passes none: offline with no journal, it stays empty
                {
                    string json = _bundledPlanJson();
                    var plan = CoreJson.Parse<PlanDto>(json);
                    Load(new AssemblyDto { assembly_id = LocalRunId, plan_id = plan.plan_id, plan_revision = plan.revision, name = "Offline run", seed = "empty", status = "active" },
                         plan, json, Enumerable.Empty<BuildEventDto>());
                }
            }
            finally { _starting = false; }
        }

        void Load(AssemblyDto assembly, PlanDto plan, string planJson, IEnumerable<BuildEventDto> events)
        {
            _assembly = assembly; _planJson = planJson;
            _store.Reset(plan, assembly.assembly_id, events);
            Save();
            RunLoaded?.Invoke();
        }

        void Save()
        {
            try
            {
                _journal.Save(new Journal.Snapshot
                {
                    assembly = _assembly, plan_json = _planJson,
                    events = _store.Events.Where(e => e.version.HasValue).ToList(), pending = _store.Provisional.ToList(),
                });
            }
            catch (System.IO.IOException) { /* a full disk must not stop the demo; the next save tries again */ }
        }

        /// <summary>The operator's tap or a spoken command: shows at once, then goes to the server. Null when there was nothing to change.</summary>
        public BuildEventDto Mark(string partId, string newState, string source = "manual")
        {
            var e = _store.Propose(partId, newState, source);
            if (e == null) return null;
            Save();
            _ = Flush();
            return e;
        }

        /// <summary>Delivers waiting taps oldest first and stops at the first one the server could not be reached for.</summary>
        public async Task Flush()
        {
            if (_flushing || !HasServerRun) return;
            _flushing = true;
            try
            {
                foreach (var e in _store.Provisional.ToList())
                {
                    var outcome = await _api.AppendEvent(_store.AssemblyId, e);
                    if (outcome.Status == AppendStatus.Unreachable) { Online = false; return; }
                    Online = true;
                    if (outcome.Status == AppendStatus.Accepted) _store.ApplyServer(outcome.Event); else _store.DropProvisional(e.event_id);
                }
            }
            finally { _flushing = false; Save(); }
        }

        /// <summary>After a reconnect, or when the stream shows a gap: fetch what was missed.</summary>
        public async Task CatchUp()
        {
            if (!HasServerRun) { await Start(); return; }
            var page = await _api.GetEvents(_store.AssemblyId, _store.Head);
            Online = page != null;
            if (page == null) return;
            foreach (var e in page.events) _store.ApplyServer(e);
            Save();
            await Flush();
        }

        /// <summary>One message from the stream, on the main thread.</summary>
        public void Handle(WsMessageDto m)
        {
            switch (m.type)
            {
                case "event_appended":
                    if (m.@event == null || m.assembly_id != _store.AssemblyId) return;
                    bool gap = m.@event.version.HasValue && m.@event.version.Value > _store.Head + 1 && !_store.Events.Any(e => e.event_id == m.@event.event_id);
                    if (gap) _ = CatchUp(); else if (_store.ApplyServer(m.@event)) Save();
                    break;
                case "assembly_changed":
                    if (m.assembly != null && m.assembly.assembly_id != _store.AssemblyId) _ = Start();
                    break;
                case "plan_ready": PlanReady?.Invoke(m.plan_id, m.revision); break;
                case "director_command": if (m.command != null) DirectorCommand?.Invoke(m.command); break;
                case "build_inventory":
                case "build_ideas": BuildMessage?.Invoke(m); break;
            }
        }
    }
}
