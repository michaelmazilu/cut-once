using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CutOnce.AR;
using CutOnce.Copilot;
using CutOnce.Copilot.Capture;
using CutOnce.Copilot.Voice;
using CutOnce.Core;
using CutOnce.Net;
using CutOnce.UI;
using UnityEngine;

namespace CutOnce.Device
{
    /// <summary>
    /// The whole headset app from one component. Add it to a scene that has an OVRCameraRig and press Play: it
    /// builds the hologram, the HUD, the pointer, placement and sync at runtime, so there are no prefabs or inspector
    /// references to keep in step across machines. It is also the copilot's host (what is selected, what to
    /// highlight, where to show the answer).
    ///
    /// Data flows one way: events → BuildStateStore → Reducer.Fold → VisualStateResolver → PartView. Nothing else
    /// holds build state, and nothing but AlignmentController moves AssemblyRoot.
    /// </summary>
    public sealed class CutOnceApp : MonoBehaviour, ICopilotHost
    {
        /// <summary>Where the run is kept between launches (Application.persistentDataPath). Tests set it aside.</summary>
        public const string JournalFolder = "cutonce-builds";
        /// <summary>What the HUD says while nothing is built: the app shows no hologram until Kit builds one.</summary>
        public const string IdleHint = "Press A to talk to Kit. Press again to send.";

        const float HighlightSeconds = 6f, RetrySeconds = 5f, WrongHoldSeconds = 0.8f, ScanButtonHoldSeconds = 1f;

        [Tooltip("Create the copilot (push-to-talk on A) if the scene has none.")]
        public bool createCopilot = true;

        ServerConfig _config; HologramPalette _palette; ApiClient _api;
        BuildStateStore _store; SyncEngine _sync; StreamClient _stream;
        AssemblyView _assembly; AlignmentController _alignment; ProofOverlay _proof; SelectionController _selection; HudController _hud; QuestInput _input;
        Material _material;
        CutOnce.Room.RoomWorkspace _roomWorkspace;
        readonly HashSet<string> _highlighted = new HashSet<string>();
        float _highlightUntil, _nextRetry, _markHeldFor;
        bool _markUsed, _dirty = true, _hudInFront;
        int _seenConnects; string _lastEventId, _toastRun;
        Action<WsMessageDto> _handleMessage;                          // cached: a method group in Update would allocate a delegate every frame
        bool _waitForMarkRelease;
        BuildMode _build;
        readonly PressOrHold _scanButton = new PressOrHold(ScanButtonHoldSeconds);
        readonly ConcurrentQueue<(string permission, bool granted)> _permissionAnswers = new ConcurrentQueue<(string, bool)>();   // filled from Android's thread

        // ── start-up ─────────────────────────────────────────────────────────────────────────────────────────────
        void Awake()
        {
            _config = LoadConfig();
            _palette = HologramPalette.Parse(Resource("CutOnce/hologram-palette"));
            _material = HologramMaterial.Create();

            _store = new BuildStateStore();
            _store.Changed += OnStateChanged;
            _api = new ApiClient(new UnityHttpTransport(), _config);
            // The app ships no plan: with no server and no journal it opens empty, and the first hologram is one Kit builds.
            // A new journal folder, so a run saved by an older app version never comes back.
            _sync = new SyncEngine(_api, _store, new Journal(Path.Combine(Application.persistentDataPath, JournalFolder)), null);
            _sync.RunLoaded += OnRunLoaded;
            _sync.PlanReady += OnPlanReady;
            _sync.DirectorCommand += OnDirectorCommand;
            _stream = new StreamClient(() => new NetSocket(), _config);
            _handleMessage = _sync.Handle;

            _input = gameObject.AddComponent<QuestInput>();
            _assembly = new GameObject("AssemblyRoot").AddComponent<AssemblyView>();
            _alignment = _assembly.gameObject.AddComponent<AlignmentController>();
            var surface = gameObject.AddComponent<QuestSurfaceRaycaster>();
            _alignment.Init(_assembly, _input, surface, gameObject.AddComponent<QuestAnchorStore>());
            _alignment.Changed += OnAlignmentChanged;
            _proof = gameObject.AddComponent<ProofOverlay>();
            _proof.Init(_alignment, _input);
            _selection = new GameObject("[Pointer]").AddComponent<SelectionController>();
            _selection.Init(_input, _assembly, () => _alignment.State == AlignmentState.Locked, _material);
            _selection.Changed += _ => _dirty = true;
            _hud = HudController.Create(null);
            _hud.ShowStatus("Starting…", IdleHint);
            _roomWorkspace = gameObject.AddComponent<CutOnce.Room.RoomWorkspace>();
            _roomWorkspace.ActiveChanged += active =>
            {
                _input.InputEnabled = !active;
                _alignment.enabled = !active;
                _selection.gameObject.SetActive(!active);
                _assembly.gameObject.SetActive(!active);
                _hud.gameObject.SetActive(!active);
                _waitForMarkRelease = true;
            };
            _hud.Toast("Try asking: What can I build?", 6f);
            // Build mode ("what can I build?"): off until a scan starts it, so other runs behave exactly as before.
            _build = gameObject.AddComponent<BuildMode>();
            _build.Init(_config, _api, _sync, _store, _assembly, _alignment, _input, surface, _hud, _material, _palette);
        }

