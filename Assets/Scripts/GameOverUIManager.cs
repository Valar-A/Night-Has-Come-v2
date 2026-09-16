using System.Linq;
using System.Text;
using Fusion;
using TMPro;
using UnityEngine;

// Attach this to a "GameOverPanel" GameObject (child of your Gameplay Canvas), the
// same way GameplayUIManager fills in VotingPanel. Shows the winning side plus a
// full role reveal the moment NetworkGameManager.CurrentPhase hits GameOver — see
// NetworkGameManager.CheckWinCondition() for how/when that phase gets set.
//
// INSPECTOR SETUP:
//   - gameOverPanel: the panel root this script shows/hides (start it inactive).
//   - winnerText: TMP_Text for "Civilians Win!" / "Mafia Wins!".
//   - roleRevealText: TMP_Text listing every player, their role, and alive/dead —
//     safe to show everyone's role here (unlike mid-match) since the game is over.
public class GameOverUIManager : MonoBehaviour
{
    [SerializeField] private GameObject gameOverPanel;
    [SerializeField] private TMP_Text winnerText;
    [SerializeField] private TMP_Text roleRevealText;

    private bool _resultsPopulatedForThisGame;

    private void Awake()
    {
        if (gameOverPanel == null)
        {
            Debug.LogError("[GameOverUIManager] Game Over Panel is not assigned in the Inspector!", this);
        }
    }

    private void Update()
    {
        var gm = NetworkGameManager.Instance;
        bool isConnected = gm != null && gm.IsReady && gm.Object != null && gm.Object.IsValid && gm.Runner != null;

        bool showGameOver = isConnected && gm.CurrentPhase == NetworkGameManager.GamePhase.GameOver;

        if (gameOverPanel != null && gameOverPanel.activeSelf != showGameOver)
        {
            gameOverPanel.SetActive(showGameOver);
        }

        // Populate once per game — CurrentPhase stays GameOver for the rest of this
        // session (no auto-advance, see NetworkGameManager.FixedUpdateNetwork's own
        // GameOver short-circuit), so there's nothing to refresh frame-to-frame; just
        // guard against re-building the same text every single Update() while shown.
        if (showGameOver && !_resultsPopulatedForThisGame)
        {
            _resultsPopulatedForThisGame = true;
            PopulateResults(gm);
        }
        else if (!showGameOver)
        {
            // Reset the guard once we leave GameOver (e.g. a brand new match after
            // returning to Lobby) so the NEXT game's results populate correctly too.
            _resultsPopulatedForThisGame = false;
        }
    }

    private void PopulateResults(NetworkGameManager gm)
    {
        if (winnerText != null)
        {
            winnerText.text = gm.Winner switch
            {
                NetworkGameManager.WinningTeam.Mafia => "Mafia Wins!",
                NetworkGameManager.WinningTeam.Civilians => "Civilians Win!",
                _ => "Game Over"
            };
        }

        if (roleRevealText != null)
        {
            var roleManager = PlayerRoleManager.Instance;
            var sb = new StringBuilder();
            int index = 1;

            // Ordered by PlayerId so this matches the same P1/P2/... labelling
            // VotingUIManager used all match — easy for players to cross-reference.
            foreach (var player in gm.Runner.ActivePlayers.OrderBy(p => p.PlayerId))
            {
                string roleName = roleManager != null ? roleManager.GetServerRole(player).ToString() : "Unknown";

                var obj = gm.Runner.GetPlayerObject(player);
                bool alive = obj != null && obj.TryGetComponent(out PlayerState state) && state.IsAlive;

                sb.AppendLine($"P{index}: {roleName}{(alive ? " (survived)" : " (eliminated)")}");
                index++;
            }

            roleRevealText.text = sb.ToString();
        }
    }

    // Wired to the "Back to Main Menu" button's OnClick() on the GameOverPanel.
    // Lives here (on Canvas, same as this whole script) rather than as a direct
    // cross-scene reference from GameplayScene's Button straight to NetworkBootstrap
    // — NetworkBootstrap's own GameObject lives in ConnectScene and gets reparented
    // under DontDestroyOnLoad at runtime, which is exactly the kind of reference
    // Unity's editor-time cross-scene serialization can't reliably resolve later.
    // Going through NetworkBootstrap.Instance (a plain runtime static, same pattern
    // already used everywhere else in this codebase — NetworkGameManager.Instance,
    // PlayerRoleManager.Instance, CameraFollow.Instance, etc.) sidesteps that
    // entirely: the Button only ever needs a same-scene reference to this Canvas.
    public void OnBackToMenuButtonClicked()
    {
        if (NetworkBootstrap.Instance != null)
        {
            NetworkBootstrap.Instance.LeaveToMainMenu();
        }
        else
        {
            Debug.LogWarning("[GameOverUIManager] NetworkBootstrap.Instance is null — can't return to main menu.");
        }
    }
}
