using System.Collections.Generic;
using System.Linq;
using Fusion;
using UnityEngine;

// One player's whole task list for the match, in a form Fusion can replicate.
//
// Six explicit byte fields instead of an array because this struct lives inside a
// NetworkDictionary value, where only plain unmanaged fields are allowed — a
// NetworkArray cannot be nested in one. Six is small enough that the switch below
// is honest rather than clever.
public struct TaskAssignment : INetworkStruct
{
    public byte Slot0, Slot1, Slot2, Slot3, Slot4, Slot5;

    // One bit per slot. Six slots, so a single byte covers it with room spare.
    public byte CompletedMask;

    // bit 0 = this player's tasks are FAKE (they are Mafia).
    //
    // A fake list is deliberately indistinguishable from a real one everywhere a
    // player can see it: same nodes, same prompt, same hold time, same tick in
    // their own HUD. The ONLY difference is that finishing one does not move the
    // global counter. That is the whole mechanic — a Mafia has to be able to
    // stand at a station and look exactly like someone doing their job.
    public byte Flags;

    public byte GetSlot(int index) => index switch
    {
        0 => Slot0,
        1 => Slot1,
        2 => Slot2,
        3 => Slot3,
        4 => Slot4,
        5 => Slot5,
        _ => TaskManager.NoTask,
    };

    public void SetSlot(int index, byte value)
    {
        switch (index)
        {
            case 0: Slot0 = value; break;
            case 1: Slot1 = value; break;
            case 2: Slot2 = value; break;
            case 3: Slot3 = value; break;
            case 4: Slot4 = value; break;
            case 5: Slot5 = value; break;
        }
    }

    public bool IsSlotCompleted(int index) => (CompletedMask & (1 << index)) != 0;
    public void MarkSlotCompleted(int index) => CompletedMask |= (byte)(1 << index);

    public bool IsFake
    {
        get => (Flags & 1) != 0;
        set => Flags = (byte)(value ? (Flags | 1) : (Flags & ~1));
    }

    public int CompletedCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < TaskManager.TasksPerPlayer; i++)
            {
                if (GetSlot(i) != TaskManager.NoTask && IsSlotCompleted(i)) n++;
            }
            return n;
        }
    }
}

// Attach to the SAME "GameManager" prefab that already carries NetworkGameManager,
// PlayerRoleManager, NightActionManager and VoteManager.
//
// Owns: who has which tasks, which of them are done, and the single global
// "tasks completed" number the HUD shows.
public partial class TaskManager : NetworkBehaviour
{
    public const int TasksPerPlayer = 6;     // 1 common + 5 assigned
    public const int AssignedPerPlayer = 5;
    public const byte NoTask = 255;

    [Networked, Capacity(16)]
    private NetworkDictionary<PlayerRef, TaskAssignment> Assignments => default;

    // Deliberately a COUNT and not a fraction.
    //
    // Showing "12 / 48" would hand the Town the crew size for free, and crew size
    // minus player count is the Mafia count. The whole reason Mafia get fake tasks
    // is so the bar cannot be used to deduce how many of them there are, and a
    // denominator would give that away in the first ten seconds of the match. See
    // the Task Board HUD for how this gets presented.
    [Networked] public int TasksCompletedThisMatch { get; private set; }

    // Which common task everybody was given this match. Networked so a late-
    // resolving client, or a new Host after a migration, can still name it.
    [Networked] public byte CommonTaskIndex { get; private set; }

    public static TaskManager Instance { get; private set; }

    // Raised locally on every peer when the completed count changes — the HUD
    // hooks this instead of polling.
    public event System.Action OnProgressChanged;

    private int _lastSeenCompleted = -1;

    public override void Spawned()
    {
        Instance = this;
        TaskNodeRegistry.Forget();
    }

    public override void Render()
    {
        if (ResourcesSettled && TryGetInventory(Runner.LocalPlayer, out var inventory))
            LocalCoinWallet.CreditOnce(RewardMatchId.ToString(), inventory.Coins);
        if (TasksCompletedThisMatch != _lastSeenCompleted)
        {
            _lastSeenCompleted = TasksCompletedThisMatch;
            OnProgressChanged?.Invoke();
        }
    }

