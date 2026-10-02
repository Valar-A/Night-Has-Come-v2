using Fusion;
using UnityEngine;

public struct CraftInventory : INetworkStruct
{
    public int Metal, Wire, Chemical, Coins;
    public NetworkBool Shield, Crafted;
}

public struct TaskWork : INetworkStruct
{
    public byte Node;
    public TickTimer Timer;
}

public partial class TaskManager
{
    public const float WorkSeconds = 5f;
    [Header("Civilian crafting balance")]
    [SerializeField, Min(1)] private int metalCost = 3;
    [SerializeField, Min(1)] private int wireCost = 3;
    [SerializeField, Min(1)] private int chemicalCost = 3;
    [SerializeField, Min(1)] private int resourceReward = 1;
    [SerializeField, Min(0)] private int coinsPerResource = 1;
    [Networked, Capacity(16)] private NetworkDictionary<PlayerRef, CraftInventory> Inventories => default;
    [Networked, Capacity(16)] private NetworkDictionary<PlayerRef, TaskWork> Work => default;
    [Networked] public NetworkBool ShieldClaimed { get; private set; }
    [Networked] public NetworkBool ResourcesSettled { get; private set; }
    [Networked] public NetworkBool TasksAssigned { get; private set; }
    [Networked] public NetworkString<_64> RewardMatchId { get; private set; }
    public string Recipe => $"{metalCost} metal / {wireCost} wire / {chemicalCost} chemical";

    private void ResetCrafting()
    {
        Inventories.Clear();
        Work.Clear();
        ShieldClaimed = false;
        ResourcesSettled = false;
        RewardMatchId = System.Guid.NewGuid().ToString("N");
        foreach (var player in Runner.ActivePlayers) Inventories.Set(player, default);
    }

    public bool TryGetInventory(PlayerRef player, out CraftInventory inventory)
    {
        inventory = default;
        return Object != null && Object.IsValid && Inventories.TryGet(player, out inventory);
    }

    public bool TryGetWork(PlayerRef player, out TaskWork work)
    {
        work = default;
        return Object != null && Object.IsValid && Work.TryGet(player, out work);
    }

    private bool CanWork(PlayerRef player, byte index)
    {
        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || !NetworkGameManager.IsFreeRoamPhase(gm.CurrentPhase)) return false;
        var obj = Runner.GetPlayerObject(player);
        if (obj == null || !obj.TryGetComponent(out PlayerState state) || !state.IsAlive) return false;
        if (!Assignments.TryGet(player, out var assignment)) return false;
        bool outstanding = false;
        for (int i = 0; i < TasksPerPlayer; i++)
            if (assignment.GetSlot(i) == index && !assignment.IsSlotCompleted(i)) outstanding = true;
        return outstanding && TaskNodeRegistry.TryGetNode(index, out var node)
            && Vector3.Distance(obj.transform.position, node.transform.position) <= node.InteractRadius + .25f;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority, HostMode = RpcHostMode.SourceIsHostPlayer)]
    public void RPC_StartTask(byte index, RpcInfo info = default)
    {
        if (Work.ContainsKey(info.Source) || !CanWork(info.Source, index)) return;
        Work.Set(info.Source, new TaskWork { Node = index, Timer = TickTimer.CreateFromSeconds(Runner, WorkSeconds) });
    }

    public override void FixedUpdateNetwork()
    {
        if (!Object.HasStateAuthority) return;
        var gm = NetworkGameManager.Instance;
        if (!TasksAssigned && gm != null && gm.IsReady
            && (gm.CurrentPhase == NetworkGameManager.GamePhase.RoleAssign || NetworkGameManager.IsFreeRoamPhase(gm.CurrentPhase))
            && TaskNodeRegistry.Resolve()) AssignTasks();
        // Snapshot keys: completion/cancellation removes entries from the dictionary.
        var players = new System.Collections.Generic.List<PlayerRef>();
        foreach (var entry in Work) players.Add(entry.Key);
        foreach (var player in players)
        {
            if (!Work.TryGet(player, out var work)) continue;
            if (!CanWork(player, work.Node)) { Work.Remove(player); continue; }
            if (!work.Timer.Expired(Runner)) continue;
            Work.Remove(player);
            CompleteTask(work.Node, player);
        }
    }

    private bool IsCivilian(PlayerRef player) => PlayerRoleManager.Instance != null
        && PlayerRoleManager.Instance.GetServerRole(player) == PlayerRole.Civilian;

    private void RewardResources(PlayerRef player)
    {
        if (!IsCivilian(player) || ResourcesSettled) return;
        Inventories.TryGet(player, out var inventory);
        int reward = Mathf.Max(1, resourceReward);
        inventory.Metal += reward;
        inventory.Wire += reward;
        inventory.Chemical += reward;
        Inventories.Set(player, inventory);
    }

    public bool CanAfford(CraftInventory inventory) => inventory.Metal >= Mathf.Max(1, metalCost)
        && inventory.Wire >= Mathf.Max(1, wireCost) && inventory.Chemical >= Mathf.Max(1, chemicalCost);

    [Rpc(RpcSources.All, RpcTargets.StateAuthority, HostMode = RpcHostMode.SourceIsHostPlayer)]
    public void RPC_CraftShield(RpcInfo info = default)
    {
        var gm = NetworkGameManager.Instance;
        if (ShieldClaimed || ResourcesSettled || gm == null || !gm.IsReady
            || !NetworkGameManager.IsFreeRoamPhase(gm.CurrentPhase) || !IsCivilian(info.Source)) return;
        var obj = Runner.GetPlayerObject(info.Source);
        if (obj == null || !obj.TryGetComponent(out PlayerState state) || !state.IsAlive
            || !CraftingStation.IsNear(obj.transform) || !Inventories.TryGet(info.Source, out var inventory)
            || !CanAfford(inventory)) return;
        // Claim and spend in one authoritative call; simultaneous requests cannot both win.
        ShieldClaimed = true;
        inventory.Metal -= Mathf.Max(1, metalCost);
        inventory.Wire -= Mathf.Max(1, wireCost);
        inventory.Chemical -= Mathf.Max(1, chemicalCost);
        inventory.Shield = true;
        inventory.Crafted = true;
        Inventories.Set(info.Source, inventory);
    }

    public bool TryAbsorbMafiaAttack(PlayerRef player)
    {
        if (!Object.HasStateAuthority || !Inventories.TryGet(player, out var inventory) || !inventory.Shield) return false;
        inventory.Shield = false;
        Inventories.Set(player, inventory);
        return true;
    }

    public void SettleResources()
    {
        if (!Object.HasStateAuthority || ResourcesSettled) return;
        ResourcesSettled = true;
        Work.Clear();
        var players = new System.Collections.Generic.List<PlayerRef>();
        foreach (var entry in Inventories) players.Add(entry.Key);
        foreach (var player in players)
        {
            var inventory = Inventories[player];
            inventory.Coins += (inventory.Metal + inventory.Wire + inventory.Chemical) * Mathf.Max(0, coinsPerResource);
            inventory.Metal = inventory.Wire = inventory.Chemical = 0;
            Inventories.Set(player, inventory);
        }
    }
}
