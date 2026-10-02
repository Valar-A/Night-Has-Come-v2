using Fusion;
using TMPro;
using UnityEngine;

// Scene-authored UI only. The host owns task time, rewards and crafting decisions.
public class TaskInteractionUI : MonoBehaviour
{
    [SerializeField] private GameObject promptRoot;
    [SerializeField] private TMP_Text promptText;
    [SerializeField] private UnityEngine.UI.Image progressFill;
    [SerializeField] private TMP_Text inventoryText;
    [SerializeField] private TMP_Text taskListText;
    private float _nextRequest;

    private void Update()
    {
        var tasks = TaskManager.Instance;
        var gm = NetworkGameManager.Instance;
        bool ready = tasks != null && tasks.Object != null && tasks.Object.IsValid
            && gm != null && gm.IsReady && gm.Object != null && gm.Object.IsValid && gm.Runner != null;
        if (!ready) { Show(null); if (inventoryText != null) inventoryText.text = ""; if (taskListText != null) taskListText.text = ""; return; }
        var player = gm.Runner.LocalPlayer;
        if (taskListText != null && tasks.TryGetLocalAssignment(out var list))
        {
            var text = new System.Text.StringBuilder("YOUR TASKS\n");
            for (int i = 0; i < TaskManager.TasksPerPlayer; i++)
                if (list.GetSlot(i) != TaskManager.NoTask)
                    text.AppendLine((list.IsSlotCompleted(i) ? "[DONE] " : "- ") + TaskNodeRegistry.GetDisplayName(list.GetSlot(i)));
            taskListText.text = text.ToString();
        }
        if (inventoryText != null)
        {
            bool hasInventory = tasks.TryGetInventory(player, out var inv);
            inventoryText.text = hasInventory
                ? $"METAL {inv.Metal}   WIRE {inv.Wire}   CHEMICAL {inv.Chemical}\n"
                    + (inv.Shield ? "SHIELD READY - blocks one mafia attack" : inv.Crafted ? "SHIELD USED" : tasks.ShieldClaimed ? "Shield already claimed" : $"SHIELD: {tasks.Recipe}")
                    + $"\nWALLET: {LocalCoinWallet.Balance} coins"
                : "";
        }
        if (!NetworkGameManager.IsFreeRoamPhase(gm.CurrentPhase)) { Show(null); return; }
        var obj = gm.Runner.GetPlayerObject(player);
        if (obj == null || !obj.TryGetComponent(out PlayerState state) || !state.IsAlive) { Show(null); return; }
        if (tasks.TryGetWork(player, out var work))
        {
            float remaining = work.Timer.RemainingTime(gm.Runner) ?? 0f;
            Show($"{TaskNodeRegistry.GetDisplayName(work.Node)} - {remaining:0.0}s\nStay near the station", 1f - remaining / TaskManager.WorkSeconds);
            return;
        }
        if (CraftingStation.IsNear(obj.transform))
        {
            tasks.TryGetInventory(player, out var inv);
            bool affordable = tasks.CanAfford(inv);
            Show(tasks.ShieldClaimed ? "Shield already crafted this match" : affordable
                ? "Press [F] - Craft shield" : $"Shield requires {tasks.Recipe}");
            if (!tasks.ShieldClaimed && affordable && Input.GetKeyDown(KeyCode.F) && Time.unscaledTime >= _nextRequest)
            { tasks.RPC_CraftShield(); _nextRequest = Time.unscaledTime + .5f; }
            return;
        }
        if (!tasks.TryGetLocalAssignment(out var assignment)) { Show(null); return; }
        byte best = TaskManager.NoTask;
        float distance = float.MaxValue;
        TaskNodeMarker node = null;
        for (int i = 0; i < TaskManager.TasksPerPlayer; i++)
        {
            byte index = assignment.GetSlot(i);
            if (index == TaskManager.NoTask || assignment.IsSlotCompleted(i)
                || !TaskNodeRegistry.TryGetNode(index, out var candidate)) continue;
            float d = Vector3.Distance(obj.transform.position, candidate.transform.position);
            if (d > candidate.InteractRadius || d >= distance) continue;
            best = index; node = candidate; distance = d;
        }
        if (node == null) { Show(null); return; }
        Show($"Press [F] - {node.DisplayName}\n5 seconds - stay near the station");
        if (Input.GetKeyDown(KeyCode.F) && Time.unscaledTime >= _nextRequest)
        { tasks.RPC_StartTask(best); _nextRequest = Time.unscaledTime + .5f; }
    }

    private void Show(string text, float progress = 0f)
    {
        if (promptRoot != null) promptRoot.SetActive(text != null);
        if (promptText != null) promptText.text = text ?? "";
        if (progressFill != null) progressFill.fillAmount = Mathf.Clamp01(progress);
    }
}
