using System.Collections.Generic;
using System.Linq;
using System.Text;
using Fusion;
using UnityEngine;

public partial class NetworkGameManager
{
    public const int ChatMaxLength = 180;
    public readonly List<string> ChatMessages = new List<string>();
    public int ChatRevision { get; private set; }
    private readonly Dictionary<PlayerRef, float> _chatLastSent = new Dictionary<PlayerRef, float>();
    public bool ChatOpen => IsReady && Object != null && Object.IsValid &&
        (CurrentPhase == GamePhase.Discussion || CurrentPhase == GamePhase.Voting);

    public static string CleanChat(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > ChatMaxLength) return string.Empty;
        var result = new StringBuilder(value.Length);
        foreach (char c in value)
            if (!char.IsControl(c) && c != '<' && c != '>') result.Append(c);
        return result.ToString().Trim();
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_SendDiscussionChat(string message, RpcInfo info = default)
    {
        if (!ChatOpen) return;
        var sender = info.Source;
        if (!Runner.ActivePlayers.Contains(sender)) return;
        var player = Runner.GetPlayerObject(sender);
        if (player == null || !player.TryGetComponent(out PlayerState state) || !state.IsAlive) return;
        string clean = CleanChat(message);
        if (clean.Length == 0) return;
        float now = Time.unscaledTime;
        if (_chatLastSent.TryGetValue(sender, out float last) && now - last < 1f) return;
        _chatLastSent[sender] = now;
        RPC_ReceiveDiscussionChat(sender.PlayerId, clean);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_ReceiveDiscussionChat(int playerId, string message)
    {
        // Plain text only; no roles or disguised identity are exposed.
        ChatMessages.Add("P" + playerId + ": " + message);
        if (ChatMessages.Count > 40) ChatMessages.RemoveAt(0);
        ChatRevision++;
    }
}
