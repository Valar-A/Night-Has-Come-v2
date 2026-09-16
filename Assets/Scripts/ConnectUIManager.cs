using UnityEngine;
using TMPro;

// Attach this to your ConnectPanel (or the UIManager object, either works).
// Purely handles showing/hiding the Join Code popup — actual connecting logic
// still lives in NetworkBootstrap. Keeps "popup visibility" separate from
// "network connect" so the two don't get tangled together.
//
// WIRING:
//   "Join Game" button OnClick()   -> ConnectUIManager.OpenJoinPanel
//   "Cancel"/"Back" button OnClick() (optional, inside the popup) -> ConnectUIManager.CloseJoinPanel
//   "Join" button inside the popup OnClick() -> NetworkBootstrap.JoinGame (call this directly, not through here)
public class ConnectUIManager : MonoBehaviour
{
    [Tooltip("The popup panel with the code input field + Join button.")]
    [SerializeField] private GameObject joinCodePanel;

    [Tooltip("The error text inside the popup (e.g. 'Enter a code first' / 'Couldn't join'). Hidden every time the popup opens.")]
    [SerializeField] private TMP_Text joinErrorText;

    [Tooltip("The code input field inside the popup. Cleared every time the popup opens.")]
    [SerializeField] private TMP_InputField joinCodeInputField;

    private void Awake()
    {
        if (joinCodePanel != null)
        {
            joinCodePanel.SetActive(false); // starts hidden
        }

        if (joinErrorText != null)
        {
            joinErrorText.gameObject.SetActive(false); // starts hidden too
        }
    }

    public void OpenJoinPanel()
    {
        if (joinCodePanel != null) joinCodePanel.SetActive(true);
        if (joinErrorText != null) joinErrorText.gameObject.SetActive(false);
        if (joinCodeInputField != null) joinCodeInputField.text = "";
    }

    public void CloseJoinPanel()
    {
        if (joinCodePanel != null) joinCodePanel.SetActive(false);
    }
}
