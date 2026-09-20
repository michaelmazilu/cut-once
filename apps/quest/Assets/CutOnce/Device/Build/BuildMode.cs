using System;
using System.Linq;
using System.Threading.Tasks;
using CutOnce.AR;
using CutOnce.Copilot;
using CutOnce.Copilot.Voice;
using CutOnce.Core;
using CutOnce.Core.Build;
using CutOnce.Net;
using CutOnce.UI;
using UnityEngine;

namespace CutOnce.Device
{
    /// <summary>
    /// The Lego Movie mode, composed from the pure BuildFlow and the build visuals. The server does the thinking; this
    /// shows what it sends, lets you pick, locks the chosen design where the server put it, flies the pieces in, and then
    /// hands over to the normal step engine, reading each step aloud.
    /// </summary>
    public sealed class BuildMode : MonoBehaviour
    {
        /// <summary>POST /v1/build/say refuses more than this many characters.</summary>
        const int MaxSpokenCharacters = 400;

        readonly BuildFlow _flow = new BuildFlow();
        ServerConfig _config; ApiClient _api; SyncEngine _sync; BuildStateStore _store;
        AssemblyView _assembly; AlignmentController _alignment; IOperatorInput _input; ISurfaceRaycaster _surface;
        HudController _hud; Material _material; HologramPalette _palette;
        BuildScanCapture _capture; TwinOverlay _twins; IdeaPreviews _previews; FlyTogether _fly;
        string _hovered, _lastStepId;
        /// <summary>Build mode was switched on while a run from the server was showing, so any other run that loads now was started on purpose.</summary>
        bool _overServerRun;
        /// <summary>Where the last design was placed, and which plan that was: another run that arrives while the hologram is still there stands there too.</summary>
        BuildOriginDto _site; string _sitePlanId;
        string _recoveringSession;

        public bool Active => _flow.Active;
        /// <summary>The pieces are between their real objects and the design: the hologram's bounds are half the room, so nothing should be stood against them.</summary>
        public bool PiecesInFlight => _flow.Phase == BuildPhase.Assembling;
        public BuildFlow Flow => _flow;

        public void HighlightTwins(string[] twinIds) => _twins?.Highlight(twinIds, 6f);

        public void Init(ServerConfig config, ApiClient api, SyncEngine sync, BuildStateStore store, AssemblyView assembly, AlignmentController alignment,
                         IOperatorInput input, ISurfaceRaycaster surface, HudController hud, Material material, HologramPalette palette)
        {
            _config = config; _api = api; _sync = sync; _store = store; _assembly = assembly; _alignment = alignment;
            _input = input; _surface = surface; _hud = hud; _material = material; _palette = palette;
            _capture = gameObject.AddComponent<BuildScanCapture>();
            _twins = new GameObject("[BuildTwins]").AddComponent<TwinOverlay>(); _twins.Init(material, palette);
            _previews = new GameObject("[BuildIdeas]").AddComponent<IdeaPreviews>();
            _fly = gameObject.AddComponent<FlyTogether>(); _fly.Finished += OnFlown;
            _sync.BuildMessage += OnBuildMessage;
        }

        void OnDestroy()
        {
            if (_sync != null) _sync.BuildMessage -= OnBuildMessage;
            if (_fly != null) _fly.Finished -= OnFlown;
            if (_twins != null) Destroy(_twins.gameObject);
            if (_previews != null) Destroy(_previews.gameObject);
        }

        /// <summary>While you look at the room and choose, the run that was showing is out of the way; it is back for the build, and when build mode ends.</summary>
        void ShowOrHideHologram()
        {
            bool show = !_flow.HidesHologram;
            if (_assembly.gameObject.activeSelf != show) _assembly.gameObject.SetActive(show);
        }

        // ── scanning ──────────────────────────────────────────────────────────────────────────────────────────────
        /// <summary>The scan button (X), as CutOnceApp reads it: a press scans until a design is chosen; holding it leaves build mode.</summary>
        public void OnScanButton(ButtonGesture gesture)
        {
            if (gesture == ButtonGesture.Hold) { if (_flow.Active) { Exit(); _hud.Toast("Left build mode", 3f); } }
            else if (gesture != ButtonGesture.Press) return;
            else if (_flow.CanScanFromButton) StartScan();
            else _hud.Toast("X is off while you build · hold it to leave build mode", 3f);   // a thumb resting on X must not throw the walkthrough away
        }

