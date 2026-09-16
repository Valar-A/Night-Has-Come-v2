using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

// Attach this to any persistent object in your Connect scene (e.g. the UIManager
// GameObject). Watches for the local client successfully connecting/spawning,
// then loads the Lobby scene ADDITIVELY (on top of the current scene) and hides
// the Connect UI.
//
// IMPORTANT: this does NOT unload the current scene. NetworkRunner and
// NetworkGameManager must stay alive for the whole match — unloading their
// scene would destroy them and reset all networked state (phase, votes, etc).
public class ConnectToLobbyTransition : MonoBehaviour
{
    [Tooltip("Exact name of your Lobby scene, as it appears in File > Build Settings.")]
    [SerializeField] private string lobbySceneName = "LobbyScene";

    [Tooltip("The Connect UI root to hide once we move to the Lobby (e.g. the ConnectPanel or its Canvas). Leave empty to not hide anything.")]
    [SerializeField] private GameObject connectUIRoot;

    private bool _lobbyLoadTriggered;

    private void Update()
    {
        if (_lobbyLoadTriggered) return;

        var gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady) return;

        // CONFIRMED LIVE BUG this guards against: same Fusion-invalidated-networked-
        // property race as LobbyUIManager/LobbyToGameplayTransition's own
        // Object.IsValid checks — during a Host Migration the OLD (about-to-be-
        // destroyed) NetworkGameManager stays reachable via the static Instance for
        // one more frame (GameObject not destroyed yet, plain IsReady bool still
        // true) even though Fusion already tore down its networked backing as part
        // of runner.Shutdown(). Reading CurrentPhase on it below throws
        // InvalidOperationException. Check Object.IsValid first.
        if (gm.Object == null || !gm.Object.IsValid) return;

