using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach to the "SheriffPanel" GameObject (child of the Gameplay Canvas).
// GameplayUIManager shows this ONLY to the local Sheriff, ONLY during Discussion,
// ONLY while they are alive, and ONLY while their one shot is still unspent.
//
// The Sheriff picks a LIVING player and fires. If that player is Mafia, the Mafia
// dies. If they are anything else, the Sheriff dies instead. All of that is
// resolved server-side in NightActionManager.RPC_SubmitSheriffShot — this panel
// deliberately knows nothing about anyone's role, because the local client is
// never told any role but its own.
//
// The confirm button is intentionally two-step (select, then confirm) rather than
// fire-on-click. A misclick here does not cost a wasted turn, it can cost the
// Sheriff their own life and hand the Town's best tool to the Mafia for free.
//
// INSPECTOR SETUP (same shape as the Doctor revive panel):
//   - playerButtonPrefab: a Button prefab with a TMP_Text child, e.g. label "P1"
//   - playerButtonContainer: parent Transform for the spawned buttons
//   - confirmButton: the single button that fires at whichever player is selected
//   - statusText: TMP_Text for feedback
public class SheriffShootUIManager : MonoBehaviour
{
    [SerializeField] private Button playerButtonPrefab;
    [SerializeField] private Transform playerButtonContainer;
    [SerializeField] private Button confirmButton;
    [SerializeField] private TMP_Text statusText;

    private readonly List<Button> _spawnedButtons = new();
    private PlayerRef? _selectedTarget;
    private bool _hasSubmitted;

    private void OnEnable()
    {
        _selectedTarget = null;
        _hasSubmitted = false;

        if (confirmButton != null)
        {
            confirmButton.onClick.RemoveAllListeners();
            confirmButton.onClick.AddListener(SubmitShot);
        }

        UpdateConfirmButtonState();
        StartCoroutine(BuildPlayerButtonsWhenReady());
    }

    private IEnumerator BuildPlayerButtonsWhenReady()
    {
        float timeout = 5f;
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            var gm = NetworkGameManager.Instance;
            bool ready = gm != null && gm.Object != null && gm.Object.IsValid && NightActionManager.Instance != null;
            if (ready) break;

            elapsed += Time.deltaTime;
            yield return null;
        }

        BuildPlayerButtons();
        UpdateConfirmButtonState();
    }

    private void BuildPlayerButtons()
    {
        foreach (var b in _spawnedButtons)
        {
            if (b != null) Destroy(b.gameObject);
        }
        _spawnedButtons.Clear();

        var gm = NetworkGameManager.Instance;
        if (gm == null || playerButtonPrefab == null) return;
        if (gm.Object == null || !gm.Object.IsValid || gm.Runner == null) return;

        var localPlayer = gm.Runner.LocalPlayer;
        int index = 1;

        foreach (var player in gm.Runner.ActivePlayers.OrderBy(p => p.PlayerId))
        {
            int myLabelIndex = index;
            index++;

            // The server rejects a self-shot outright; don't even offer it here.
            if (player == localPlayer) continue;

            NetworkObject obj = gm.Runner.GetPlayerObject(player);
            if (obj == null || !obj.TryGetComponent(out PlayerState state)) continue;

            // Only living players are valid targets — the server rejects shots at a
            // corpse, and offering one here would just look like a backfire bug.
            if (!state.IsAlive) continue;

            var button = Instantiate(playerButtonPrefab, playerButtonContainer);
            var label = button.GetComponentInChildren<TMP_Text>();
            if (label != null) label.text = $"P{myLabelIndex}";

            var capturedPlayer = player;
            var capturedButton = button;
            button.onClick.AddListener(() => SelectPlayer(capturedPlayer, capturedButton));

            _spawnedButtons.Add(button);
        }
    }

    private void SelectPlayer(PlayerRef player, Button pressedButton)
    {
        if (_hasSubmitted) return;

        _selectedTarget = player;

        // Red rather than the green used on the revive panel — this button ends
        // somebody's match, and which somebody depends on being right.
        foreach (var b in _spawnedButtons)
        {
            var colors = b.colors;
            colors.normalColor = (b == pressedButton) ? new Color(0.85f, 0.35f, 0.35f) : Color.white;
            b.colors = colors;
        }

        UpdateConfirmButtonState();
    }

    private void SubmitShot()
    {
        if (!_selectedTarget.HasValue || _hasSubmitted || NightActionManager.Instance == null) return;

        NightActionManager.Instance.RPC_SubmitSheriffShot(_selectedTarget.Value);
        _hasSubmitted = true;

        foreach (var b in _spawnedButtons) b.interactable = false;
        UpdateConfirmButtonState();
    }

    private void UpdateConfirmButtonState()
    {
        if (confirmButton != null)
        {
            confirmButton.interactable = _selectedTarget.HasValue && !_hasSubmitted;
        }

        if (statusText == null) return;

        if (_hasSubmitted)
        {
            statusText.text = "Shot fired.";
        }
        else if (_selectedTarget.HasValue)
        {
            statusText.text = "Confirm to shoot. Mafia dies — anyone else, and YOU die.";
        }
        else
        {
            statusText.text = "Pick who to shoot. One shot all game. Wrong target kills you.";
        }
    }
}
