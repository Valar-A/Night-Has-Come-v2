using Fusion;
using UnityEngine;

// Attach to the SAME "GameManager" scene object as NetworkGameManager and PlayerRoleManager.
//
// Kill flow: a Mafia kill applies THE INSTANT the Kill button is pressed while in
// range (see NightActionUIManager) — no queueing. The body stays visible in the
// world only until Night actually ends (see PlayerState.IsBodyVisible /
// NetworkGameManager.HideNightVictimBody) — OR until some other living player
// walks up and reports it first, which jumps the match straight into Discussion
// (RPC_ReportBody / NetworkGameManager.ForceDiscussion), skipping the rest of
// this entirely.
//
// Doctor flow — REWORKED AGAIN: the Doctor has no nightly ability at all now.
// They get ONE revive for the entire match, spent during Discussion on any dead
// player they choose (RPC_SubmitDoctorRevive). The previous design — a DoctorSave
// phase after every Night in which the Doctor blind-guessed who had been killed,
// reviving them on a right guess and killing an innocent on a wrong one — is gone
// entirely, along with that phase.
//
// Sheriff flow — NEW: the Sheriff gets ONE shot for the entire match, also fired
// during Discussion (RPC_SubmitSheriffShot). Hit a Mafia and that Mafia dies. Hit
// anyone else — Civilian, Doctor, anyone — and the SHERIFF dies instead. That
// asymmetry is the whole point: the shot is only worth taking once the Sheriff is
// genuinely confident, because a wrong call costs the Town both a power role and
// a living body in the same moment.
public class NightActionManager : NetworkBehaviour
{
    public static NightActionManager Instance { get; private set; }

    // Server-writes, everyone reads. Set when a Mafia kill lands, cleared at the
    // start of each Night. Still needed so the corpse can be hidden the instant
    // Night ends (HideNightVictimBody) — it no longer feeds any guessing UI.
    [Networked] public PlayerRef LastNightVictim { get; private set; }

    // ONCE PER MATCH, not once per round.
    //
    // [Networked] on purpose rather than plain server-side bools: the acting
    // player's own UI has to know the ability is spent so it can stop offering the
    // panel, and that only works if the flag actually replicates to them. Neither
    // flag leaks hidden information — both become obvious to everyone the moment
    // the ability is used (a corpse stands back up, or someone drops dead in the
    // middle of Discussion). Cleared ONLY in NetworkGameManager.BeginMatch().
    [Networked] public NetworkBool DoctorReviveUsed { get; private set; }
    [Networked] public NetworkBool SheriffShotUsed { get; private set; }

    // Who the Doctor queued to bring back. Set when the revive is submitted during
    // Discussion, applied and cleared the moment Discussion ends. None whenever
    // nothing is waiting. [Networked] so it survives a Host Migration mid-round —
    // otherwise a migration between the Doctor's click and the end of Discussion
    // would silently swallow the ability, spent but never resolved.
    [Networked] public PlayerRef PendingRevive { get; private set; }

    private void Awake()
    {
        Instance = this;
    }

    // Called by the Mafia player's own client, the instant they press Kill while
    // in range of a target (see NightActionUIManager's proximity scan) — applies
    // the kill immediately.
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_SubmitMafiaTarget(PlayerRef target, RpcInfo info = default)
    {
        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.CurrentPhase != NetworkGameManager.GamePhase.Night) return;

        // Reject the call unless the sender is actually the Mafia — prevents a modified
        // client from pretending to be Mafia and submitting a kill target.
        if (PlayerRoleManager.Instance.GetServerRole(info.Source) != PlayerRole.Mafia) return;

        // CONFIRMED bug class fix — same "eliminated player can still play" issue
        // as VoteManager: roles are assigned once at RoleAssign and never cleared
        // on death, so a Mafia player eliminated earlier still passes the role
        // check above and could otherwise keep submitting kill targets from
        // spectate mode. Reject any action from (or targeting) a player who is
        // no longer alive.
        if (!IsPlayerAlive(info.Source)) return;
        if (!IsPlayerAlive(target)) return;

