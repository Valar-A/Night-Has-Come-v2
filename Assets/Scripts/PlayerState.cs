using Fusion;
using Fusion.Addons.SimpleKCC;
using UnityEngine;

// Attach this to the PLAYER PREFAB (same one with NetworkObject + NetworkPlayerController).
// Unlike role data, alive/dead status is meant to be public — everyone needs to see who's
// still alive — so this IS a [Networked] property.
public class PlayerState : NetworkBehaviour
{
    [Networked] public NetworkBool IsAlive { get; set; }

    // Whether a dead player's corpse should currently be RENDERED. This is
    // separate from IsAlive on purpose: a Mafia kill needs the body visible
    // in the world (so someone can walk up and Report it — see
    // NightActionManager.RPC_ReportBody) for the rest of that Night, but the
    // body must then disappear the instant Night ends (see
    // NetworkGameManager.HideNightVictimBody), whether or not the Doctor goes
    // on to revive them in the DoctorSave phase that follows. Defaults to
    // false — a player who dies any other way (voted out, or the Doctor's own
    // wrong guess) is never shown as a lingering corpse at all.
    [Networked] public NetworkBool IsBodyVisible { get; set; }

    // HasBeenReported tracks whether some other living player has already found and
    // reported THIS specific body (see NightActionManager.RPC_ReportBody) — once
    // true, ReportUIManager's proximity scan skips it (a body only triggers
    // Discussion once, not every time someone new walks past the same corpse).
    // Reset back to false on every fresh Kill() so a player who gets revived and
    // later killed again can be reported anew.
    [Networked] public NetworkBool HasBeenReported { get; set; }

    // Local-only (not networked) — every peer detects the IsAlive/IsBodyVisible
    // change on its own and reacts visually. Same pattern as NetworkGameManager's
    // _lastSeenPhase for detecting networked-value changes in Render().
    private bool _wasAlive = true;
    private bool _wasBodyVisible;
    private Renderer[] _renderers;
    private SimpleKCC _kcc;
    private Animator _animator;

    public override void Spawned()
    {
        _renderers = GetComponentsInChildren<Renderer>();
        _kcc = GetComponent<SimpleKCC>();
        _animator = GetComponent<Animator>();

        if (Object.HasStateAuthority)
        {
            IsAlive = true;
            IsBodyVisible = false;
            HasBeenReported = false;
        }

        _wasAlive = IsAlive;
        _wasBodyVisible = IsBodyVisible;
        ApplyVisualState();
    }

    // Server-only — call this to eliminate a player. Called the INSTANT a Mafia
    // presses Kill while in range (see NightActionManager.RPC_SubmitMafiaTarget),
    // or by the Doctor's own wrong guess during DoctorSave.
    public void Kill()
    {
        if (!Object.HasStateAuthority) return;
        if (!IsAlive) return; // already dead, ignore

        IsAlive = false;
        IsBodyVisible = true; // visible immediately so it can be found & reported this Night
        HasBeenReported = false; // fresh death — reset in case this player was revived+re-killed before
        Debug.Log($"[PlayerState] Player {Object.InputAuthority} was eliminated.");
    }

    // Server-only — brings a player back into the match. Called only by the
    // Doctor correctly guessing this player as the Night's victim (see
    // NightActionManager.RPC_SubmitDoctorSave).
    public void Revive()
    {
        if (!Object.HasStateAuthority) return;
        if (IsAlive) return; // already alive, nothing to do

        IsAlive = true;
        IsBodyVisible = false; // no corpse to show once they're back up
        Debug.Log($"[PlayerState] Player {Object.InputAuthority} was revived by the Doctor!");
    }

    // Server-only — sets whether this (dead) player's corpse is currently
    // rendered. Called by NetworkGameManager.HideNightVictimBody() the instant
    // Night ends.
    public void SetBodyVisible(bool visible)
    {
        if (!Object.HasStateAuthority) return;
        IsBodyVisible = visible;
    }

    // Server-only — marks this body as already reported, so ReportUIManager's
    // proximity scan (on every peer) stops offering it as a reportable target.
    // Called by NightActionManager.RPC_ReportBody once a living player reports it.
    public void MarkReported()
    {
        if (!Object.HasStateAuthority) return;
        HasBeenReported = true;
    }

    // Runs on every client every rendered frame — detects the networked
    // IsAlive/IsBodyVisible flipping and reacts locally (this runs identically
    // on host and clients).
    public override void Render()
    {
        if (IsAlive == _wasAlive && IsBodyVisible == _wasBodyVisible) return;

        bool justDied = !IsAlive && _wasAlive;
        bool justRevived = IsAlive && !_wasAlive;

        _wasAlive = IsAlive;
        _wasBodyVisible = IsBodyVisible;
        ApplyVisualState();

        if (!Object.HasInputAuthority) return;

        // Only the affected player's OWN client should flip spectate mode —
        // everyone else just sees the body drop, disappear, or stand back up.
        if (justDied)
        {
            SpectatorController.Instance?.EnterSpectateMode();
        }
        else if (justRevived)
        {
            SpectatorController.Instance?.ExitSpectateMode();
        }
    }

    private void ApplyVisualState()
    {
        // Alive players are always rendered; a dead player's corpse is rendered
        // only while IsBodyVisible is true (see the field's own comment above).
        bool showRenderers = IsAlive || IsBodyVisible;
        if (_renderers != null)
        {
            foreach (var r in _renderers)
            {
                if (r != null) r.enabled = showRenderers;
            }
        }

        // Deactivating the KCC also stops PlayerMovement's kcc.Move() calls from
        // doing anything, as a second layer of protection alongside the IsAlive
        // check that should also guard movement there.
        if (_kcc != null)
        {
            _kcc.SetActive(IsAlive);
        }

        if (!IsAlive)
        {
            // Stop whatever idle/walk animation was mid-playback so the body doesn't
            // keep animating while "dead" on the ground.
            if (_animator != null) _animator.enabled = false;

            // Tip the whole body over so it visually reads as a corpse lying on the
            // ground rather than a frozen standing statue. Safe to do freely here —
            // the KCC is already disabled by this point, so nothing else is left
            // driving this object's rotation.
            transform.rotation = Quaternion.Euler(90f, transform.eulerAngles.y, 0f);
        }
        else
        {
            // Coming back from Revive() — stand back up and let the animator run again.
            if (_animator != null) _animator.enabled = true;
            transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        }
    }
}
