using System.Linq;
using Fusion;
using UnityEngine;

// Attach this to a GameObject called "GameManager" that has a NetworkObject component.
//
// CONFIRMED LIVE BUG this replaced: this used to be placed directly IN ConnectScene as
// a "Scene NetworkObject", on the assumption Fusion would automatically discover and
// spawn it once the Host's session started. Live testing proved that assumption wrong
// specifically for THIS project's setup: ConnectScene is already loaded (NetworkBootstrap
// itself lives there) BEFORE StartGame() is ever called, and passing that SAME
// already-loaded scene into StartGameArgs.Scene never actually re-triggers Fusion's
// scene-load pipeline that scans for and registers Scene NetworkObjects — Unity's own
// SceneManager.sceneLoaded event only fires on a genuine load transition, which never
// happens here since the scene was already sitting there. The result, confirmed via
// live reflection: NetworkObject.IsValid stayed False and Spawned() was NEVER called on
// it (Awake() ran, from the scene just being open in the Editor, but nothing past that) —
// most of the time. It intermittently DID work once, which fits a registration path that
// depends on unreliable scene-reload timing rather than something structurally
// impossible. GameManager is now a genuine PREFAB, spawned dynamically by the Host via
// runner.Spawn() the instant a new session starts (see NetworkBootstrap.StartGame()) —
// the exact same, already proven-reliable mechanism the Player prefab has used
// successfully this whole time. No scene-object registration dependency left at all.
//
// This is the single source of truth for what phase the game is in. Only the server
// (state authority) ever changes CurrentPhase — clients just read it.
public class NetworkGameManager : NetworkBehaviour
{
    public enum GamePhase
    {
        Lobby,
        RoleAssign,
        Day,
        Sunset,
        Night,
        // NOTE: a DoctorSave phase used to sit here, between Night and Discussion,
        // in which the Doctor blind-guessed who the Mafia had killed that Night —
        // right guess revived them, wrong guess killed whoever was picked. That
        // whole mechanic is gone. The Doctor now gets ONE revive for the entire
        // match, spent during an event-triggered Discussion on any dead player they
        // choose. Night returns to Day unless a hall-button/body-report meeting
        // interrupts free-roam play. See NightActionManager.RPC_SubmitDoctorRevive.
        Discussion,
        Voting,
        GameOver
    }

    // Which side won — None until CheckWinCondition() actually decides the match,
    // at which point CurrentPhase also flips to GameOver in the same tick.
    public enum WinningTeam
    {
        None,
        Civilians,
        Mafia
    }

    // --- Networked state (server writes, everyone reads) ---
    [Networked] public GamePhase CurrentPhase { get; private set; }
    [Networked] public WinningTeam Winner { get; private set; }
    [Networked] private TickTimer PhaseTimer { get; set; }
    [Networked] private GamePhase ResumePhaseAfterMeeting { get; set; }
    [Networked] private float ResumePhaseTimeRemaining { get; set; }

    // --- Phase durations in seconds (tune these later against your GDD's timing table) ---
    [Header("Phase Durations (seconds)")]
    [SerializeField] private float dayDuration = 60f;
    [SerializeField] private float sunsetDuration = 5f;
    [SerializeField] private float nightDuration = 30f;
    [SerializeField] private float discussionDuration = 120f;
    [SerializeField] private float votingDuration = 20f;

    // Track last phase locally on each client so we can detect a change and react to it
    // (e.g. swap UI, trigger role-assignment) without every client needing authority.
    private GamePhase _lastSeenPhase = (GamePhase)(-1);

    // Server-side twin of _lastSeenPhase. _lastSeenPhase is driven from Render(),
    // which runs on every peer and is only for local presentation; this one runs in
    // FixedUpdateNetwork on the Host and drives authoritative side effects — right
    // now, moving everybody between the school and the discussion room. They are kept
    // separate on purpose: a client must never trigger a teleport.
    private GamePhase _lastServerPhase = (GamePhase)(-1);

    private PlayerRoleManager _roleManager;
    private NightActionManager _nightActionManager;
    private VoteManager _voteManager;
    private TaskManager _taskManager;
    public static NetworkGameManager Instance { get; private set; }

