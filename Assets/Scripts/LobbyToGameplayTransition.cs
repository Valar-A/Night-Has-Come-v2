using System.Collections;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

// Attach this to the SAME persistent object as ConnectToLobbyTransition
// (e.g. the UIManager in your Connect scene) — NOT to something inside
// LobbyScene itself, since we're about to unload that scene and don't want
// this script's own GameObject getting destroyed mid-transition.
//
// Watches for CurrentPhase leaving Lobby (i.e. Start Game was pressed and
// BeginMatch() ran), then loads GameplayScene additively and unloads
// LobbyScene. NetworkGameManager itself doesn't need any changes — it
// already advances RoleAssign -> Day -> Sunset -> Night ->
// Discussion -> Voting -> Day automatically via its own timers.
public class LobbyToGameplayTransition : MonoBehaviour
{
    [Tooltip("Exact name of your Gameplay scene, as it appears in Build Settings.")]
    [SerializeField] private string gameplaySceneName = "GameplayScene";

    [Tooltip("Exact name of your Lobby scene — this gets unloaded once Gameplay is up.")]
    [SerializeField] private string lobbySceneNameToUnload = "LobbyScene";

    private bool _triggered;

    private void Update()
    {
        if (_triggered) return;

        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady) return;

        // CONFIRMED LIVE BUG this guards against: same race as LobbyUIManager's own
        // Object.IsValid check — during a Host Migration, the OLD (about-to-be-
        // destroyed) NetworkGameManager stays reachable via the static Instance for
        // one more frame (GameObject not destroyed yet, plain IsReady bool still
        // true) even though Fusion has already torn down its networked backing as
        // part of runner.Shutdown(). Reading CurrentPhase on it threw
        // InvalidOperationException ("Networked properties can only be accessed
        // when Spawned() has been called"), caught live on a client mid-migration.
        // Checking Object.IsValid first avoids ever touching a Fusion-invalidated
        // networked property.
        if (gm.Object == null || !gm.Object.IsValid) return;