        /// <summary>"What can I build?" (and X, and the trigger on empty space): scan this view. Ignored while the pieces are flying.</summary>
        public void StartScan()
        {
            if (_capture.Busy) return;
            bool wasOff = !_flow.Active;
            if (!_flow.StartScan()) return;                                  // the pieces are flying, or the last scan has not been answered yet
            int ticket = _flow.ScanTicket;                                   // names this scan: whatever comes back for another one is stale
            if (wasOff) _overServerRun = _sync.HasServerRun;
            _previews.Clear(); _hovered = null;
            ShowOrHideHologram();
            ScannerRunning(false);                                           // the room scanner's labels stand down: this scan names the objects
            _hud.Toast("Scanning… hold still for a second", 3f);
            var copilot = FindAnyObjectByType<CopilotController>();
            var frames = copilot != null ? copilot.frameSourceBehaviour as ICameraFrameSource : null;
            _capture.Begin(frames, _surface, _config.device_id, scan => Scanned(scan, ticket), error => ScanFailed(error, ticket));
        }

        void Scanned(BuildScanUploadDto scan, int ticket)
        {
            if (!_flow.Active || ticket != _flow.ScanTicket) return;         // build mode was left meanwhile: the scan is dropped, not uploaded
            _hud.Toast("Got it · finding the objects…", 4f);              // the rays are done: you can move again
            Run(Upload(scan, ticket));
        }

        void ScanFailed(string error, int ticket)
        {
            if (ticket != _flow.ScanTicket || !_flow.ScanInFlight) return;   // a scan that was given up: its failure is nobody's news
            _hud.Toast("Couldn't scan: " + error, 4f);
            _flow.ScanFailed(ticket);
            if (_flow.Phase == BuildPhase.Ideas) ShowPreviews();             // a look-around scan that failed: the ideas are still good
            ShowOrHideHologram();
            if (!_flow.Active) ScannerRunning(true);                          // the first scan failed, so build mode is off again: the room is the scanner's
        }

        async Task Upload(BuildScanUploadDto scan, int ticket)
        {
            scan.session_id = _flow.SessionId;                               // as it is now, when the scan leaves: not as it was when the rays started
            var accepted = await _api.PostBuildScan(scan);
            if (this == null) return;
            if (accepted == null) { ScanFailed("the server didn't get the scan", ticket); return; }
            if (_flow.OnScanAccepted(ticket, accepted.session_id))
                Run(RecoverSession(accepted.session_id));                    // the WebSocket is an accelerator, not the only path to blueprints
        }

        // ── what the server sends ─────────────────────────────────────────────────────────────────────────────────
        void OnBuildMessage(WsMessageDto m)
        {
            if (m.type == "build_inventory")
            {
                bool wasOff = !_flow.Active;
                if (!_flow.OnInventory(m.inventory)) return;
                if (wasOff) _overServerRun = _sync.HasServerRun;             // a Director replay starts build mode too
                _twins.Show(m.inventory);
                if (_flow.Phase != BuildPhase.Ideas) { _previews.Clear(); _hovered = null; }   // another session's objects: the old session's ideas went with it
                ShowOrHideHologram();
                if (!string.IsNullOrEmpty(m.inventory.message)) _hud.Toast(m.inventory.message, 5f);
            }
            else if (m.type == "build_ideas")
            {
                if (!_flow.OnIdeas(m.session_id, m.ideas, m.final)) return;
                if (_flow.Phase == BuildPhase.Ideas) ShowPreviews(); else { _previews.Clear(); _hovered = null; }
                if (m.final && !string.IsNullOrEmpty(m.message)) _hud.ShowAnswer(m.message);
                if (m.final && !string.IsNullOrEmpty(m.audio_url)) Play(m.audio_url);
            }
        }

        /// <summary>
        /// The stream (re)connected. It only resends the current run, so labels and ideas sent while the Wi-Fi was down
        /// (a window of up to half a minute) are gone: ask the server for the session and take what was missed through
        /// the same two doors the stream uses.
        /// </summary>
        public void OnStreamReconnected()
        {
            if (!_flow.Active || _catchingUp) return;
            Run(CatchUp());
        }

        bool _catchingUp;