    // External scripts (UI, etc.) MUST check this before reading any [Networked]
    // property like CurrentPhase — those throw if accessed before Spawned() runs.
    public bool IsReady { get; private set; }

    private void Awake()
    {
        _roleManager = GetComponent<PlayerRoleManager>();
        _nightActionManager = GetComponent<NightActionManager>();
        _voteManager = GetComponent<VoteManager>();
        _taskManager = GetComponent<TaskManager>();
        Instance = this;
    }

    public override void Spawned()
    {
        // Runs on EVERY peer (host and clients) once this object has finished spawning.
        IsReady = true;

        if (Object.HasStateAuthority)
        {
            // CONFIRMED LIVE BUG: this used to unconditionally reset CurrentPhase to
            // Lobby every single time this Scene NetworkObject spawns — including
            // after a Host Migration reloads GameplayScene on the new Host. That made
            // a mid-match game (Day/Night/Voting/whatever) jump straight back to the
            // Lobby the instant the old Host disconnected, even though nothing about
            // the actual match should have changed — only the Host process did.
            //
            // NetworkBootstrap.PendingResumePhase is captured (on every peer, before
            // any of them even knows who the new Host will be) right before the old
            // runner shuts down for migration — see OnHostMigration(). If it's set,
            // this Spawned() call is a migration resume, not a fresh session, so we
            // restore the match to whatever phase it was actually in instead of
            // wiping it back to Lobby.
            if (NetworkBootstrap.PendingResumePhase.HasValue)
            {
                CurrentPhase = NetworkBootstrap.PendingResumePhase.Value;
                NetworkBootstrap.PendingResumePhase = null;
                Debug.Log($"[NetworkGameManager] Host Migration resume — restored CurrentPhase to {CurrentPhase} instead of resetting to Lobby.");
            }
            else
            {
                CurrentPhase = GamePhase.Lobby;
                // Match now starts manually via LobbyUIManager's "Start Game" button (host-only),
                // once enough players have joined. See BeginMatch().
            }
        }
    }

    public override void FixedUpdateNetwork()
    {
        // Only the server drives phase transitions.
        if (!Object.HasStateAuthority) return;

        // Detect the phase actually CHANGING, server-side, before anything else.
        //
        // Deliberately a separate check rather than extra lines inside
        // AdvanceToNextPhase(). CurrentPhase has four different ways to change — the
        // normal timer path, ForceDiscussion() when a body is reported,
        // ForceAdvancePhase() when everyone has already voted, and DebugForcePhase()
        // — and the room swap has to happen for all of them. Hanging it off the value
        // itself means no future fifth path can forget to trigger it, which is exactly
        // the kind of silent gap that let the missing-Doctor bug run unnoticed.
        if (CurrentPhase != _lastServerPhase)
        {
            GamePhase previousServerPhase = _lastServerPhase;
            _lastServerPhase = CurrentPhase;
            OnServerPhaseEntered(previousServerPhase, CurrentPhase);
        }

        // Lobby and GameOver don't auto-advance on a timer — they're triggered manually
        // (Lobby -> RoleAssign when enough players ready; GameOver ends the loop).
        if (CurrentPhase == GamePhase.Lobby || CurrentPhase == GamePhase.GameOver) return;

        // If no timer is running yet for this phase, start one.
        if (PhaseTimer.ExpiredOrNotRunning(Runner) && PhaseTimer.IsRunning == false)
        {
            StartTimerForCurrentPhase();
        }

        // When the timer runs out, advance to the next phase.
        if (PhaseTimer.Expired(Runner))
        {
            AdvanceToNextPhase();
        }
    }

    private float GetDurationForPhase(GamePhase phase)
    {
        return phase switch
        {
            GamePhase.RoleAssign => 3f, // short buffer so clients receive their role RPC
            GamePhase.Day => dayDuration,
            GamePhase.Sunset => sunsetDuration,
            GamePhase.Night => nightDuration,
            GamePhase.Discussion => discussionDuration,
            GamePhase.Voting => votingDuration,
            _ => 5f
        };
    }

