// Touched to force a domain reload across all Editor instances after a Fusion
// prefab-table rebuild, so every peer's NetworkProjectConfig.Global cache
// reflects the same table (a plain AssetDatabase.Refresh() on its own didn't
// reliably invalidate the already-loaded in-memory config on a running instance).
// Re-touched again after restructuring the "root" bone under a new, non-animated
// "ModelOffset" container in Survivalist (4).prefab (feet-clipping fix, take 2).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Fusion;
using Fusion.Photon.Realtime;
using Fusion.Sockets;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

// Attach this script to an empty GameObject in your test scene (e.g. "NetworkManager").
// It creates the NetworkRunner, starts a Host or Client session, and spawns the
// player prefab whenever someone joins.
//
// GAME CODE FLOW:
//   Host Game -> generates a random code, uses it as the session name, shows it
//                on screen so the host can share it.
//   Join Game -> reads whatever code the player typed into joinCodeInputField and
//                uses that as the session name to connect to.
public class NetworkBootstrap : MonoBehaviour, INetworkRunnerCallbacks
{
    [Tooltip("Drag your networked player prefab here (must have a NetworkObject component).")]
    [SerializeField] private NetworkPrefabRef _playerPrefab;

    // CONFIRMED LIVE BUG this was added to fix: GameManager used to be a Scene
    // NetworkObject placed directly in ConnectScene — see NetworkGameManager.cs's own
    // header comment for the full story of why that silently failed to spawn most of
    // the time. It's now a genuine prefab, spawned here by the Host the instant a new
    // session actually starts (see the `mode == GameMode.Host` block in StartGame()) —
    // a plain NetworkObject field (not NetworkPrefabRef) works fine here since
    // NetworkRunner.Spawn() has a direct NetworkObject-prefab overload, and a plain
    // object field is simpler to wire/verify than NetworkPrefabRef's own asset-guid
    // indirection.
    [Tooltip("Drag the GameManager prefab here (NetworkObject + NetworkGameManager + PlayerRoleManager + NightActionManager + VoteManager, all on the same GameObject). Spawned once by the Host at the start of every new session.")]
    [SerializeField] private NetworkObject _gameManagerPrefab;

    [Header("Game Code UI")]
    [Tooltip("Input field on the Join screen where the player types the code they were given.")]
    [SerializeField] private TMP_InputField joinCodeInputField;

    [Tooltip("The whole error POPUP panel (background + text + OK button). Leave empty to skip.")]
    [SerializeField] private GameObject errorPopupPanel;

    [Tooltip("Text inside the error popup showing what went wrong.")]
    [SerializeField] private TMP_Text joinErrorText;

    [Tooltip("How many characters the generated game code should have.")]
    [SerializeField] private int gameCodeLength = 5;

    // Ambiguous characters (0/O, 1/I) excluded so codes are easy to read and type out loud.
    private const string CodeCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    // Fusion creates a separate lobby PER GAME MODE by default — Host and a plain
    // JoinSessionLobby() call can end up in different buckets, making the session
    // invisible to the browser. Using the same explicit Custom lobby name on both
    // sides guarantees they're always looking at the same list.
    public const string PublicLobbyName = "TheNightHasCome_PublicLobby";

    // Session name — must match on Host and Client to connect to the same room.
    // Set automatically from the generated/entered game code, no need to assign in Inspector.
    private string _sessionName;

    // Lets other scripts (e.g. LobbyUIManager, once the player is in the Lobby scene)
    // read the current game code without needing a direct Inspector reference.
    public static NetworkBootstrap Instance { get; private set; }
    public string GameCode => _sessionName;

    // Host's chosen settings for this session — set by HostGame(), read later by
    // PlayerRoleManager (mafia count) and shown in the Lobby (player count / visibility).
    public int SelectedPlayerCount { get; private set; } = 10;
    public int SelectedMafiaCount { get; private set; } = 2;
    public bool IsPublicLobby { get; private set; } = true;

    private NetworkRunner _runner;
    private NetworkSceneManagerDefault _sceneManager;
    private readonly Dictionary<PlayerRef, NetworkObject> _spawnedPlayers = new();

    // CONFIRMED LIVE BUG this was added to fix: NetworkGameManager (a Scene
    // NetworkObject, not a spawned prefab) was unconditionally resetting
    // CurrentPhase to Lobby every time its Spawned() ran — including after a Host
    // Migration reloads GameplayScene on the new Host, which made a mid-match game
    // (Day/Night/Voting/whatever) jump straight back to the Lobby the instant the
    // old Host disconnected.
    //
    // Captured here, on EVERY peer, right before that peer's own runner shuts down
    // for migration (we don't yet know which peer hostMigrationToken.GameMode will
    // make the new Host, so every peer needs its own copy of the last-known phase
    // just in case it's the one). NetworkGameManager.Spawned() on whichever peer
    // ends up with state authority reads this to resume the match where it left
    // off instead of wiping it back to Lobby, then clears it.
    //
    // Cleared at the top of a normal StartGame() too (HostGame/JoinGame/
    // JoinSpecificGame) so a stale value left over from an earlier migration can
    // never leak into a genuinely fresh, unrelated session later.
    public static NetworkGameManager.GamePhase? PendingResumePhase;

    // CONFIRMED LIVE: making PlayerRoleManager.ServerRoles a proper [Networked]
    // NetworkDictionary (instead of the old plain, never-replicated Dictionary) was
    // NOT enough on its own to survive a Host Migration — a live test after that
    // change showed every player's role silently reverting to Civilian on the new
    // Host anyway. That disproves the assumption (also baked into the original
    // OnHostMigration comments) that Fusion automatically restores a Scene
    // NetworkObject's [Networked] state across a migration — it does not; the
    // scene just reloads fresh from Build Settings with every [Networked] property
    // back at its default. CurrentPhase above only survives because we explicitly
    // capture and restore it ourselves — ServerRoles needs the exact same explicit
    // treatment, captured on every peer (same reasoning as PendingResumePhase) right
    // before the old runner shuts down.
    public static Dictionary<PlayerRef, PlayerRole> PendingResumeRoles;