        {
            // CONFIRMED LIVE BUG this guards against: this script's own GameObject
            // lives in ConnectScene, which reloads FRESH on every single Host
            // Migration (see NetworkBootstrap.OnHostMigration's sceneInfo, which
            // always reloads ConnectScene with LoadSceneMode.Single) — so
            // _lobbyLoadTriggered resets to false every migration too, exactly like
            // LobbyToGameplayTransition's own _triggered flag. Without this check,
            // this fresh instance only ever asked "is NetworkGameManager ready" —
            // never "are we actually still in the Lobby" — so EVERY migration that
            // happened after the match had already moved past Lobby still loaded a
            // BRAND NEW, fully-working LobbyScene (Start Game button and all) right
            // alongside GameplayScene. That's what made "host disconnect during
            // gameplay" look exactly like getting sent back to the Lobby, reliably,
            // on every single migration — not a rare race. Only ever load Lobby
            // content if the match genuinely hasn't left Lobby yet.
            if (gm.CurrentPhase != NetworkGameManager.GamePhase.Lobby)
            {
                _lobbyLoadTriggered = true;
                Debug.Log("[ConnectToLobbyTransition] CurrentPhase is already past Lobby (Host Migration resume, most likely) — skipping the Lobby load.");

                // CONFIRMED LIVE BUG this fixes: skipping the Lobby load above also
                // used to skip DisableConnectSceneCamera(), since that call only ever
                // happened at the end of LoadLobbyScene()'s coroutine. ConnectScene is
                // never unloaded by this project's design and its own "Main Camera"
                // reloads fresh and ENABLED every migration (same as this script's own
                // GameObject) — with nothing left to disable it, it stayed active
                // forever after any migration, sitting right alongside GameplayScene's
                // camera. Both tagged MainCamera, both enabled, no explicit
                // depth/stacking between them — Unity's Camera.main resolution (and
                // actual on-screen rendering) became a coin flip between the two,
                // caught live returning ConnectScene's stray camera instead of
                // GameplayScene's correct one even though SceneManager's own
                // "active scene" was already correctly pointing at GameplayScene's
                // content. Call it here too so the stray camera gets disabled on every
                // migration, not just on a genuine first-time Lobby entry.
                DisableConnectSceneCamera();
                return;
            }

            _lobbyLoadTriggered = true;
            StartCoroutine(LoadLobbyScene());
        }
    }

    private IEnumerator LoadLobbyScene()
    {
        // Guard against loading it twice if this scene is somehow already loaded.
        //
        // POST HOST-MIGRATION CASE — confirmed live: after a Host Migration, Fusion
        // merges ConnectScene/LobbyScene into ONE physical scene and wraps each
        // original scene's content in a root GameObject named "[LobbyScene]" instead
        // of keeping it as a separately loadable Unity Scene. SceneManager.GetSceneByName
        // can't see that anymore, so without this extra check, a freshly-reloaded
        // ConnectToLobbyTransition instance (this script itself gets recreated by that
        // same migration reload) would think Lobby was never loaded and load a second,
        // genuinely duplicate copy of LobbyScene right on top of the merged one.
        bool alreadyPresent = SceneManager.GetSceneByName(lobbySceneName).isLoaded
            || GameObject.Find("[" + lobbySceneName + "]") != null;

        if (alreadyPresent)
        {
            Debug.LogWarning($"[ConnectToLobbyTransition] {lobbySceneName} already loaded (or present as a post-migration merged-scene wrapper), skipping.");
        }
        else
        {
            var op = SceneManager.LoadSceneAsync(lobbySceneName, LoadSceneMode.Additive);
            yield return op;
            Debug.Log($"[ConnectToLobbyTransition] {lobbySceneName} loaded additively.");
        }

        // Loading a scene ADDITIVELY does not make it the "active" scene — Unity keeps
        // whatever was active before (ConnectScene, in our case) as active. RenderSettings
        // (skybox, ambient light, fog) are per-scene data that Unity only applies from
        // whichever scene is currently marked active, so without this call Lobby stays
        // additively loaded but the ConnectScene's own (flat/default) skybox keeps being
        // shown instead of LobbyScene's — exactly the "lobby ka skybox nahi aata" symptom.
        // ConnectScene itself still stays loaded underneath (NetworkRunner/NetworkGameManager
        // are untouched by this) — only which scene's render settings/new-object placement
        // is considered "active" changes.
        var lobbyScene = SceneManager.GetSceneByName(lobbySceneName);
        if (lobbyScene.IsValid() && lobbyScene.isLoaded)
        {
            SceneManager.SetActiveScene(lobbyScene);
            Debug.Log($"[ConnectToLobbyTransition] {lobbySceneName} set as the active scene (for skybox/lighting).");
        }

        if (connectUIRoot != null)
        {
            connectUIRoot.SetActive(false);
        }

        DisableConnectSceneCamera();
    }

    // CONFIRMED live via diagnostic: ConnectScene's own "Main Camera" and LobbyScene's
    // own "Main Camera" are BOTH separate, plain Cameras (clearFlags=Skybox) that stay
    // enabled at the same time — ConnectScene is never unloaded by this project's design,
    // so its camera just keeps sitting there active. Two active Cameras with no explicit
    // stacking/priority between them render in an effectively undefined order, which is
    // the REAL mechanical cause of the "lobby ka skybox sahi nahi aata" symptom — SetActiveScene
    // above only fixes which scene's RenderSettings apply, it does nothing about a second
    // camera physically still rendering. Disabling ConnectScene's camera once we've moved
    // into the Lobby fixes this regardless of which one happens to render "on top".
    //
    // Handles both the normal case (ConnectScene is still a real, separate Scene) and the
    // post Host-Migration case (its content is wrapped in a "[ConnectScene]" GameObject
    // inside one merged scene — see the same pattern used for the Lobby-side wrapper above).
    private void DisableConnectSceneCamera()
    {
        GameObject cameraObject = FindConnectSceneRoot("Main Camera");
        if (cameraObject != null)
        {
            cameraObject.SetActive(false);
            Debug.Log("[ConnectToLobbyTransition] Disabled ConnectScene's own camera now that Lobby's camera is active.");
        }

        // DELIBERATELY NOT touching ConnectScene's leftover "Plane" floor here.
        //
        // REGRESSION CAUGHT LIVE: an earlier version of this method also disabled it,
        // on the reasoning that it is a stray test floor hovering 0.21m above the real
        // ground. That is true during GAMEPLAY, but at THIS point in the flow it is
        // still the floor the players are physically standing on — the Lobby has only
        // just loaded and players spawned at y=1 have already settled onto it. Pulling
        // it out from under them dropped every player through the world and they never
        // stopped falling (caught at y = -63,000 and counting). LobbyScene has its own
        // Plane at the identical position, so in principle it should have caught them,
        // but both are zero-thickness single-sided Unity Plane meshes: a capsule that
        // is resting exactly tangent to one, with its support removed in the same
        // frame, is a degenerate contact case and the controller simply does not
        // re-acquire the ground.
        //
        // The stray floor is now removed one step later instead — in
        // LobbyToGameplayTransition, once GameplayScene is loaded and the school's
        // actual slabs are there to stand on. See the comment there.
    }

    // Finds a root object inside ConnectScene by name, covering both the normal case
    // (ConnectScene is still a real, separate Scene) and the post Host-Migration case
    // (its content is merged into one scene and wrapped in a "[ConnectScene]"
    // GameObject — the same pattern used for the Lobby-side wrapper above).
    private GameObject FindConnectSceneRoot(string objectName)
    {
        var connectScene = SceneManager.GetSceneByName("ConnectScene");
        if (connectScene.IsValid() && connectScene.isLoaded)
        {
            foreach (var root in connectScene.GetRootGameObjects())
            {
                if (root.name == objectName) return root;
            }
        }

        var wrapper = GameObject.Find("[" + "ConnectScene" + "]");
        if (wrapper != null)
        {
            var found = wrapper.transform.Find(objectName);
            if (found != null) return found.gameObject;
        }

        return null;
    }
}