    // =======================================================================
    // ASSIGNMENT — server only, once per match
    // =======================================================================

    // Called from NetworkGameManager.BeginMatch(), right after roles are dealt.
    //
    // Tasks are for the WHOLE MATCH, not per round — same as the genre standard.
    // A player works through their six over however many Days they survive, which
    // is what makes "he still hasn't finished a single task on day three" mean
    // something.
    public void AssignTasks()
    {
        if (!Object.HasStateAuthority) return;

        Assignments.Clear();
        ResetCrafting();
        TasksCompletedThisMatch = 0;
        _lastSeenCompleted = -1;

        TasksAssigned = false;
        if (!TaskNodeRegistry.Resolve()) return; // GameplayScene loads after BeginMatch.

        // One common task for everyone, picked fresh each match.
        CommonTaskIndex = TaskNodeRegistry.PickRandomCommonIndex();

        PlayerRoleManager roles = PlayerRoleManager.Instance;

        foreach (PlayerRef player in Runner.ActivePlayers)
        {
            bool isMafia = roles != null && roles.GetServerRole(player) == PlayerRole.Mafia;
            Assignments.Set(player, BuildAssignment(isMafia));
        }

        int real = Assignments.Count(kv => !kv.Value.IsFake);
        TasksAssigned = true;
        Debug.Log($"[TaskManager] Dealt {TasksPerPlayer} task(s) each to {Assignments.Count} player(s) — {real} real list(s), {Assignments.Count - real} fake. Common task this match: {TaskNodeRegistry.GetDisplayName(CommonTaskIndex)}.");
    }

    private TaskAssignment BuildAssignment(bool fake)
    {
        var assignment = new TaskAssignment { Flags = 0 };
        for (int i = 0; i < TasksPerPlayer; i++) assignment.SetSlot(i, NoTask);
        assignment.IsFake = fake;

        // Slot 0 is always the common task.
        assignment.SetSlot(0, CommonTaskIndex);

        // Slots 1..5 are route-balanced: one task per floor, floors visited in a
        // shuffled order.
        //
        // Drawing five at random from the pool of 25 would regularly hand someone
        // four basement tasks and one on the roof — which reads as broken rather
        // than unlucky, and makes "where were you?" much less informative because
        // half the building never has anybody in it. One per floor guarantees every
        // player's route crosses the whole school, so everyone has a plausible
        // reason to be anywhere, which is exactly the ambiguity this game runs on.
        List<int> floors = TaskNodeRegistry.FloorsWithAssignedNodes();
        Shuffle(floors);

        var used = new HashSet<byte>();
        int slot = 1;
        int guard = 0;

        while (slot <= AssignedPerPlayer && guard < 200)
        {
            guard++;
            int floor = floors[(slot - 1) % floors.Count];

            if (TaskNodeRegistry.TryPickAssignedOnFloor(floor, used, out byte nodeIndex))
            {
                assignment.SetSlot(slot, nodeIndex);
                used.Add(nodeIndex);
                slot++;
            }
            else
            {
                // That floor is exhausted for this player — fall back to anything
                // still unused anywhere, rather than leaving an empty slot.
                if (TaskNodeRegistry.TryPickAnyAssigned(used, out byte anyIndex))
                {
                    assignment.SetSlot(slot, anyIndex);
                    used.Add(anyIndex);
                    slot++;
                }
                else
                {
                    Debug.LogError($"[TaskManager] Ran out of task nodes while filling slot {slot} — the level has only {TaskNodeRegistry.AssignedCount} assigned node(s) but each player needs {AssignedPerPlayer}.");
                    break;
                }
            }
        }

        return assignment;
    }

    private void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // =======================================================================
    // COMPLETION
    // =======================================================================