        NetworkObject victimObject = Runner.GetPlayerObject(target);
        if (victimObject != null && victimObject.TryGetComponent(out PlayerState state))
        {
            state.Kill();
            LastNightVictim = target;
            Debug.Log($"[NightActionManager] Mafia ({info.Source}) killed {target} instantly.");

            // A kill can end the game right there (last non-Mafia dies, etc.) —
            // check the instant it actually lands. The Doctor's post-Night save
            // (if it happens) will call this again after resolving.
            gm.CheckWinCondition();
        }
    }

    // Called by ANY living player's own client (Mafia included, same as Among Us —
    // an impostor reporting their own kill to look innocent is legitimate) the
    // instant they press Report while standing next to an unreported body.
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_ReportBody(PlayerRef body, RpcInfo info = default)
    {
        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.CurrentPhase != NetworkGameManager.GamePhase.Night) return;

        // A dead player can't report anything — same "eliminated player can still
        // act" defense used everywhere else in this file.
        if (!IsPlayerAlive(info.Source)) return;

        NetworkObject bodyObject = Runner.GetPlayerObject(body);
        if (bodyObject == null || !bodyObject.TryGetComponent(out PlayerState state)) return;

        // Must actually be a body (not a living player), and not already reported —
        // otherwise the same corpse could be reported over and over, re-triggering
        // Discussion every time someone walks past it again in a later Night.
        if (state.IsAlive || state.HasBeenReported) return;

        state.MarkReported();
        Debug.Log($"[NightActionManager] {info.Source} reported {body}'s body!");

        gm.ForceDiscussion();
    }

    // THE DOCTOR'S ONE REVIVE. Called by the Doctor's own client during Discussion.
    // Brings any one dead player — killed at Night, voted out, or shot by the
    // Sheriff, it makes no difference — back into the match, permanently spending
    // the ability.
    //
    // Deliberately NOT restricted to non-Mafia targets: the Doctor has no more
    // information about anyone's role than a Civilian does, so reviving a Mafia by
    // mistake is a real and intended risk. The win check at the bottom exists
    // precisely because of that case — putting a Mafia back on the board can hand
    // them the match on the spot.
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_SubmitDoctorRevive(PlayerRef target, RpcInfo info = default)
    {
        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.CurrentPhase != NetworkGameManager.GamePhase.Discussion) return;

        // Server-side role check — a modified client must not be able to revive by
        // simply claiming to be the Doctor. Same reasoning as the Mafia kill above.
        if (PlayerRoleManager.Instance == null) return;
        if (PlayerRoleManager.Instance.GetServerRole(info.Source) != PlayerRole.Doctor) return;

        // A dead Doctor has no ability. Roles are assigned once and never cleared on
        // death, so without this an eliminated Doctor could still act from spectate.
        if (!IsPlayerAlive(info.Source)) return;

        if (DoctorReviveUsed) return; // once per match, and it is already spent

        NetworkObject targetObject = Runner.GetPlayerObject(target);
        if (targetObject == null || !targetObject.TryGetComponent(out PlayerState targetState)) return;

        // Only a genuinely dead player can be revived. Without this the Doctor could
        // burn the ability on a living player and silently get nothing for it.
        if (targetState.IsAlive) return;

        // The ability is spent the moment it is submitted, but the player does NOT
        // come back yet — they are only queued here and actually revived when
        // Discussion ends (see ResolvePendingRevive, called from
        // NetworkGameManager.AdvanceToNextPhase).
        //
        // The delay is the whole point. If the revive landed instantly, the player
        // would rejoin the very Discussion they were killed before and could
        // immediately say what happened to them — who they saw, where they died —
        // which is far too much information handed to the Town for one ability.
        // Coming back after Discussion means they return with their mouth shut for
        // that round.
        DoctorReviveUsed = true;
        PendingRevive = target;
        Debug.Log($"[NightActionManager] Doctor ({info.Source}) spent their one revive on {target} — they will come back when Discussion ends.");

        // Deliberately NO win check here: nobody's alive/dead state has actually
        // changed yet. The check happens in ResolvePendingRevive, once the revive
        // really lands.
    }

    // Server-only. Called by NetworkGameManager the moment Discussion ends, and
    // nowhere else. Applies whatever revive the Doctor queued during that
    // Discussion, then clears the queue.
    //
    // Note this resolves even if the Doctor themself died during that same
    // Discussion — e.g. shot by the Sheriff after submitting. The Doctor already
    // spent the ability; killing them afterwards does not undo a decision they had
    // already made.
    public void ResolvePendingRevive()
    {
        if (!Object.HasStateAuthority) return;
        if (PendingRevive == PlayerRef.None) return;

        PlayerRef target = PendingRevive;
        PendingRevive = PlayerRef.None; // clear first, so a failure below can't leave it stuck

        NetworkObject targetObject = Runner.GetPlayerObject(target);
        if (targetObject == null || !targetObject.TryGetComponent(out PlayerState targetState))
        {
            Debug.LogWarning($"[NightActionManager] Pending revive for {target} could not be applied — no player object.");
            return;
        }

        targetState.Revive();
        Debug.Log($"[NightActionManager] Discussion ended — {target} has been revived by the Doctor and is back in the match.");

        // Now the alive counts really have changed, and this can end the match
        // outright if the player brought back was Mafia.
        NetworkGameManager.Instance?.CheckWinCondition();
    }

    // THE SHERIFF'S ONE SHOT. Called by the Sheriff's own client during Discussion.
    // Hit a Mafia -> that Mafia dies. Hit anyone else -> the Sheriff dies instead.
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_SubmitSheriffShot(PlayerRef target, RpcInfo info = default)
    {
        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.CurrentPhase != NetworkGameManager.GamePhase.Discussion) return;

        if (PlayerRoleManager.Instance == null) return;
        if (PlayerRoleManager.Instance.GetServerRole(info.Source) != PlayerRole.Sheriff) return;
        if (!IsPlayerAlive(info.Source)) return;

        if (SheriffShotUsed) return; // once per match, and it is already spent

        // Shooting yourself is not a play, it is a bug waiting to happen — the
        // backfire branch below would kill the shooter either way, so an accidental
        // self-click would silently read as a "wrong guess".
        if (target == info.Source) return;

        // Only living players are valid targets. Shooting a corpse would otherwise
        // count as hitting a non-Mafia and backfire onto the Sheriff.
        if (!IsPlayerAlive(target)) return;

        NetworkObject targetObject = Runner.GetPlayerObject(target);
        if (targetObject == null || !targetObject.TryGetComponent(out PlayerState targetState)) return;

        SheriffShotUsed = true;

        bool targetIsMafia = PlayerRoleManager.Instance.GetServerRole(target) == PlayerRole.Mafia;
        if (targetIsMafia)
        {
            targetState.Kill();
            Debug.Log($"[NightActionManager] Sheriff ({info.Source}) shot {target} — correct, they were Mafia and are eliminated.");
        }
        else
        {
            // Backfire. Note this covers every non-Mafia role, not just Civilian:
            // shooting the Doctor, or any future Town role, is just as wrong.
            NetworkObject sheriffObject = Runner.GetPlayerObject(info.Source);
            if (sheriffObject != null && sheriffObject.TryGetComponent(out PlayerState sheriffState))
            {
                sheriffState.Kill();
            }
            Debug.Log($"[NightActionManager] Sheriff ({info.Source}) shot {target} — wrong, they were not Mafia. The Sheriff dies instead.");
        }

        // Either branch removes a living player, which can end the match outright.
        gm.CheckWinCondition();
    }

    // Server-only. Called by NetworkGameManager right as Night ends — hides
    // tonight's victim's body regardless of what happens next (Doctor save may
    // still revive them, but the body itself never lingers into DoctorSave/
    // Discussion).
    public void HideNightVictimBody()
    {
        if (!Object.HasStateAuthority) return;
        if (LastNightVictim == PlayerRef.None) return;

        NetworkObject obj = Runner.GetPlayerObject(LastNightVictim);
        if (obj != null && obj.TryGetComponent(out PlayerState state))
        {
            state.SetBodyVisible(false);
        }
    }

    // Server-only helper for NetworkGameManager — whether anyone was actually
    // killed this Night (used to decide whether DoctorSave has anything to do).
    public bool HasNightVictim => LastNightVictim != PlayerRef.None;

    // Server-only: called by NetworkGameManager.BeginMatch() and NOWHERE ELSE.
    // Both of these abilities are once per MATCH — wiring this into any per-round
    // transition would hand the Doctor a fresh revive and the Sheriff a fresh shot
    // every cycle, silently turning two one-shot abilities into repeating ones.
    public void ResetOncePerMatchAbilities()
    {
        if (!Object.HasStateAuthority) return;
        DoctorReviveUsed = false;
        SheriffShotUsed = false;
        PendingRevive = PlayerRef.None;
    }

    // Shared alive-check helper — PlayerState.IsAlive (see PlayerState.cs) is the
    // single source of truth for alive/dead, same as VoteManager.IsEliminated().
    private bool IsPlayerAlive(PlayerRef player)
    {
        NetworkObject obj = Runner.GetPlayerObject(player);
        return obj != null && obj.TryGetComponent(out PlayerState state) && state.IsAlive;
    }

    // Called by NetworkGameManager (server-only) the moment a fresh Night phase
    // actually starts — clears last Night's victim so DoctorSave's guess list
    // (and HasNightVictim) never leaks a stale answer into a new round.
    public void ResetForNewNight()
    {
        if (!Object.HasStateAuthority) return;
        LastNightVictim = PlayerRef.None;
    }
}
