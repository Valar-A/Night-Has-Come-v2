using UnityEngine;

public enum TaskNodeKind
{
    // One of the 25 station tasks. Each player is given five of these, spread
    // across the building.
    Assigned,

    // The one task EVERY player is given, same node for everybody, for the whole
    // match. That is the entire point of it: "I watched him walk past the
    // register three times and never sign it" is only a usable accusation if
    // everyone is supposed to sign the same register.
    Common,
}

// Dropped onto every task marker by BlackwoodSchoolGenerator. The task system
// binds to these, never to coordinates.
//
// Floor is stored rather than worked out from the marker's Y at runtime. It
// would be easy to derive — the levels are a flat 4m apart and the markers sit
// 0.6m above each slab — but that arithmetic would be a silent dependency on two
// generator constants, and the day either one changes, task assignment would
// quietly start thinking the roof is the second floor with nothing to show for
// it. Writing it down at generation time means the number can only ever be wrong
// if the generator itself is wrong.
[DisallowMultipleComponent]
public class TaskNodeMarker : MonoBehaviour
{
    [SerializeField] private string displayName;
    [SerializeField] private TaskNodeKind kind = TaskNodeKind.Assigned;
    [SerializeField] private int floor;
    [SerializeField] private string floorName;

    [Tooltip("How close a player has to be to work at this station. The SERVER re-checks this distance before accepting a completion, so raising it here also raises what the server will accept.")]
    [SerializeField] private float interactRadius = 2.2f;

    public string DisplayName => string.IsNullOrEmpty(displayName) ? name : displayName;
    public TaskNodeKind Kind => kind;
    public int Floor => floor;
    public string FloorName => floorName;
    public float InteractRadius => interactRadius;

    // Called by the generator. Public because the generator is an Editor script in
    // a different assembly and cannot touch private serialized fields directly.
    public void Configure(string display, TaskNodeKind nodeKind, int floorIndex, string floorLabel)
    {
        displayName = display;
        kind = nodeKind;
        floor = floorIndex;
        floorName = floorLabel;
    }
}
