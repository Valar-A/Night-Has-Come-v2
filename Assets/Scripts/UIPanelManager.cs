using UnityEngine;

public class UIPanelManager : MonoBehaviour
{
    [Tooltip("Panel with the Host Game / Join Game / Browse Games buttons.")]
    [SerializeField] private GameObject connectPanel;

    [Tooltip("Panel showing the browsable list of public games (LobbyBrowserManager's panel).")]
    [SerializeField] private GameObject browsePanel;

    [Tooltip("Panel for configuring player count / mafia count / public-private before hosting.")]
    [SerializeField] private GameObject hostSetupPanel;

    [Tooltip("Popup with the code input field, Join button, and Cancel button.")]
    [SerializeField] private GameObject joinCodePanel;

    private float _lastLogTime;

    private void Awake()
    {
        if (connectPanel == null)
            Debug.LogError("[UIPanelManager] Connect Panel is not assigned in the Inspector!", this);
        if (browsePanel == null)
            Debug.LogError("[UIPanelManager] Browse Panel is not assigned in the Inspector!", this);
        if (hostSetupPanel == null)
            Debug.LogError("[UIPanelManager] Host Setup Panel is not assigned in the Inspector!", this);
        if (joinCodePanel == null)
            Debug.LogError("[UIPanelManager] Join Code Panel is not assigned in the Inspector!", this);

        ShowConnectPanel();
    }

    private void Update()
    {
        var gm = NetworkGameManager.Instance;
        bool isConnected = gm != null && gm.IsReady;

        if (Time.time - _lastLogTime > 1f)
        {
            _lastLogTime = Time.time;
            Debug.Log($"[UIPanelManager DEBUG] gm={(gm == null ? "NULL" : "found")}, " +
                      $"IsReady={(gm != null ? gm.IsReady.ToString() : "N/A")}, " +
                      $"isConnected={isConnected}");
        }

        if (isConnected)
        {
            if (connectPanel != null) connectPanel.SetActive(false);
            if (browsePanel != null) browsePanel.SetActive(false);
            if (hostSetupPanel != null) hostSetupPanel.SetActive(false);
            if (joinCodePanel != null) joinCodePanel.SetActive(false);
        }
    }

    public void ShowBrowsePanel()
    {
        connectPanel.SetActive(false);
        hostSetupPanel.SetActive(false);
        joinCodePanel.SetActive(false);
        browsePanel.SetActive(true);
    }

    public void ShowHostSetupPanel()
    {
        connectPanel.SetActive(false);
        browsePanel.SetActive(false);
        joinCodePanel.SetActive(false);
        hostSetupPanel.SetActive(true);
    }

    public void ShowJoinCodePanel()
    {
        connectPanel.SetActive(false);
        browsePanel.SetActive(false);
        hostSetupPanel.SetActive(false);
        joinCodePanel.SetActive(true);
    }

    public void ShowConnectPanel()
    {
        browsePanel.SetActive(false);
        hostSetupPanel.SetActive(false);
        joinCodePanel.SetActive(false);
        connectPanel.SetActive(true);
    }
}