        void Start()
        {
            Run(_sync.Start());
            _stream.Run();
            if (createCopilot) TryCreateCopilot();
            // AGENTS rule 3: any copilot, built here or placed in the scene (Rhythm's [Copilot] prefab), needs the camera
            // and microphone. Ask on the headset before first use (the Editor grants at once). The camera waits for its
            // grant by itself; a refusal only costs the copilot its eyes or ears, so the HUD says what still works.
            // Depth rays (build mode, and pointing at a real table to place a build) need spatial data, copilot or not.
            // The camera is always wanted: the copilot asks with a photo, and the room scanner names what it sees.
            var wanted = FindAnyObjectByType<CopilotController>() != null
                ? new[] { QuestPermissions.Camera, QuestPermissions.Microphone, QuestPermissions.Scene }
                : new[] { QuestPermissions.Camera, QuestPermissions.Scene };
            QuestPermissions.Request(wanted, (p, ok) => _permissionAnswers.Enqueue((p, ok)));
        }

        void OnDestroy()
        {
            _stream?.Dispose();
            // Sync work still in flight (SyncEngine.Start, a catch-up) must not call back into a destroyed app: that
            // throws MissingReferenceException on a scene reload, and in tests it fails whichever test runs next.
            if (_sync != null) { _sync.RunLoaded -= OnRunLoaded; _sync.PlanReady -= OnPlanReady; _sync.DirectorCommand -= OnDirectorCommand; }
            if (_store != null) _store.Changed -= OnStateChanged;
        }

        static string Resource(string path)
        {
            var asset = Resources.Load<TextAsset>(path);
            if (asset == null) throw new FileNotFoundException($"Resources/{path} is missing from the build");
            return asset.text;
        }

        /// <summary>Server address and token: a file pushed to the headset wins, then a local (git-ignored) Resources file, then localhost.</summary>
        static ServerConfig LoadConfig()
        {
            string pushed = Path.Combine(Application.persistentDataPath, "cutonce.config.json");
            try
            {
                if (File.Exists(pushed)) { Debug.Log("[CutOnce] Server settings from " + pushed); return CoreJson.Parse<ServerConfig>(File.ReadAllText(pushed)); }
                var bundled = Resources.Load<TextAsset>("CutOnce/config");
                if (bundled != null) { Debug.Log("[CutOnce] Server settings from Resources/CutOnce/config.json"); return CoreJson.Parse<ServerConfig>(bundled.text); }
            }
            catch (Exception e) { Debug.LogError("[CutOnce] Could not read the server settings, using localhost: " + e.Message); }
            Debug.LogWarning("[CutOnce] No server settings found; using http://127.0.0.1:8080 (fine in the Editor, useless on the headset). See apps/quest/Assets/CutOnce/README.md.");
            return new ServerConfig();
        }

        /// <summary>Fire-and-forget without losing the exception.</summary>
        static async void Run(Task task)
        {
            try { await task; } catch (Exception e) { Debug.LogException(e); }
        }

        // ── state → hologram and HUD ─────────────────────────────────────────────────────────────────────────────
        void OnRunLoaded() => Run(BuildHologram());

