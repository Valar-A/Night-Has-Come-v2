using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach to the "DoctorRevivePanel" GameObject (child of the Gameplay Canvas).
// GameplayUIManager shows this ONLY to the local Doctor, ONLY during Discussion,
// ONLY while they are alive, and ONLY while their one revive is still unspent.
//
// This replaces the old DoctorSaveUIManager, which drove a completely different
// mechanic: a blind guess at who the Mafia had killed, during a dedicated
// DoctorSave phase that no longer exists. There is nothing blind about this one —
// the Doctor sees exactly who is dead and picks one to bring back. The only thing
// they do NOT know is what role that person was, which is what makes reviving a
// Mafia a genuine risk (see NightActionManager.RPC_SubmitDoctorRevive).
//
// INSPECTOR SETUP (same shape as the panel it replaces):
//   - playerButtonPrefab: a Button prefab with a TMP_Text child, e.g. label "P1"
//   - playerButtonContainer: parent Transform for the spawned buttons
//   - confirmButton: the single button that confirms whichever player is selected
//   - statusText: TMP_Text for feedback
public class DoctorReviveUIManager : MonoBehaviour
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
            confirmButton.onClick.AddListener(SubmitRevive);
        }

        UpdateConfirmButtonState();
        StartCoroutine(BuildPlayerButtonsWhenReady());
    }

    // Same reasoning as the panel this replaces — it can get enabled a frame or
    // two before NetworkGameManager/NightActionManager have finished spawning.
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

        int index = 1;

        // Ordered by PlayerId so P1/P2 labels line up across every client — same
        // convention VotingUIManager and NightActionUIManager already use.
        foreach (var player in gm.Runner.ActivePlayers.OrderBy(p => p.PlayerId))
        {
            int myLabelIndex = index;
            index++;

            NetworkObject obj = gm.Runner.GetPlayerObject(player);
            if (obj == null || !obj.TryGetComponent(out PlayerState state)) continue;

            // Only DEAD players can be revived. This is the exact inverse of the
            // voting/kill panels, which list the living.
            if (state.IsAlive) continue;

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

        foreach (var b in _spawnedButtons)
        {
            var colors = b.colors;
            colors.normalColor = (b == pressedButton) ? new Color(0.4f, 0.8f, 0.4f) : Color.white;
            b.colors = colors;
        }

        UpdateConfirmButtonState();
    }

    private void SubmitRevive()
    {
        if (!_selectedTarget.HasValue || _hasSubmitted || NightActionManager.Instance == null) return;

        NightActionManager.Instance.RPC_SubmitDoctorRevive(_selectedTarget.Value);
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
            // Deliberately says "when Discussion ends", not "they're back" — the
            // revive is queued here and only lands on the Discussion -> Voting
            // transition (see NightActionManager.ResolvePendingRevive). Telling the
            // Doctor they were already revived would have them watching for someone
            // to stand up who isn't coming back until the phase changes.
            statusText.text = "Revive locked in — they return when Discussion ends.";
        }
        else if (_spawnedButtons.Count == 0)
        {
            // Worth saying explicitly rather than showing an empty panel: with
            // nobody dead yet there is simply nothing to spend the revive on.
            statusText.text = "Nobody is dead yet — nothing to revive.";
        }
        else if (_selectedTarget.HasValue)
        {
            statusText.text = "Confirm to revive. They return when Discussion ends. ONE use all game.";
        }
        else
        {
            statusText.text = "Pick a dead player to revive. One use for the whole match.";
        }
    }
}