    // CONFIRMED LIVE BUG this was added to fix: fixing CurrentPhase to survive a
    // Host Migration (see PendingResumePhase above) uncovered a SECOND bug that only
    // shows up once the first one is fixed. LobbyToGameplayTransition lives on a
    // ConnectScene GameObject that is NOT DontDestroyOnLoad-protected (unlike this
    // NetworkBootstrap instance) — ConnectScene always reloads FRESH on every
    // migration (see OnHostMigration()'s sceneInfo, which always adds ConnectScene
    // with LoadSceneMode.Single), so that script's own "_triggered" one-shot guard
    // resets to false every single migration too. Its Update() only knows "start the
    // Lobby -> Gameplay transition" by watching for CurrentPhase leaving Lobby — which,
    // now that CurrentPhase correctly stays wherever the match actually was (Day,
    // Night, whatever) across a migration instead of resetting, looks EXACTLY like a
    // genuine "Start Game was just pressed" transition to that fresh script instance.
    // It would load a SECOND, brand new, un-migrated copy of GameplayScene right on
    // top of the one the migration's own sceneInfo already restored — with its own
    // duplicate NetworkGameManager fighting the correctly-migrated one for the
    // Instance singleton, and (since PendingResumePhase was already consumed by the
    // correct one) resetting CurrentPhase back to Lobby via that duplicate's own
    // fresh Spawned() — which is exactly why the Lobby-jump bug came back even after
    // the CurrentPhase fix above.
    //
    // CONFIRMED LIVE BUG, second iteration: this used to be a simple bool
    // (LastStartWasHostMigration) set true right before OnHostMigration()'s
    // StartGame() call and only ever reset to false at the top of a genuinely NEW
    // StartGame() (HostGame/JoinGame/JoinSpecificGame). That silently broke the
    // "host disconnects WHILE STILL IN THE LOBBY" case: migration correctly resumed
    // CurrentPhase as Lobby (not past it), so LobbyToGameplayTransition's Update()
    // correctly didn't fire yet right after the migration — but the bool stayed
    // stuck at true from then on, with nothing left to ever clear it (no new
    // StartGame() call happens just because the game continues in the SAME migrated
    // session). Later, when the new Host genuinely clicked "Start Game" for the
    // first time, CurrentPhase finally left Lobby for real — but
    // LobbyToGameplayTransition read that same STALE true value and wrongly treated
    // a completely genuine transition as "just a migration resume, nothing to do",
    // skipping the Lobby scene unload entirely. That's why the Start Game
    // button/code/player-count UI never went away even though the match had
    // genuinely started.
    //
    // Fixed by capturing, at the exact moment of THIS migration (alongside
    // PendingResumePhase, right before the old runner shuts down), whether the
    // match had ALREADY left Lobby before this specific migration happened — not
    // "was the most recent connection a migration" in general. If the match was
    // still in Lobby at migration time, this reads false and stays false (nothing
    // touches it again until the NEXT migration), so a genuine later "Start Game"
    // press is never mistaken for a migration resume — no matter how much later it
    // happens.
    public static bool MigrationResumedPastLobby;

    // Prevents a second Host/Join click from starting an overlapping attempt
    // while a previous one is still in flight — two concurrent StartGame() calls
    // would fight over the same _runner field and corrupt each other's state.
    private bool _isConnecting;

    // CONFIRMED LIVE BUG this was added to fix: _isConnecting above only guards
    // the brief async WINDOW while StartGame() is in flight — it gets reset to
    // false the instant that call finishes (success OR failure), so it does
    // NOTHING to stop HostGame()/JoinGame()/JoinSpecificGame() from being called
    // again later, once a session is already fully connected and running. If the
    // Host Setup / Join / Browse panel is ever still reachable while a lobby is
    // already live (e.g. the player clicks back to ConnectScene, or a stale
    // Button click lands after the panel should have hidden) and the player
    // presses Confirm/Join again, this called CreateRunner() a SECOND time —
    // silently orphaning the live _runner (never shut down) and overwriting the
    // field with a brand new one — then called _runner.StartGame() again with
    // Scene = ConnectScene in LoadSceneMode.Single, which force-unloads/reloads
    // every currently loaded scene including GameplayScene/LobbyScene. That is
    // exactly what created a SECOND duplicate lobby out from under the first,
    // live one, and is the most likely explanation for the live-observed
    // "Host Setup panel came back with IsReady=False and a pile of console
    // errors" symptom too — the first session's GameManager Scene NetworkObject
    // gets yanked out from under it mid-flight by the second StartGame() call.
    //
    // Fix: an explicit, steady-state check — "is there already a runner that is
    // actually connected/running right now" — checked BEFORE _isConnecting at
    // the top of every entry point that can start a session.
    private bool IsInActiveSession()
    {
        return _runner != null && _runner.IsRunning;
    }

    private void Awake()
    {
        // CRITICAL FIX: this object was getting destroyed during Host Migration's scene
        // reload (it lives in ConnectScene, which was never explicitly protected), which
        // silently killed input polling entirely — OnInput() only fires on a NetworkBootstrap
        // instance that Fusion still has registered via AddCallbacks(), so once this object
        // was gone, GetInput() in PlayerMovement.FixedUpdateNetwork() always returned false
        // and NOBODY could move after a migration. DontDestroyOnLoad keeps this one instance
        // alive across every scene load/unload/reload for the rest of the whole session.
        //
        // Guard against ending up with two of these if a scene reload ever tries to spawn a
        // second copy anyway (e.g. ConnectScene reloading fresh while the original,
        // now-persistent instance is still alive) — keep the first, discard the duplicate.
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[NetworkBootstrap] Duplicate instance detected — destroying the new one, keeping the original.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Application.targetFrameRate defaults to -1 (uncapped) and vSyncCount to 0.
        // The Unity Editor's own overhead implicitly throttles Play Mode to a
        // reasonable, fairly steady frame rate, which is why movement looked smooth
        // there — but a compiled Standalone Build has none of that overhead and can
        // run at hundreds/thousands of uncapped FPS with wildly inconsistent frame
        // timing. Fusion's client-side prediction/interpolation assumes reasonably
        // steady frame pacing, so an uncapped Build is exactly the situation that
        // produces the "smooth in Editor, teleports/jitters in the Build" symptom.
        // Capping to a fixed, consistent rate makes both behave the same way.
        Application.targetFrameRate = 60;
    }

    // Call this from the Host Setup panel's "Confirm & Host" button OnClick(),
    // after the player has picked player count, mafia count, and public/private.
    public void HostGame(int playerCount, int mafiaCount, bool isPublic)
    {
        if (IsInActiveSession())
        {
            Debug.LogWarning("[NetworkBootstrap] Already in an active session — ignoring Host click (would have created a duplicate lobby).");
            ShowErrorPopup("You're already in a game — leave it first before hosting a new one.");
            return;
        }

        if (_isConnecting)
        {
            Debug.LogWarning("[NetworkBootstrap] Already connecting, ignoring extra click.");
            return;
        }

        SelectedPlayerCount = playerCount;
        SelectedMafiaCount = mafiaCount;
        IsPublicLobby = isPublic;

        _sessionName = GenerateGameCode();
        Debug.Log($"[NetworkBootstrap] Generated game code: {_sessionName} " +
                  $"(Players: {playerCount}, Mafia: {mafiaCount}, Public: {isPublic})");
        StartGame(GameMode.Host);
    }

