using Fusion;
using UnityEngine;

// OBSOLETE TEST SCRIPT — kept only as an inert stub so any existing prefab
// reference to this component doesn't turn into a "missing script" error.
//
// Originally a keypress shortcut ('K' for Mafia kill, 'P' for Doctor protect)
// to test the night-action RPCs before real UI existed. Both are superseded
// now: Mafia kills go through NightActionUIManager's proximity scan (walking
// up to a target, not a global keypress — the 'K' shortcut here would have
// bypassed that range check entirely, which is exactly the exploit real UI
// was built to prevent), and the Doctor's action moved to a completely
// different mechanic (a post-Night blind guess during the DoctorSave phase,
// via DoctorSaveUIManager + NightActionManager.RPC_SubmitDoctorSave) that a
// simple "protect the other player" keypress can't represent any more.
// Safe to delete this component (and this file) entirely once you've
// confirmed nothing still references it.
public class NightActionTestInput : NetworkBehaviour
{
    public override void FixedUpdateNetwork() { } // not used, just satisfying NetworkBehaviour
}
