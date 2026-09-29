using System.Linq;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach this to a "NightActionPanel" GameObject (child of Canvas). GameplayUIManager
// is the one turning this panel on/off (for living Mafia during free-roam) —
// this script drives the Kill button and replicated cooldown while
// the panel is active.
//
// Among Us style: no target-select list. The Mafia has to physically walk up next
// to another player — every frame, this script finds the nearest ALIVE player
// within killRange of the local player's own position and enables the Kill button
// only while someone is in range. Pressing it kills whoever is currently nearest.
//
// INSPECTOR SETUP:
//   - killButton: the single "Kill" button, anchored somewhere that doesn't block
//     the view (e.g. bottom-center), NOT behind a full-screen dimming panel — the
//     player still needs to see/move around the world to close the distance.
//   - statusText: TMP_Text for feedback ("No one nearby", "Press Kill to eliminate
//     P2", "Kill ready in 30s").
//   - killRange: how close (in world units) the Mafia needs to be to a target
//     before Kill lights up. Tune against your player capsule size / room scale.
public class NightActionUIManager : MonoBehaviour
{
    [SerializeField] private Button killButton;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private float killRange = 3f;

    private float _requestPendingUntil;
    private TMP_Text _buttonLabel;
    private PlayerRef? _nearbyTarget;
    private int _nearbyTargetLabelIndex;

    // Rebinding the panel never resets the server's cooldown.
    private void OnEnable()
    {
        _requestPendingUntil = 0f;
        _nearbyTarget = null;

        if (killButton != null)
        {
            killButton.onClick.RemoveListener(SubmitKill);
            killButton.onClick.AddListener(SubmitKill);
            _buttonLabel = killButton.GetComponentInChildren<TMP_Text>(true);
        }

        UpdateKillButtonState();
    }

    private void Update()
    {
        FindNearbyTarget();
        UpdateKillButtonState();
    }

    // Server authority does the actual alive/role/range validation again in
    // NightActionManager.RPC_SubmitMafiaTarget — this is purely a LOCAL, client-side
    // convenience scan to decide when to light up the button. A modified client
    // could light its own button whenever it wants; it still can't force a kill the
    // server itself wouldn't accept (see NightActionManager's own IsPlayerAlive/role
    // checks), same trust boundary as everywhere else in this codebase.
    private void FindNearbyTarget()
    {
        _nearbyTarget = null;

        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.Object == null || !gm.Object.IsValid || gm.Runner == null) return;

        NetworkObject localObj = gm.Runner.GetPlayerObject(gm.Runner.LocalPlayer);
        if (localObj == null) return;

        Vector3 myPos = localObj.transform.position;
        float range = Mathf.Min(killRange, PlayerState.KillRange);
        float closestSqrDist = range * range;
        PlayerRef? closest = null;
        int closestLabelIndex = 0;
        int index = 1;

        foreach (var player in gm.Runner.ActivePlayers.OrderBy(p => p.PlayerId))
        {
            int myLabelIndex = index; // capture before it's incremented for the next player
            index++;

            if (player == gm.Runner.LocalPlayer) continue;

            NetworkObject obj = gm.Runner.GetPlayerObject(player);
            if (obj == null || !obj.TryGetComponent(out PlayerState state) || !state.IsAlive) continue;

            float sqrDist = (obj.transform.position - myPos).sqrMagnitude;
            if (sqrDist <= closestSqrDist)
            {
                closestSqrDist = sqrDist;
                closest = player;
                closestLabelIndex = myLabelIndex;
            }
        }

        _nearbyTarget = closest;
        _nearbyTargetLabelIndex = closestLabelIndex;
    }

    private void SubmitKill()
    {
        if (!_nearbyTarget.HasValue || Time.unscaledTime < _requestPendingUntil || NightActionManager.Instance == null) return;
        var local = GetLocalState();
        if (local == null || !local.MafiaAbilitiesInitialized || local.KillCooldownRemaining > 0f) return;

        _requestPendingUntil = Time.unscaledTime + 1f;
        NightActionManager.Instance.RPC_SubmitMafiaTarget(_nearbyTarget.Value);

        UpdateKillButtonState();
    }

    private void UpdateKillButtonState()
    {
        var local = GetLocalState();
        float remaining = local != null ? local.KillCooldownRemaining : 0f;
        bool ready = local != null && local.MafiaAbilitiesInitialized && local.IsAlive;
        bool pending = Time.unscaledTime < _requestPendingUntil;
        if (killButton != null)
        {
            killButton.interactable = ready && remaining <= 0f && _nearbyTarget.HasValue && !pending;
        }
        if (_buttonLabel != null) _buttonLabel.text = remaining > 0f ? $"KILL ({Mathf.CeilToInt(remaining)}s)" : "KILL";

        if (statusText != null)
        {
            statusText.text = !ready ? "Waiting for match..."
                : remaining > 0f ? $"Kill ready in {Mathf.CeilToInt(remaining)}s"
                : pending ? "Checking target..."
                : (_nearbyTarget.HasValue ? $"Press Kill to eliminate P{_nearbyTargetLabelIndex}." : "Get close to a player to kill them.");
        }
    }

    private PlayerState GetLocalState()
    {
        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.Object == null || !gm.Object.IsValid || gm.Runner == null) return null;
        var obj = gm.Runner.GetPlayerObject(gm.Runner.LocalPlayer);
        return obj != null && obj.IsValid ? obj.GetComponent<PlayerState>() : null;
    }
}