    // Call this from the Join popup's "Join" button OnClick().
    public void JoinGame()
    {
        Debug.Log("[NetworkBootstrap] JoinGame() clicked."); // TEMP DEBUG: confirms the click is actually registering.

        if (IsInActiveSession())
        {
            Debug.LogWarning("[NetworkBootstrap] Already in an active session — ignoring Join click (would have created a duplicate session).");
            ShowErrorPopup("You're already in a game — leave it first before joining another one.");
            return;
        }

        if (_isConnecting)
        {
            Debug.LogWarning("[NetworkBootstrap] Already connecting, ignoring extra click.");
            return;
        }

        HideErrorPopup();

        if (joinCodeInputField == null || string.IsNullOrWhiteSpace(joinCodeInputField.text))
        {
            ShowErrorPopup("Enter a code first.");
            Debug.LogWarning("[NetworkBootstrap] Enter a game code before joining.");
            return;
        }

        // Normalize so casing/spacing typed by the player doesn't matter.
        _sessionName = joinCodeInputField.text.Trim().ToUpperInvariant();
        StartGame(GameMode.Client);
    }

    // Called by LobbyBrowserManager when the player picks a session from the browse
    // list — skips manual code typing and joins that exact session directly.
    public void JoinSpecificGame(string code)
    {
        if (IsInActiveSession())
        {
            Debug.LogWarning("[NetworkBootstrap] Already in an active session — ignoring Browse-list join click (would have created a duplicate session).");
            ShowErrorPopup("You're already in a game — leave it first before joining another one.");
            return;
        }

        if (_isConnecting)
        {
            Debug.LogWarning("[NetworkBootstrap] Already connecting, ignoring extra click.");
            return;
        }

        HideErrorPopup();
        _sessionName = code;
        StartGame(GameMode.Client);
    }

    private string GenerateGameCode()
    {
        var sb = new StringBuilder(gameCodeLength);
        for (int i = 0; i < gameCodeLength; i++)
        {
            sb.Append(CodeCharacters[UnityEngine.Random.Range(0, CodeCharacters.Length)]);
        }
        return sb.ToString();
    }

    // Give the runner (and its scene manager) their OWN throwaway GameObject each
    // attempt — used for both a fresh StartGame() and a Host Migration restart.
    // NetworkRunner.Shutdown() destroys the GameObject it's on — we don't want that
    // to be the same object NetworkBootstrap itself lives on, or a failed/migrated
    // attempt would wipe out this whole script.
    private void CreateRunner(string nameSuffix)
    {
        var runnerHost = new GameObject($"NetworkRunner_{nameSuffix}");
        _runner = runnerHost.AddComponent<NetworkRunner>();
        _runner.ProvideInput = true;
        _runner.AddCallbacks(this); // required since the runner isn't on our own GameObject anymore

        _sceneManager = runnerHost.AddComponent<NetworkSceneManagerDefault>();
    }

    private async void StartGame(GameMode mode)
    {
        _isConnecting = true;

        // Guard against a stale PendingResumePhase/PendingResumeRoles/
        // MigrationResumedPastLobby leaking in from an earlier Host Migration into a
        // brand new, unrelated session (see each field's own comment).
        PendingResumePhase = null;
        PendingResumeRoles = null;
        MigrationResumedPastLobby = false;

        try
        {
            CreateRunner($"{mode}_{_sessionName}");

            var sceneRef = SceneRef.FromIndex(SceneManager.GetActiveScene().buildIndex);
            var sceneInfo = new NetworkSceneInfo();
            if (sceneRef.IsValid)
                sceneInfo.AddSceneRef(sceneRef, LoadSceneMode.Single);

            var result = await _runner.StartGame(new StartGameArgs()
            {
                GameMode = mode,
                SessionName = _sessionName,
                CustomLobbyName = PublicLobbyName,
                PlayerCount = mode == GameMode.Host ? SelectedPlayerCount : (int?)null,
                IsVisible = mode == GameMode.Host ? IsPublicLobby : true,
                Scene = sceneInfo,
                SceneManager = _sceneManager
            });

            if (!result.Ok)
            {
                // LogWarning, not LogError — a wrong code / room-not-found is an expected,
                // handled outcome, not a real bug. Using LogError here would trip Unity's
                // "Error Pause" (if enabled in the Console), auto-pausing Play mode.
                Debug.LogWarning($"[NetworkBootstrap] Failed to start: {result.ShutdownReason}");

                if (mode == GameMode.Client)
                {
                    ShowErrorPopup("Couldn't join — check the code and try again.");
                }

                CleanUpFailedRunner();
                return;
            }

            Debug.Log($"[NetworkBootstrap] Started as {mode}. Session: {_sessionName}");

            // Only the Host spawns GameManager — Clients get it automatically via
            // normal Fusion replication the instant the Host's Spawn() call
            // reaches them, exactly like every other dynamically-spawned
            // NetworkObject (see the Player prefab, which already worked
            // reliably this whole time). Skipped entirely on OnHostMigration's
            // own StartGame() call further down — that path is a resume, not a
            // fresh session, and restores the existing GameManager instance via
            // OnHostMigrationResume()'s generic snapshot-restore loop instead.
            if (mode == GameMode.Host)
            {
                if (_gameManagerPrefab != null)
                {
                    // CONFIRMED LIVE BUG this replaced: the plain synchronous
                    // _runner.Spawn(_gameManagerPrefab, ...) call THREW right here —
                    // "Failed to load prefab synchronously. Use async spawn instead
                    // or enable EnqueueIncompleteSynchronousSpawns" — which got
                    // caught by the catch block below and tore the whole
                    // just-started session back down (CleanUpFailedRunner), even
                    // though the Host had already connected fine. Fusion's own
                    // prefab table resolves prefabs lazily and, called this early
                    // (the instant StartGame()'s own await returns, before the
                    // runner has ticked even once), isn't guaranteed to have the
                    // asset loaded yet — unlike the Player prefab's Spawn() call,
                    // which only ever happens later, from inside OnPlayerJoined(), a
                    // callback that fires once the runner is already actively
                    // simulating. SpawnAsync — awaited, same as StartGame() itself —
                    // is Fusion's own documented fix for exactly this case: it waits
                    // for the prefab to actually finish loading instead of demanding
                    // it be ready synchronously.
                    // NOTE: awaiting a NetworkSpawnOp resolves to the spawned
                    // NetworkObject itself (like awaiting a Task<T>), not the
                    // NetworkSpawnOp struct — confirmed via a compile error when
                    // this was first written as `NetworkSpawnOp spawnOp = await ...`.
                    NetworkObject spawnedGameManager = await _runner.SpawnAsync(_gameManagerPrefab, Vector3.zero, Quaternion.identity);
                    if (spawnedGameManager != null)
                    {
                        Debug.Log("[NetworkBootstrap] Spawned GameManager for this new Host session.");
                    }
                    else
                    {
                        Debug.LogError("[NetworkBootstrap] Failed to spawn GameManager — spawnedGameManager came back null.");
                    }
                }
                else
                {
                    Debug.LogError("[NetworkBootstrap] _gameManagerPrefab is not assigned in the Inspector! NetworkGameManager will never exist for this session — nothing will ever become IsReady.");
                }
            }
        }
        catch (Exception ex)
        {
            // Catch-all safety net: whatever went wrong (network layer exception, timeout,
            // anything), never leave the player stuck with a dead Join button.
            Debug.LogWarning($"[NetworkBootstrap] StartGame threw an exception: {ex.Message}");

            if (mode == GameMode.Client)
            {
                ShowErrorPopup("Couldn't join — check the code and try again.");
            }

            CleanUpFailedRunner();
        }
        finally
        {
            // Runs on EVERY exit path (success, handled failure, or exception) —
            // guarantees the Join button can never get permanently stuck.
            _isConnecting = false;
        }
    }