    private void StartTimerForCurrentPhase(float overrideDuration = -1f)
    {
        float duration = overrideDuration > 0f ? overrideDuration : GetDurationForPhase(CurrentPhase);
        PhaseTimer = TickTimer.CreateFromSeconds(Runner, duration);
    }

    private void AdvanceToNextPhase()
    {
        GamePhase previousPhase = CurrentPhase;

        bool returningFromMeeting = previousPhase == GamePhase.Voting;
        float resumedTime = ResumePhaseTimeRemaining;

        CurrentPhase = CurrentPhase switch
        {
            GamePhase.RoleAssign => GamePhase.Day,
            GamePhase.Day => GamePhase.Sunset,
            GamePhase.Sunset => GamePhase.Night,
            // Meetings are event-driven. A Night ending normally starts a new Day;
            // it never opens Discussion/Voting by itself.
            GamePhase.Night => GamePhase.Day,
            GamePhase.Discussion => GamePhase.Voting,
            GamePhase.Voting => IsFreeRoamPhase(ResumePhaseAfterMeeting) ? ResumePhaseAfterMeeting : GamePhase.Day,
            _ => CurrentPhase
        };

        // Bodies now remain in the school until somebody reports them. Ending
        // Night must not hide them, otherwise a Day-time report is impossible.

        // The Doctor's revive is CHOSEN during Discussion but only LANDS here, the
        // moment Discussion ends — so a revived player never gets to rejoin the very
        // Discussion they were killed before and immediately report what they saw.
        // They come back for the Voting phase that follows.
        if (previousPhase == GamePhase.Discussion)
        {
            _nightActionManager?.ResolvePendingRevive();
        }

        // A fresh Night phase starting needs last Night's victim cleared, so
        // HideNightVictimBody never acts on a stale corpse from a previous round.
        if (CurrentPhase == GamePhase.Night && previousPhase == GamePhase.Sunset)
        {
            _nightActionManager?.ResetForNewNight();
        }

        if (previousPhase == GamePhase.Voting && _voteManager != null)
        {
            PlayerRef? eliminated = _voteManager.ResolveVotes();
            if (eliminated.HasValue)
            {
                NetworkObject victimObject = Runner.GetPlayerObject(eliminated.Value);
                if (victimObject != null && victimObject.TryGetComponent(out PlayerState state))
                {
                    state.Kill(false);
                }
                Debug.Log($"[NetworkGameManager] Vote result: {eliminated.Value} was voted out.");
            }
            else
            {
                Debug.Log("[NetworkGameManager] Vote result: no one was eliminated.");
            }

            // Same reasoning as the Night-kill check above — the Town can just as
            // easily vote out the last Mafia (Civilians win) as accidentally vote
            // out their last Doctor/Civilian and hand it to the Mafia.
            CheckWinCondition();
        }

        // CheckWinCondition() above may have just switched CurrentPhase to GameOver —
        // don't start a phase timer for a phase that never advances on its own
        // (see FixedUpdateNetwork's own GameOver short-circuit).
        if (CurrentPhase != GamePhase.GameOver)
        {
            StartTimerForCurrentPhase(returningFromMeeting ? resumedTime : -1f);
        }
    }

    // =====================================================================
    // ROOM SWAP — school <-> discussion table
    // =====================================================================

    // Server-only. Runs once, on the tick CurrentPhase actually changes.
    private void OnServerPhaseEntered(GamePhase previous, GamePhase current)
    {
        // Everyone goes to the table for Discussion. Voting runs straight after in
        // the same room, so it deliberately does NOT move anybody again — the
        // argument and the vote are one continuous meeting.
        if (current == GamePhase.Discussion)
        {
            MovePlayersToDiscussionTable();
            return;
        }

        // ...and back out into the school when Day begins again.
        //
        // The `previous` check matters. This method also fires on the very first
        // tick after a Host Migration, when `previous` is the (GamePhase)(-1)
        // sentinel because the new Host's own tracker starts empty. Without the
        // check, a migration that happened in the middle of Day would rip every
        // player out of wherever they were and dump them back on the spawn markers,
        // undoing the position restore the migration had just done correctly. Only
        // a genuine "the meeting is over" or "the match is starting" transition
        // should relocate anyone.
        bool leavingTheMeeting = previous == GamePhase.Voting || previous == GamePhase.Discussion;
        bool matchJustStarted = previous == GamePhase.RoleAssign;

        if ((leavingTheMeeting && IsFreeRoamPhase(current))
            || (current == GamePhase.Day && matchJustStarted))
        {
            MovePlayersToSchool(matchJustStarted);
        }
    }

