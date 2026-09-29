using System.Linq;
using Fusion;
using TMPro;
using UnityEngine;

// All controls are saved scene GameObjects; this controller only updates them.
public class MafiaShapeshiftUI : MonoBehaviour
{
    [SerializeField] private UnityEngine.UI.Button openButton;
    [SerializeField] private TMP_Text abilityLabel;
    [SerializeField] private GameObject selectionPanel;
    [SerializeField] private UnityEngine.UI.Button closeButton;
    [SerializeField] private UnityEngine.UI.Button[] targetButtons;
    [SerializeField] private TMP_Text[] targetLabels;
    [SerializeField] private TMP_Text statusText;
    private readonly PlayerRef[] _targets = new PlayerRef[9];
    private PlayerState _local;
    private float _pendingUntil;

    private void Awake()
    {
        openButton.onClick.AddListener(OpenSelection);
        closeButton.onClick.AddListener(() => selectionPanel.SetActive(false));
        for (int i = 0; i < targetButtons.Length; i++)
        {
            int slot = i;
            targetButtons[i].onClick.AddListener(() => SelectTarget(slot));
        }
        selectionPanel.SetActive(false);
    }

    private void OnDisable()
    {
        if (selectionPanel != null) selectionPanel.SetActive(false);
    }

    private bool CanChoose()
    {
        var gm = NetworkGameManager.Instance;
        var roles = PlayerRoleManager.Instance;
        return _local != null && _local.Object != null && _local.Object.IsValid
            && _local.IsAlive && _local.MafiaAbilitiesInitialized && !_local.ShapeshiftUsed
            && gm != null && gm.IsReady && gm.Object != null && gm.Object.IsValid
            && gm.CurrentPhase == NetworkGameManager.GamePhase.Night
            && roles != null && roles.HasReceivedRole && roles.LocalRole == PlayerRole.Mafia;
    }

    private void Update()
    {
        var gm = NetworkGameManager.Instance;
        _local = null;
        if (gm != null && gm.IsReady && gm.Object != null && gm.Object.IsValid && gm.Runner != null)
        {
            var obj = gm.Runner.GetPlayerObject(gm.Runner.LocalPlayer);
            if (obj != null && obj.IsValid) _local = obj.GetComponent<PlayerState>();
        }
        bool canChoose = CanChoose();
        openButton.interactable = canChoose;
        abilityLabel.text = _local != null && _local.ShapeshiftUsed
            ? (_local.IsShapeshifted ? "DISGUISED" : "SHIFT USED")
            : canChoose ? "SHAPESHIFT" : "SHIFT (NIGHT)";
        if (!canChoose) selectionPanel.SetActive(false);
        if (selectionPanel.activeSelf) RefreshTargets();
    }

    private void OpenSelection()
    {
        if (!CanChoose()) return;
        _pendingUntil = 0f;
        selectionPanel.SetActive(true);
        RefreshTargets();
    }

    private void RefreshTargets()
    {
        var runner = _local.Runner;
        int slot = 0;
        foreach (var player in runner.ActivePlayers.OrderBy(p => p.PlayerId))
        {
            if (player == runner.LocalPlayer || slot >= targetButtons.Length) continue;
            var obj = runner.GetPlayerObject(player);
            var state = obj != null && obj.IsValid ? obj.GetComponent<PlayerState>() : null;
            if (state == null || !state.IsAlive || !state.HasSelectedCharacter) continue;
            _targets[slot] = player;
            targetButtons[slot].gameObject.SetActive(true);
            targetButtons[slot].interactable = Time.unscaledTime >= _pendingUntil;
            targetButtons[slot].image.color = CharacterSelectionCatalog.GetTint(state.SelectedCharacterId);
            targetLabels[slot].text = CharacterSelectionCatalog.GetName(state.SelectedCharacterId);
            slot++;
        }
        for (int i = slot; i < targetButtons.Length; i++) targetButtons[i].gameObject.SetActive(false);
        statusText.text = Time.unscaledTime < _pendingUntil ? "Requesting disguise..."
            : slot == 0 ? "No living players available."
            : "Choose a player. One use per match. Ends at dawn or a meeting.";
    }

    private void SelectTarget(int slot)
    {
        if (!CanChoose() || Time.unscaledTime < _pendingUntil) return;
        _pendingUntil = Time.unscaledTime + 1.5f;
        _local.RPC_RequestShapeshift(_targets[slot]);
    }
}
