using TMPro;
using UnityEngine;

// References point to saved Canvas objects. No runtime UI generation.
public sealed class DiscussionChatUI : MonoBehaviour
{
    public GameObject panel;
    public TMP_InputField input;
    public TMP_Text messages;
    public TMP_Text status;
    public UnityEngine.UI.Button sendButton;
    public UnityEngine.UI.ScrollRect scroll;
    public UnityEngine.UI.Button continueButton;
    public TMP_Text continueLabel;
    private NetworkGameManager _manager;
    private int _revision = -1;
    private float _nextSend;

    private void Awake()
    {
        input.characterLimit = NetworkGameManager.ChatMaxLength;
        input.richText = false;
        messages.richText = false;
        input.onSubmit.AddListener(Submit);
        sendButton.onClick.AddListener(Send);
        if (continueButton != null) continueButton.onClick.AddListener(ContinueToVoting);
    }

    private void OnDestroy()
    {
        input.onSubmit.RemoveListener(Submit);
        sendButton.onClick.RemoveListener(Send);
        if (continueButton != null) continueButton.onClick.RemoveListener(ContinueToVoting);
    }

    private void Update()
    {
        var gm = NetworkGameManager.Instance;
        bool open = gm != null && gm.ChatOpen;
        if (panel.activeSelf != open)
        {
            panel.SetActive(open);
            if (!open) { input.DeactivateInputField(); input.text = string.Empty; }
        }
        if (!open) return;
        if (_manager != gm) { _manager = gm; _revision = -1; }
        var local = gm.Runner.GetPlayerObject(gm.Runner.LocalPlayer);
        bool alive = local != null && local.TryGetComponent(out PlayerState state) && state.IsAlive;
        if (continueButton != null)
        {
            bool discussion = gm.CurrentPhase == NetworkGameManager.GamePhase.Discussion;
            continueButton.gameObject.SetActive(discussion);
            bool requested = gm.HasRequestedVoting(gm.Runner.LocalPlayer);
            continueButton.interactable = discussion && alive && !requested;
            continueLabel.text = (requested ? "WAITING FOR PLAYERS" : "CONTINUE TO VOTING") +
                " (" + gm.DiscussionReadyCount + "/" + gm.DiscussionLivingCount + ")";
        }
        input.interactable = alive;
        sendButton.interactable = alive && Time.unscaledTime >= _nextSend &&
            NetworkGameManager.CleanChat(input.text).Length > 0;
        status.text = !alive ? "Spectating - read only" : Time.unscaledTime < _nextSend ?
            "Please wait..." : "Enter to send | 180 characters";
        if (_revision == gm.ChatRevision) return;
        _revision = gm.ChatRevision;
        messages.text = gm.ChatMessages.Count == 0 ? "Discuss your suspicions here." :
            string.Join("\n\n", gm.ChatMessages);
        Canvas.ForceUpdateCanvases();
        scroll.verticalNormalizedPosition = 0;
    }

    private void Submit(string value) => Send();
    private void ContinueToVoting()
    {
        var gm = NetworkGameManager.Instance;
        if (gm != null && gm.ChatOpen && continueButton.interactable) gm.RPC_ContinueToVoting();
    }
    public void Send()
    {
        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.ChatOpen || !input.interactable || Time.unscaledTime < _nextSend) return;
        string clean = NetworkGameManager.CleanChat(input.text);
        if (clean.Length == 0) return;
        gm.RPC_SendDiscussionChat(clean);
        _nextSend = Time.unscaledTime + 1f;
        input.text = string.Empty;
        input.ActivateInputField();
    }
}
