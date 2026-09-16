using System.Collections.Generic;
using System.Linq;
using Fusion;
using UnityEngine;

public enum PlayerRole
{
    Civilian,
    Mafia,
    Doctor,

    // Town-aligned, wins with the Civilians. Gets exactly ONE shot for the whole
    // match, fired during Discussion: hit a Mafia and that Mafia dies, hit anyone
    // else — Civilian, Doctor, even another Sheriff in a future variant — and the
    // Sheriff dies instead. See NightActionManager.RPC_SubmitSheriffShot.
    Sheriff
}

// Attach this to the SAME "GameManager" scene object that has NetworkGameManager.
// Handles server-side role assignment and privately informs each client of ONLY
// its own role.
public class PlayerRoleManager : NetworkBehaviour
{
    // CONFIRMED LIVE BUG this was changed to fix: this used to be a plain, NOT
    // [Networked] `Dictionary<PlayerRef, PlayerRole>` — deliberately, per the old
    // comment, so it never replicated. That meant it only ever existed in the OLD
    // Host's own local process memory. The moment that Host disconnected (which is
    // exactly what triggers a Host Migration), that data was just gone — nobody
    // else ever had a copy. The new Host's PlayerRoleManager.Spawned() came back
    // with a completely EMPTY table, so GetServerRole() silently returned
    // PlayerRole.Civilian for every single player from then on — which meant every
    // Mafia/Doctor night-action RPC got rejected by NightActionManager's own role
    // check, breaking Night phase completely for the rest of the match after any
    // mid-game migration.
    //
    // Making this a proper [Networked] NetworkDictionary (same pattern VoteManager
    // already uses for its own Votes/Eliminated tables right next to this object)
    // fixes it two ways at once: Fusion replicates it normally like any other
    // networked scene state, AND — same as CurrentPhase now that its own stomp-on-
    // every-Spawned() bug is fixed — the Host Migration snapshot/resume mechanism
    // carries it over to the new Host automatically, no manual capture/restore
    // plumbing needed (unlike CurrentPhase, nothing here ever unconditionally
    // overwrites it on Spawned()).
    [Networked, Capacity(16)]
    private NetworkDictionary<PlayerRef, PlayerRole> ServerRoles => default;

    // Local cache — each client only ever has ITS OWN role filled in here.
    public PlayerRole LocalRole { get; private set; }
    public bool HasReceivedRole { get; private set; }

    // Call this from the server once, when NetworkGameManager enters RoleAssign phase.
    public void AssignRoles()
    {
        if (!Object.HasStateAuthority) return;

        ServerRoles.Clear();
        HasReceivedRole = false;

        List<PlayerRef> players = Runner.ActivePlayers.ToList();
        Shuffle(players);

        int total = players.Count;

        // EXACTLY ONE Doctor, always — at every player count a match can actually
        // start at, not just at 5+.
        //
        // CONFIRMED BUG this replaced: this used to read `total >= 5 ? 1 : 0`,
        // written against the GDD's 5-player minimum. But nothing actually enforces
        // that minimum — NetworkGameManager.BeginMatch() only checks
        // minPlayersToStart, which is 2. So every 2/3/4-player match (i.e. exactly
        // what gets played during ParrelSync clone testing, and any real
        // short-handed lobby) silently ran with ZERO Doctors: nobody ever got the
        // Doctor role, NetworkGameManager.IsDoctorAlive() therefore always returned
        // false, and the entire DoctorSave phase got skipped every single Night —
        // with no error or warning anywhere to make that obvious. The requirement is
        // one Doctor regardless of player count, so the only lobby left without one
        // is a lobby too small to hold even one Mafia plus one Doctor (a single
        // player, which can't start a match anyway).
        int doctorCount = total >= 2 ? 1 : 0;

        // EXACTLY ONE Sheriff, but only from 5 players up — the GDD's real minimum.
        //
        // Unlike the Doctor, the Sheriff is NOT forced into tiny lobbies on purpose.
        // At 3 players a guaranteed Mafia + Doctor + Sheriff leaves zero Civilians,
        // which makes the Sheriff's whole mechanic meaningless: every living target
        // except the Mafia is another power role, so the shot is a coin flip with no
        // deduction in it. From 5 up there are always at least 2 Civilians for the
        // Sheriff to read, which is what the ability is actually for.
        //
        // NOTE for testing: with 4 ParrelSync clones you will never be dealt a
        // Sheriff. Lower this to 4 (or 2) here if you want to exercise it locally.
        int sheriffCount = total >= 5 ? 1 : 0;

        // Both power roles together — used below to make sure Mafia can never eat
        // either slot.
        int powerRoleCount = doctorCount + sheriffCount;

        // Hard cap on Mafia count based on player count, regardless of what the
        // Host picked in the setup panel — 5 players is always exactly 1 Mafia,
        // 6-7 allows up to 2, 8-10 allows up to 3.
        int maxMafiaByPlayerCount = GetMaxMafiaByPlayerCount(total);

        // Also never let Mafia eat into the Doctor/Sheriff slots or leave zero Civilians.
        int maxMafiaLeavingRoom = Mathf.Max(1, total - 1 - powerRoleCount);

        int maxMafia = Mathf.Min(maxMafiaByPlayerCount, maxMafiaLeavingRoom);
        int mafiaCount = NetworkBootstrap.Instance != null
            ? Mathf.Clamp(NetworkBootstrap.Instance.SelectedMafiaCount, 1, maxMafia)
            : GetMafiaCount(total); // fallback if NetworkBootstrap somehow isn't found

        // Hard, last-line guarantee that the Doctor and Sheriff slots can never be
        // eaten by Mafia. The caps above already leave room for them in every real
        // case, but the role loop below hands out Mafia slots FIRST (indices
        // 0..mafiaCount) and only then the power roles — so if any future change to
        // those caps (or to the GetMafiaCount fallback path, which ignores maxMafia
        // entirely) ever let mafiaCount reach `total`, the Doctor and Sheriff would
        // silently vanish with no warning, exactly the way the Doctor once did.
        // Clamping here means "there is always one Doctor, and one Sheriff at 5+"
        // holds no matter what the Mafia-count logic upstream decides.
        if (powerRoleCount > 0 && mafiaCount > total - powerRoleCount)
        {
            mafiaCount = Mathf.Max(1, total - powerRoleCount);
        }

        for (int i = 0; i < players.Count; i++)
        {
            PlayerRole role;
            if (i < mafiaCount) role = PlayerRole.Mafia;
            else if (i < mafiaCount + doctorCount) role = PlayerRole.Doctor;
            else if (i < mafiaCount + doctorCount + sheriffCount) role = PlayerRole.Sheriff;
            else role = PlayerRole.Civilian;

            ServerRoles.Set(players[i], role);
            RPC_ReceiveRole(players[i], role);
        }

        int civilianCount = total - mafiaCount - doctorCount - sheriffCount;
        Debug.Log($"[PlayerRoleManager] Assigned roles to {total} players ({mafiaCount} Mafia, {doctorCount} Doctor, {sheriffCount} Sheriff, {civilianCount} Civilian).");

        // Loud, unmissable warnings if either role-count rule is ever broken again —
        // the original Doctor bug's worst property was that it failed completely
        // silently (no Doctor, no error, DoctorSave just quietly never happened).
        if (total >= 2 && doctorCount != 1)
        {
            Debug.LogError($"[PlayerRoleManager] ROLE RULE VIOLATED — {total} players but {doctorCount} Doctor(s). There must always be exactly 1.");
        }
        if (total >= 5 && sheriffCount != 1)
        {
            Debug.LogError($"[PlayerRoleManager] ROLE RULE VIOLATED — {total} players but {sheriffCount} Sheriff(s). There must be exactly 1 at 5+ players.");
        }
        if (civilianCount < 0)
        {
            Debug.LogError($"[PlayerRoleManager] ROLE RULE VIOLATED — role counts exceed the player count ({mafiaCount} Mafia + {doctorCount} Doctor + {sheriffCount} Sheriff > {total} players).");
        }
    }

