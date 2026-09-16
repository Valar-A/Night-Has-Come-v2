using System.Linq;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach this to a "NightActionPanel" GameObject (child of Canvas). GameplayUIManager
// is the one turning this panel on/off (only for the local Mafia player, only during
// Night, only while alive) — this script just drives the single Kill button while
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
//     P2", "Target eliminated — waiting for dawn...").
//   - killRange: how close (in world units) the Mafia needs to be to a target
//     before Kill lights up. Tune against your player capsule size / room scale.
public class NightActionUIManager : MonoBehaviour
{
    [SerializeField] private Button killButton;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private float killRange = 3f;

    private bool _hasSubmitted;
    private PlayerRef? _nearbyTarget;
    private int _nearbyTargetLabelIndex;

    // Fires every time the panel is enabled by GameplayUIManager — i.e. every time
    // a new Night phase starts — so the once-per-night submit lock resets each round.
    private void OnEnable()
    {
        _hasSubmitted = false;
        _nearbyTarget = null;

        killButton.onClick.RemoveAllListeners();
        killButton.onClick.AddListener(SubmitKill);

        UpdateKillButtonState();
    }

    private void Update()
    {
        if (_hasSubmitted)
        {
            // Already locked in a kill this Night — stop scanning, just show the
            // "waiting" state until NetworkGameManager takes the panel away at Dawn.
            return;
        }

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
        float closestSqrDist = killRange * killRange;
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
        if (!_nearbyTarget.HasValue || _hasSubmitted || NightActionManager.Instance == null) return;

        NightActionManager.Instance.RPC_SubmitMafiaTarget(_nearbyTarget.Value);
        _hasSubmitted = true;

        UpdateKillButtonState();
    }

    private void UpdateKillButtonState()
    {
        if (killButton != null)
        {
            killButton.interactable = _nearbyTarget.HasValue && !_hasSubmitted;
        }

        if (statusText != null)
        {
            statusText.text = _hasSubmitted
                ? "Target eliminated."
                : (_nearbyTarget.HasValue ? $"Press Kill to eliminate P{_nearbyTargetLabelIndex}." : "Get close to a player to kill them.");
        }
    }
}
