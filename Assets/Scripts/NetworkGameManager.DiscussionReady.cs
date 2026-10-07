using Fusion;

public partial class NetworkGameManager
{
    [Networked, Capacity(10)]
    private NetworkDictionary<PlayerRef, NetworkBool> DiscussionReady => default;

    public int DiscussionLivingCount
    {
        get
        {
            int count = 0;
            foreach (var p in Runner.ActivePlayers) if (IsLivingParticipant(p)) count++;
            return count;
        }
    }

    public int DiscussionReadyCount
    {
        get
        {
            int count = 0;
            foreach (var p in Runner.ActivePlayers)
                if (IsLivingParticipant(p) && HasRequestedVoting(p)) count++;
            return count;
        }
    }

    public bool HasRequestedVoting(PlayerRef player) =>
        DiscussionReady.TryGet(player, out var ready) && ready;

    private bool IsLivingParticipant(PlayerRef player)
    {
        var obj = Runner.GetPlayerObject(player);
        return obj != null && obj.TryGetComponent(out PlayerState state) && state.IsAlive;
    }

    private bool EveryoneReadyForVoting()
    {
        int living = DiscussionLivingCount;
        return living > 0 && DiscussionReadyCount == living;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority, HostMode = RpcHostMode.SourceIsHostPlayer)]
    public void RPC_ContinueToVoting(RpcInfo info = default)
    {
        if (CurrentPhase != GamePhase.Discussion || !IsLivingParticipant(info.Source)) return;
        // Idempotent: repeated clicks count as one request, with no client-supplied identity.
        DiscussionReady.Set(info.Source, true);
    }
}
