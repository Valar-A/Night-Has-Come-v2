using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach to the row PREFAB used by LobbyBrowserManager (one row = one joinable game).
// The row prefab should have: a TMP_Text for the code, a TMP_Text for player count,
// and a Button — all wired into this script's fields.
public class SessionListItem : MonoBehaviour
{
    [SerializeField] private TMP_Text codeText;
    [SerializeField] private TMP_Text playerCountText;
    [SerializeField] private Button joinButton;

    private string _sessionCode;
    private Action<string> _onJoinClicked;

    public void Setup(string code, int playerCount, int maxPlayers, Action<string> onJoinClicked)
    {
        _sessionCode = code;
        _onJoinClicked = onJoinClicked;

        if (codeText != null) codeText.text = code;
        if (playerCountText != null) playerCountText.text = $"{playerCount}/{maxPlayers}";

        joinButton.onClick.RemoveAllListeners();
        joinButton.onClick.AddListener(() => _onJoinClicked?.Invoke(_sessionCode));
    }
}