    private void MovePlayersToDiscussionTable()
    {
        if (!DiscussionRoom.IsReady)
        {
            Debug.LogError("[NetworkGameManager] Discussion started but the discussion room is not loaded — everyone is staying in the school. Check that DiscussionScene is in Build Settings and that LobbyToGameplayTransition ran DiscussionRoom.LoadAndPlace().");
            return;
        }

        // Ordered by PlayerId, the same convention VotingUIManager, NightActionUIManager
        // and the revive/shoot panels all use for their P1..P10 labels — so the player
        // sitting behind placard 3 is the same player the vote buttons call P3.
        var players = Runner.ActivePlayers.OrderBy(p => p.PlayerId).ToList();

        if (players.Count > DiscussionRoom.StandPointCount)
        {
            Debug.LogError($"[NetworkGameManager] {players.Count} players but only {DiscussionRoom.StandPointCount} places at the table — the extras will be left in the school.");
        }

        int moved = 0;
        for (int i = 0; i < players.Count; i++)
        {
            Vector3 position;
            Quaternion rotation;
            if (!DiscussionRoom.TryGetStandPose(i, out position, out rotation)) continue;
            if (TeleportPlayer(players[i], position, rotation)) moved++;
        }

        Debug.Log($"[NetworkGameManager] Discussion — moved {moved}/{players.Count} player(s) to the round table.");
    }

    private void MovePlayersToSchool(bool matchJustStarted)
    {
        if (!SchoolSpawns.IsReady)
        {
            Debug.LogError("[NetworkGameManager] Day started but the school's SpawnPoints group could not be found — players are staying wherever they were. Re-run Tools/Blackwood/Generate School Blockout if the markers are missing.");
            return;
        }

        var players = Runner.ActivePlayers.OrderBy(p => p.PlayerId).ToList();

        if (players.Count > SchoolSpawns.Count)
        {
            Debug.LogError($"[NetworkGameManager] {players.Count} players but only {SchoolSpawns.Count} spawn markers in the school — the extras will be left where they were.");
        }

        int moved = 0;
        for (int i = 0; i < players.Count; i++)
        {
            Vector3 position;
            Quaternion rotation;
            if (!SchoolSpawns.TryGetSpawnPose(Runner, i, out position, out rotation)) continue;
            if (TeleportPlayer(players[i], position, rotation)) moved++;
        }

        string why = matchJustStarted ? "match start" : "meeting over";
        Debug.Log($"[NetworkGameManager] School ({why}) — moved {moved}/{players.Count} player(s) back into play.");
    }

    private bool TeleportPlayer(PlayerRef player, Vector3 position, Quaternion rotation)
    {
        NetworkObject obj = Runner.GetPlayerObject(player);
        if (obj == null)
        {
            Debug.LogWarning($"[NetworkGameManager] No player object registered for {player} — could not move them.");
            return false;
        }

        PlayerMovement movement;
        if (obj.TryGetComponent(out movement))
        {
            movement.TeleportTo(position, rotation);
            return true;
        }

        obj.transform.SetPositionAndRotation(position, rotation);
        return true;
    }

