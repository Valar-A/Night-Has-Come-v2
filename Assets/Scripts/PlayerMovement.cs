using Fusion;
using Fusion.Addons.SimpleKCC;
using UnityEngine;

// SWITCHED FROM Unity's CharacterController TO Photon's own SimpleKCC addon.
// Unity's CharacterController keeps internal PhysX state that Fusion cannot rewind
// correctly during client-side prediction's resimulation, so the predicting machine
// (always the Client, since the Host is authoritative and never resimulates itself)
// got a tiny, near-constant stream of correction snaps — the "atak atak" stutter.
// SimpleKCC is Photon's purpose-built, deterministic, resimulation-safe replacement
// (the same kind of component their own Host-mode samples like BR200 use), so we can
// go back to FULL client-side prediction — instant local response for your own
// input, same as before the Host-authoritative workaround — without the stutter.
[RequireComponent(typeof(SimpleKCC))]
public class PlayerMovement : NetworkBehaviour
{
    [Header("Movement")]
    public float walkSpeed = 3f;
    public float runSpeed = 6f;
    public float rotationSpeed = 10f;
    [Tooltip("Face camera yaw while strafing/backing up, rather than turning toward travel.")]
    public bool firstPersonMovement = true;

    [Header("Movement Smoothing (PUBG Mobile-style accelerate/decelerate)")]
    [Tooltip("How fast speed ramps UP toward the target (walk/run). Higher = snappier start.")]
    public float acceleration = 6f;
    [Tooltip("How fast speed ramps DOWN toward a stop when input is released. Lower = longer slide-to-stop.")]
    public float deceleration = 8f;

    [Header("Jump / Gravity")]
    public float jumpHeight = 2f;
    public float gravity = -9.81f;

    [Header("Discussion Room")]
    [Tooltip("While Discussion/Voting run, players are teleported to the round table and held in place there. Turn this off to let them walk around the discussion room freely.")]
    [SerializeField] private bool lockMovementInDiscussionRoom = true;

    SimpleKCC kcc;
    Animator animator;
    PlayerState playerState;

    // currentSpeed01 is the SMOOTHED 0..1 blend value (0 = idle, 0.5 = walk, 1 = run) that drives
    // both the actual movement distance AND the Animator "Speed" parameter. It ramps gradually via
    // acceleration/deceleration instead of snapping instantly — this (plus lastMoveDirection, so the
    // character keeps sliding in the direction it was already moving while decelerating) is what
    // gives the PUBG Mobile-style smooth start/stop feel instead of the old instant on/off movement.
    // Both must be [Networked] (not plain fields) so Fusion resets them correctly on resimulation —
    // a plain field wouldn't be part of the predicted/reconciled state and would desync like the
    // Camera.main bug did.
    [Networked] float currentSpeed01 { get; set; }
    [Networked] Vector3 lastMoveDirection { get; set; }

    // One-shot jump "event" counter. Trigger parameters can't be networked directly, and firing
    // animator.SetTrigger() straight from FixedUpdateNetwork can double-fire during resimulation
    // (a predicting client can re-run the same tick more than once). So the trigger is applied in
    // FixedUpdateNetwork as before, but the Jump ANIMATION is driven from this Networked counter,
    // watched once per rendered frame in Render() — see the field's use below.
    [Networked] int jumpEventTicks { get; set; }
    int lastSeenJumpEventTicks;
    [Networked] int turnEventTicks { get; set; }
    [Networked] int turnDirection { get; set; }
    [Networked] int previousTurnInput { get; set; }
    int lastSeenTurnEventTicks;

    void Awake()
    {
        kcc = GetComponent<SimpleKCC>();
        animator = GetComponent<Animator>();
        playerState = GetComponent<PlayerState>();
    }

    // Called by NetworkBootstrap.OnHostMigrationResume right after CopyStateFrom()
    // restores this object from the previous Host's last snapshot. currentSpeed01/
    // lastMoveDirection are transient, tick-by-tick input state — carrying over
    // whatever they happened to be at snapshot time (e.g. "mid-run") is meaningless
    // once simulation resumes fresh, and (combined with FixedUpdateNetwork not yet
    // ticking for this object on this exact frame) is what caused the "running
    // animation stuck in place" look right after a migration. Snap back to a clean
    // idle so the character starts from a known, correct state instead.
    public void ResetTransientMovementState()
    {
        currentSpeed01 = 0f;
        lastMoveDirection = Vector3.zero;
        jumpEventTicks = 0;
        lastSeenJumpEventTicks = 0;
        turnEventTicks = 0;
        turnDirection = previousTurnInput = 0;
        lastSeenTurnEventTicks = 0;
    }