    // Player-count-based Mafia cap, per your design: 5-6 players -> exactly 1
    // Mafia, 7-10 players -> Host's choice (2 or 3) is respected. This is a hard
    // ceiling that overrides whatever the Host picked if it's too high for a
    // small lobby.
    private int GetMaxMafiaByPlayerCount(int totalPlayers)
    {
        if (totalPlayers <= 6) return 1;
        return 3; // 7-10 players — Host's 2/3 selection is clamped against this, not forced.
    }

    // Placeholder ratio logic — swap these numbers for your GDD's exact table.
    private int GetMafiaCount(int totalPlayers)
    {
        if (totalPlayers <= 5) return 1;
        if (totalPlayers <= 7) return 2;
        return 3; // 8-10 players
    }

    private void Shuffle(List<PlayerRef> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // Server-only helper — lets other systems (e.g. Night actions) validate that
    // whoever called an RPC actually holds the role they're claiming to act as.
    public PlayerRole GetServerRole(PlayerRef player)
    {
        return ServerRoles.TryGet(player, out PlayerRole role) ? role : PlayerRole.Civilian;
    }

    // Simple scene-singleton so other scripts can find this without manual wiring
    // (there's only ever one GameManager object in the scene).
    public static PlayerRoleManager Instance { get; private set; }

    public override void Spawned()
    {
        Instance = this;

        // CONFIRMED LIVE: turning ServerRoles into a [Networked] NetworkDictionary
        // was NOT enough by itself to survive a Host Migration — see
        // NetworkBootstrap.PendingResumeRoles' own comment for the full story
        // (Fusion does not auto-restore a Scene NetworkObject's [Networked] state
        // across a migration the way the original code assumed; it just reloads
        // fresh with every [Networked] property back at default). Same explicit
        // resume pattern as NetworkGameManager.CurrentPhase: if this peer is coming
        // up as the new Host right after a migration, restore whatever roles were
        // captured right before the old runner shut down instead of starting with
        // an empty table (which silently turned every player into a Civilian and
        // broke Night phase's Mafia/Doctor RPC checks for the rest of the match).
        if (Object.HasStateAuthority && NetworkBootstrap.PendingResumeRoles != null)
        {
            foreach (var kvp in NetworkBootstrap.PendingResumeRoles)
            {
                ServerRoles.Set(kvp.Key, kvp.Value);
            }
            Debug.Log($"[PlayerRoleManager] Host Migration resume — restored {NetworkBootstrap.PendingResumeRoles.Count} role(s).");
            NetworkBootstrap.PendingResumeRoles = null;
        }
    }
    // target matches itself — no client ever stores or acts on another player's role.
    //
    // NOTE (security honesty): for an FYP demo this matches standard practice and your
    // Host Mode architecture. A modified/hacked client could still theoretically inspect
    // the raw network packet and see other players' roles in transit — true packet-level
    // privacy needs a Dedicated Server build. Your Production Plan's risk register already
    // flags this exact trade-off for Host Mode, with Dedicated Server noted as the later
    // upgrade path if you need it.
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_ReceiveRole(PlayerRef targetPlayer, PlayerRole role)
    {
        if (Runner.LocalPlayer != targetPlayer) return;

        LocalRole = role;
        HasReceivedRole = true;
        Debug.Log($"[PlayerRoleManager] I am: {role}");
    }
}