    // Server-only. Counts alive Mafia vs. everyone else still alive (Civilian +
    // Doctor) and ends the match the moment either side has definitively won:
    //   - Mafia all dead                    -> Civilians win.
    //   - Mafia count >= remaining non-Mafia -> Mafia win (they can no longer
    //     lose a straight vote, the standard Mafia/Werewolf win condition).
    // Called right after any kill (Night resolution or Vote resolution) — never
    // on a timer, so the game ends the instant the condition is actually met
    // instead of lagging a full phase behind.
    public void CheckWinCondition()
    {
        if (!Object.HasStateAuthority) return;
        if (CurrentPhase == GamePhase.GameOver) return; // already decided

        int aliveMafia = 0;
        int aliveOthers = 0;

        foreach (PlayerRef p in Runner.ActivePlayers)
        {
            NetworkObject obj = Runner.GetPlayerObject(p);
            if (obj == null || !obj.TryGetComponent(out PlayerState state) || !state.IsAlive)
            {
                continue; // dead, or not spawned yet — doesn't count either way
            }

            PlayerRole role = _roleManager != null ? _roleManager.GetServerRole(p) : PlayerRole.Civilian;
            if (role == PlayerRole.Mafia) aliveMafia++;
            else aliveOthers++;
        }

        WinningTeam result = WinningTeam.None;
        if (aliveMafia == 0)
        {
            result = WinningTeam.Civilians;
        }
        else if (aliveMafia >= aliveOthers)
        {
            result = WinningTeam.Mafia;
        }

        if (result != WinningTeam.None)
        {
            Winner = result;
            CurrentPhase = GamePhase.GameOver;
            Debug.Log($"[NetworkGameManager] Game Over — {result} win! (alive Mafia={aliveMafia}, alive others={aliveOthers})");
        }
    }

    // (IsDoctorAlive() used to live here — it existed purely to decide whether the
    // old DoctorSave phase had anything to do that Night. With that phase gone, and
    // the Doctor's revive now a once-per-match action taken during Discussion, there
    // is nothing left to gate on it.)

    // Server-only: call this once enough players are in the Lobby to begin the match.
    [SerializeField] private int minPlayersToStart = 2;
    public int MinPlayersToStart => minPlayersToStart;

    public void BeginMatch()
    {
        if (!Object.HasStateAuthority) return;
        if (CurrentPhase != GamePhase.Lobby) return;
        if (Runner.ActivePlayers.Count() < minPlayersToStart)
        {
            Debug.LogWarning($"[NetworkGameManager] Not enough players to start yet ({Runner.ActivePlayers.Count()}/{minPlayersToStart}).");
            return;
        }

        if (!AllPlayersHaveCharacterSelections())
        {
            Debug.LogWarning("[NetworkGameManager] Match cannot start until every player has selected a character.");
            return;
        }

        CurrentPhase = GamePhase.RoleAssign;
        StartTimerForCurrentPhase();
        _roleManager?.AssignRoles();

        // The Doctor's revive and the Sheriff's shot are ONCE PER MATCH, not once
        // per round, so their "already used" flags are cleared exactly here — at the
        // start of a match — and nowhere else. Resetting them on any per-round
        // boundary would quietly hand both roles a fresh ability every cycle, which
        // is the single easiest way to break this whole design without any error
        // showing up.
        _nightActionManager?.ResetOncePerMatchAbilities();
        foreach (PlayerRef player in Runner.ActivePlayers)
        {
            var playerObject = Runner.GetPlayerObject(player);
            if (playerObject != null && playerObject.TryGetComponent(out PlayerState state))
                state.InitializeMafiaAbilities();
        }

        // Task lists are dealt ONCE, here, for the whole match — not per round.
        // Must come after AssignRoles() above, because who gets a FAKE list is
        // decided by who turned out to be Mafia.
        _taskManager?.AssignTasks();

        // Match has started — remove it from the public Browse Lobbies list and
        // stop accepting new joiners. Only the Host can touch SessionInfo, which
        // is exactly who's running this (guarded by HasStateAuthority above).
        Runner.SessionInfo.IsVisible = false;
        Runner.SessionInfo.IsOpen = false;
    }

    public bool AllPlayersHaveCharacterSelections()
    {
        if (!IsReady || Object == null || !Object.IsValid || Runner == null) return false;

        foreach (PlayerRef player in Runner.ActivePlayers)
        {
            NetworkObject playerObject = Runner.GetPlayerObject(player);
            if (playerObject == null
                || !playerObject.TryGetComponent(out PlayerState state)
                || !state.HasSelectedCharacter)
            {
                return false;
            }
        }

        return Runner.ActivePlayers.Any();
    }

    // Server-only: skips whatever's left of the current phase's timer and immediately
    // advances to the next phase. Used by VoteManager to end Voting early once every
    // alive player has voted, instead of making everyone wait out the full timer.
    public void ForceAdvancePhase()
    {
        if (!Object.HasStateAuthority) return;
        AdvanceToNextPhase();
    }

