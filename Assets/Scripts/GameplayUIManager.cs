using Fusion;
using UnityEngine;

// Attach this to a "GameplayUIManager" GameObject in GameplayScene (e.g. as a
// child of your Gameplay Canvas). Shows/hides gameplay-phase panels based on
// NetworkGameManager.CurrentPhase — separate from UIPanelManager, which only
// handles the Connect-scene panels (Connect/Browse/HostSetup/JoinCode).
//
// Handles VotingPanel and NightActionPanel. Add more panels the same way later
// (e.g. a Discussion panel) — just add a field + a SetActive line following the
// same pattern.
public class GameplayUIManager : MonoBehaviour
{
    [Tooltip("Panel with player select buttons + the Vote/Skip buttons.")]
    [SerializeField] private GameObject votingPanel;

    [Tooltip("Panel with player select buttons + the Kill button — Mafia-only, Night-only.")]
    [SerializeField] private GameObject nightActionPanel;

    [Tooltip("Panel with the Report button — shown to EVERY alive player (not role-gated, same as Among Us) during Night, so anyone who stumbles on a body can report it.")]
    [SerializeField] private GameObject reportPanel;

    [Tooltip("Panel with the DEAD-player list + Confirm button — Doctor-only, shown during Discussion while their ONE revive for the whole match is still unspent.")]
    [SerializeField] private GameObject doctorRevivePanel;

    [Tooltip("Panel with the LIVING-player list + Confirm button — Sheriff-only, shown during Discussion while their ONE shot for the whole match is still unspent.")]
    [SerializeField] private GameObject sheriffPanel;

    private void Awake()
    {
        if (votingPanel == null)
        {
            Debug.LogError("[GameplayUIManager] Voting Panel is not assigned in the Inspector!", this);
        }

        if (nightActionPanel == null)
        {
            Debug.LogError("[GameplayUIManager] Night Action Panel is not assigned in the Inspector!", this);
        }

        if (reportPanel == null)
        {
            Debug.LogError("[GameplayUIManager] Report Panel is not assigned in the Inspector!", this);
        }

        if (doctorRevivePanel == null)
        {
            Debug.LogError("[GameplayUIManager] Doctor Revive Panel is not assigned in the Inspector!", this);
        }

        if (sheriffPanel == null)
        {
            Debug.LogError("[GameplayUIManager] Sheriff Panel is not assigned in the Inspector!", this);
        }
    }

    private void Update()
    {
        var gm = NetworkGameManager.Instance;
        bool isConnected = gm != null && gm.IsReady && gm.Object != null && gm.Object.IsValid && gm.Runner != null;

        // CONFIRMED LIVE BUG this guards against: this used to show the Voting
        // panel to EVERY player purely based on CurrentPhase, with no check on
        // whether the LOCAL player themself was still alive. An eliminated
        // player (voted out, or killed at Night) still got the full
        // player-select + Vote/Skip UI and could go through the motions of
        // "playing" — selecting a target, pressing Vote, seeing a status
        // message — even though they should be locked into spectate mode by
        // this point (see PlayerState.Render() -> SpectatorController.
        // EnterSpectateMode()). Gate this on the local player's own IsAlive
        // too, so a dead player never sees a gameplay panel meant for players
        // still in the match.
        bool localPlayerAlive = true;
        if (isConnected)
        {
            NetworkObject localObj = gm.Runner.GetPlayerObject(gm.Runner.LocalPlayer);
            if (localObj != null && localObj.TryGetComponent(out PlayerState localState))
            {
                localPlayerAlive = localState.IsAlive;
            }
        }

        bool showVoting = isConnected && localPlayerAlive && gm.CurrentPhase == NetworkGameManager.GamePhase.Voting;

        if (votingPanel != null && votingPanel.activeSelf != showVoting)
        {
            votingPanel.SetActive(showVoting);
        }

        // Kill button/target list only ever shows for the local player if they
        // are (a) still alive, (b) it's actually Night, AND (c) their own role
        // is Mafia — a Civilian/Doctor should never see a Kill button at all,
        // same reasoning as PlayerRoleManager never telling a client any role
        // but its own (see RPC_ReceiveRole's own comment).
        bool localPlayerIsMafia = PlayerRoleManager.Instance != null
            && PlayerRoleManager.Instance.HasReceivedRole
            && PlayerRoleManager.Instance.LocalRole == PlayerRole.Mafia;

        bool showNightAction = isConnected && localPlayerAlive && localPlayerIsMafia
            && gm.CurrentPhase == NetworkGameManager.GamePhase.Night;

        if (nightActionPanel != null && nightActionPanel.activeSelf != showNightAction)
        {
            nightActionPanel.SetActive(showNightAction);
        }

        // Report button is NOT role-gated — any alive player (Mafia included,
        // same as Among Us) can walk up to a body and report it during Night.
        bool showReport = isConnected && localPlayerAlive && gm.CurrentPhase == NetworkGameManager.GamePhase.Night;

        if (reportPanel != null && reportPanel.activeSelf != showReport)
        {
            reportPanel.SetActive(showReport);
        }

        // Doctor's revive panel and Sheriff's shoot panel — same role-gating
        // reasoning as the Kill panel above, except both are Discussion-phase
        // abilities rather than Night ones, and both are ONCE PER MATCH.
        //
        // The "already used" check is what makes them one-shot from the player's
        // point of view. The server enforces it regardless (see
        // NightActionManager.DoctorReviveUsed / SheriffShotUsed), so a client that
        // somehow kept the panel open still can't fire twice — but leaving a dead
        // button on screen all match would read as broken.
        bool abilityStateKnown = NightActionManager.Instance != null
            && NightActionManager.Instance.Object != null
            && NightActionManager.Instance.Object.IsValid;

        bool isDiscussion = isConnected && gm.CurrentPhase == NetworkGameManager.GamePhase.Discussion;

        bool localPlayerIsDoctor = PlayerRoleManager.Instance != null
            && PlayerRoleManager.Instance.HasReceivedRole
            && PlayerRoleManager.Instance.LocalRole == PlayerRole.Doctor;

        bool showDoctorRevive = isDiscussion && localPlayerAlive && localPlayerIsDoctor
            && abilityStateKnown && !NightActionManager.Instance.DoctorReviveUsed;

        if (doctorRevivePanel != null && doctorRevivePanel.activeSelf != showDoctorRevive)
        {
            doctorRevivePanel.SetActive(showDoctorRevive);
        }

        bool localPlayerIsSheriff = PlayerRoleManager.Instance != null
            && PlayerRoleManager.Instance.HasReceivedRole
            && PlayerRoleManager.Instance.LocalRole == PlayerRole.Sheriff;

        bool showSheriff = isDiscussion && localPlayerAlive && localPlayerIsSheriff
            && abilityStateKnown && !NightActionManager.Instance.SheriffShotUsed;

        if (sheriffPanel != null && sheriffPanel.activeSelf != showSheriff)
        {
            sheriffPanel.SetActive(showSheriff);
        }
    }
}