        /// <summary>Fetches the plan's model files (if it has any), then builds. A newer run arriving meanwhile wins.</summary>
        async Task BuildHologram()
        {
            var plan = _store.Plan;
            var models = await PlanModels.Load(_api, plan, _sync.Online);
            if (this == null || plan != _store.Plan) return;
            var skipped = _assembly.Build(plan, models);
            if (skipped.Count > 0) Debug.LogWarning("[CutOnce] Parts with no drawable shape: " + string.Join(", ", skipped));
            _proof.Rebuild(_assembly, _material);
            _dirty = true;
            // A build-mode design is locked where the server put it, which stands the HUD behind it (OnAlignmentChanged)
            // before its pieces fly off to their real objects; standing it again here would measure them mid-flight.
            if (!_build.OnHologramBuilt(plan) && _alignment.State == AlignmentState.Locked) StandHud();
        }

        void OnStateChanged()
        {
            _dirty = true;
            if (_build != null) _build.OnStateChanged();                 // build mode reads a new step aloud
            var last = Reducer.OrderEvents(_store.Events).LastOrDefault();
            if (_store.AssemblyId != _toastRun)                       // a run was just loaded: its history is not news
            {
                _toastRun = _store.AssemblyId; _lastEventId = last?.event_id;
                return;
            }
            if (last == null || last.event_id == _lastEventId || _store.Plan == null) return;
            _lastEventId = last.event_id;
            _hud.Toast(HudText.EventLine(_store.Plan, last, _store.Current));
        }

        /// <summary>No run yet, or the blank run: nothing to draw, place or mark.</summary>
        bool NothingBuilt => !_store.IsLoaded || _store.Plan.parts.Count == 0;

        void OnAlignmentChanged()
        {
            _hud.ShowStatus(_sync.StatusLine, NothingBuilt ? IdleHint : _alignment.Hint);
            // While build mode's pieces fly in from their real objects the hologram's bounds are half the room: the HUD was
            // stood by the lock itself, with every piece at rest, and stays there.
            if (_alignment.State == AlignmentState.Locked) { if (_build == null || !_build.PiecesInFlight) StandHud(); _waitForMarkRelease = true; }   // the B that finished a touch alignment is not a mark
            else _hudInFront = false;                                                                  // placing again: bring the panel back to the operator
        }

        void OnPlanReady(string planId, int revision) => _hud.Toast($"New plan ready: {planId} revision {revision}", 4f);

        void OnDirectorCommand(DirectorCommandDto command)
        {
            // new_run and force_state reach the headset as assembly_changed / event_appended; nothing to do for them here.
            if (command.type == "goto") _hud.Toast("Director: " + command.demo_state);
        }

        void StandHud()
        {
            if (_assembly.Views.Count == 0) return;
            var bounds = new Bounds(); bool any = false;
            foreach (var view in _assembly.Views.Values) { if (!any) { bounds = view.WorldBounds; any = true; } else bounds.Encapsulate(view.WorldBounds); }
            var head = Camera.main != null ? Camera.main.transform.position : bounds.center + Vector3.back + Vector3.up * 1.6f;
            _hud.StandBehind(bounds, head);
        }

        void Refresh()
        {
            _dirty = false;
            if (!_store.IsLoaded) { _hud.ShowStatus(_sync.StatusLine, IdleHint); return; }
            var lit = Time.time < _highlightUntil ? _highlighted : null;
            _assembly.Show(VisualStateResolver.Resolve(_store.Plan, _store.Current, _selection.SelectedPartId, lit), _palette);
            _hud.ShowState(_store.Plan, _store.Current, MaterialList.For(_store.Plan, _store.Current), _store.Events, _assembly.ScaleLabel);
            var part = _assembly.ViewOf(_selection.SelectedPartId)?.Part;
            _hud.ShowPart(part != null && _store.Current.parts.TryGetValue(part.part_id, out var status) ? HudText.PartCard(_store.Plan, part, status, _store.Current) : "");
            _hud.ShowStatus(_sync.StatusLine, NothingBuilt ? IdleHint : _alignment.State == AlignmentState.Locked ? "" : _alignment.Hint);
        }

