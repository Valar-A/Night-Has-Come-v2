using UnityEngine;
using UnityEngine.SceneManagement;

public class CameraFollow : MonoBehaviour
{
    // Singleton so PlayerMovement can find "the" gameplay camera reliably even
    // across additive scene loads (ConnectScene/LobbyScene/GameplayScene each
    // may or may not have their own Main Camera present at any given moment).
    public static CameraFollow Instance { get; private set; }

    public Transform target;

    public float distance = 5f;
    public float height = 2.5f;

    public float mouseSensitivity = 3f;
    public float smoothSpeed = 10f;

    float yaw;
    float pitch = 15f;

    void Awake()
    {
        Instance = this;

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
        if (gm == null || !gm.IsReady || gm.Runner == null) return;

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
        if (target == null) return;

        // Mouse input
        yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
        pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;

        // Limit vertical rotation
        pitch = Mathf.Clamp(pitch, -20f, 60f);

        // Calculate camera rotation
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0);

        // Calculate camera position
        Vector3 targetPosition = target.position + Vector3.up * height;

        Vector3 desiredPosition =
            targetPosition - rotation * Vector3.forward * distance;

        // Smooth camera movement
        transform.position = Vector3.Lerp(
            transform.position,
            desiredPosition,
            smoothSpeed * Time.deltaTime
        );

        // Look at player
        transform.LookAt(targetPosition);
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
