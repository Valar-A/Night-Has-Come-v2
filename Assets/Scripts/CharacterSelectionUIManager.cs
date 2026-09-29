using System.Linq;
using Fusion;
using TMPro;
using UnityEngine;

// Scene-wired controller for LobbyScene/Canvas/LobbyPanel/CharacterSelectionTab.
// This script never creates UI. All panels, buttons, images and layout groups are
// real GameObjects authored in the Unity Editor and referenced through the Inspector.
public class CharacterSelectionUIManager : MonoBehaviour
{
    [SerializeField] private UnityEngine.UI.Button[] characterButtons;
    [SerializeField] private UnityEngine.UI.Image[] slotBackgrounds;
    [SerializeField] private UnityEngine.UI.RawImage[] characterPortraits;
    [SerializeField] private GameObject[] takenOverlays;
    [SerializeField] private TMP_Text[] takenLabels;
    [SerializeField] private TMP_Text statusText;

    private PlayerState _localPlayerState;
    private int _pendingCharacterId = -1;
    private float _pendingSince;
    private string _feedback;
    private float _feedbackUntil;

    private static readonly Color AvailableSlot = new Color32(24, 31, 41, 245);
    private static readonly Color SelectedSlot = new Color32(20, 184, 166, 255);
    private static readonly Color TakenSlot = new Color32(45, 48, 55, 245);

    private void Awake()
    {
        if (!HasCompleteInspectorSetup())
        {
            Debug.LogError("[CharacterSelectionUI] Expected exactly 10 wired character slots.", this);
            enabled = false;
            return;
        }

        for (int i = 0; i < CharacterSelectionCatalog.CharacterCount; i++)
        {
            int characterId = i;
            characterButtons[i].onClick.AddListener(() => RequestCharacter(characterId));
        }
    }

    private void Update()
    {
        NetworkGameManager gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.Object == null || !gm.Object.IsValid || gm.Runner == null) return;
        if (gm.CurrentPhase != NetworkGameManager.GamePhase.Lobby) return;

        ResolveLocalPlayer(gm);
        UpdatePendingRequest();
        RefreshSlots(gm);
        RefreshStatus(gm);
    }

    private bool HasCompleteInspectorSetup()
    {
        int count = CharacterSelectionCatalog.CharacterCount;
        return characterButtons != null && characterButtons.Length == count
            && slotBackgrounds != null && slotBackgrounds.Length == count
            && characterPortraits != null && characterPortraits.Length == count
            && takenOverlays != null && takenOverlays.Length == count
            && takenLabels != null && takenLabels.Length == count
            && statusText != null;
    }

    private void ResolveLocalPlayer(NetworkGameManager gm)
    {
        if (_localPlayerState != null
            && _localPlayerState.Object != null
            && _localPlayerState.Object.IsValid)
        {
            return;
        }

        NetworkObject localObject = gm.Runner.GetPlayerObject(gm.Runner.LocalPlayer);
        _localPlayerState = localObject != null && localObject.TryGetComponent(out PlayerState state)
            ? state
            : null;
    }

    private void RequestCharacter(int characterId)
    {
        if (_localPlayerState == null || _pendingCharacterId >= 0) return;
        if (_localPlayerState.HasSelectedCharacter
            && _localPlayerState.SelectedCharacterId == characterId) return;

        _pendingCharacterId = characterId;
        _pendingSince = Time.unscaledTime;
        _feedback = $"Reserving {CharacterSelectionCatalog.GetName(characterId)}...";
        _feedbackUntil = Time.unscaledTime + 2f;
        _localPlayerState.RPC_RequestCharacter(characterId);
    }

    private void UpdatePendingRequest()
    {
        if (_pendingCharacterId < 0) return;

        bool accepted = _localPlayerState != null
            && _localPlayerState.HasSelectedCharacter
            && _localPlayerState.SelectedCharacterId == _pendingCharacterId;

        if (accepted)
        {
            _feedback = $"{CharacterSelectionCatalog.GetName(_pendingCharacterId)} selected";
            _feedbackUntil = Time.unscaledTime + 1.5f;
            _pendingCharacterId = -1;
        }
        else if (Time.unscaledTime - _pendingSince > 2f)
        {
            _feedback = "That character was just taken. Choose another.";
            _feedbackUntil = Time.unscaledTime + 2.5f;
            _pendingCharacterId = -1;
        }
    }

    private void RefreshSlots(NetworkGameManager gm)
    {
        PlayerRef[] owners = new PlayerRef[CharacterSelectionCatalog.CharacterCount];
        int[] ownerNumbers = new int[CharacterSelectionCatalog.CharacterCount];
        for (int i = 0; i < owners.Length; i++) owners[i] = PlayerRef.None;

        int playerNumber = 1;
        foreach (PlayerRef player in gm.Runner.ActivePlayers.OrderBy(p => p.PlayerId))
        {
            NetworkObject playerObject = gm.Runner.GetPlayerObject(player);
            if (playerObject != null
                && playerObject.TryGetComponent(out PlayerState state)
                && state.HasSelectedCharacter
                && CharacterSelectionCatalog.IsValid(state.SelectedCharacterId))
            {
                owners[state.SelectedCharacterId] = player;
                ownerNumbers[state.SelectedCharacterId] = playerNumber;
            }
            playerNumber++;
        }

        for (int characterId = 0; characterId < CharacterSelectionCatalog.CharacterCount; characterId++)
        {
            bool isMine = _localPlayerState != null
                && _localPlayerState.HasSelectedCharacter
                && _localPlayerState.SelectedCharacterId == characterId;
            bool takenByOther = owners[characterId] != PlayerRef.None && !isMine;

            slotBackgrounds[characterId].color = isMine
                ? SelectedSlot
                : (takenByOther ? TakenSlot : AvailableSlot);
            characterPortraits[characterId].color = takenByOther
                ? Color.Lerp(CharacterSelectionCatalog.GetTint(characterId), Color.black, 0.62f)
                : CharacterSelectionCatalog.GetTint(characterId);
            characterButtons[characterId].interactable = _localPlayerState != null
                && !takenByOther
                && !isMine
                && _pendingCharacterId < 0;

            takenOverlays[characterId].SetActive(takenByOther);
            if (takenByOther) takenLabels[characterId].text = $"TAKEN\nP{ownerNumbers[characterId]}";
        }
    }

    private void RefreshStatus(NetworkGameManager gm)
    {
        if (Time.unscaledTime < _feedbackUntil)
        {
            statusText.text = _feedback;
            return;
        }

        if (_localPlayerState == null)
        {
            statusText.text = "Loading your player...";
        }
        else if (!_localPlayerState.HasSelectedCharacter)
        {
            statusText.text = "SELECT ONE CHARACTER";
        }
        else
        {
            string name = CharacterSelectionCatalog.GetName(_localPlayerState.SelectedCharacterId);
            statusText.text = gm.AllPlayersHaveCharacterSelections()
                ? $"SELECTED: {name.ToUpperInvariant()}  •  EVERYONE READY"
                : $"SELECTED: {name.ToUpperInvariant()}  •  WAITING FOR PLAYERS";
        }
    }
}