    public override void Spawned()
    {
        kcc.SetGravity(gravity);

        // NOTE: camera targeting used to be assigned FROM HERE (the player finding
        // CameraFollow.Instance once, at spawn time). That broke the moment
        // GameplayScene loaded: LobbyToGameplayTransition unloads LobbyScene (and its
        // camera) once GameplayScene's own camera is up, so a brand new CameraFollow
        // instance takes over — but this Spawned() coroutine had already run and
        // completed long before that, back when only the Lobby camera existed, so the
        // new Gameplay camera's target was simply never set. CameraFollow now finds
        // and locks onto the local player ITSELF via Runner.GetPlayerObject(), every
        // frame, whenever a new instance wakes up (see CameraFollow.TryAcquireTarget),
        // which correctly handles every scene transition instead of just the first one.

        // CRITICAL: Runner.GetPlayerObject()/SetPlayerObject() is a PER-RUNNER LOCAL
        // lookup table — it is NOT automatically replicated across peers just because
        // the NetworkObject itself is networked. Before this fix, SetPlayerObject was
        // only ever called from NetworkBootstrap.OnPlayerJoined() (guarded by
        // `if (!runner.IsServer) return;` — Host-only) and OnHostMigrationResume()
        // (also only runs on the NEW Host's own runner, per its own comment). That
        // means every peer that was NOT the Host — a completely ordinary Client during
        // a normal join, or ANY surviving Client (not just the new Host) after a Host
        // Migration — never registered its own local player object in its OWN runner's
        // table. GetPlayerObject(LocalPlayer) on those peers returned NULL forever,
        // even though the object existed, was fully networked, and had the correct
        // InputAuthority — confirmed live: after a migration, a surviving Client showed
        // PlayerMovement.InputAuthority == Runner.LocalPlayer (correct!) but
        // Runner.GetPlayerObject(Runner.LocalPlayer) still returned null, which is what
        // left CameraFollow's target stuck null on that peer specifically.
        //
        // Spawned() runs on EVERY peer, for EVERY player object, the moment that object
        // becomes networked-visible to them (Fusion's standard per-object lifecycle
        // callback) — regardless of whether this happened via a normal join or a
        // migration resume. That makes it the right single place to cover every case
        // (replacing the two narrower call sites in NetworkBootstrap), BUT it must only
        // actually CALL SetPlayerObject from the STATE AUTHORITY peer.
        //
        // CONFIRMED LIVE (this was a second, deeper bug hiding behind the first): calling
        // Runner.SetPlayerObject() from a Client peer is a silent no-op — it does NOT
        // register locally and does NOT replicate anywhere. Only a call made by the peer
        // that actually HAS STATE AUTHORITY over the object takes effect, and THAT
        // registration then correctly replicates out to every other peer automatically.
        // Verified directly: on a Client after Host Migration, GetPlayerObject() for the
        // NEW HOST's own player (registered via the Host's own Spawned()/HasStateAuthority
        // call) resolved correctly, while GetPlayerObject() for the Client's OWN player and
        // another Client's player (both would only ever have been registered by the
        // unconditional, HasStateAuthority-less call this comment used to describe)
        // stayed NULL — even immediately after manually re-calling SetPlayerObject from
        // that same Client, in the same frame, with the exact right PlayerRef/NetworkObject.
        //
        // In this project's Host-mode topology, Object.HasStateAuthority is true for every
        // player object ONLY on whichever peer currently IS the Host — so this guard keeps
        // working correctly across Host Migrations too, since after a migration only the
        // NEW Host's own Spawned() calls will satisfy it.
        if (Object != null && Object.HasStateAuthority && Object.InputAuthority.IsRealPlayer)
        {
            Runner.SetPlayerObject(Object.InputAuthority, Object);
        }
    }

    // SERVER-ONLY. Called by NetworkGameManager when the match moves players between
    // the school and the discussion room. Must never be called from a client — the
    // Host owns every player's position in this topology, and a client-side write
    // would just be stomped by the next snapshot anyway.
    public void TeleportTo(Vector3 position, Quaternion rotation)
    {
        if (Object == null || !Object.HasStateAuthority)
        {
            Debug.LogWarning("[PlayerMovement] TeleportTo called without state authority — ignored.");
            return;
        }

        if (kcc == null)
        {
            transform.SetPositionAndRotation(position, rotation);
            return;
        }

        // teleport:true is not optional. With it false, SimpleKCC INTERPOLATES from
        // the old position to the new one, and the school-to-discussion-room jump is
        // 300 metres — every player would visibly streak up through the sky and back
        // down again instead of just being somewhere else on the next frame.
        kcc.SetPosition(position, true, false);
        kcc.SetLookRotation(rotation, false, false);
        kcc.ResetVelocity();

        // A dead player has had their KCC switched off by PlayerState.ApplyVisualState,
        // so it is not simulating and will not push the transform to the new position
        // on its own. Write the transform directly for that case — it matters because
        // the Doctor can revive someone DURING Discussion, and a revived player has to
        // stand up at the table with everyone else, not back in the school where they
        // were killed.
        if (!kcc.IsActive)
        {
            transform.SetPositionAndRotation(position, rotation);
        }

        // Clear the movement blend, otherwise a player who was sprinting when
        // Discussion started arrives at the table still playing the run animation on
        // the spot.
        currentSpeed01 = 0f;
        lastMoveDirection = Vector3.zero;
        previousTurnInput = 0;
    }