    private void CleanUpFailedRunner()
    {
        // IMPORTANT: we deliberately do NOT call runner.Shutdown() here. Photon's own
        // forums confirm Shutdown() can itself throw/hang when called on a runner that
        // never fully connected (e.g. GameNotFound) — which was the actual root cause
        // of the "Join button stuck after one wrong code" bug. Official Fusion guidance
        // is explicit: once a runner fails to connect, it should just be DESTROYED, not
        // gracefully shut down — a fresh runner is created for every attempt anyway.
        if (_runner != null)
        {
            Destroy(_runner.gameObject);
        }
        _runner = null;
        _sceneManager = null;

        // NOTE: we do NOT reset the input field here anymore — that now happens when
        // the player dismisses the error popup (see OnErrorPopupOkClicked()), matching
        // the requested UX: popup stays up with the wrong code still visible until OK.
    }

    // Call this from the Game Over panel's "Back to Main Menu" button OnClick().
    //
    // Only ends THIS peer's own participation in the match — cleanly shuts down the
    // local NetworkRunner, resets the same static migration-state fields StartGame()
    // itself guards against leaking (see PendingResumePhase/PendingResumeRoles/
    // MigrationResumedPastLobby comments above), and reloads ConnectScene as
    // LoadSceneMode.Single. Single unloads whatever else is currently loaded
    // (LobbyScene/GameplayScene and everything spawned in them) — NetworkBootstrap
    // itself survives that unload because Awake() already marked its own GameObject
    // DontDestroyOnLoad, exactly like it survives a Host Migration's own scene reload.
    //
    // NOTE on the Host clicking this: if this peer happens to be the Host, the other
    // peers see this exactly like any other Host disconnect and go through the
    // existing OnHostMigration() flow above — completely harmless here specifically
    // because this button is only ever reachable once CurrentPhase is already
    // GameOver (see GameOverUIManager), i.e. the match itself has nothing left to
    // resume. Whichever remaining peer becomes the new Host just keeps sitting on
    // the same Game Over screen until they, too, click this same button.
    public async void LeaveToMainMenu()
    {
        if (_isConnecting)
        {
            Debug.LogWarning("[NetworkBootstrap] Still connecting/shutting down — ignoring extra Leave click.");
            return;
        }

        Debug.Log("[NetworkBootstrap] LeaveToMainMenu() clicked — shutting down and returning to ConnectScene.");
        _isConnecting = true;

        try
        {
            if (_runner != null)
            {
                // Same reasoning as OnHostMigration's own `await runner.Shutdown(...)` —
                // let Fusion actually tear the session down cleanly before anything else
                // touches _runner/_sceneManager.
                await _runner.Shutdown(shutdownReason: ShutdownReason.Ok);
            }
        }
        catch (Exception ex)
        {
            // Same catch-all safety net as StartGame() — a player leaving the match
            // should never get stuck on this screen no matter what Shutdown() does.
            Debug.LogWarning($"[NetworkBootstrap] Shutdown during LeaveToMainMenu threw: {ex.Message}");
        }
        finally
        {
            _runner = null;
            _sceneManager = null;
            _spawnedPlayers.Clear();

            // Same guard as the top of StartGame() — never let a stale value from
            // this finished match leak into whatever the player does next.
            PendingResumePhase = null;
            PendingResumeRoles = null;
            MigrationResumedPastLobby = false;

            _isConnecting = false;

            // LoadSceneMode.Single unloads every other currently-loaded scene —
            // NetworkBootstrap's own GameObject survives via DontDestroyOnLoad.
            SceneManager.LoadScene("ConnectScene", LoadSceneMode.Single);
        }
    }

    // Call this from the error popup's "OK" button OnClick().
    public void OnErrorPopupOkClicked()
    {
        HideErrorPopup();
        ReactivateInputField();
    }

    private void ShowErrorPopup(string message)
    {
        if (joinErrorText != null) joinErrorText.text = message;
        if (errorPopupPanel != null) errorPopupPanel.SetActive(true);
    }

    private void HideErrorPopup()
    {
        if (errorPopupPanel != null) errorPopupPanel.SetActive(false);
    }

    // Forces the code input field back into an editable, focused state — plain
    // ActivateInputField() alone can silently fail to grab keyboard focus if the
    // EventSystem still thinks something else (like the Join button) is selected.
    private void ReactivateInputField()
    {
        joinCodeInputField.text = string.Empty;
        joinCodeInputField.interactable = true;

        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }

