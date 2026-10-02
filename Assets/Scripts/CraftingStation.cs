using UnityEngine;

// Authored on the existing workbench in the crafting room, never created at runtime.
public class CraftingStation : MonoBehaviour
{
    [SerializeField, Min(.1f)] private float interactRadius = 2.5f;
    public static bool IsNear(Transform player)
    {
        foreach (var station in FindObjectsByType<CraftingStation>(FindObjectsSortMode.None))
            if (station.gameObject.scene == player.gameObject.scene
                && Vector3.Distance(player.position, station.transform.position) <= station.interactRadius) return true;
        return false;
    }
}
