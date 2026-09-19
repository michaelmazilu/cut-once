using System.Collections.Generic;

namespace CutOnce.Core
{
    public enum BuildPhase { Off, Scanning, Labelled, Ideas, Starting, Assembling, Walkthrough }

    /// <summary>
    /// Build mode's states, with no Unity in them, so every transition is unit-tested. Messages that arrive in the wrong
    /// phase are ignored: a late inventory never interrupts a build, and ideas from an old session never replace new ones.
    /// </summary>
    public sealed class BuildFlow
    {
        BuildPhase _beforeScan = BuildPhase.Off;
        /// <summary>Sessions this headset has left. Whatever they still send (labels on their way, a scan that landed late) is not ours any more.</summary>
        readonly HashSet<string> _leftSessions = new HashSet<string>();

        public BuildPhase Phase { get; private set; } = BuildPhase.Off;
        public string SessionId { get; private set; }
        public InventoryDto Inventory { get; private set; }
        public List<BuildIdeaDto> Ideas { get; private set; } = new List<BuildIdeaDto>();
        public BuildIdeaDto Chosen { get; private set; }
        public bool Active => Phase != BuildPhase.Off;
        /// <summary>Where the design stands in the room: the chosen one's origin, else (while choosing) where the first idea would go. Null before there are ideas.</summary>
        public BuildOriginDto Site => Chosen?.origin ?? (Ideas.Count > 0 ? Ideas[0]?.origin : null);
        /// <summary>While you look at the room and choose, the run that was showing (the last design) is out of the way.</summary>
        public bool HidesHologram => Phase == BuildPhase.Scanning || Phase == BuildPhase.Labelled || Phase == BuildPhase.Ideas || Phase == BuildPhase.Starting;
        bool Building => Phase == BuildPhase.Starting || Phase == BuildPhase.Assembling || Phase == BuildPhase.Walkthrough;
        /// <summary>
        /// The scan BUTTON (X) only works until a design is chosen: a thumb resting on it mid-build must not throw the
        /// walkthrough away. Saying "what can I build?" stays the deliberate way to start over.
        /// </summary>
        public bool CanScanFromButton => !Building;
        /// <summary>
        /// The trigger on empty space scans another view only while there are no previews to miss. With previews showing, a
        /// trigger that hits none of them is a near miss, and a rescan would clear the very previews being picked from.
        /// </summary>
        public bool CanScanFromTrigger => Phase == BuildPhase.Labelled;
        /// <summary>A scan has started and the server has not answered it yet (or it has not failed yet).</summary>
        public bool ScanInFlight { get; private set; }
        /// <summary>Names the scan in flight. A result carrying another number belongs to a scan that was given up (you left, then scanned again).</summary>
        public int ScanTicket { get; private set; }

        /// <summary>
        /// A new scan ("what can I build?" mid-build starts over, keeping the session so views merge). False when it is
        /// refused. While the pieces are flying: only the flight's end leaves Assembling, so a scan that stopped the
        /// flight and then failed would strand the build there. While a scan is in flight: the session comes back with
        /// the first scan's answer, so a second scan sent before it would open a second session on the server.
        /// </summary>
        public bool StartScan()
        {
            if (Phase == BuildPhase.Assembling || ScanInFlight) return false;
            if (Phase != BuildPhase.Scanning) _beforeScan = Phase;
            Phase = BuildPhase.Scanning; ScanInFlight = true; ScanTicket++;
            return true;
        }

        /// <summary>The scan never reached the server: back to where you were (nothing, the ideas you had, the build you were on).</summary>
        public void ScanFailed(int ticket)
        {
            if (ticket != ScanTicket || !ScanInFlight) return;               // a scan that was given up: its failure is not this scan's
            ScanInFlight = false;
            if (Phase == BuildPhase.Scanning) Phase = _beforeScan;
        }

        /// <summary>
        /// The server took the scan. False for a scan that was given up (you left build mode while it was uploading): the
        /// session it opened or joined is then a left one, so its objects cannot switch build mode back on.
        /// </summary>
        public bool OnScanAccepted(int ticket, string sessionId)
        {
            if (ticket != ScanTicket || !ScanInFlight) { if (sessionId != null && sessionId != SessionId) _leftSessions.Add(sessionId); return false; }
            ScanInFlight = false; SessionId = sessionId;
            return true;
        }

