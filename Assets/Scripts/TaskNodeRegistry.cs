using System.Collections.Generic;
using UnityEngine;

// The level's task stations, in ONE fixed order that every peer agrees on.
//
// That agreement is the whole job. The Host deals out task lists as byte indices
// into this order; every client turns those bytes back into stations. If two
// peers built the list in a different order, a player would be told to fix the
// boiler while the server checked whether they were standing on the roof — and
// nothing would log an error, the completions would just silently fail. So the
// order is by NAME, not by hierarchy order (one drag in the Hierarchy would
// reshuffle that) and not by discovery order (GetComponentsInChildren is
// documented as hierarchy order, but it is not a promise worth betting the whole
// system on).
//
// Index layout: commons first, then assigned. Both blocks sorted by name.
// Everything fits in a byte as long as the level has fewer than 255 stations.
public static class TaskNodeRegistry
{
    private const string SchoolRootName = "BlackwoodSchool_Blockout";

    private static TaskNodeMarker[] _all;      // commons then assigned
    private static int _commonCount;
    private static readonly Dictionary<int, List<byte>> _assignedByFloor = new();

    public static int Count => _all?.Length ?? 0;
    public static int CommonCount => _commonCount;
    public static int AssignedCount => (_all?.Length ?? 0) - _commonCount;

    public static void Forget()
    {
        _all = null;
        _commonCount = 0;
        _assignedByFloor.Clear();
    }

    public static bool Resolve()
    {
        if (_all != null && _all.Length > 0) return true;

        GameObject root = GameObject.Find(SchoolRootName);
        if (root == null) return false;

        var commons = new List<TaskNodeMarker>();
        var assigned = new List<TaskNodeMarker>();

        foreach (TaskNodeMarker marker in root.GetComponentsInChildren<TaskNodeMarker>(true))
        {
            if (marker.Kind == TaskNodeKind.Common) commons.Add(marker);
            else assigned.Add(marker);
        }

        if (commons.Count == 0 && assigned.Count == 0)
        {
            Debug.LogError($"[TaskNodeRegistry] '{SchoolRootName}' has no TaskNodeMarker components. The blockout in this scene predates the task system — re-run Tools/Blackwood/Generate School Blockout.");
            return false;
        }

        commons.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        assigned.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        _commonCount = commons.Count;
        _all = new TaskNodeMarker[commons.Count + assigned.Count];
        commons.CopyTo(_all, 0);
        assigned.CopyTo(_all, commons.Count);

        if (_all.Length >= TaskManager.NoTask)
        {
            Debug.LogError($"[TaskNodeRegistry] {_all.Length} task nodes — indices are bytes and {TaskManager.NoTask} is reserved for 'empty', so the level cannot have that many.");
            return false;
        }

        _assignedByFloor.Clear();
        for (int i = _commonCount; i < _all.Length; i++)
        {
            int floor = _all[i].Floor;
            if (!_assignedByFloor.TryGetValue(floor, out List<byte> list))
            {
                list = new List<byte>();
                _assignedByFloor[floor] = list;
            }
            list.Add((byte)i);
        }

        Debug.Log($"[TaskNodeRegistry] {_commonCount} common + {AssignedCount} assigned station(s) across {_assignedByFloor.Count} floor(s).");
        return true;
    }

    public static bool TryGetNode(byte index, out TaskNodeMarker node)
    {
        node = null;
        if (!Resolve()) return false;
        if (index >= _all.Length) return false;
        node = _all[index];
        return node != null;
    }

    public static string GetDisplayName(byte index)
        => TryGetNode(index, out TaskNodeMarker node) ? node.DisplayName : "(unknown task)";

    public static byte PickRandomCommonIndex()
    {
        if (!Resolve() || _commonCount == 0) return TaskManager.NoTask;
        return (byte)Random.Range(0, _commonCount);
    }

    public static List<int> FloorsWithAssignedNodes()
    {
        Resolve();
        return new List<int>(_assignedByFloor.Keys);
    }

    public static bool TryPickAssignedOnFloor(int floor, HashSet<byte> exclude, out byte index)
    {
        index = TaskManager.NoTask;
        if (!Resolve()) return false;
        if (!_assignedByFloor.TryGetValue(floor, out List<byte> candidates)) return false;

        // Collect what is left on this floor, then pick one at random — rather than
        // picking at random and retrying, which can loop for a long time once a
        // floor is nearly exhausted.
        var available = new List<byte>();
        foreach (byte b in candidates)
        {
            if (!exclude.Contains(b)) available.Add(b);
        }
        if (available.Count == 0) return false;

        index = available[Random.Range(0, available.Count)];
        return true;
    }

    public static bool TryPickAnyAssigned(HashSet<byte> exclude, out byte index)
    {
        index = TaskManager.NoTask;
        if (!Resolve()) return false;

        var available = new List<byte>();
        for (int i = _commonCount; i < _all.Length; i++)
        {
            if (!exclude.Contains((byte)i)) available.Add((byte)i);
        }
        if (available.Count == 0) return false;

        index = available[Random.Range(0, available.Count)];
        return true;
    }
}
