using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach to a "HostSetupPanel" UI GameObject. Shown when the player clicks
// "Host Game" on the Connect panel, BEFORE the actual session is created —
// lets them configure the room, then confirms and calls NetworkBootstrap.HostGame().
//
// SUGGESTED UI:
//  - A Slider or TMP_Dropdown for player count (5-10, matching your GDD's range)
//  - A Toggle Group with two Toggles ("2 Mafia" / "3 Mafia")
//  - A Toggle for Public/Private (or two Toggles in a Toggle Group)
//  - A "Confirm & Host" button -> OnConfirmClicked()
public class HostSetupPanel : MonoBehaviour
{
    [Header("Player Count")]
    [Tooltip("Slider for player count. Set Min Value = 5, Max Value = 10, Whole Numbers = true in the Inspector.")]
    [SerializeField] private Slider playerCountSlider;
    [SerializeField] private TMP_Text playerCountLabel; // shows "Players: 8" etc.

    [Header("Mafia Count")]
    [Tooltip("Toggle for '2 Mafia'. Put both mafia toggles in the same Toggle Group so only one can be on.")]
    [SerializeField] private Toggle twoMafiaToggle;
    [Tooltip("Toggle for '3 Mafia'.")]
    [SerializeField] private Toggle threeMafiaToggle;

    [Header("Public / Private")]
    [Tooltip("Toggle for Public lobby (shows up in Browse Games). If off, treated as Private.")]
    [SerializeField] private Toggle publicToggle;

    [SerializeField] private NetworkBootstrap networkBootstrap;

    private void OnEnable()
    {
        // Reasonable defaults each time the panel opens.
        if (playerCountSlider != null)
        {
            playerCountSlider.value = 10;
            UpdatePlayerCountLabel(playerCountSlider.value);
        }
        if (twoMafiaToggle != null) twoMafiaToggle.isOn = true;
        if (publicToggle != null) publicToggle.isOn = true;
    }

    // Hook this to the player count Slider's OnValueChanged().
    public void OnPlayerCountChanged(float value)
    {
        UpdatePlayerCountLabel(value);
    }

    private void UpdatePlayerCountLabel(float value)
    {
        if (playerCountLabel != null)
        {
            playerCountLabel.text = $"Players: {Mathf.RoundToInt(value)}";
        }
    }

    // Hook this to the "Confirm & Host" button's OnClick().
    public void OnConfirmClicked()
    {
        int playerCount = playerCountSlider != null ? Mathf.RoundToInt(playerCountSlider.value) : 10;
        int mafiaCount = (threeMafiaToggle != null && threeMafiaToggle.isOn) ? 3 : 2;
        bool isPublic = publicToggle == null || publicToggle.isOn;

        networkBootstrap.HostGame(playerCount, mafiaCount, isPublic);
    }
}