        // ── every frame ──────────────────────────────────────────────────────────────────────────────────────────
        void Update()
        {
            _stream.Drain(_handleMessage);

            if (_stream.Connects != _seenConnects) { _seenConnects = _stream.Connects; Run(_sync.CatchUp()); _build.OnStreamReconnected(); }   // (re)connected: fetch what was missed
            else if (!_sync.Online && Time.time > _nextRetry) { _nextRetry = Time.time + RetrySeconds; Run(_sync.CatchUp()); }

            if (_highlighted.Count > 0 && Time.time >= _highlightUntil) { _highlighted.Clear(); _dirty = true; }
            if (_alignment.State == AlignmentState.Locked) ReadMarkButton();
            while (_permissionAnswers.TryDequeue(out var answer))
                if (!answer.granted) _hud.Toast(answer.permission == QuestPermissions.Camera
                    ? "Camera not allowed: the copilot answers without seeing the desk. Allow it in Settings > Privacy."
                    : answer.permission == QuestPermissions.Scene
                    ? "Spatial data not allowed: build mode can't measure objects. Allow it in Settings > Privacy."
                    : "Microphone not allowed: use the question buttons, or allow it in Settings > Privacy.", 6f);
            // X on the left controller: a press scans this view (what "what can I build?" does), holding it for a second leaves build mode.
            var x = _scanButton.Update(OVRInput.GetDown(OVRInput.RawButton.X), OVRInput.Get(OVRInput.RawButton.X), OVRInput.GetUp(OVRInput.RawButton.X), Time.deltaTime);
            if (x != ButtonGesture.None) _build.OnScanButton(x);
            if (_dirty) Refresh();
        }

        void LateUpdate()
        {
            // XR supplies its first pose after Awake/Start. Wait for tracking and updated anchors
            // before world-locking the panel, otherwise it is placed relative to the floor origin.
            if (_hudInFront || _alignment.State == AlignmentState.Locked || Camera.main == null) return;
            if (OVRManager.instance != null && !OVRManager.tracker.isPositionTracked) return;
            _hud.StandInFrontOf(Camera.main.transform.position, Camera.main.transform.forward);
            _hudInFront = true;
        }

        /// <summary>B on the pointed part: a press toggles built / missing (so it is also the undo); holding it flags the part wrong.</summary>
        void ReadMarkButton()
        {
            if (_waitForMarkRelease) { if (!_input.MarkHeld && !_input.MarkUp) _waitForMarkRelease = false; return; }
            string partId = _selection.SelectedPartId;
            if (_input.MarkDown) { _markHeldFor = 0f; _markUsed = false; }
            if (_input.MarkHeld && !_markUsed)
            {
                _markHeldFor += Time.deltaTime;
                if (_markHeldFor >= WrongHoldSeconds && partId != null) { _markUsed = true; _sync.Mark(partId, "wrong"); }
            }
            if (_input.MarkUp && !_markUsed && partId == null && _build.MarkCurrentStep()) return;   // build mode: B with nothing pointed at marks the whole step
            if (_input.MarkUp && !_markUsed && partId != null && _store.IsLoaded && _store.Current.parts.TryGetValue(partId, out var status))
                _sync.Mark(partId, status.state == "built" ? "missing" : "built");
        }

        // ── copilot host ─────────────────────────────────────────────────────────────────────────────────────────
        sealed class ProjectablePart : IProjectablePart
        {
            public string PartId { get; set; }
            public string State { get; set; }
            public Bounds WorldBounds { get; set; }
        }

        public string AssemblyId => _store.AssemblyId;
        public int PlanRevision => _store.Plan?.revision ?? 0;
        public int StateVersion => _store.Current?.version ?? 0;
        public string Mode => _build != null && _build.Active ? "build" : "overlay";
        public string SelectedPartId => _selection.SelectedPartId;
        public string SelectionSource => _selection.SelectedPartId == null ? "none" : "controller_ray";
        public string CurrentStepId => _store.Current?.current_step_id;

        public IReadOnlyList<IProjectablePart> PartsForProjection() =>
            !_assembly.gameObject.activeInHierarchy ? new List<IProjectablePart>()    // build mode is looking at the room: no part of the hidden run is in view
            : _assembly.Views.Values.Select(v => (IProjectablePart)new ProjectablePart
            { PartId = v.PartId, State = _store.Current.parts.TryGetValue(v.PartId, out var s) ? s.state : "missing", WorldBounds = v.WorldBounds }).ToList();

        public void Highlight(string[] partIds, string style)
        {
            _highlighted.Clear();
            foreach (var id in partIds ?? Array.Empty<string>()) _highlighted.Add(id);
            _highlightUntil = Time.time + HighlightSeconds;
            _dirty = true;
        }

