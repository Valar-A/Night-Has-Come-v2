using Fusion;
using UnityEngine;

// TEMP DEBUG TOOL — delete once the real voting UI exists.
//
// Attach this to any GameObject in the scene (e.g. the same UIManager object).
// Draws a simple OnGUI panel so you can test the Voting phase end-to-end in the
// two-window ParrelSync setup without having built player-list UI yet:
//   - Host-only: a button to force-jump straight to the Voting phase.
//   - Everyone: a "Vote for <PlayerRef>" button per active player, plus Skip.
// Run this in both the host and client windows and click around to simulate votes.
public class DebugVoteTester : MonoBehaviour
{
    private VoteManager _voteManager;
    private Vector2 _scroll;

    private void Awake()
    {
        _voteManager = FindObjectOfType<VoteManager>();
    }

    private void OnGUI()
    {
        var gm = NetworkGameManager.Instance;
        // IsReady MUST be checked before touching any [Networked] property like
        // CurrentPhase — accessing one before Spawned() has run throws
        // InvalidOperationException. This was missing here, so OnGUI (which runs
        // every GUI event, not just once a frame) threw repeatedly during the brief
        // window right after a Host Migration scene reload, before the resumed
        // NetworkGameManager had finished spawning on this peer.
        if (gm == null || !gm.IsReady || _voteManager == null)
        {
            GUI.Label(new Rect(10, 10, 300, 20), "[DebugVoteTester] Waiting for NetworkGameManager/VoteManager...");
            return;
        }

        GUILayout.BeginArea(new Rect(10, 10, 320, 400), GUI.skin.box);
        GUILayout.Label($"Phase: {gm.CurrentPhase}  (local player: {gm.Runner.LocalPlayer})");
        GUILayout.Label($"Remaining: {gm.GetRemainingTime():F1}s");

#if UNITY_EDITOR
        if (gm.Object.HasStateAuthority && GUILayout.Button("[HOST] Force phase -> Voting"))
        {
            gm.DebugForcePhase(NetworkGameManager.GamePhase.Voting);
        }

        if (gm.Object.HasStateAuthority && GUILayout.Button("[HOST] Force phase -> Lobby"))
        {
            gm.DebugForcePhase(NetworkGameManager.GamePhase.Lobby);
        }
#endif

        GUILayout.Space(10);

        if (gm.CurrentPhase != NetworkGameManager.GamePhase.Voting)
        {
            GUILayout.Label("Not in Voting phase — vote buttons hidden.");
            GUILayout.EndArea();
            return;
        }

        // CONFIRMED bug class fix — same as the real VotingUIManager: this used to
        // only disable the button for each ELIMINATED TARGET, never checking
        // whether the LOCAL player casting the vote was themself eliminated. A
        // dead local player (voted out, or killed at Night) could still click
        // "Vote for X" here and, before VoteManager.IsEliminated() was fixed to
        // read PlayerState.IsAlive directly, that vote could even succeed for a
        // Night-killed local player. Gate the whole section on local alive status.
        bool localEliminated = _voteManager.IsEliminated(gm.Runner.LocalPlayer);
        if (localEliminated)
        {
            GUILayout.Label("You have been eliminated — spectating only.");
            GUILayout.EndArea();
            return;
        }

        GUILayout.Label("Cast a vote as the local player:");
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(200));
        foreach (var player in gm.Runner.ActivePlayers)
        {
            bool eliminated = _voteManager.IsEliminated(player);
            string label = $"Vote for {player}{(eliminated ? " (eliminated)" : "")}";

            GUI.enabled = !eliminated;
            if (GUILayout.Button(label))
            {
                _voteManager.RPC_SubmitVote(player);
            }
            GUI.enabled = true;
        }
        GUILayout.EndScrollView();

        if (GUILayout.Button("Skip vote"))
        {
            _voteManager.RPC_SkipVote();
        }

        GUILayout.EndArea();
    }
}
