using UnityEngine;
using UnityEngine.SceneManagement;

public class CameraFollow : MonoBehaviour
{
    // Singleton so PlayerMovement can find "the" gameplay camera reliably even
    // across additive scene loads (ConnectScene/LobbyScene/GameplayScene each
    // may or may not have their own Main Camera present at any given moment).
    public static CameraFollow Instance { get; private set; }

    public Transform target;

    [Header("First-person view")]
    public bool firstPerson = true;
    [Min(0.1f)] public float eyeHeight = 1.65f;
    [Range(40f, 100f)] public float firstPersonFov = 75f;
    private readonly System.Collections.Generic.Dictionary<Renderer, UnityEngine.Rendering.ShadowCastingMode> _ownerRenderers
        = new System.Collections.Generic.Dictionary<Renderer, UnityEngine.Rendering.ShadowCastingMode>();
    private readonly System.Collections.Generic.List<Renderer> _rendererBuffer = new System.Collections.Generic.List<Renderer>();
    private Transform _hiddenOwner;
    private bool _ownsCursor;
    private bool _wasFirstPersonLook;

    public float distance = 5f;
    public float height = 2.5f;

    public float mouseSensitivity = 3f;
    public float smoothSpeed = 10f;

    [Header("Movement framing")]
    [Min(0.1f)] public float fullSpeed = 6f;
    [Min(0f)] public float movementLookAhead = 0.2f;
    [Min(0f)] public float runningDistance = 0f;
    [Range(-15f, 15f)] public float runningPitchOffset = 2f;
    [Range(0f, 20f)] public float runningFovIncrease = 0f;
    [Min(0.1f)] public float framingResponse = 4f;
    [Min(1f)] public float teleportResetDistance = 10f;

    [Header("Shoulder framing (free roam)")]
    [Min(0.5f)] public float shoulderDistance = 2.2f;
    [Min(0.1f)] public float shoulderHeight = 1.65f;
    [Range(-1.5f, 1.5f)] public float shoulderOffset = 0.55f;

    [Header("Movement camera assistance")]
    public bool autoAlign = true;
    [Min(0f)] public float autoAlignDelay = 1.25f;
    [Min(0f)] public float autoAlignSpeed = 70f;
    [Min(0f)] public float autoAlignMinimumSpeed = 0.35f;
    [Range(-20f, 60f)] public float neutralPitch = 15f;

    [Header("Wall avoidance")]
    public LayerMask obstructionLayers = Physics.DefaultRaycastLayers;
    [Min(0.05f)] public float collisionRadius = 0.22f;
    [Min(0.01f)] public float wallPadding = 0.06f;
    [Min(0.1f)] public float collisionRecoverySpeed = 4f;

    [Header("Meeting framing")]
    [Min(0.5f)] public float meetingDistance = 4f;
    [Range(-20f, 60f)] public float meetingPitch = 18f;

    // Movement input uses the orbit yaw, so a sideways focus offset cannot
    // feed back into camera-relative movement and gradually steer the player.
    public float MovementYaw => _movementYaw;
    private float _movementYaw;
    private Vector2 _previousMoveInput;
    private float _manualLookGrace;
    private bool _wasMeeting;
    private float _boomLength;
    private float _shoulderBlend;
    private RaycastHit[] _wallHits = new RaycastHit[32];
    private Collider[] _overlaps = new Collider[32];
    private Camera _camera;
    private float _baseFov;
    private Transform _trackedTarget;
    private Vector3 _lastTargetPosition;
    private Vector3 _smoothedVelocity;
    private Vector3 _focus;
    private float _speedBlend;
    private bool _resetFraming = true;

    float yaw;
    float pitch = 15f;

