using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach to a GameObject under the Gameplay Canvas. Purely LOCAL: it decides what
// this one player sees and when their hold finishes, then asks the server. It
// never decides anything.
//
// Deliberately NOT on the player prefab. CameraFollow already established the
// pattern of a scene-side singleton that finds the local player itself every
// frame via Runner.GetPlayerObject — that survives every scene transition and the
// school/discussion-room teleports, and it means the player prefab does not need
// another component bolted onto it.
//
// Also deliberately NOT routed through NetworkInputData. Fusion input is for
// things the simulation has to predict and re-simulate tick by tick — movement,
// jumping. Finishing a task is a one-shot request that the server is going to
// re-validate against its own copy of the world anyway, so it goes over an RPC,
// exactly like the Mafia kill, the vote, the Doctor revive and the Sheriff shot
// already do.
//
// INSPECTOR SETUP:
//   - promptRoot:   the panel that shows/hides (leave the label + bar inside it)
//   - promptText:   TMP_Text, e.g. "Hold E — Repair Generator"
//   - progressFill: an Image set to Filled, used as the hold bar
public class TaskInteractionUI : MonoBehaviour
{
    [SerializeField] private GameObject promptRoot;
    [SerializeField] private TMP_Text promptText;
    [SerializeField] private Image progressFill;

    [Header("Interaction")]
    [Tooltip("How long the key must be held to finish a station.")]
    [SerializeField] private float holdSeconds = 3f;
    [SerializeField] private KeyCode interactKey = KeyCode.E;

    private byte _currentNode = TaskManager.NoTask;
    private float _held;
    private bool _submitted;

    private void Update()
    {
        if (!TryFindWorkableNode(out byte nodeIndex, out TaskNodeMarker node))
        {
            Reset();
            return;
        }

        // Moving to a different station always restarts the bar — otherwise a
        // player could bank most of a hold at one node, walk to another and finish
        // it instantly.
        if (nodeIndex != _currentNode)
        {
            _currentNode = nodeIndex;
            _held = 0f;
            _submitted = false;
        }

        if (promptRoot != null && !promptRoot.activeSelf) promptRoot.SetActive(true);

        if (Input.GetKey(interactKey) && !_submitted)
        {
            _held += Time.deltaTime;

            if (_held >= holdSeconds)
            {
                TaskManager.Instance.RPC_CompleteTask(nodeIndex);

                // Latched rather than reset: the server has the final say, and
                // until its answer replicates back the node still reads as
                // outstanding locally. Without the latch this would fire an RPC
                // every single frame during that round trip.
                _submitted = true;
                _held = holdSeconds;
            }
        }
        else if (!Input.GetKey(interactKey) && !_submitted)
        {
            // Let go and the progress drains rather than vanishing — the same
            // forgiveness a player expects from any hold-to-interact, and it means
            // briefly clipping a doorway does not cost the whole hold.
            _held = Mathf.Max(0f, _held - Time.deltaTime * 2f);
        }

        if (promptText != null)
        {
            promptText.text = _submitted
                ? $"Working… {node.DisplayName}"
                : $"Hold [{interactKey}] — {node.DisplayName}";
        }

        if (progressFill != null)
        {
            progressFill.fillAmount = holdSeconds > 0f ? Mathf.Clamp01(_held / holdSeconds) : 0f;
        }
    }

    // Finds the nearest station that is BOTH on this player's list and still
    // outstanding, and is within that station's own radius.
    private bool TryFindWorkableNode(out byte nodeIndex, out TaskNodeMarker node)
    {
        nodeIndex = TaskManager.NoTask;
        node = null;

        TaskManager tasks = TaskManager.Instance;
        if (tasks == null || tasks.Object == null || !tasks.Object.IsValid) return false;

        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.Object == null || !gm.Object.IsValid) return false;

        // Same rule the server enforces in RPC_CompleteTask — showing a prompt in
        // any other phase would just be an invitation to press a key that does
        // nothing.
        if (gm.CurrentPhase != NetworkGameManager.GamePhase.Day) return false;

        NetworkRunner runner = gm.Runner;
        if (runner == null) return false;

        NetworkObject me = runner.GetPlayerObject(runner.LocalPlayer);
        if (me == null) return false;
        if (me.TryGetComponent(out PlayerState state) && !state.IsAlive) return false;

        if (!tasks.TryGetLocalAssignment(out TaskAssignment assignment)) return false;

        Vector3 myPosition = me.transform.position;
        float bestDistance = float.MaxValue;

        for (int slot = 0; slot < TaskManager.TasksPerPlayer; slot++)
        {
            byte candidate = assignment.GetSlot(slot);
            if (candidate == TaskManager.NoTask) continue;
            if (assignment.IsSlotCompleted(slot)) continue;
            if (!TaskNodeRegistry.TryGetNode(candidate, out TaskNodeMarker candidateNode)) continue;

            float distance = Vector3.Distance(myPosition, candidateNode.transform.position);
            if (distance > candidateNode.InteractRadius) continue;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                nodeIndex = candidate;
                node = candidateNode;
            }
        }

        return node != null;
    }

    private void Reset()
    {
        _currentNode = TaskManager.NoTask;
        _held = 0f;
        _submitted = false;
        if (progressFill != null) progressFill.fillAmount = 0f;
        if (promptRoot != null && promptRoot.activeSelf) promptRoot.SetActive(false);
    }
}