        /// <summary>
        /// Venue Wi-Fi and tunnels can drop the inventory/ideas WebSocket messages after the scan's HTTP 202 already
        /// arrived. Poll the session snapshot while this headset is waiting, so a successful blueprint job cannot be
        /// stranded on the server. BuildFlow validates the session and phase before accepting anything recovered.
        /// </summary>
        async Task RecoverSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || _recoveringSession == sessionId) return;
            _recoveringSession = sessionId;
            try
            {
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    await Task.Delay(1500);
                    if (this == null || !_flow.Active || _flow.SessionId != sessionId || _flow.Phase == BuildPhase.Ideas) return;
                    await CatchUp();
                }
                if (this != null && _flow.Active && _flow.SessionId == sessionId && _flow.Phase != BuildPhase.Ideas)
                    _hud.Toast("The scan reached Kit, but its designs are still delayed. Check the tunnel/Wi-Fi; recovery is still available on reconnect.", 8f);
            }
            finally { if (_recoveringSession == sessionId) _recoveringSession = null; }
        }

        async Task CatchUp()
        {
            _catchingUp = true;
            try
            {
                var snapshot = await _api.GetBuildSession();
                if (this == null) return;
                foreach (var m in _flow.CatchUp(snapshot)) OnBuildMessage(m);
            }
            finally { _catchingUp = false; }
        }

        void ShowPreviews()
        {
            _hovered = null;
            // Above the pile; with no objects on this headset (a run of ideas without its inventory), above where the first design would go.
            if (!_twins.TryGetCentre(out var centre))
            {
                var origin = _flow.Ideas.Count > 0 ? _flow.Ideas[0]?.origin?.position : null;
                centre = origin != null && origin.Length == 3 ? ModelSpace.Point(origin) : Head() + new Vector3(0f, -0.5f, 0.8f);
            }
            _previews.Show(_flow.Ideas, centre, Head(), _material, _palette);
        }

        // ── picking ───────────────────────────────────────────────────────────────────────────────────────────────
        void Update()
        {
            if (_flow.Phase != BuildPhase.Ideas && _flow.Phase != BuildPhase.Labelled) return;
            if (!_input.TryGetPointer(out var ray)) return;
            string hit = _flow.Phase == BuildPhase.Ideas ? _previews.Hit(ray) : null;
            if (hit != _hovered) { _hovered = hit; _previews.Highlight(hit); }
            if (!_input.TriggerDown) return;
            if (hit != null) Run(Pick(hit));
            else if (_flow.CanScanFromTrigger) StartScan();                  // trigger on empty space, with no previews to miss: add this view (D11)
        }

        async Task Pick(string ideaId)
        {
            if (!_flow.Pick(ideaId)) return;
            _hud.Toast("Building: " + _flow.Chosen.title, 3f);
            var outcome = await _api.StartBuildIdea(ideaId);
            if (this == null) return;
            var started = outcome.Started;
            if (started == null)
            {
                if (_flow.Phase != BuildPhase.Starting) return;              // the answer was lost, but the run arrived and is placed
                if (outcome.Gone)
                {
                    // 404: the server's session no longer has these ideas (it restarted, or the Director page opened a new
                    // session). None of them can ever start, so they go; the objects stay, and X scans again.
                    _flow.PickGone();
                    _previews.Clear(); _hovered = null;
                    _hud.Toast("Those ideas are gone. Scan again (X).", 6f);
                    return;
                }
                _hud.Toast("Couldn't start that build", 4f);
                _flow.PickFailed();
                return;
            }
            // The run arrives on the stream (assembly_changed) and OnHologramBuilt places it. If the stream dropped that
            // message, ask for the current run once; SyncEngine.Start ignores the call while a load is already under way.
            if (_store.AssemblyId != started.assembly_id) Run(_sync.Start());
        }

        /// <summary>CutOnceApp calls this after AssemblyView.Build. True when build mode placed the hologram itself.</summary>
        public bool OnHologramBuilt(PlanDto plan)
        {
            if (plan != null && _flow.TryPlace(plan.plan_id))
            {
                _previews.Clear(); _hovered = null;
                _assembly.gameObject.SetActive(true);                        // hidden until now; OnPlaced below makes that the flow's view too
                var origin = _flow.Chosen.origin;
                if (origin?.position != null && origin.position.Length == 3)
                    _alignment.LockAt(new Pose(ModelSpace.Point(origin.position), ModelSpace.Rotation(origin.rotation_quat)), "build");
                _site = origin; _sitePlanId = plan.plan_id;
                _flow.OnPlaced();
                _fly.Play(_assembly, plan, _flow.Chosen, _twins);
                return true;
            }
            // Another run took over (the Director cleared the build, or a design this headset never saw). While a build is
            // under way that always ends build mode. Before that, only when build mode was switched on over a server run: the
            // app's own first load, arriving after an early scan, is not a takeover.
            bool building = _flow.Phase == BuildPhase.Assembling || _flow.Phase == BuildPhase.Walkthrough;
            var site = _flow.Site ?? _site;                                  // read before Exit forgets the ideas
            bool tookOver = _flow.Active && (building || _overServerRun);
            if (tookOver) Exit();
            // The new run stands on the build site, where the viewer is looking: its own origin may be a corner
            // (a plan's often is), and the hologram is where build mode put it or was about to. Not when build mode is still choosing
            // (the run stays hidden), not for the placed design itself coming round again, and not once the operator has
            // placed something by hand since.
            if (!_flow.Active && plan != null && plan.plan_id != _sitePlanId && (tookOver || _alignment.Method == "build")) StandOnTheBuildSite(site);
            return false;
        }

        void StandOnTheBuildSite(BuildOriginDto site)
        {
            if (site?.position == null || site.position.Length != 3) return;
            _alignment.StandAt(ModelSpace.Point(site.position), ModelSpace.Rotation(site.rotation_quat).eulerAngles.y, "build");
        }

        void OnFlown()
        {
            if (_flow.Phase != BuildPhase.Assembling) return;
            _flow.OnAssembled();
            // Keep every scanned object outlined after the design is chosen; leaving build mode owns the cleanup.
            SpeakCurrentStep();
        }

        // ── the walkthrough ───────────────────────────────────────────────────────────────────────────────────────
        /// <summary>
        /// The run in the store is the design that was placed. It is not for a moment when another run takes over: the
        /// store changes first, and the hologram (where build mode steps aside) is rebuilt after, so nothing of that run is
        /// read aloud or marked here.
        /// </summary>
        bool WalkingItsOwnRun => _flow.Phase == BuildPhase.Walkthrough && _store.IsLoaded && _store.Plan.plan_id == _flow.Chosen?.plan?.plan_id;

        /// <summary>A step changed: read the new one aloud, unless it was a voice "done" (whose answer already said it).</summary>
        public void OnStateChanged()
        {
            if (!WalkingItsOwnRun) return;
            var step = _store.Current.current_step_id;
            if (step == _lastStepId) return;
            var last = Reducer.OrderEvents(_store.Events).LastOrDefault();
            if (last != null && last.source == "voice") { _lastStepId = step; return; }
            SpeakCurrentStep();
        }

        void SpeakCurrentStep()
        {
            if (!WalkingItsOwnRun) return;
            _lastStepId = _store.Current.current_step_id;
            var step = _store.Plan.steps.FirstOrDefault(s => s.step_id == _lastStepId);
            var text = step == null ? "That's the whole build. Nice work!" : step.instruction;
            if (string.IsNullOrEmpty(text)) return;
            _hud.ShowAnswer(text);
            Run(Say(SpokenText.Fit(text, MaxSpokenCharacters)));
        }

        async Task Say(string text)
        {
            var said = await _api.BuildSay(text);
            if (this != null && said?.audio_url != null) Play(said.audio_url);
        }

        void Play(string audioUrl)
        {
            var speaker = FindAnyObjectByType<PcmStreamPlayer>();            // the copilot's voice (Rhythm's); none in a scene without a copilot
            if (speaker != null) speaker.Play(_config.BaseUrl, audioUrl, _config.api_token);
        }

        /// <summary>B with nothing pointed at, mid-build: the whole current step is done.</summary>
        public bool MarkCurrentStep()
        {
            if (!WalkingItsOwnRun) return false;
            var step = _store.Plan.steps.FirstOrDefault(s => s.step_id == _store.Current.current_step_id);
            if (step == null) return false;
            foreach (var partId in step.part_ids.ToList())                   // a copy: marking refolds the state while we walk the step
                if (!_store.Current.parts.TryGetValue(partId, out var status) || status.state != "built") _sync.Mark(partId, "built");
            return true;
        }

        public void Exit()
        {
            _capture.Cancel();                                               // a scan still casting rays must not upload after build mode has ended
            _fly.Stop();
            _flow.Exit();
            _twins.Clear(); _previews.Clear();
            _hovered = null; _lastStepId = null;
            ShowOrHideHologram();
            ScannerRunning(true);
        }

        /// <summary>
        /// The room scanner (YOLO on the passthrough camera) runs while the room is yours to look at, and stops while
        /// build mode has it. Both read one camera, so this is about names and frame time, not about who owns it.
        /// </summary>
        static void ScannerRunning(bool running)
        {
            var scanner = CutOnce.Vision.RoomScannerBootstrap.Instance;
            if (scanner != null) scanner.Paused = !running;
        }

        void OnDisable() => ScannerRunning(true);                            // build mode going away must never leave the room quiet

        Vector3 Head() => Camera.main != null ? Camera.main.transform.position : new Vector3(0f, 1.6f, 0f);

        static async void Run(Task task) { try { await task; } catch (Exception e) { Debug.LogException(e); } }
    }
}
