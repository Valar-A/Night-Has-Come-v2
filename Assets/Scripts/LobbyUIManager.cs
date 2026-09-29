using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach this to your "LobbyPanel" UI GameObject (a child of your Canvas) that contains:
//  - a TMP_Text showing player count
//  - a "Start Game" Button (only visible/usable for the Host)
//
// NOTE: This script only fills in the panel's content (text/button state). Whether the
// panel is shown or hidden at all is controlled centrally by UIPanelManager — don't add
// SetActive() calls here, or the two scripts will fight over visibility.
public class LobbyUIManager : MonoBehaviour
{
    [SerializeField] private TMP_Text playerCountText;
    [SerializeField] private Button startGameButton;

    [Tooltip("Text that shows the game code — visible to the Host only, hidden for clients.")]
    [SerializeField] private TMP_Text gameCodeText;

    private void Awake()
    {
        if (playerCountText == null) Debug.LogError("[LobbyUIManager] Player Count Text is not assigned!", this);
        if (startGameButton == null) Debug.LogError("[LobbyUIManager] Start Game Button is not assigned!", this);
        else startGameButton.interactable = false;
        if (gameCodeText == null) Debug.LogWarning("[LobbyUIManager] Game Code Text is not assigned — the game code won't be shown.", this);
    }

    private void Update()
    {
        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady) return;

        // CONFIRMED LIVE BUG this guards against: this check used to run AFTER the
        // CurrentPhase read below. During a Host Migration, the OLD (about-to-be-
        // destroyed) NetworkGameManager stays reachable via the static Instance for
        // one more frame — its GameObject isn't destroyed yet (so `gm == null` is
        // still false) and its plain `IsReady` bool is still true (nothing resets
        // it), but Fusion has already torn down that object's networked backing as
        // part of runner.Shutdown(). Reading a [Networked] property like
        // CurrentPhase on it throws InvalidOperationException ("Networked
        // properties can only be accessed when Spawned() has been called"), caught
        // live on a client mid-migration. Checking Object.IsValid BEFORE touching
        // CurrentPhase avoids ever reading a networked property on a
        // Fusion-invalidated object.
        if (gm.Object == null || !gm.Object.IsValid || gm.Runner == null) return;

        if (gm.CurrentPhase != NetworkGameManager.GamePhase.Lobby) return;
        if (playerCountText == null || startGameButton == null) return;

        int playerCount = gm.Runner.ActivePlayers.Count();
        playerCountText.text = $"Players in Lobby: {playerCount}";

        // Only the Host ever sees/uses the Start Game button — clients just wait.
        bool isHost = gm.Runner.IsServer;
        startGameButton.gameObject.SetActive(isHost);
        // A character is a required lobby choice. The button stays disabled until
        // every connected player owns one unique selection; BeginMatch validates
        // this again server-side so a modified client cannot bypass the UI.
        bool allCharactersSelected = gm.AllPlayersHaveCharacterSelections();
        startGameButton.interactable = isHost
            && playerCount >= gm.MinPlayersToStart
            && allCharactersSelected;

        // Game code is only meaningful to the Host (who generated it) — clients
        // already used it to join, no need to show it to them.
        if (gameCodeText != null)
        {
            if (isHost && NetworkBootstrap.Instance != null)
            {
                gameCodeText.gameObject.SetActive(true);
                gameCodeText.text = $"Code: {NetworkBootstrap.Instance.GameCode}";
            }
            else
            {
                gameCodeText.gameObject.SetActive(false);
            }
        }
    }

    // Hook this to the Start Game button's OnClick() in the Inspector.
    public void OnStartGameClicked()
    {
        NetworkGameManager.Instance?.BeginMatch();
    }
}