        public void HighlightTwins(string[] twinIds, string style) => _build?.HighlightTwins(twinIds);

        public void ShowCopilotActivity(CopilotActivity activity) =>
            _hud.ShowCopilotActivity(activity == CopilotActivity.Listening ? "listening" : activity == CopilotActivity.Thinking ? "thinking" : "idle");

        /// <summary>The answer, then the drawing it came from (the "source card": sheet and page).</summary>
        public void ShowAnswer(CopilotResponseDto response)
        {
            var source = response?.drawing_refs != null && response.drawing_refs.Length > 0 ? response.drawing_refs[0] : null;
            _hud.ShowAnswer(HudText.AnswerCard(response?.answer_text, source?.title, source?.sheet_id, source?.page ?? 0));
        }

        public void OnActionApplied(CopilotActionDto action)
        {
            if (action?.type == "start_scan") { _build.StartScan(); return; }   // "What can I build?": the headset scans, the server does the rest
            // The server already wrote the event; it arrives on the stream. This only tells the operator what happened.
            if (action?.type == "mark_state") _hud.Toast($"Voice: {action.part_ids?.Length ?? 0} part(s) → {action.new_state} · say \"undo\" to revert", 2f);
        }

        public void StepNav(string direction)
        {
            if (!_store.IsLoaded) return;
            var steps = _store.Plan.steps.OrderBy(s => s.index).ToList();
            int i = steps.FindIndex(s => s.step_id == _store.Current.current_step_id);
            var target = steps.ElementAtOrDefault(i + (direction == "back" ? -1 : 1));
            if (target != null) _hud.Toast($"Step {target.index}: {target.title}", 4f);
        }

        void TryCreateCopilot()
        {
            if (FindAnyObjectByType<CopilotController>() != null) return;
            try
            {
                var go = new GameObject("[Copilot]");
                go.SetActive(false);                                   // fields must be set before CopilotController.Awake reads them
                go.AddComponent<AudioSource>();
                var controller = go.AddComponent<CopilotController>();
                controller.baseUrl = _config.BaseUrl; controller.apiToken = _config.api_token;
                controller.frameSourceBehaviour = AddCameraSource(go); controller.hostBehaviour = this;
                controller.pushToTalkBehaviour = go.AddComponent<QuestPushToTalk>();
                controller.mic = go.AddComponent<MicRecorder>(); controller.speaker = go.AddComponent<PcmStreamPlayer>();
                go.SetActive(true);
            }
            catch (Exception e) { Debug.LogWarning("[CutOnce] The copilot could not be created; the build guide still works: " + e.Message); }
        }

        /// <summary>
        /// The copilot's camera (AGENTS rule 1). On the headset, Meta's PassthroughCameraAccess. In the Editor it
        /// depends on what is plugged in: over Meta Horizon Link with a Quest 3, MRUK 205 hands the Editor the real
        /// passthrough camera, so Kit sees the actual room while you run from Unity. With no headset (a Mac, or the
        /// simulator, which gives a pose but no pixels) it is the stored photo instead: `pnpm sync:fixtures` puts that
        /// in StreamingAssets, and without it the copilot asks with no frame at all.
        /// </summary>
        static MonoBehaviour AddCameraSource(GameObject go)
        {
            var headset = OVRPlugin.GetSystemHeadsetType();
            var linked = headset is OVRPlugin.SystemHeadset.Meta_Link_Quest_3 or OVRPlugin.SystemHeadset.Meta_Link_Quest_3S;
            if (Application.isEditor && !linked) return go.AddComponent<FixtureFrameSource>();
            var frames = go.AddComponent<PcaFrameSource>();
            // MRUK allows ONE PassthroughCameraAccess per camera position: a second one logs an error, disables itself
            // and leaves whoever asked second blind for the session. The room scanner starts before us (AfterSceneLoad),
            // so share whatever is already there. One camera, both readers: its texture for the scanner, its pixels for us.
            var shared = FindAnyObjectByType<Meta.XR.PassthroughCameraAccess>();
            frames.cameraAccess = shared != null && shared.enabled ? shared : go.AddComponent<Meta.XR.PassthroughCameraAccess>();
            return frames;
        }
    }
}
