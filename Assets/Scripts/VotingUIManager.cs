using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach this to a "VotingPanel" GameObject (child of Canvas), the same way
// LobbyUIManager fills in the LobbyPanel — UIPanelManager should be the one
// turning this panel on/off (see the votingPanel field added to it), this
// script only fills in content once the panel is already active.
//
// INSPECTOR SETUP:
//   - playerButtonPrefab: a Button prefab with a TMP_Text child, e.g. label "P1"
//   - playerButtonContainer: parent Transform for the spawned buttons
//     (put a Horizontal/Vertical Layout Group on it so P1/P2/... lay out automatically)
//   - voteButton: the single "Vote" button that confirms whichever player is selected
//   - statusText: TMP_Text for feedback ("Select a player", "Vote submitted", etc.)
public class VotingUIManager : MonoBehaviour
{
    [SerializeField] private Button playerButtonPrefab;
    [SerializeField] private Transform playerButtonContainer;
    [SerializeField] private Button voteButton;
    [SerializeField] private Button skipButton;
    [SerializeField] private TMP_Text statusText;

    private VoteManager _voteManager;
    private readonly List<Button> _spawnedButtons = new();
    private PlayerRef? _selectedTarget;
    private bool _hasVoted;

    // Fires every time the panel is enabled by UIPanelManager — i.e. every
    // time a new Voting phase starts — so selection/vote state resets each round.
    private void OnEnable()
    {
        _voteManager = FindObjectOfType<VoteManager>();
        _selectedTarget = null;
        _hasVoted = false;

        voteButton.onClick.RemoveAllListeners();
        voteButton.onClick.AddListener(SubmitVote);

        skipButton.onClick.RemoveAllListeners();
        skipButton.onClick.AddListener(SubmitSkip);

        UpdateVoteButtonState();
        StartCoroutine(BuildPlayerButtonsWhenReady());
    }

    // Defense in depth alongside GameplayUIManager's own IsAlive gate on the
    // whole panel — if this panel is ever shown to a local player who is
    // (or becomes) eliminated, never let them actually interact with it. An
    // eliminated player should be spectating, not voting.
    private bool IsLocalPlayerEliminated()
    {
        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.Runner == null) return false;

        return _voteManager != null && _voteManager.IsEliminated(gm.Runner.LocalPlayer);
    }

    private void Update()
    {
        if (IsLocalPlayerEliminated())
        {
            LockAllButtons();
            if (voteButton != null) voteButton.interactable = false;
            if (statusText != null) statusText.text = "You have been eliminated — spectating.";
        }
    }

    // The panel can get enabled a frame or two before NetworkGameManager's
    // network object has actually finished spawning (Object/Runner not yet
    // valid) — wait for that instead of assuming it's ready immediately.
    private IEnumerator BuildPlayerButtonsWhenReady()
    {
        float timeout = 5f;
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            var gm = NetworkGameManager.Instance;
            _voteManager ??= FindObjectOfType<VoteManager>();

            bool ready = gm != null && gm.Object != null && gm.Object.IsValid && _voteManager != null;
            if (ready) break;

            elapsed += Time.deltaTime;
            yield return null;
        }

        BuildPlayerButtons();
        UpdateVoteButtonState();
    }

    private void BuildPlayerButtons()
    {
        foreach (var b in _spawnedButtons) Destroy(b.gameObject);
        _spawnedButtons.Clear();

        var gm = NetworkGameManager.Instance;
        if (gm == null || _voteManager == null || playerButtonPrefab == null) return;

        if (gm.Object == null || !gm.Object.IsValid || gm.Runner == null)
        {
            Debug.LogWarning("[VotingUIManager] NetworkGameManager still isn't spawned — can't list players yet.");
            return;
        }

        var localPlayer = gm.Runner.LocalPlayer;
        int index = 1;

        // Ordered by PlayerId so P1/P2 labels are consistent across all clients.
        foreach (var player in gm.Runner.ActivePlayers.OrderBy(p => p.PlayerId))
        {
            var button = Instantiate(playerButtonPrefab, playerButtonContainer);
            var label = button.GetComponentInChildren<TMP_Text>();
            if (label != null)
            {
                label.text = $"P{index}" + (player == localPlayer ? " (You)" : "");
            }

            bool eliminated = _voteManager.IsEliminated(player);
            button.interactable = !eliminated;

            var capturedPlayer = player;
            var capturedButton = button;
            button.onClick.AddListener(() => SelectPlayer(capturedPlayer, capturedButton));

            _spawnedButtons.Add(button);
            index++;
        }
    }

    private void SelectPlayer(PlayerRef player, Button pressedButton)
    {
        if (_hasVoted) return;

        _selectedTarget = player;

        // Simple visual highlight: selected button turns green, rest stay white.
        foreach (var b in _spawnedButtons)
        {
            var colors = b.colors;
            colors.normalColor = (b == pressedButton) ? new Color(0.4f, 0.8f, 0.4f) : Color.white;
            b.colors = colors;
        }

        UpdateVoteButtonState();
    }

    private void SubmitVote()
    {
        if (!_selectedTarget.HasValue || _hasVoted || _voteManager == null) return;

        _voteManager.RPC_SubmitVote(_selectedTarget.Value);
        _hasVoted = true;

        LockAllButtons();
        UpdateVoteButtonState();
    }

    private void SubmitSkip()
    {
        if (_hasVoted || _voteManager == null) return;

        _voteManager.RPC_SkipVote();
        _hasVoted = true;

        LockAllButtons();
        UpdateVoteButtonState();
    }

    private void LockAllButtons()
    {
        foreach (var b in _spawnedButtons) b.interactable = false;
        if (skipButton != null) skipButton.interactable = false;
    }

    private void UpdateVoteButtonState()
    {
        if (voteButton != null)
        {
            voteButton.interactable = _selectedTarget.HasValue && !_hasVoted;
        }

        if (statusText != null)
        {
            statusText.text = _hasVoted
                ? "Choice submitted — waiting for other players..."
                : (_selectedTarget.HasValue ? "Press Vote to confirm, or Skip to abstain." : "Select a player to vote for, or press Skip.");
        }
    }
}