    // Sent by a client the moment its local hold-to-work finishes. Everything the
    // client claims is re-checked here; nothing about this RPC is trusted.
    private void CompleteTask(byte nodeIndex, PlayerRef sender)
    {
        if (!Object.HasStateAuthority) return;

        var gm = NetworkGameManager.Instance;
        if (gm == null || !NetworkGameManager.IsFreeRoamPhase(gm.CurrentPhase))
        {
            Debug.LogWarning($"[TaskManager] Rejected task completion from {sender} — tasks can only be worked on during Day (phase is {(gm != null ? gm.CurrentPhase.ToString() : "unknown")}).");
            return;
        }

        NetworkObject senderObject = Runner.GetPlayerObject(sender);
        if (senderObject == null)
        {
            Debug.LogWarning($"[TaskManager] Rejected task completion — no player object for {sender}.");
            return;
        }

        if (senderObject.TryGetComponent(out PlayerState state) && !state.IsAlive)
        {
            Debug.LogWarning($"[TaskManager] Rejected task completion from {sender} — they are dead.");
            return;
        }

        if (!Assignments.TryGet(sender, out TaskAssignment assignment))
        {
            Debug.LogWarning($"[TaskManager] Rejected task completion from {sender} — they have no task list.");
            return;
        }

        // Is this node actually on their list, and still outstanding?
        int slot = -1;
        for (int i = 0; i < TasksPerPlayer; i++)
        {
            if (assignment.GetSlot(i) == nodeIndex) { slot = i; break; }
        }

        if (slot < 0)
        {
            Debug.LogWarning($"[TaskManager] Rejected task completion from {sender} — node {nodeIndex} is not on their list.");
            return;
        }

        if (assignment.IsSlotCompleted(slot)) return; // already done, nothing to say

        // THE distance check. Without it the RPC is an "I finished everything"
        // button: a modified client could fire one per node from the lobby and
        // clear its whole list before anyone left the spawn room. The client's own
        // proximity test is for showing a prompt; this one is what actually
        // decides, and it uses the position the SERVER has for that player.
        if (!TaskNodeRegistry.TryGetNode(nodeIndex, out TaskNodeMarker node))
        {
            Debug.LogWarning($"[TaskManager] Rejected task completion from {sender} — node {nodeIndex} does not exist in this level.");
            return;
        }

        float distance = Vector3.Distance(senderObject.transform.position, node.transform.position);
        float allowed = node.InteractRadius + DistanceTolerance;
        if (distance > allowed)
        {
            Debug.LogWarning($"[TaskManager] Rejected task completion from {sender} — they are {distance:F1}m from '{node.DisplayName}', limit is {allowed:F1}m.");
            return;
        }

        assignment.MarkSlotCompleted(slot);
        Assignments.Set(sender, assignment);

        // A fake list moves the player's own HUD but never the global counter.
        if (!assignment.IsFake)
        {
            TasksCompletedThisMatch++;
            RewardResources(sender);
        }

        Debug.Log($"[TaskManager] {sender} completed '{node.DisplayName}' (slot {slot}{(assignment.IsFake ? ", FAKE" : "")}). Global completed: {TasksCompletedThisMatch}.");
    }

    // Generous on purpose. The client decides to send at the moment its own
    // predicted position was in range, and by the time the RPC lands the server
    // may have simulated the player a little further on — plus they are allowed to
    // be walking away as the bar fills. Anything under about a metre of slack
    // produces completions that fail for no reason the player can see, which is
    // far worse than a cheater gaining 1.5m of reach.
    private const float DistanceTolerance = 1.5f;

    // =======================================================================
    // LOCAL READS — for the HUD and the interaction prompt
    // =======================================================================

    public bool TryGetLocalAssignment(out TaskAssignment assignment)
    {
        assignment = default;
        if (Object == null || !Object.IsValid || Runner == null) return false;
        return Assignments.TryGet(Runner.LocalPlayer, out assignment);
    }

    public bool TryGetAssignment(PlayerRef player, out TaskAssignment assignment)
    {
        assignment = default;
        if (Object == null || !Object.IsValid) return false;
        return Assignments.TryGet(player, out assignment);
    }

    // How many of the local player's own six are done — this one IS shown as a
    // fraction, because it only ever reveals information about yourself.
    public bool TryGetLocalProgress(out int completed, out int total)
    {
        completed = 0;
        total = 0;
        if (!TryGetLocalAssignment(out TaskAssignment a)) return false;

        for (int i = 0; i < TasksPerPlayer; i++)
        {
            if (a.GetSlot(i) == NoTask) continue;
            total++;
            if (a.IsSlotCompleted(i)) completed++;
        }
        return true;
    }
}