        /// <summary>True when the inventory was taken (so it is the one to show). With build mode off it starts it: a Director replay counts.</summary>
        public bool OnInventory(InventoryDto inventory)
        {
            if (inventory == null || Building) return false;
            if (inventory.session_id != null && _leftSessions.Contains(inventory.session_id)) return false;   // labels that were still on their way when you left
            if (SessionId != null && inventory.session_id != SessionId)
            {
                // Another session (the Director replayed a scan or opened a new one): the ideas on show belong to the one
                // before, and the server would answer 404 to every one of them.
                Ideas = new List<BuildIdeaDto>(); Chosen = null;
                if (Phase == BuildPhase.Ideas) Phase = BuildPhase.Scanning;
            }
            SessionId = inventory.session_id; Inventory = inventory;
            if (inventory.labelled && Phase != BuildPhase.Ideas) Phase = BuildPhase.Labelled;
            else if (Phase == BuildPhase.Off) Phase = BuildPhase.Scanning;
            return true;
        }

        /// <summary>True when the ideas were taken: build mode is on, no build is under way, and they belong to this session.</summary>
        public bool OnIdeas(string sessionId, List<BuildIdeaDto> ideas, bool final)
        {
            if (Phase == BuildPhase.Off || Building || (SessionId != null && sessionId != SessionId)) return false;
            Ideas = ideas ?? new List<BuildIdeaDto>();
            if (Ideas.Count > 0) Phase = BuildPhase.Ideas;
            else if (Phase == BuildPhase.Ideas) Phase = BuildPhase.Labelled;      // a rethink that found nothing: the old previews go
            return true;
        }

        public bool Pick(string ideaId)
        {
            if (Phase != BuildPhase.Ideas) return false;
            var idea = ideaId == null ? null : Ideas.Find(i => i?.idea_id == ideaId);
            if (idea == null) return false;
            Chosen = idea; Phase = BuildPhase.Starting;
            return true;
        }

        public void PickFailed() { if (Phase == BuildPhase.Starting) { Chosen = null; Phase = BuildPhase.Ideas; } }

        /// <summary>
        /// The start answered 404: the idea is not in the server's session any more (it restarted, or the Director opened a
        /// new session). None of these ideas can ever start, so they go; the objects stay, and X scans again.
        /// </summary>
        public void PickGone()
        {
            if (Phase != BuildPhase.Starting) return;
            Chosen = null; Ideas = new List<BuildIdeaDto>(); Phase = BuildPhase.Labelled;
        }

        /// <summary>
        /// True when this run is the chosen design: picked here, or started by voice or from the Director (then it is found
        /// among the ideas, even while another view is being scanned). Never twice: once built, a reload is just a run.
        /// </summary>
        public bool TryPlace(string planId)
        {
            if (!Active || planId == null || Phase == BuildPhase.Assembling || Phase == BuildPhase.Walkthrough) return false;
            var idea = Chosen?.plan != null && Chosen.plan.plan_id == planId ? Chosen : Ideas.Find(i => i?.plan != null && i.plan.plan_id == planId);
            if (idea == null) return false;
            Chosen = idea; Phase = BuildPhase.Starting;
            return true;
        }

        /// <summary>
        /// After a reconnect: the stream only resends the current run, so labels and ideas sent while the Wi-Fi was down
        /// are gone. This turns the server's session into the messages that were missed, to be handled like any others
        /// (so nothing is said twice: the ideas are not final). Empty unless the snapshot is this headset's session and
        /// build mode is waiting on it: the server only keeps objects it has named, so what it holds is labelled.
        /// </summary>
        public List<WsMessageDto> CatchUp(BuildSessionSnapshotDto snapshot)
        {
            var messages = new List<WsMessageDto>();
            string session = snapshot?.session?.session_id;
            if (!Active || Building || session == null || SessionId == null || session != SessionId) return messages;
            if (snapshot.twins != null && snapshot.twins.Count > 0)
                messages.Add(new WsMessageDto { type = "build_inventory", inventory = new InventoryDto { session_id = session, labelled = true, surfaces = snapshot.surfaces ?? new List<SurfaceDto>(), twins = snapshot.twins } });
            if (snapshot.ideas != null && snapshot.ideas.Count > 0)
                messages.Add(new WsMessageDto { type = "build_ideas", session_id = session, ideas = snapshot.ideas, final = false });
            return messages;
        }

        public void OnPlaced() { if (Phase == BuildPhase.Starting) Phase = BuildPhase.Assembling; }
        public void OnAssembled() { if (Phase == BuildPhase.Assembling) Phase = BuildPhase.Walkthrough; }

        public void Exit()
        {
            if (SessionId != null) _leftSessions.Add(SessionId);
            Phase = BuildPhase.Off; _beforeScan = BuildPhase.Off; SessionId = null; Inventory = null; Chosen = null; ScanInFlight = false;
            Ideas = new List<BuildIdeaDto>();
        }
    }
}