    public override void FixedUpdateNetwork()
    {
        if (!GetInput(out NetworkInputData input))
            return;

        // CONFIRMED bug class fix — same "eliminated player can still play" issue
        // fixed for voting/night actions: PlayerState.ApplyVisualState() disables
        // the renderer and calls kcc.SetActive(false) once IsAlive flips false,
        // but this method itself never checked IsAlive at all, so a dead player's
        // movement/jump input was still being consumed and fed into kcc.Move()
        // every tick regardless — an invisible collider still fully player-
        // controlled instead of frozen in place once eliminated.
        if (playerState != null && !playerState.IsAlive)
            return;

        // Emergency discussion uses the same Fusion input path as movement and
        // jumping. Scene-side Input.GetKeyDown was unreliable after GameplayScene
        // roots were moved into the runner's simulation scene. The state-authority
        // copy handles the request once and validates distance at the hall station.
        if (input.discussionPressed && Object.HasStateAuthority)
        {
            NetworkGameManager.Instance?.TryCallDiscussionFromHall(Object.InputAuthority);
        }

        // Freeze everyone (alive players included) once the match has actually
        // ended — see NetworkGameManager.CheckWinCondition()/GameOver — so the
        // winner reveal screen is the sole focus instead of the "winning" side
        // still being able to wander the map while everyone reads the result.
        var gm = NetworkGameManager.Instance;
        if (gm != null && gm.IsReady && gm.CurrentPhase == NetworkGameManager.GamePhase.GameOver)
            return;

        // Discussion + Voting happen at the round table, and everyone is teleported
        // there by NetworkGameManager the moment Discussion starts. Holding them in
        // place for those two phases is what makes it a meeting instead of ten
        // people immediately sprinting away from the table they were just sat at.
        //
        // kcc.Move(zero, 0) rather than a bare `return`: Move() is what runs the
        // KCC's gravity, ground snap and depenetration. Skipping it entirely would
        // leave a player standing on nothing if they were mid-air when the phase
        // flipped, and would stop them being pushed out of any geometry they were
        // teleported slightly inside of. Feeding it zero velocity keeps all of that
        // working while ignoring input completely.
        if (lockMovementInDiscussionRoom && gm != null && gm.IsReady
            && (gm.CurrentPhase == NetworkGameManager.GamePhase.Discussion
                || gm.CurrentPhase == NetworkGameManager.GamePhase.Voting))
        {
            currentSpeed01 = 0f;   // so the Animator blends back to idle instead of holding a run pose
            previousTurnInput = 0;
            kcc.Move(Vector3.zero, 0f);
            return;
        }

        float dt = Runner.DeltaTime;
        bool isGrounded = kcc.IsGrounded;

        float h = input.horizontal;
        float v = input.vertical;
        bool running = input.running;
        int side = h < -.1f ? -1 : h > .1f ? 1 : 0;
        if (firstPersonMovement && isGrounded && !input.jumpPressed && side != 0 && side != previousTurnInput)
        {
            turnDirection = side;
            turnEventTicks++;
        }
        previousTurnInput = side;

        // IMPORTANT: this must come from input.cameraYaw (captured on the owning
        // player's own machine in NetworkBootstrap.OnInput), NOT from a local
        // Camera.main lookup here. FixedUpdateNetwork() runs on the HOST as the
        // authoritative simulation for every player AND locally on the owning
        // Client as a prediction — Camera.main on either machine only ever
        // resolves to that machine's own camera, which would silently desync
        // host vs. client if used directly instead of the value carried in input.
        Quaternion camYawRot = Quaternion.Euler(0, input.cameraYaw, 0);

        // =========================
        // MOVEMENT (GTA5-style: WASD relative to the camera, character turns
        // smoothly to face wherever it's actually travelling)
        // =========================
        Vector3 inputDir = new Vector3(h, 0, v);
        bool hasMoveInput = inputDir.magnitude > 0.1f;

        if (hasMoveInput)
        {
            Vector3 moveDir = camYawRot * Vector3.forward;
            Vector3 right = camYawRot * Vector3.right;

            // A/D (strafe) and S (backward) must produce their own world-space
            // direction here, not just "forward" — this normalized combination of
            // camera-forward and camera-right is what makes moving left/right/back
            // actually move (and turn) the character that way instead of only W
            // doing anything, which is what a forward-only vector would look like.
            lastMoveDirection = (moveDir * v + right * h).normalized;
        }

        // Ramp the 0..1 speed blend toward its target instead of snapping instantly —
        // faster acceleration than deceleration so starting feels responsive while
        // stopping still has a short, natural slide instead of a dead halt.
        float targetSpeed01 = hasMoveInput ? (running ? 1f : 0.5f) : 0f;
        float rampRate = targetSpeed01 > currentSpeed01 ? acceleration : deceleration;
        currentSpeed01 = Mathf.MoveTowards(currentSpeed01, targetSpeed01, rampRate * dt);

        Vector3 planarVelocity = Vector3.zero;
        if (currentSpeed01 > 0.01f && lastMoveDirection.sqrMagnitude > 0.01f)
        {
            planarVelocity = lastMoveDirection * ResolveSpeed(currentSpeed01);

            // Character rotates to face the direction it's actually moving in (GTA5
            // on-foot behaviour) — NOT locked to the camera every tick. The camera
            // (CameraFollow) orbits freely via the mouse on its own; only actual
            // movement turns the character body.
            //
            // IMPORTANT: SimpleKCC owns transform.rotation itself (it re-applies its own
            // tracked look rotation every kcc.Move() call), so writing transform.rotation
            // directly here gets silently overwritten the moment Move() runs below — the
            // body never actually turns, only the position moves. That's exactly why S
            // (and A/D strafing) walked/ran backward-or-sideways while still playing the
            // forward animation facing the original direction. Feed the smoothed rotation
            // through kcc.SetLookRotation() instead so it sticks.
            if (!firstPersonMovement)
            {
                Quaternion targetRot = Quaternion.LookRotation(lastMoveDirection);
                Quaternion smoothedRot = Quaternion.Slerp(kcc.TransformRotation, targetRot, rotationSpeed * dt);
                kcc.SetLookRotation(smoothedRot);
            }
        }

        if (firstPersonMovement) kcc.SetLookRotation(camYawRot);

        // =========================
        // JUMP
        // =========================
        float jumpImpulse = 0f;
        if (input.jumpPressed && isGrounded)
        {
            jumpImpulse = Mathf.Sqrt(jumpHeight * -2f * gravity);
            jumpEventTicks++; // Render() on every peer turns this into the Jump trigger — see field comment.
        }

        // SimpleKCC applies gravity internally (via SetGravity in Spawned()) and handles
        // ground snapping/step-up/collision resolution itself — no manual vel.y/gravity
        // integration needed here like the old CharacterController version required.
        kcc.Move(planarVelocity, jumpImpulse);
    }