    public bool CanCallDiscussion => IsFreeRoamPhase(CurrentPhase);

    public static bool IsFreeRoamPhase(GamePhase phase)
    {
        return phase == GamePhase.Day || phase == GamePhase.Sunset || phase == GamePhase.Night;
    }

    // Called by a validated body report or emergency-button request. The phase
    // and its remaining time are remembered so play resumes where it was interrupted.
    public void ForceDiscussion(string reason = "body report")
    {
        if (!Object.HasStateAuthority) return;
        if (!IsFreeRoamPhase(CurrentPhase)) return;

        ResumePhaseAfterMeeting = CurrentPhase;
        ResumePhaseTimeRemaining = PhaseTimer.RemainingTime(Runner) ?? GetDurationForPhase(CurrentPhase);

        CurrentPhase = GamePhase.Discussion;
        StartTimerForCurrentPhase();
        Debug.Log($"[NetworkGameManager] Discussion called by {reason}; play will resume in {ResumePhaseAfterMeeting}.");
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_RequestEmergencyDiscussion(RpcInfo info = default)
    {
        TryCallDiscussionFromHall(info.Source);
    }

    // Server-side entry point used by both the Fusion input stream and the optional
    // RPC path. Resolve the station from the requester's simulation scene rather
    // than a static singleton; multi-peer play can have several scene copies alive.
    public void TryCallDiscussionFromHall(PlayerRef requester)
    {
        if (!Object.HasStateAuthority || !CanCallDiscussion) return;

        NetworkObject playerObject = Runner.GetPlayerObject(requester);
        if (playerObject == null || !playerObject.TryGetComponent(out PlayerState playerState) || !playerState.IsAlive)
        {
            Debug.LogWarning($"[NetworkGameManager] Hall discussion rejected: {requester} has no living player object.");
            return;
        }

        EmergencyDiscussionButton station = null;
        foreach (GameObject root in playerObject.gameObject.scene.GetRootGameObjects())
        {
            station = root.GetComponentInChildren<EmergencyDiscussionButton>(true);
            if (station != null) break;
        }

        if (station == null)
        {
            Debug.LogWarning($"[NetworkGameManager] Hall discussion rejected: no station exists in {requester}'s simulation scene.");
            return;
        }

        float allowedDistance = station.InteractRadius + 1.5f;
        if (Vector3.Distance(playerObject.transform.position, station.transform.position) > allowedDistance)
        {
            Debug.LogWarning($"[NetworkGameManager] Hall discussion rejected: {requester} is not near the station.");
            return;
        }

        Debug.Log($"[NetworkGameManager] Hall discussion accepted from {requester}.");
        ForceDiscussion($"hall button ({requester})");
    }

    // DEBUG ONLY — jumps straight to an arbitrary phase, bypassing normal transition
    // rules (role assignment, night/vote resolution, etc.). Useful for testing a
    // specific phase's UI/logic without playing through the whole loop first.
    // Do NOT call this from real gameplay code — only from DebugVoteTester or similar.
    public void DebugForcePhase(GamePhase phase)
    {
        if (!Object.HasStateAuthority) return;
        CurrentPhase = phase;
        StartTimerForCurrentPhase();
    }

    // Runs on every client (including host) once per rendered frame — good place to
    // detect phase changes locally and drive UI/logic that doesn't need to touch the network.
    public override void Render()
    {
        if (CurrentPhase != _lastSeenPhase)
        {
            _lastSeenPhase = CurrentPhase;
            OnPhaseChangedLocally(CurrentPhase);
        }
    }

    private void OnPhaseChangedLocally(GamePhase newPhase)
    {
        // TEMP: just log for now so we can verify the state machine over the network.
        // Later this is where we'll swap HUD panels, enable/disable role-specific UI, etc.
        Debug.Log($"[NetworkGameManager] Phase changed to: {newPhase} (Remaining: {GetRemainingTime():F1}s)");
    }

    // Handy for UI countdown text later.
    public float GetRemainingTime()
    {
        return PhaseTimer.RemainingTime(Runner) ?? 0f;
    }
}
