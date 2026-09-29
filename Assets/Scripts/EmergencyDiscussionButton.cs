using Fusion;
using TMPro;
using UnityEngine;

// Scene-side hall button. It is intentionally not a NetworkObject: the authoritative
// request goes through NetworkGameManager, which validates the caller, game phase and
// distance against its own copy of this marker before starting a meeting.
[DisallowMultipleComponent]
public class EmergencyDiscussionButton : MonoBehaviour
{
    public static EmergencyDiscussionButton Instance { get; private set; }

    [SerializeField] private float interactRadius = 3.5f;
    [SerializeField] private KeyCode interactKey = KeyCode.E;
    [SerializeField] private TMP_Text promptLabel;

    public float InteractRadius => interactRadius;

    private void Awake()
    {
        Instance = this;
        SetPrompt(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        NetworkGameManager gm = NetworkGameManager.Instance;
        bool canUse = gm != null && gm.IsReady && gm.Object != null && gm.Object.IsValid
            && gm.Runner != null && gm.CanCallDiscussion;

        NetworkObject localPlayer = canUse ? gm.Runner.GetPlayerObject(gm.Runner.LocalPlayer) : null;
        if (localPlayer == null || (localPlayer.TryGetComponent(out PlayerState state) && !state.IsAlive))
        {
            SetPrompt(false);
            return;
        }

        bool nearby = Vector3.Distance(localPlayer.transform.position, transform.position) <= interactRadius;
        SetPrompt(nearby);
    }

    private void SetPrompt(bool showInteraction)
    {
        if (promptLabel == null) return;
        promptLabel.text = showInteraction
            ? $"EMERGENCY DISCUSSION\nPRESS [{interactKey}]"
            : "EMERGENCY DISCUSSION";
        promptLabel.color = showInteraction ? Color.white : new Color(1f, 0.75f, 0.75f, 1f);
    }
}