        joinCodeInputField.Select();
        joinCodeInputField.ActivateInputField();

        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(joinCodeInputField.gameObject);
        }
    }

    // Called on the server/host only when a player (including the host itself) joins.
    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        if (!runner.IsServer) return;

        // Defensive guard: a Host Migration resume reconstructs already-connected
        // players' objects itself (see OnHostMigrationResume) instead of relying on
        // this callback firing again for them. This just makes sure we never end up
        // with two player objects for the same PlayerRef if that ever overlaps.
        if (_spawnedPlayers.ContainsKey(player))
        {
            Debug.LogWarning($"[NetworkBootstrap] Player {player} already has a spawned object — skipping duplicate spawn.");
            return;
        }

        Vector3 spawnPos = new Vector3(UnityEngine.Random.Range(-3f, 3f), 1f, UnityEngine.Random.Range(-3f, 3f));
        NetworkObject playerObj = runner.Spawn(_playerPrefab, spawnPos, Quaternion.identity, player);
        _spawnedPlayers.Add(player, playerObj);

        // CRITICAL: runner.Spawn(..., player) only sets InputAuthority on the object —
        // it does NOT register it in Fusion's own player-object lookup table. Anything
        // that calls runner.GetPlayerObject(player) (CameraFollow, NightActionManager's
        // victim lookup, SpectatorController's alive-target list) was silently getting
        // NULL back for every player, always, even though the objects clearly existed
        // and were rendering — this one call is what was actually missing.
        runner.SetPlayerObject(player, playerObj);

        Debug.Log($"[NetworkBootstrap] Player {player} joined and spawned.");
    }

    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        if (_spawnedPlayers.TryGetValue(player, out NetworkObject obj))
        {
            runner.Despawn(obj);
            _spawnedPlayers.Remove(player);
        }
    }

    // Polls local input every network tick and packs it for the player's NetworkBehaviour.
    public void OnInput(NetworkRunner runner, NetworkInput input)
    {
        // cameraYaw is captured HERE, on the actual player's own machine, and sent
        // as part of the networked input — NOT read via Camera.main inside
        // PlayerMovement.FixedUpdateNetwork(), which also runs on the host as the
        // authoritative sim for every player and would otherwise use the HOST's own
        // camera instead of this player's camera (see PlayerMovement.cs comment).
        //
        // IMPORTANT: reading CameraFollow.Instance.transform here instead of
        // Camera.main — this project loads ConnectScene, LobbyScene and GameplayScene
        // all ADDITIVELY and never unloads ConnectScene (see ConnectToLobbyTransition's
        // own comment), so ConnectScene's own Main Camera is still sitting there,
        // active, the whole time. With two GameObjects tagged MainCamera loaded at
        // once, Camera.main's result is effectively undefined — it could resolve to
        // either one — which is exactly why mouse-driven movement direction was
        // unreliable ("kabhi kaam karta kabhi nahi") during normal play: it depended
        // on whichever camera Camera.main happened to pick that moment, not
        // necessarily the actual mouse-orbiting gameplay camera. CameraFollow.Instance
        // is the same singleton PlayerMovement already trusts for exactly this reason
        // (see its own "reliably across additive scene loads" comment) — using it here
        // too removes the ambiguity instead of relying on a lucky Camera.main pick.
        float cameraYaw = 0f;
        if (CameraFollow.Instance != null)
        {
            cameraYaw = CameraFollow.Instance.MovementYaw;
        }

        var data = new NetworkInputData
        {
            horizontal = Input.GetAxisRaw("Horizontal"),
            vertical = Input.GetAxisRaw("Vertical"),
            running = Input.GetKey(KeyCode.LeftShift),
            jumpPressed = Input.GetKeyDown(KeyCode.Space),
            discussionPressed = Input.GetKeyDown(KeyCode.E),
            cameraYaw = cameraYaw
        };
        input.Set(data);
    }

    // --- Required interface stubs (safe to leave empty for this test setup) ---
    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }
    public void OnConnectedToServer(NetworkRunner runner) { }
    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
    public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
    public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
    public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
    // Fires on EVERY peer (the client being promoted to Host AND every other client
    // staying a Client) the moment Fusion/Photon Cloud detects the old Host dropped.
    // hostMigrationToken.GameMode tells THIS peer what role it will have in the new
    // session — most peers get GameMode.Client again, and exactly one gets Host.
    //
    // Without this, losing the Host disconnects the whole session for everyone (the
    // "sari clients offline hojati hain" problem) — this restarts each peer's local
    // NetworkRunner against the SAME session using the migration token instead of
    // creating a brand new one, so the game keeps running instead of dying.
    public async void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken)
    {
        Debug.Log($"[NetworkBootstrap] Host Migration triggered — this peer becomes {hostMigrationToken.GameMode}.");

        // Snapshot the match's current phase BEFORE the old runner (and the
        // GameplayScene NetworkGameManager living on it) goes away — see
        // PendingResumePhase's own comment for why this needs to happen on every
        // peer, not just whoever ends up the new Host. Any peer that was still in
        // GameplayScene with a ready NetworkGameManager can read CurrentPhase (it's
        // a normal replicated [Networked] property — readable by clients too, only
        // writable by the Host), so this doesn't depend on this particular peer
        // having state authority.
        if (NetworkGameManager.Instance != null && NetworkGameManager.Instance.IsReady)
        {
            PendingResumePhase = NetworkGameManager.Instance.CurrentPhase;
            Debug.Log($"[NetworkBootstrap] Captured CurrentPhase={PendingResumePhase} before migration, to resume after.");
        }

        // See MigrationResumedPastLobby's own comment — captured from the SAME
        // snapshot as PendingResumePhase above (not "was the most recent connection
        // a migration" in general), so it stays correct no matter how much later the
        // match actually leaves Lobby for real.
        MigrationResumedPastLobby = PendingResumePhase.HasValue && PendingResumePhase.Value != NetworkGameManager.GamePhase.Lobby;

        // Same idea, for role assignments — see PendingResumeRoles' own comment for
        // why the NetworkDictionary change on ServerRoles alone wasn't sufficient.
        if (PlayerRoleManager.Instance != null)
        {
            var roles = new Dictionary<PlayerRef, PlayerRole>();
            foreach (var p in runner.ActivePlayers)
            {
                roles[p] = PlayerRoleManager.Instance.GetServerRole(p);
            }
            PendingResumeRoles = roles;
            Debug.Log($"[NetworkBootstrap] Captured {roles.Count} role(s) before migration, to resume after.");
        }

        // The old runner is on its way out regardless of outcome — let it clean up
        // before we spin up its replacement on a fresh GameObject.
        await runner.Shutdown(shutdownReason: ShutdownReason.HostMigration);

        CreateRunner($"Migrated_{hostMigrationToken.GameMode}_{_sessionName}");

        // IMPORTANT: migration can happen at ANY point — ConnectScene + LobbyScene both
        // additively loaded (still in the Lobby), or ConnectScene + GameplayScene (match
        // in progress). An earlier version of this walked SceneManager's CURRENTLY LOADED
        // scenes (SceneManager.GetSceneAt(i)) to rebuild the list — that looked right, but
        // broke in TWO ways once actually tested:
        //   1) After a migration, Fusion can leave its own auto-generated per-runner scene
        //      as the only thing "loaded" (its buildIndex is -1 — it's not a real Build
        //      Settings scene). Feeding -1 into SceneRef.FromIndex() is exactly what threw
        //      "ArgumentOutOfRangeException: index" the moment a SECOND migration tried to
        //      walk the (by then already-garbage) loaded-scene list.
        //   2) It made scene preservation depend on whatever happened to still be loaded at
        //      that exact moment, instead of on what SHOULD be loaded for the game state.
        // Fix: never introspect the runtime scene list here. Resolve ConnectScene/
        // LobbyScene/GameplayScene's Build Settings indices directly BY PATH (works
        // regardless of what's currently loaded or already collapsed), and decide whether
        // Lobby/Gameplay belong in the reconstructed session by checking THOSE SPECIFIC
        // scenes by name — never a blind index-based walk.
        var sceneInfo = new NetworkSceneInfo();

        int connectBuildIndex = SceneUtility.GetBuildIndexByScenePath("Assets/Scenes/ConnectScene.unity");
        if (connectBuildIndex >= 0)
        {
            sceneInfo.AddSceneRef(SceneRef.FromIndex(connectBuildIndex), LoadSceneMode.Single);
        }
        else
        {
            Debug.LogWarning("[NetworkBootstrap] ConnectScene not found in Build Settings — migration scene setup will be incomplete.");
        }

        // CONFIRMED LIVE (bug found on the SECOND Host Migration specifically): checking
        // ONLY `SceneManager.GetSceneByName(name).isLoaded` here misses the case where
        // that scene's content survived the FIRST migration merged into a "[SceneName]"
        // wrapper GameObject instead of as a separately-named real Scene (this is the
        // exact same merged-scene shape CameraFollow.BelongsToAnyLoadedInstance and
        // ConnectToLobbyTransition already know to check for — see their comments). After
        // migration #1, GameplayScene's content lives ONLY as a "[GameplayScene]" wrapper
        // inside the runner's own merged scene — there is no separately-named "GameplayScene"
        // Scene left to find. So on migration #2, this check silently read false, GameplayScene's
        // sceneRef never made it into sceneInfo, and the whole scene — camera included —
        // simply vanished from the reconstructed session instead of carrying over. Checking
        // for the wrapper too (same dual check used everywhere else in this codebase) fixes
        // it for every migration after the first, not just the second.
        // CONFIRMED LIVE BUG this replaced: checking "is LobbyScene (real or wrapper)
        // currently loaded" to decide whether to carry it into THIS migration was
        // wrong in a way that only shows up once a match is well underway. A single
        // pre-existing race — disconnecting the Host in the narrow window while
        // LobbyToGameplayTransition's own unload coroutine hadn't finished yet — can
        // leave a genuinely separate "LobbyScene" Scene (or, after that first
        // migration, a merged "[LobbyScene]" wrapper) loaded alongside GameplayScene.
        // The old check above would then see it "still loaded" and re-include it in
        // sceneInfo — and because that same leftover survives INTO the new migrated
        // session too, EVERY SUBSEQUENT migration re-included it again, forever. Its
        // own Lobby Canvas (Start Game button and all) never got hidden by anything —
        // CameraFollow.DeactivateIfStale() only ever hid its CAMERA, nothing hid its
        // UI — so the player visibly saw the Lobby screen render on top of/instead of
        // Gameplay after every migration from then on. That's what "host disconnect
        // during gameplay -> goes back to Lobby scene" actually was, separate from
        // (and surviving) both the CurrentPhase-reset fix and the duplicate-
        // GameplayScene-load fix above.
        //
        // MigrationResumedPastLobby (captured earlier in this same method, from the
        // same snapshot as PendingResumePhase, before Shutdown) is the authoritative
        // signal instead: if the match had already left Lobby, Lobby content must
        // NEVER be carried into this or any future migration again — and any
        // leftover real Scene/wrapper found anyway is a bug to clean up, not content
        // to preserve.
        bool lobbyLoaded = false;
        if (!MigrationResumedPastLobby)
        {
            lobbyLoaded = SceneManager.GetSceneByName("LobbyScene").isLoaded || GameObject.Find("[LobbyScene]") != null;
        }
        else
        {
            var staleLobbyScene = SceneManager.GetSceneByName("LobbyScene");
            if (staleLobbyScene.IsValid() && staleLobbyScene.isLoaded)
            {
                SceneManager.UnloadSceneAsync(staleLobbyScene);
                Debug.LogWarning("[NetworkBootstrap] Match already past Lobby — unloading a stale leftover LobbyScene instead of carrying it into this migration.");
            }

            var staleLobbyWrapper = GameObject.Find("[LobbyScene]");
            if (staleLobbyWrapper != null)
            {
                Destroy(staleLobbyWrapper);
                Debug.LogWarning("[NetworkBootstrap] Match already past Lobby — destroying a stale leftover [LobbyScene] wrapper instead of carrying it into this migration.");
            }
        }

        if (lobbyLoaded)
        {
            int lobbyBuildIndex = SceneUtility.GetBuildIndexByScenePath("Assets/Scenes/LobbyScene.unity");
            if (lobbyBuildIndex >= 0)
            {
                sceneInfo.AddSceneRef(SceneRef.FromIndex(lobbyBuildIndex), LoadSceneMode.Additive);
            }
        }

        bool gameplayLoaded = SceneManager.GetSceneByName("GameplayScene").isLoaded || GameObject.Find("[GameplayScene]") != null;
        if (gameplayLoaded)
        {
            int gameplayBuildIndex = SceneUtility.GetBuildIndexByScenePath("Assets/Scenes/GameplayScene.unity");
            if (gameplayBuildIndex >= 0)
            {
                sceneInfo.AddSceneRef(SceneRef.FromIndex(gameplayBuildIndex), LoadSceneMode.Additive);
            }
        }

        var result = await _runner.StartGame(new StartGameArgs()
        {
            GameMode = hostMigrationToken.GameMode,
            HostMigrationToken = hostMigrationToken,
            HostMigrationResume = OnHostMigrationResume,
            SessionName = _sessionName,
            CustomLobbyName = PublicLobbyName,
            PlayerCount = hostMigrationToken.GameMode == GameMode.Host ? SelectedPlayerCount : (int?)null,
            IsVisible = false, // match is already in progress — never re-list it in Browse Lobbies
            Scene = sceneInfo,
            SceneManager = _sceneManager
        });

        if (!result.Ok)
        {
            Debug.LogWarning($"[NetworkBootstrap] Host Migration restart failed: {result.ShutdownReason}");
            return;
        }

        // Same reasoning as ConnectToLobbyTransition/LobbyToGameplayTransition's own
        // SetActiveScene calls (see their comments) — a scene only being loaded
        // ADDITIVELY doesn't make it "active", and RenderSettings/new-object
        // placement follow whichever scene IS active. Reported live: after a Host
        // Migration, the active scene can end up being wrong instead of GameplayScene.
        // Runs on EVERY peer (Host and Client alike) — unlike the ghost-cleanup/
        // re-registration block below, this is purely a local rendering/scene
        // concern, not something requiring state authority.
        //
        // TWO EARLIER ATTEMPTS AT THIS FIX WERE BOTH WRONG, BOTH CONFIRMED LIVE:
        //  1) Falling back to LobbyScene whenever "GameplayScene" isn't a separately-
        //     named loaded Scene made things WORSE — active scene ended up as the
        //     STALE, leftover LobbyScene (see CameraFollow.DeactivateIfStale for how
        //     that leftover scene ends up loaded) instead of the runner's own scene,
        //     which is where GameplayScene's real content actually lives once merged
        //     into it (no separately-named "GameplayScene" Scene survives that merge).
        //  2) Gating "prefer the runner's own scene" on
        //     `NetworkGameManager.Instance.CurrentPhase != Lobby`, checked immediately
        //     right here, ALSO still picked LobbyScene — even switching to checking for
        //     GameplayScene's PRESENCE directly (real scene OR merged "[GameplayScene]"
        //     wrapper) instead of gm.CurrentPhase didn't help, because checking for
        //     EITHER signal immediately after `await _runner.StartGame(...)` is too
        //     early: exactly like `_spawnedPlayers` being empty at this same point
        //     (see ReRegisterPlayerObjectsDelayed below, discovered for the exact same
        //     underlying reason) — the migration's scene content, including the
        //     "[GameplayScene]" wrapper GameObject itself, is not necessarily fully
        //     instantiated yet the instant StartGame()'s Task resolves. A live test
        //     confirmed zero "re-set as the active scene" log lines ever fired.
        // Fix: apply the SAME delayed-retry pattern already proven for that other
        // timing race — check a few times over a couple of seconds instead of once,
        // so whichever attempt lands after the scene content actually exists is
        // enough. Runs for every peer, not just the Host, since this is unrelated to
        // player-object state authority.
        StartCoroutine(FixActiveSceneDelayed());

        // OnHostMigrationResume above restores EVERY player object from the old
        // Host's last snapshot — including whoever the old Host itself was, if that
        // peer isn't rejoining as a plain client. GetResumeSnapshotNetworkObjects()
        // has no idea who's actually still connected (it only fires the moment
        // StartGame() is still starting up, so runner.ActivePlayers isn't reliable
        // there yet) — that's why the departed Host's character stayed standing in
        // the scene as a "ghost" after migration instead of disappearing.
        // Now that StartGame() has fully finished, runner.ActivePlayers reflects
        // who's really still in the session — only the new Host does this cleanup,
        // since despawning requires state authority (a Client can't do it).
        if (hostMigrationToken.GameMode == GameMode.Host)
        {
            var stillConnected = new HashSet<PlayerRef>(_runner.ActivePlayers);
            var stalePlayers = new List<PlayerRef>();

            foreach (var kvp in _spawnedPlayers)
            {
                if (!stillConnected.Contains(kvp.Key))
                {
                    stalePlayers.Add(kvp.Key);
                }
            }

            foreach (var stalePlayer in stalePlayers)
            {
                Debug.Log($"[NetworkBootstrap] Despawning ghost object for {stalePlayer} — that player didn't come back after Host Migration.");
                _runner.Despawn(_spawnedPlayers[stalePlayer]);
                _spawnedPlayers.Remove(stalePlayer);
            }

            // CRITICAL: re-register every surviving player object in the new Host's own
            // player-object table — but NOT synchronously right here.
            //
            // CONFIRMED LIVE, in two stages:
            //  1) Calling runner.SetPlayerObject() from inside OnHostMigrationResume()
            //     does NOT reliably stick, even on the New Host's own runner, even though
            //     Object.HasStateAuthority reads true for that object by the time you
            //     check it afterward.
            //  2) An attempt to fix that by re-registering right HERE (immediately after
            //     `await _runner.StartGame(...)` returns) turned out to be racing the SAME
            //     problem from the other direction: live logging proved
            //     OnHostMigrationResume() — despite its own doc comment saying it runs
            //     "before [StartGame] fully resumes simulating" — actually fires AFTER
            //     control returns from this await, on a later frame. So code placed
            //     immediately after the await found `_spawnedPlayers` still EMPTY
            //     (`_spawnedPlayers.Clear()` had run, the restore loop hadn't yet) and had
            //     nothing to re-register at all.
            // Manually re-calling the exact same SetPlayerObject(playerRef, playerObj) a
            // few seconds later, once everything had settled, always made it stick
            // immediately — so the association itself is fine, only ever the TIMING of
            // when it's attempted is unsafe. A short delayed coroutine, run after both
            // Resume has populated _spawnedPlayers AND Fusion's internal state has had
            // time to settle, sidesteps both timing issues without depending on either
            // one's exact undocumented ordering.
            //
            // This table is what every other peer's association gets replicated FROM (see
            // PlayerMovement.Spawned()'s HasStateAuthority-guarded call, which has the
            // exact same early-timing problem for the same reason) — so redoing it here is
            // what actually makes GetPlayerObject() work reliably for everyone (including
            // each client's own local player) after every migration, not just for
            // whichever player happened to get lucky timing.
            StartCoroutine(ReRegisterPlayerObjectsDelayed());
        }

        Debug.Log($"[NetworkBootstrap] Host Migration complete — running as {hostMigrationToken.GameMode}.");
    }

    // Repeatedly re-registers every currently-known player object in the (new) Host's
    // own runner.SetPlayerObject() table, spaced a bit apart, instead of a single
    // one-shot attempt at one specific moment. See the long comment where this is
    // started (in OnHostMigration, right after the ghost-cleanup block) for the full
    // story of why a single well-placed call isn't safe here: neither immediately
    // after OnHostMigrationResume, nor immediately after StartGame() returns, reliably
    // lands — the exact frame Fusion's internal state finishes settling after a
    // migration isn't documented and, live-tested, wasn't consistent either. Retrying
    // a handful of times over a couple of seconds means this doesn't depend on getting
    // that exact timing right even once: whichever attempt happens to land after
    // things are ready is enough, and the ones before/after it are harmless no-ops
    // (SetPlayerObject with the same key/value is idempotent).
    private IEnumerator ReRegisterPlayerObjectsDelayed()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            yield return new WaitForSeconds(0.5f);

            if (_runner == null) yield break; // another migration/shutdown took over

            foreach (var kvp in _spawnedPlayers)
            {
                _runner.SetPlayerObject(kvp.Key, kvp.Value);
            }
        }

        Debug.Log($"[NetworkBootstrap] Finished delayed re-registration pass for {_spawnedPlayers.Count} player object(s).");
    }

    // Retries setting the correct active scene a few times over a couple of seconds,
    // for the same reason ReRegisterPlayerObjectsDelayed above does — see the long
    // comment where this is started (in OnHostMigration, right after `await
    // _runner.StartGame(...)`) for why a single immediate attempt doesn't work here.
    // Runs on EVERY peer (unlike ReRegisterPlayerObjectsDelayed, which is Host-only),
    // since which scene is "active" is a purely local rendering concern.
    private IEnumerator FixActiveSceneDelayed()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            yield return new WaitForSeconds(0.5f);

            if (_runner == null) yield break; // another migration/shutdown took over

            var gameplayScene = SceneManager.GetSceneByName("GameplayScene");
            var lobbyScene = SceneManager.GetSceneByName("LobbyScene");
            var gameplayWrapper = GameObject.Find("[GameplayScene]");

            Scene desiredActiveScene = default;
            if (gameplayScene.IsValid() && gameplayScene.isLoaded)
            {
                desiredActiveScene = gameplayScene;
            }
            else if (gameplayWrapper != null)
            {
                // GameplayScene's content has been merged into some real, named Scene
                // (no separately-named "GameplayScene" Scene survives that merge) —
                // take THAT scene from the wrapper GameObject itself, never LobbyScene
                // even if a stale copy of that happens to still be loaded.
                //
                // CONFIRMED LIVE this needed a second correction: using
                // `_runner.gameObject.scene` here throws
                // `ArgumentException: ... the internal DontDestroyOnLoad scene cannot
                // be set active` — Fusion's NetworkRunner calls DontDestroyOnLoad on
                // itself internally, so the runner's OWN GameObject lives in Unity's
                // special DontDestroyOnLoad scene, not the real named merged scene
                // that the migrated content (including this very wrapper) actually
                // lives in. Reading `.scene` off the wrapper instead sidesteps that
                // entirely — the wrapper is provably in the right scene since it IS
                // the GameplayScene content itself.
                desiredActiveScene = gameplayWrapper.scene;
            }
            else if (lobbyScene.IsValid() && lobbyScene.isLoaded)
            {
                desiredActiveScene = lobbyScene;
            }

            if (desiredActiveScene.IsValid() && desiredActiveScene.isLoaded && SceneManager.GetActiveScene() != desiredActiveScene)
            {
                SceneManager.SetActiveScene(desiredActiveScene);
                Debug.Log($"[NetworkBootstrap] '{desiredActiveScene.name}' re-set as the active scene after Host Migration (attempt {attempt + 1}).");
            }
        }
    }

    // Runs once, only on the new Host's runner, before it fully resumes simulating —
    // this is where the previous Host's last-known state (snapshotted periodically,
    // see NetworkProjectConfig > Host Migration) gets turned back into live
    // NetworkObjects. This generic loop restores EVERY dynamically-spawned prefab —
    // Player AND GameManager both go through here now (GameManager used to be a
    // Scene NetworkObject and relied on the scene reloading to come back after a
    // migration; it's a spawned prefab now too, see NetworkGameManager.cs's header
    // comment, so it's restored the exact same way as Player with no extra code
    // needed here).
    private void OnHostMigrationResume(NetworkRunner runner)
    {
        _spawnedPlayers.Clear();

        int restoredCount = 0;
        foreach (NetworkObject oldPlayerObject in runner.GetResumeSnapshotNetworkObjects())
        {
            NetworkObject newPlayerObject = runner.Spawn(
                oldPlayerObject,
                oldPlayerObject.transform.position,
                oldPlayerObject.transform.rotation,
                oldPlayerObject.InputAuthority,
                onBeforeSpawned: (r, newlySpawned) =>
                {
                    newlySpawned.CopyStateFrom(oldPlayerObject);

                    // See PlayerMovement.ResetTransientMovementState() — wipes the
                    // carried-over "mid-run" speed/direction snapshot so the character
                    // starts idle instead of looking frozen mid-animation.
                    if (newlySpawned.TryGetComponent(out PlayerMovement movement))
                    {
                        movement.ResetTransientMovementState();
                    }
                });

            if (oldPlayerObject.InputAuthority.IsRealPlayer)
            {
                _spawnedPlayers[oldPlayerObject.InputAuthority] = newPlayerObject;

                // Same registration this new runner needs too — GetResumeSnapshotNetworkObjects()
                // + Spawn() restores the object itself but not its entry in THIS (brand new)
                // runner's player-object table, so GetPlayerObject() would go right back to
                // returning null for everyone after every migration without this.
                runner.SetPlayerObject(oldPlayerObject.InputAuthority, newPlayerObject);
            }

            restoredCount++;
        }

        Debug.Log($"[NetworkBootstrap] Host Migration resume — restored {restoredCount} object(s) from the previous Host's last snapshot.");
    }
    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
    public void OnSceneLoadDone(NetworkRunner runner) { }
    public void OnSceneLoadStart(NetworkRunner runner) { }
    public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
}

// Shared input struct — sent from client to server every tick.
// Shared input struct — sent from client to server every tick.
public struct NetworkInputData : INetworkInput
{
    public float horizontal;
    public float vertical;
    public NetworkBool running;
    public NetworkBool jumpPressed;
    public NetworkBool discussionPressed;
    public float cameraYaw;
}