    // Render() runs once per VISUAL frame on every peer (Host and every client) —
    // unlike FixedUpdateNetwork(), it never re-runs mid-frame during resimulation, so
    // it's the safe place to: (1) drive the Animator from the latest predicted/synced
    // state, and (2) detect the one-shot jump event (see jumpEventTicks) without risking
    // a double-fire, since Animator triggers can't be networked directly.
    public override void Render()
    {
        if (animator == null)
            return;

        animator.SetFloat("Speed", currentSpeed01);
        animator.SetBool("Grounded", kcc.IsGrounded);
        if (turnEventTicks != lastSeenTurnEventTicks)
        {
            lastSeenTurnEventTicks = turnEventTicks;
            animator.ResetTrigger("TurnLeft");
            animator.ResetTrigger("TurnRight");
            animator.SetTrigger(turnDirection < 0 ? "TurnLeft" : "TurnRight");
        }

        if (jumpEventTicks != lastSeenJumpEventTicks)
        {
            lastSeenJumpEventTicks = jumpEventTicks;
            animator.SetTrigger("Jump");
        }
    }

    // Maps the smoothed 0..1 speed blend to an actual m/s value, matching the same
    // 0 -> walk -> run curve the Animator's Locomotion blend tree uses (thresholds
    // 0 / 0.5 / 1), so the visual animation speed and the physical KCC movement
    // always stay in sync while currentSpeed01 ramps up/down.
    float ResolveSpeed(float speed01)
    {
        if (speed01 <= 0.5f)
            return Mathf.Lerp(0f, walkSpeed, speed01 / 0.5f);

        return Mathf.Lerp(walkSpeed, runSpeed, (speed01 - 0.5f) / 0.5f);
    }
}