    void Awake()
    {
        Instance = this;
        _camera = GetComponent<Camera>();
        if (_camera != null) _baseFov = _camera.fieldOfView;

        // CONFIRMED LIVE (a second, separate bug found on top of the migration one
        // below): GameplayScene's Main Camera had `target` pre-assigned in the
        // Inspector to the "Survivalist" PREFAB ASSET itself (dragged from the
        // Project window instead of the Hierarchy, or a leftover from early scene
        // authoring) — not any real spawned player. Diagnostic proof: the assigned
        // target's own `gameObject.scene.IsValid()` was false, its NetworkObject's
        // `IsValid`/`Runner` were both false, and its name had no "(Clone)" suffix
        // (real spawned players are always "...(Clone)") — all telltale signs of a
        // Project-asset reference, not a scene instance. Because that field started
        // OUT non-null, the old `if (target == null)` gate in LateUpdate() below
        // never even tried to resolve the real local player — the camera stayed
        // stuck on that static, disconnected asset forever, in completely normal
        // play with zero disconnects/migrations involved. Clearing it here means
        // whatever happens to be serialized in the scene/prefab can never matter —
        // TryAcquireTarget() below always establishes the real target itself.
        target = null;
    }

    // CONTINUOUSLY (re-)acquires the local player as target, instead of the old
    // one-shot coroutine that grabbed it once and stopped watching forever after.
    //
    // Root cause of "camera kisi bhi player ko follow nahi kar raha" happening
    // specifically AFTER a Host Migration (confirmed live, in both LobbyScene and
    // GameplayScene): the old coroutine WOULD find a valid player object early on
    // and assign it to `target` — but NetworkBootstrap.OnHostMigrationResume then
    // goes on to DESTROY every old player object and Spawn a fresh replacement for
    // each one (that's how the migration snapshot gets restored). That leaves
    // `target` pointing at an object that's since been destroyed. Unity overloads
    // `== null` for its own Object types to also mean "destroyed", so every
    // `target != null` check silently read as false from that point on — and
    // because the coroutine had already run its `yield break` and finished, nothing
    // was left watching to notice and re-acquire a fresh target.
    //
    // Checking every frame here instead means the camera self-heals from ANY case
    // where target goes missing or gets swapped out from under it — a migration
    // respawn, a reconnect, anything — not just the very first connection.
    private void TryAcquireTarget()
    {
        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.Object == null || !gm.Object.IsValid || gm.Runner == null) return;

