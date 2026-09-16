using System.Collections.Generic;
using System.Linq;
using Fusion;
using UnityEngine;

// Attach this to the same GameObject as NetworkGameManager (the scene NetworkObject).
// Handles vote submission, tallying, and elimination for the Voting phase.
//
// ASSUMPTIONS (flag these if they don't match your project — adjust accordingly):
//   1. Players cannot vote for themselves (RPC_SubmitVote rejects voter == target).
//   2. A player CAN change their vote (or switch between voting and skipping) any
//      time before the phase ends — Votes.Set() overwrites their previous entry.
//      If you want first-vote-locks-in instead, only call Set() when the key
//      doesn't already exist.
//   3. Tie for the most votes -> no elimination (per your design decision).
//   4. Players CAN skip via RPC_SkipVote(). A skip is recorded as PlayerRef.None
//      and still counts toward "has this player voted yet" for the early-advance
//      check below — it just contributes no votes to anyone's tally.
public class VoteManager : NetworkBehaviour
{
    [Networked, Capacity(16)]
    private NetworkDictionary<PlayerRef, PlayerRef> Votes => default;

    // CONFIRMED LIVE BUG this replaces: this used to be its own separate
    // [Networked] Eliminated NetworkDictionary, set only from ResolveVotes()
    // below. PlayerState.IsAlive (set by PlayerState.Kill()) is ALSO
    // "eliminated status" — but a player killed by the Mafia at Night
    // (NightActionManager.ResolveNight() calls state.Kill() directly, never
    // touching this class at all) never got an entry in this dictionary. That
    // meant a player who died at Night still read back as "not eliminated"
    // here: VoteManager kept accepting their own votes, and other players
    // could still vote for their corpse, in the very next Voting phase — the
    // eliminated player was fully able to keep "playing" the voting game
    // instead of being locked into spectate. PlayerState.IsAlive is the
    // single source of truth for alive/dead now, checked directly below, so
    // it can never go stale relative to whichever system did the killing.
    public bool IsEliminated(PlayerRef player)
    {
        NetworkObject obj = Runner.GetPlayerObject(player);
        if (obj == null || !obj.TryGetComponent(out PlayerState state))
        {
            return false; // no player object yet (e.g. still connecting) — don't treat as eliminated
        }
        return !state.IsAlive;
    }

    // Called by clients (including host, acting as a client) to cast/change a vote.
    // Runs on the server; validates before recording anything.
    // NOTE: voter comes from RpcInfo.Source (who actually sent the RPC), not a
    // client-supplied parameter — otherwise any client could pass someone else's
    // PlayerRef and vote on their behalf.
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_SubmitVote(PlayerRef target, RpcInfo info = default)
    {
        if (!Object.HasStateAuthority) return;

        PlayerRef voter = info.Source;

        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.CurrentPhase != NetworkGameManager.GamePhase.Voting)
        {
            Debug.LogWarning($"[VoteManager] Rejected vote from {voter}: not in Voting phase.");
            return;
        }

        if (IsEliminated(voter))
        {
            Debug.LogWarning($"[VoteManager] Rejected vote from {voter}: voter is eliminated.");
            return;
        }

        if (IsEliminated(target))
        {
            Debug.LogWarning($"[VoteManager] Rejected vote targeting {target}: target is eliminated.");
            return;
        }

        if (voter == target)
        {
            Debug.LogWarning($"[VoteManager] Rejected vote from {voter}: cannot vote for yourself.");
            return;
        }

        Votes.Set(voter, target);
        Debug.Log($"[VoteManager] {voter} voted for {target} ({Votes.Count}/{AlivePlayerCount()} votes in).");

        TryResolveEarly();
    }

    // Called by clients who want to abstain instead of voting for a target.
    // Recorded as PlayerRef.None so it still counts toward "everyone has voted"
    // for the early phase-advance check, but contributes nothing to the tally.
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_SkipVote(RpcInfo info = default)
    {
        if (!Object.HasStateAuthority) return;

        PlayerRef voter = info.Source;

        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.CurrentPhase != NetworkGameManager.GamePhase.Voting)
        {
            Debug.LogWarning($"[VoteManager] Rejected skip from {voter}: not in Voting phase.");
            return;
        }

        if (IsEliminated(voter))
        {
            Debug.LogWarning($"[VoteManager] Rejected skip from {voter}: voter is eliminated.");
            return;
        }

        Votes.Set(voter, PlayerRef.None);
        Debug.Log($"[VoteManager] {voter} skipped their vote ({Votes.Count}/{AlivePlayerCount()} votes in).");

        TryResolveEarly();
    }

    // If every alive player has voted, skip the rest of the Voting timer and
    // move straight to tallying instead of making everyone wait.
    private void TryResolveEarly()
    {
        if (Votes.Count >= AlivePlayerCount())
        {
            NetworkGameManager.Instance.ForceAdvancePhase();
        }
    }

    private int AlivePlayerCount()
    {
        return Runner.ActivePlayers.Count(p => !IsEliminated(p));
    }

    // Server-only. Called by NetworkGameManager when leaving the Voting phase.
    // Tallies votes, eliminates the sole top target (if there is one), clears
    // the vote list for next time, and reports who (if anyone) was eliminated.
    public PlayerRef? ResolveVotes()
    {
        if (!Object.HasStateAuthority) return null;

        PlayerRef? eliminated = null;

        if (Votes.Count > 0)
        {
            var tally = new Dictionary<PlayerRef, int>();
            foreach (var kvp in Votes)
            {
                if (kvp.Value == PlayerRef.None) continue; // skipped — contributes nothing

                tally.TryGetValue(kvp.Value, out int count);
                tally[kvp.Value] = count + 1;
            }

            if (tally.Count > 0)
            {
                int maxVotes = tally.Values.Max();
                var topCandidates = tally.Where(kv => kv.Value == maxVotes).Select(kv => kv.Key).ToList();

                if (topCandidates.Count == 1)
                {
                    eliminated = topCandidates[0];
                    // NOTE: PlayerState.Kill() (which is what actually flips IsAlive —
                    // the single source of truth IsEliminated() above reads — to false)
                    // is called by NetworkGameManager right after this method returns.
                    // Nothing else needs to happen here now that there's no separate
                    // Eliminated dictionary to keep in sync.
                }
                // else: tie for most votes -> no elimination.
            }
            // If everyone skipped, tally is empty -> no elimination.
        }

        Votes.Clear();
        return eliminated;
    }
}