        if (gm.CurrentPhase != NetworkGameManager.GamePhase.Lobby)
        {
            _triggered = true;

            // CONFIRMED LIVE BUG this guards against: this script's own GameObject
            // lives in ConnectScene, which is NOT DontDestroyOnLoad-protected — every
            // Host Migration reloads ConnectScene fresh (see NetworkBootstrap.
            // OnHostMigration's sceneInfo), so a brand new instance of this script
            // shows up with _triggered reset to false every single migration too.
            // Now that CurrentPhase correctly survives a migration instead of
            // resetting to Lobby (see NetworkGameManager.Spawned()), that fresh
            // instance's very first Update() sees CurrentPhase already past Lobby and
            // — with no way to tell the difference — treated that exactly like a
            // genuine "Start Game was just pressed" transition, loading a SECOND,
            // brand new, un-migrated copy of GameplayScene right on top of the one
            // the migration's own sceneInfo already restored. See
            // NetworkBootstrap.MigrationResumedPastLobby's own comment for the full
            // chain of what that duplicate broke (fought the correctly-migrated
            // NetworkGameManager for the Instance singleton and reset CurrentPhase
            // back to Lobby all over again).
            //
            // CONFIRMED LIVE BUG, second iteration: this used to check a simple
            // "was the most recent connection a migration" bool that never got reset
            // until the NEXT fresh connection. That silently broke the "Host
            // disconnects WHILE STILL IN THE LOBBY" case — CurrentPhase correctly
            // stayed Lobby right after that migration (so this block correctly didn't
            // fire yet), but the bool stayed stuck true with nothing left to clear it
            // (no new connection happens just because the game continues in the same
            // migrated session). When the new Host genuinely clicked "Start Game"
            // afterwards, CurrentPhase finally left Lobby for real — but this still
            // read that same stale true value and wrongly skipped the transition,
            // which is why the Lobby UI (Start Game button, code, player count) never
            // went away even though the match had genuinely started.
            // MigrationResumedPastLobby fixes this by reflecting whether THIS
            // specific migration resumed past Lobby or not, decided once at the
            // moment of that migration and never touched again until the next one —
            // so a later genuine Start Game press is never mistaken for a migration
            // resume, no matter how much later it happens.
            if (NetworkBootstrap.MigrationResumedPastLobby)
            {
                Debug.Log("[LobbyToGameplayTransition] CurrentPhase is past Lobby because of a Host Migration resume, not a fresh Start Game — skipping the load/unload transition (GameplayScene is already being restored by the migration itself).");
                return;
            }

            StartCoroutine(TransitionToGameplay());
        }
    }

    private IEnumerator TransitionToGameplay()
    {
        // CONFIRMED LIVE BUG this replaced: this used to `yield break` HERE the
        // instant GameplayScene was found "already present" for ANY reason — which
        // skipped EVERYTHING below it too, including the SetActiveScene switch and,
        // critically, unloading LobbyScene (i.e. hiding its Start Game button/code/
        // player-count UI). Update() above already guarantees this coroutine is only
        // ever started for a genuine, non-migration "Start Game was just pressed"
        // transition — a real migration resume returns out of Update() before ever
        // calling this coroutine at all (see the MigrationResumedPastLobby check
        // there). So by the time execution reaches here, GameplayScene being
        // "already loaded" is NEVER a legitimate reason to skip hiding the Lobby.
        // Live-caught cause: simply having GameplayScene open ADDITIVELY in the
        // EDITOR (e.g. mid-edit on its UI, forgetting to close it) before pressing
        // Play carries that leftover scene straight into Play Mode — this check then
        // misread it as "gameplay already transitioned" and silently bailed out of
        // the whole coroutine, which is exactly why "game start hoti hai lekin lobby
        // ki UI hide nahi hoti" happened on a completely normal, non-migration Start
        // Game press, with no error or duplicate anything to make the cause obvious.
        // Only the actual LOAD call below is genuinely redundant when this is true —
        // the active-scene switch and Lobby unload/hide must still always happen.
        bool gameplayAlreadyPresent = SceneManager.GetSceneByName(gameplaySceneName).isLoaded
            || GameObject.Find("[" + gameplaySceneName + "]") != null;

        if (gameplayAlreadyPresent)
        {
            Debug.Log($"[LobbyToGameplayTransition] {gameplaySceneName} already present — skipping the (redundant) load, but still switching active scene and unloading {lobbySceneNameToUnload}.");
        }
        else
        {
            var loadOp = SceneManager.LoadSceneAsync(gameplaySceneName, LoadSceneMode.Additive);
            yield return loadOp;
            Debug.Log($"[LobbyToGameplayTransition] {gameplaySceneName} loaded additively.");
        }

        // ===================================================================
        // Move GameplayScene's contents into the RUNNER'S OWN simulation scene.
        // ===================================================================
        //
        // CONFIRMED LIVE BUG — this is the one that made players "walk fine on the
        // old plane, then fall straight through the grass and keep falling forever".
        //
        // Fusion does not simulate in Unity's default physics scene. This runner
        // creates its OWN PhysicsScene, and SimpleKCC on the player prefab does all
        // of its ground checks against that one (runner.GetPhysicsScene()), NOT
        // against Physics.defaultPhysicsScene. Measured live during a real match:
        //
        //     runnerPhysicsScene == Physics.defaultPhysicsScene   ->  False
        //     player  scene = NetworkRunner_Host_..._[Player:1]   ->  runner physics
        //     Lawn    scene = GameplayScene                       ->  DEFAULT physics
        //     runnerPhysics.Raycast(under the player)             ->  NOTHING
        //     Physics.Raycast(same ray, default scene)            ->  hits Lawn
        //
        // ConnectScene's content works because Fusion merged it into the runner's
        // scene when the runner started, so it lives in the runner's physics scene.
        // But GameplayScene is loaded here with a plain SceneManager.LoadSceneAsync,
        // which leaves every collider in it — the whole school, every floor slab,
        // every wall, the lawn — in the DEFAULT physics scene, completely invisible
        // to the character controller. The level was, from the player's point of
        // view, simply not there. That is why they only fell once ConnectScene's
        // plane stopped holding them up, and why the fall never ended: there is no
        // geometry at all in the runner's physics scene below y = 0.
        //
        // This also explains why every static check passed while the game was
        // broken. Editor-side raycasts, capsule casts and the floor-hole sweep all
        // query the default physics scene, where the geometry genuinely is solid.
        // Only a query through runner.GetPhysicsScene() shows the hole.
        //
        // Moving the roots across puts the level in the same physics scene as the
        // players. RenderSettings (skybox, ambient, fog) stay with the GameplayScene
        // asset itself rather than its GameObjects, so the SetActiveScene call below
        // still applies the right lighting even though the scene is left empty.
        NetworkRunner runner = NetworkGameManager.Instance != null ? NetworkGameManager.Instance.Runner : null;
        if (runner != null)
        {
            Scene simulationScene = runner.SimulationUnityScene;
            Scene loadedGameplay = SceneManager.GetSceneByName(gameplaySceneName);

            if (simulationScene.IsValid() && loadedGameplay.IsValid() && loadedGameplay.isLoaded
                && loadedGameplay != simulationScene)
            {
                GameObject[] gameplayRoots = loadedGameplay.GetRootGameObjects();
                foreach (GameObject gameplayRoot in gameplayRoots)
                {
                    SceneManager.MoveGameObjectToScene(gameplayRoot, simulationScene);
                }
                Debug.Log($"[LobbyToGameplayTransition] Moved {gameplayRoots.Length} root object(s) from {gameplaySceneName} into the runner's simulation scene '{simulationScene.name}' so their colliders exist in Fusion's physics scene.");
            }
        }
        else
        {
            Debug.LogError("[LobbyToGameplayTransition] No NetworkRunner available — GameplayScene's colliders will stay in the default physics scene and players will fall through the level.");
        }

        // Same reasoning as ConnectToLobbyTransition's SetActiveScene call — GameplayScene
        // was only loaded ADDITIVELY, so without this the active scene stays LobbyScene (or
        // whatever it already was) and GameplayScene's own skybox/lighting never takes over.
        // Set BEFORE unloading LobbyScene below so there's never a gap with no active scene.
        //
        // CONFIRMED LIVE BUG this replaced: `SceneManager.GetSceneByName(gameplaySceneName)`
        // only ever finds a genuinely SEPARATE Scene — live testing showed Fusion's own
        // NetworkSceneManagerDefault (assigned as the runner's SceneManager) merges
        // GameplayScene's content into a "[GameplayScene]" wrapper GameObject inside the
        // runner's own scene EVERY time it's loaded while a NetworkRunner with that scene
        // manager is active — not just after a Host Migration, as this code originally
        // assumed (see the identical pattern already handled in NetworkBootstrap's own
        // FixActiveSceneDelayed/lobbyLoaded checks and ConnectToLobbyTransition's
        // DisableConnectSceneCamera). That meant `gameplayScene.IsValid()` was false on a
        // completely normal, first-time Start Game press too, so this block silently never
        // ran and the active scene stayed ConnectScene the whole match. Fall back to the
        // wrapper's own `.scene` (the real, named scene it actually lives in) the same way
        // the rest of the codebase already does.
        var gameplayScene = SceneManager.GetSceneByName(gameplaySceneName);
        Scene desiredActiveScene = new Scene();
        if (gameplayScene.IsValid() && gameplayScene.isLoaded)
        {
            desiredActiveScene = gameplayScene;
        }
        else
        {
            GameObject gameplayWrapper = GameObject.Find("[" + gameplaySceneName + "]");
            if (gameplayWrapper != null)
            {
                desiredActiveScene = gameplayWrapper.scene;
            }
        }

        if (desiredActiveScene.IsValid() && desiredActiveScene.isLoaded)
        {
            SceneManager.SetActiveScene(desiredActiveScene);
            Debug.Log($"[LobbyToGameplayTransition] '{desiredActiveScene.name}' set as the active scene (for skybox/lighting).");
        }

        // Bring the discussion room up NOW, in this same transition, instead of
        // loading it each time Discussion starts.
        //
        // Per-round loading would mean an async scene load landing on the exact tick
        // the Host wants to teleport ten players into that room — the teleport would
        // run against a room that does not exist yet, and every peer would be racing
        // its own copy of that load independently. Loading it once, up front, means
        // that by the time the first Discussion arrives (several phases later) the
        // room is guaranteed present, already inside the runner's physics scene, and
        // already parked at DiscussionRoom.WorldOrigin on every peer — which is what
        // makes the Host's computed seat positions land in the same place on every
        // client. It costs one additive scene sitting 300m above the map that nothing
        // renders until somebody is standing in it.
        //
        // Deliberately placed AFTER SetActiveScene so the active scene stays
        // GameplayScene and the school's skybox/lighting are the ones that apply.
        yield return DiscussionRoom.LoadAndPlace(runner);

        // Now — and ONLY now — kill ConnectScene's leftover flat "Plane" test floor.
        //
        // It is a 21.7m square sitting at y = 0.21, centred on the world origin, and
        // because ConnectScene is deliberately never unloaded (NetworkRunner and
        // NetworkGameManager live there for the whole match) it otherwise stays active
        // for the entire game — hovering 0.21m above the school's real Ground floor
        // and covering everything within ~11m of the origin, which is both the spawn
        // area and the south-west corner of the building. Players ended up standing on
        // a stray slab instead of the actual level, and walking off its edge is what
        // dropped them onto the lawn in the first place.
        //
        // The TIMING matters more than the removal. Doing this in
        // ConnectToLobbyTransition (where it was first tried) pulled the floor out from
        // under players while it was still the only thing holding them up, and they
        // fell out of the world permanently — caught live at y = -63,000 and still
        // accelerating. By this line GameplayScene is loaded and active, so there is
        // real geometry underneath: the Ground floor slab at y = 0 inside the building
        // footprint, and the lawn at y = -0.30 outside it. Worst case a player drops
        // half a metre onto solid ground.
        var connectScene = SceneManager.GetSceneByName("ConnectScene");
        GameObject strayFloor = null;
        if (connectScene.IsValid() && connectScene.isLoaded)
        {
            foreach (var root in connectScene.GetRootGameObjects())
            {
                if (root.name == "Plane") { strayFloor = root; break; }
            }
        }
        if (strayFloor == null)
        {
            // Post-migration / Fusion-merged case: ConnectScene's content lives under a
            // "[ConnectScene]" wrapper instead of in a scene of its own.
            var connectWrapper = GameObject.Find("[ConnectScene]");
            if (connectWrapper != null)
            {
                var planeTransform = connectWrapper.transform.Find("Plane");
                if (planeTransform != null) strayFloor = planeTransform.gameObject;
            }
        }
        if (strayFloor != null && strayFloor.activeSelf)
        {
            strayFloor.SetActive(false);
            Debug.Log("[LobbyToGameplayTransition] Disabled ConnectScene's leftover 'Plane' test floor — GameplayScene's real ground is now under the players.");
        }

        var lobbyScene = SceneManager.GetSceneByName(lobbySceneNameToUnload);
        if (lobbyScene.IsValid() && lobbyScene.isLoaded)
        {
            var unloadOp = SceneManager.UnloadSceneAsync(lobbyScene);
            yield return unloadOp;
            Debug.Log($"[LobbyToGameplayTransition] {lobbySceneNameToUnload} unloaded.");
        }
        else
        {
            // POST HOST-MIGRATION CASE — confirmed live: after a Host Migration, Fusion
            // doesn't keep ConnectScene/LobbyScene as separate loadable Unity Scenes
            // anymore. It merges everything into ONE physical scene (named after the
            // new runner) and wraps each original scene's content in a root GameObject
            // literally named "[LobbyScene]" / "[ConnectScene]" instead. SceneManager.
            // GetSceneByName("LobbyScene") can never find that (it's not a real Scene),
            // so the block above silently did nothing — LobbyScene's Canvas/UI just sat
            // there forever on top of Gameplay. This exact symptom is why "gameplay
            // scene load hota hai lekin lobby ki UI hide nahi hoti" only ever showed up
            // AFTER a migration, never on a normal (no-migration) Start Game press.
            // Fall back to finding and destroying that wrapper GameObject directly.
            var wrapper = GameObject.Find("[" + lobbySceneNameToUnload + "]");
            if (wrapper != null)
            {
                Destroy(wrapper);
                Debug.Log($"[LobbyToGameplayTransition] Post-migration case: destroyed the merged-scene wrapper for {lobbySceneNameToUnload} instead of unloading a scene.");
            }
        }
    }
}