        var localPlayerObject = gm.Runner.GetPlayerObject(gm.Runner.LocalPlayer);
        if (localPlayerObject != null)
        {
            target = localPlayerObject.transform;
        }
    }

    void LateUpdate()
    {
        // THIRD bug found in this same area, on top of the migration-null-target one
        // and the stray-prefab-asset one above: a stale Connect/Lobby-scene camera
        // can win the CameraFollow.Instance singleton race and stay "in control" of
        // mouse-look/movement input even after GameplayScene is up and a DIFFERENT
        // camera is the one actually rendering. CONFIRMED LIVE, after a real Host
        // Migration: THREE "Main Camera" GameObjects existed at once — GameplayScene's
        // own (which Camera.main correctly pointed to, i.e. what the player actually
        // saw), a disabled ConnectScene one (correctly disabled, that part's fine),
        // and a THIRD, separately-loaded "LobbyScene" camera that was somehow still
        // fully loaded and enabled — and THAT one, not GameplayScene's, had ended up
        // as CameraFollow.Instance, purely because its Awake() happened to run AFTER
        // GameplayScene's during that migration's scene reconstruction. Since
        // NetworkBootstrap.OnInput() reads cameraYaw from CameraFollow.Instance, every
        // mouse-look motion was rotating the INVISIBLE Lobby camera while the player
        // watched a DIFFERENT, un-rotated camera — so pressing W moved the player
        // relative to a direction they couldn't see and never intended, showing up as
        // "W moves sideways instead of forward". It only ever "fixed itself" on a
        // SECOND migration by coincidence of load order that time going the other way
        // — not because anything was actually resolved.
        //
        // DeactivateIfStale() below removes the luck dependency: any camera that
        // belongs to ConnectScene or LobbyScene defers (deactivates itself and gives
        // up Instance) the moment GameplayScene exists, checked freshly EVERY FRAME
        // (not just once in Awake()) so it self-corrects no matter which camera's
        // Awake() happened to run first/last, or how many stray duplicates piled up.
        if (DeactivateIfStale()) return;

        // CONFIRMED LIVE BUG this fixes: this script used to unconditionally
        // re-acquire and follow the LOCAL PLAYER'S OWN transform every frame,
        // with no awareness that the local player could be eliminated. Once
        // PlayerState.Kill() fires, SpectatorController.EnterSpectateMode()
        // takes over Camera.main and tries to follow an ALIVE target instead —
        // but this LateUpdate() runs AFTER SpectatorController's own Update()
        // (Unity's fixed Update -> LateUpdate order) and was overwriting that
        // with the dead player's frozen position on the very same frame, so
        // spectating never visibly worked; the camera just stayed stuck on the
        // corpse forever. Bail out completely while spectating is active and
        // let SpectatorController own the camera for the rest of the match.
        if (SpectatorController.Instance != null && SpectatorController.Instance.IsSpectating)
        {
            RestoreOwnerVisibility();
            ReleaseCursor();
            _resetFraming = true;
            if (_camera != null) _camera.fieldOfView = _baseFov;
            return;
        }

        if (Instance != this) Instance = this;

        // ALWAYS attempt to (re-)sync target to the real local player every frame —
        // not just when target == null. This is what actually closes off the whole
        // class of "stuck on a stale/wrong reference" bug, regardless of WHY target
        // ended up wrong (a pre-assigned Inspector/prefab reference that was never
        // null to begin with, a destroyed migration-era object, a leftover from a
        // previous scene, anything else not yet seen) — instead of only reacting
        // once target already reads null, this keeps target continuously pinned to
        // whatever Fusion's own authoritative GetPlayerObject(LocalPlayer) says it
        // should be, every single frame, whenever that's available. If resolution
        // is momentarily unavailable (e.g. NetworkGameManager not ready yet, right
        // at scene load), TryAcquireTarget() simply leaves target as-is for that
        // frame and the next frame tries again.
        TryAcquireTarget();
        if (target == null)
        {
            RestoreOwnerVisibility();
            ReleaseCursor();
            _resetFraming = true;
            return;
        }

        var gm = NetworkGameManager.Instance;
        bool phaseKnown = gm != null && gm.IsReady && gm.Object != null && gm.Object.IsValid;
        bool meeting = phaseKnown && (gm.CurrentPhase == NetworkGameManager.GamePhase.Discussion
            || gm.CurrentPhase == NetworkGameManager.GamePhase.Voting);
        // Lobby walking uses the same close, movement-responsive camera as the
        // match. Keep this camera-only: lobby must not enable gameplay abilities.
        bool freeRoam = !phaseKnown || gm.CurrentPhase == NetworkGameManager.GamePhase.Lobby
            || NetworkGameManager.IsFreeRoamPhase(gm.CurrentPhase);
        bool overUI = UnityEngine.EventSystems.EventSystem.current != null
            && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
        if (firstPerson)
        {
            bool selectionComplete = target.TryGetComponent(out PlayerState owner) && owner.HasSelectedCharacter;
            UpdateFirstPerson(Time.deltaTime, freeRoam && selectionComplete, meeting, overUI);
            return;
        }
        RestoreOwnerVisibility();
        ReleaseCursor();
        Vector2 look = overUI || meeting ? Vector2.zero
            : new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
        Vector2 move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        UpdateFraming(Time.deltaTime, look, move, freeRoam, meeting, overUI);
    }

    private void UpdateFirstPerson(float dt, bool freeRoam, bool meeting, bool overUI)
    {
        bool leavingMeeting = _wasMeeting && !meeting;
        bool startingLook = freeRoam && !meeting && !_wasFirstPersonLook;
        _wasFirstPersonLook = freeRoam && !meeting;
        bool reset = _resetFraming || _trackedTarget != target
            || (target.position - _lastTargetPosition).sqrMagnitude > teleportResetDistance * teleportResetDistance;
        if (reset || (meeting && !_wasMeeting))
        {
            yaw = target.eulerAngles.y;
            pitch = 0f;
            _trackedTarget = target;
            _resetFraming = false;
        }
        _wasMeeting = meeting;
        _lastTargetPosition = target.position;
        if (!freeRoam || meeting) ReleaseCursor();
        else if (Input.GetKeyDown(KeyCode.Escape)) ReleaseCursor();
        else if (!_ownsCursor && !overUI && Input.GetMouseButtonDown(0))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _ownsCursor = true;
        }
        // Lock on initial acquisition; Escape releases it until the player clicks the world.
        if ((reset || leavingMeeting || startingLook) && freeRoam && !meeting)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _ownsCursor = true;
        }
        if (freeRoam && !meeting && _ownsCursor && Cursor.lockState == CursorLockMode.Locked)
        {
            yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
            pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * mouseSensitivity, -85f, 85f);
        }
        _movementYaw = yaw;
        // No orbit boom, shoulder offset, auto-turn or sprint pull-back in first person.
        transform.SetPositionAndRotation(target.position + Vector3.up * eyeHeight, Quaternion.Euler(pitch, yaw, 0f));
        if (_camera != null)
        {
            _camera.nearClipPlane = .03f;
            _camera.fieldOfView = firstPersonFov;
        }
        if (_hiddenOwner != target)
        {
            RestoreOwnerVisibility();
            _hiddenOwner = target;
        }
        target.GetComponentsInChildren(true, _rendererBuffer);
        foreach (var renderer in _rendererBuffer)
        {
            if (!_ownerRenderers.ContainsKey(renderer)) _ownerRenderers.Add(renderer, renderer.shadowCastingMode);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        }
    }

    private void RestoreOwnerVisibility()
    {
        foreach (var entry in _ownerRenderers)
            if (entry.Key != null) entry.Key.shadowCastingMode = entry.Value;
        _ownerRenderers.Clear();
        _hiddenOwner = null;
    }

    private void ReleaseCursor()
    {
        if (!_ownsCursor) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        _ownsCursor = false;
    }

    private void OnDisable()
    {
        RestoreOwnerVisibility();
        ReleaseCursor();
        _resetFraming = true;
        _wasFirstPersonLook = false;
        if (Instance == this) Instance = null;
    }

    // Input is supplied separately so the framing/collision behaviour can also
    // be exercised deterministically without a network session or physical mouse.
    private void UpdateFraming(float dt, Vector2 look, Vector2 move, bool freeRoam, bool meeting, bool overUI)
    {
        if (dt <= 0f || target == null) return;
        Vector3 displacement = target.position - _lastTargetPosition;
        bool reset = _resetFraming || _trackedTarget != target
            || displacement.sqrMagnitude > teleportResetDistance * teleportResetDistance;
        bool enteringMeeting = meeting && !_wasMeeting;
        _wasMeeting = meeting;
        float focusHeight = freeRoam || meeting ? shoulderHeight : height;
        if (reset)
        {
            _trackedTarget = target;
            _smoothedVelocity = Vector3.zero;
            _speedBlend = 0f;
            _focus = target.position + Vector3.up * focusHeight;
            // On a new player/respawn start behind their facing direction.
            yaw = target.eulerAngles.y;
            _movementYaw = yaw;
            _manualLookGrace = autoAlignDelay;
            _shoulderBlend = freeRoam ? shoulderOffset : 0f;
            _resetFraming = false;
        }
        if (enteringMeeting)
        {
            yaw = target.eulerAngles.y;
            pitch = meetingPitch;
            _smoothedVelocity = Vector3.zero;
            _speedBlend = 0f;
        }
        _lastTargetPosition = target.position;

        float framingBlend = 1f - Mathf.Exp(-Mathf.Max(.1f, framingResponse) * dt);
        Vector3 velocity = reset || !freeRoam ? Vector3.zero : displacement / dt;
        velocity.y = 0f;
        velocity = Vector3.ClampMagnitude(velocity, Mathf.Max(.1f, fullSpeed) * 1.5f);
        _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, velocity, framingBlend);
        float speed = Mathf.Clamp01(_smoothedVelocity.magnitude / Mathf.Max(.1f, fullSpeed));
        _speedBlend = Mathf.Lerp(_speedBlend, speed, framingBlend);

        bool manualLook = !meeting && !overUI && look.sqrMagnitude > 0.0001f;
        _manualLookGrace = Mathf.Max(0f, _manualLookGrace - dt);
        if (manualLook)
        {
            yaw += look.x * mouseSensitivity;
            pitch -= look.y * mouseSensitivity;
            _manualLookGrace = autoAlignDelay;
        }
        if (overUI) _manualLookGrace = autoAlignDelay;

        // Hold a stable movement reference while automatic orbit is turning.
        // A new direction or manual look restores camera-relative steering.
        if (reset || manualLook || move.sqrMagnitude < 0.01f
            || (move.normalized - _previousMoveInput.normalized).sqrMagnitude > 0.01f)
            _movementYaw = yaw;
        _previousMoveInput = move;

        if (autoAlign && freeRoam && !overUI && !manualLook && _manualLookGrace <= 0f
            && move.sqrMagnitude > 0.01f && _smoothedVelocity.magnitude >= autoAlignMinimumSpeed)
        {
            float desiredYaw = Mathf.Atan2(_smoothedVelocity.x, _smoothedVelocity.z) * Mathf.Rad2Deg;
            float delta = Mathf.DeltaAngle(yaw, desiredYaw);
            // Ease near the desired angle, with a hard maximum turn speed.
            yaw += Mathf.Clamp(delta * (1f - Mathf.Exp(-2.5f * dt)), -autoAlignSpeed * dt, autoAlignSpeed * dt);
            pitch = Mathf.MoveTowards(pitch, neutralPitch, 12f * dt);
        }

        // Limit vertical rotation
        pitch = Mathf.Clamp(pitch, -20f, 60f);

        // Calculate camera rotation
        Quaternion rotation = Quaternion.Euler(Mathf.Clamp(pitch + (freeRoam ? runningPitchOffset * _speedBlend : 0f), -20f, 60f), yaw, 0);

        // Calculate camera position
        Vector3 lead = _smoothedVelocity / Mathf.Max(.1f, fullSpeed) * movementLookAhead;
        lead = Vector3.ClampMagnitude(lead, movementLookAhead);
        if (!freeRoam) lead = Vector3.zero;
        Vector3 pivot = target.position + Vector3.up * focusHeight;
        Vector3 targetPosition = pivot + lead;
        _focus = reset ? targetPosition : Vector3.Lerp(_focus, targetPosition, framingBlend);
        _shoulderBlend = Mathf.Lerp(_shoulderBlend, freeRoam ? shoulderOffset : 0f, framingBlend);
        // Offset both camera and its aim point: aiming at the player's centre
        // would cancel the shoulder composition and put them back in the centre.
        Vector3 aim = _focus + rotation * Vector3.right * _shoulderBlend;
        float desiredDistance = meeting ? meetingDistance : freeRoam
            ? shoulderDistance + runningDistance * _speedBlend : distance;
        Vector3 desiredPosition = aim - rotation * Vector3.forward * Mathf.Max(.5f, desiredDistance);
        Vector3 candidate = reset ? desiredPosition : Vector3.Lerp(transform.position, desiredPosition,
            1f - Mathf.Exp(-Mathf.Max(.1f, smoothSpeed) * dt));
        // Collision is resolved AFTER smoothing; otherwise interpolation itself
        // can put the camera through a corner. Pull in immediately, ease out.
        Vector3 offset = candidate - pivot;
        float wantedLength = offset.magnitude;
        float safeLength = FindClearDistance(pivot, offset);
        // Recover towards the unobstructed destination, not the already-smoothed
        // candidate; using the latter would compound damping every frame.
        float clearDestination = FindClearDistance(pivot, desiredPosition - pivot);
        if (reset || clearDestination < _boomLength) _boomLength = clearDestination;
        else _boomLength = Mathf.Lerp(_boomLength, clearDestination,
            1f - Mathf.Exp(-collisionRecoverySpeed * dt));
        transform.position = pivot + (wantedLength > .001f ? offset / wantedLength * Mathf.Min(_boomLength, safeLength) : Vector3.zero);
        Vector3 view = aim - transform.position;
        transform.rotation = view.sqrMagnitude > .001f ? Quaternion.LookRotation(view, Vector3.up) : rotation;
        if (_camera != null)
            _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView,
                Mathf.Clamp(_baseFov + (freeRoam ? runningFovIncrease * _speedBlend : 0f), 20f, 100f), framingBlend);
    }

    private float FindClearDistance(Vector3 origin, Vector3 offset)
    {
        float length = offset.magnitude;
        if (length < .001f) return 0f;
        // Fusion Multiple Peer mode gives each runner a separate physics scene.
        PhysicsScene physics = target.gameObject.scene.GetPhysicsScene();
        if (!physics.IsValid()) return length;
        float radius = Mathf.Max(.05f, collisionRadius);
        if (_camera != null)
        {
            float halfHeight = _camera.nearClipPlane * Mathf.Tan(_camera.fieldOfView * .5f * Mathf.Deg2Rad);
            radius = Mathf.Max(radius, Mathf.Sqrt(halfHeight * halfHeight * (1f + _camera.aspect * _camera.aspect)
                + _camera.nearClipPlane * _camera.nearClipPlane));
        }
        // Sphere casts do not reliably report a collider containing their origin.
        // Fail closed in that case rather than allowing a view through the wall.
        int overlaps;
        do
        {
            overlaps = physics.OverlapSphere(origin, radius, _overlaps, obstructionLayers, QueryTriggerInteraction.Ignore);
            if (overlaps < _overlaps.Length) break;
            System.Array.Resize(ref _overlaps, _overlaps.Length * 2);
        } while (true);
        for (int i = 0; i < overlaps; i++) if (BlocksCamera(_overlaps[i])) return 0f;

        int count;
        do
        {
            count = physics.SphereCast(origin, radius, offset / length, _wallHits, length, obstructionLayers, QueryTriggerInteraction.Ignore);
            if (count < _wallHits.Length) break;
            System.Array.Resize(ref _wallHits, _wallHits.Length * 2);
        } while (true);
        float nearest = length;
        for (int i = 0; i < count; i++)
            if (BlocksCamera(_wallHits[i].collider))
                nearest = Mathf.Min(nearest, Mathf.Max(0f, _wallHits[i].distance - wallPadding));
        return nearest;
    }

    private bool BlocksCamera(Collider collider)
    {
        // Players must not push the camera around when passing behind its owner.
        return collider != null && !collider.transform.IsChildOf(target)
            && collider.GetComponentInParent<PlayerState>() == null;
    }

    // Returns true (and deactivates this camera) if it belongs to ConnectScene or
    // LobbyScene while GameplayScene already exists — see the long comment in
    // LateUpdate() above for why this needs to be checked fresh every frame rather
    // than once in Awake(). Relinquishes Instance first so whichever camera actually
    // belongs to GameplayScene (or, in the fully-merged post-migration case, whatever
    // camera legitimately remains) can claim it on its own very next LateUpdate().
    private bool DeactivateIfStale()
    {
        bool gameplayPresent = BelongsToAnyLoadedInstance("GameplayScene");
        if (!gameplayPresent) return false;

        if (BelongsTo("ConnectScene") || BelongsTo("LobbyScene"))
        {
            if (Instance == this) Instance = null;
            gameObject.SetActive(false);
            return true;
        }

        return false;
    }

    // True if THIS camera's own GameObject lives inside the named original scene —
    // handles both the normal case (a real, separately-loaded Unity Scene by that
    // exact name) and the post-Host-Migration merged case (Fusion collapses
    // everything into one physical scene and wraps each original scene's content in
    // a root GameObject literally named "[SceneName]" instead — see the same pattern
    // already used in ConnectToLobbyTransition/LobbyToGameplayTransition).
    private bool BelongsTo(string sceneName)
    {
        if (gameObject.scene.name == sceneName) return true;

        Transform root = transform;
        while (root.parent != null) root = root.parent;
        return root.gameObject.name == "[" + sceneName + "]";
    }

    // True if the named original scene currently exists ANYWHERE in the loaded
    // scene set — as a real separate Scene, or as a post-migration "[SceneName]"
    // merged wrapper. Static-scoped check (not "this camera's own scene") since this
    // is used to ask "has the match progressed far enough that MY scene is stale",
    // not "am I in that scene".
    private static bool BelongsToAnyLoadedInstance(string sceneName)
    {
        return SceneManager.GetSceneByName(sceneName).isLoaded || GameObject.Find("[" + sceneName + "]") != null;
    }
}
