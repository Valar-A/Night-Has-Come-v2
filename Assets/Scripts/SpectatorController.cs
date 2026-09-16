using System.Collections.Generic;
using UnityEngine;

// Attach this to a PERSISTENT object in GameplayScene (e.g. the same GameObject
// as GameplayUIManager). When the LOCAL player is eliminated, PlayerState calls
// EnterSpectateMode() on this, and it takes over Camera.main to follow other
// still-alive players instead of the player's now-frozen body.
//
// ASSUMPTION (I don't have your existing camera-follow script, so flagging this):
//   This assumes Camera.main is a free-floating camera NOT parented to the player
//   prefab. If your camera IS currently a child of the player (common third-person
//   setup), it will need to be unparented when spectating starts, or it'll just sit
//   at the dead player's frozen position instead of moving to the target. See the
//   commented-out transform.SetParent(null) line below — uncomment and adjust if
//   that's your setup.
public class SpectatorController : MonoBehaviour
{
    public static SpectatorController Instance { get; private set; }

    [Tooltip("Offset from the spectated player's position — match whatever your normal follow camera uses.")]
    [SerializeField] private Vector3 cameraOffset = new Vector3(0f, 5f, -6f);

    [Tooltip("How fast the camera moves/rotates toward a newly selected target.")]
    [SerializeField] private float followLerpSpeed = 8f;

    private bool _isSpectating;
    private int _targetIndex = -1;
    private Transform _currentTarget;

    // CONFIRMED LIVE BUG this exposes: CameraFollow.LateUpdate() runs on the SAME
    // Camera.main every single frame and unconditionally re-locks its position/
    // rotation onto the LOCAL PLAYER'S OWN transform (via TryAcquireTarget()) —
    // it has no idea spectate mode exists. Since Unity always runs LateUpdate()
    // after Update(), CameraFollow was overwriting whatever position this
    // script's own Update() had just set one line earlier, every frame, snapping
    // the camera straight back onto the eliminated (now frozen, invisible) body
    // instead of the spectated alive player. That's why "spectating" visually
    // never worked — the camera never actually moved. CameraFollow now checks
    // this flag and bails out of LateUpdate() entirely while true, handing full
    // control of Camera.main to this script for the rest of the match.
    public bool IsSpectating => _isSpectating;

    private void Awake()
    {
        Instance = this;
    }

    // Called by PlayerState on the local client's own PlayerState.Render(), the
    // moment their IsAlive flips to false.
    public void EnterSpectateMode()
    {
        if (_isSpectating) return;
        _isSpectating = true;

        // if (Camera.main != null) Camera.main.transform.SetParent(null); // uncomment if camera is a child of the player prefab

        _targetIndex = -1;
        SelectNextAliveTarget();
        Debug.Log("[SpectatorController] Entered spectate mode.");
    }

    // Called by PlayerState on the local client's own PlayerState.Render(), the
    // moment their IsAlive flips back to true — i.e. the Doctor correctly
    // guessed them during DoctorSave (see NightActionManager.RPC_SubmitDoctorSave).
    // Hands control of Camera.main back to CameraFollow, which re-acquires the
    // local player's own transform on its very next LateUpdate().
    public void ExitSpectateMode()
    {
        if (!_isSpectating) return;
        _isSpectating = false;

        _currentTarget = null;
        _targetIndex = -1;
        Debug.Log("[SpectatorController] Exited spectate mode — back in the game!");
    }

    private void Update()
    {
        if (!_isSpectating) return;

        // TEMP input — wire Next/Prev UI buttons to SelectNextAliveTarget() /
        // SelectPreviousAliveTarget() later instead of (or alongside) these keys.
        if (Input.GetKeyDown(KeyCode.RightArrow)) SelectNextAliveTarget();
        if (Input.GetKeyDown(KeyCode.LeftArrow)) SelectPreviousAliveTarget();

        if (_currentTarget != null && Camera.main != null)
        {
            Vector3 desiredPos = _currentTarget.position + cameraOffset;
            Camera.main.transform.position = Vector3.Lerp(
                Camera.main.transform.position, desiredPos, followLerpSpeed * Time.deltaTime);
            Camera.main.transform.LookAt(_currentTarget.position + Vector3.up * 1.5f);
        }
    }

    // Rebuilds the alive-player list fresh each call — simplest way to stay correct
    // as more players die mid-match without needing extra event wiring for now.
    private List<Transform> GetAliveTargets()
    {
        var targets = new List<Transform>();

        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.Object == null || !gm.Object.IsValid || gm.Runner == null)
            return targets;

        foreach (var player in gm.Runner.ActivePlayers)
        {
            var obj = gm.Runner.GetPlayerObject(player);
            if (obj != null && obj.TryGetComponent(out PlayerState state) && state.IsAlive)
            {
                targets.Add(obj.transform);
            }
        }
        return targets;
    }

    public void SelectNextAliveTarget()
    {
        var targets = GetAliveTargets();
        if (targets.Count == 0) { _currentTarget = null; return; }

        _targetIndex = (_targetIndex + 1) % targets.Count;
        _currentTarget = targets[_targetIndex];
    }

    public void SelectPreviousAliveTarget()
    {
        var targets = GetAliveTargets();
        if (targets.Count == 0) { _currentTarget = null; return; }

        _targetIndex = (_targetIndex - 1 + targets.Count) % targets.Count;
        _currentTarget = targets[_targetIndex];
    }
}
