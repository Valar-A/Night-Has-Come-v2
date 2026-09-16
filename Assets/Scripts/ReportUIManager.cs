using System.Linq;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach this to a "ReportPanel" GameObject (child of Canvas, sibling of
// NightActionPanel). GameplayUIManager turns this panel on/off (for EVERY
// alive player — not role-gated, unlike NightActionPanel — only during
// Night) — this script just drives the single Report button while active.
//
// Same Among-Us-style proximity pattern as NightActionUIManager, except it
// scans for the nearest DEAD, NOT-YET-REPORTED player instead of a living
// target. Reporting jumps the whole match straight into Discussion (see
// NightActionManager.RPC_ReportBody / NetworkGameManager.ForceDiscussion).
//
// INSPECTOR SETUP:
//   - reportButton: the single "Report" button.
//   - statusText: TMP_Text for feedback ("No body nearby", "Press Report to
//     call it in").
//   - reportRange: how close (in world units) a player needs to be to a body
//     before Report lights up. Matches killRange by default.
public class ReportUIManager : MonoBehaviour
{
    [SerializeField] private Button reportButton;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private float reportRange = 3f;

    private bool _hasReported;
    private PlayerRef? _nearbyBody;
    private int _nearbyBodyLabelIndex;

    // Fires every time the panel is enabled by GameplayUIManager — i.e. every
    // time a new Night phase starts — so the guard resets each round.
    private void OnEnable()
    {
        _hasReported = false;
        _nearbyBody = null;

        reportButton.onClick.RemoveAllListeners();
        reportButton.onClick.AddListener(SubmitReport);

        UpdateReportButtonState();
    }

    private void Update()
    {
        if (_hasReported)
        {
            // Already reported this Night (phase is about to change anyway,
            // see NetworkGameManager.ForceDiscussion) — stop scanning.
            return;
        }

        FindNearbyBody();
        UpdateReportButtonState();
    }

    // Server authority re-validates everything again in
    // NightActionManager.RPC_ReportBody (alive sender, body actually dead,
    // not already reported) — this is purely a LOCAL, client-side convenience
    // scan to decide when to light up the button, same trust boundary as
    // NightActionUIManager's own scan.
    private void FindNearbyBody()
    {
        _nearbyBody = null;

        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.Object == null || !gm.Object.IsValid || gm.Runner == null) return;

        NetworkObject localObj = gm.Runner.GetPlayerObject(gm.Runner.LocalPlayer);
        if (localObj == null) return;

        Vector3 myPos = localObj.transform.position;
        float closestSqrDist = reportRange * reportRange;
        PlayerRef? closest = null;
        int closestLabelIndex = 0;
        int index = 1;

        foreach (var player in gm.Runner.ActivePlayers.OrderBy(p => p.PlayerId))
        {
            int myLabelIndex = index; // capture before it's incremented for the next player
            index++;

            if (player == gm.Runner.LocalPlayer) continue;

            NetworkObject obj = gm.Runner.GetPlayerObject(player);
            if (obj == null || !obj.TryGetComponent(out PlayerState state)) continue;

            // Only a dead, not-yet-reported body counts as reportable.
            if (state.IsAlive || state.HasBeenReported) continue;

            float sqrDist = (obj.transform.position - myPos).sqrMagnitude;
            if (sqrDist <= closestSqrDist)
            {
                closestSqrDist = sqrDist;
                closest = player;
                closestLabelIndex = myLabelIndex;
            }
        }

        _nearbyBody = closest;
        _nearbyBodyLabelIndex = closestLabelIndex;
    }

    private void SubmitReport()
    {
        if (!_nearbyBody.HasValue || _hasReported || NightActionManager.Instance == null) return;

        NightActionManager.Instance.RPC_ReportBody(_nearbyBody.Value);
        _hasReported = true;

        UpdateReportButtonState();
    }

    private void UpdateReportButtonState()
    {
        if (reportButton != null)
        {
            reportButton.interactable = _nearbyBody.HasValue && !_hasReported;
        }

        if (statusText != null)
        {
            statusText.text = _hasReported
                ? "Reporting body..."
                : (_nearbyBody.HasValue ? $"Press Report to call in P{_nearbyBodyLabelIndex}'s body." : "");
        }
    }
}
