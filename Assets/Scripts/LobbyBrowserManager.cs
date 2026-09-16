using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

// Attach to a "LobbyBrowser" GameObject (a UI panel). Handles browsing publicly
// available game sessions and populating a scrollable list — Among Us style.
//
// SETUP:
//  - listContent: the Content object inside a ScrollRect (needs a Vertical Layout Group).
//  - rowPrefab: a prefab with a SessionListItem script + code text + player-count text + Join button.
//  - networkBootstrap: drag your NetworkManager object (with NetworkBootstrap) here.
public class LobbyBrowserManager : MonoBehaviour, INetworkRunnerCallbacks
{
    [SerializeField] private Transform listContent;
    [SerializeField] private SessionListItem rowPrefab;
    [SerializeField] private NetworkBootstrap networkBootstrap;

    private NetworkRunner _browseRunner;
    private readonly List<SessionListItem> _spawnedRows = new();

    // Call this when the player opens the "Browse Games" panel (e.g. a button's OnClick()).
    public async void StartBrowsing()
    {
        StopBrowsing(); // in case one was already running

        var browserHost = new GameObject("LobbyBrowseRunner");
        _browseRunner = browserHost.AddComponent<NetworkRunner>();
        _browseRunner.AddCallbacks(this);

        Debug.Log("[LobbyBrowserManager DEBUG] Calling JoinSessionLobby...");
        var result = await _browseRunner.JoinSessionLobby(SessionLobby.Custom, NetworkBootstrap.PublicLobbyName);

        if (!result.Ok)
        {
            Debug.LogWarning($"[LobbyBrowserManager] Failed to join session lobby: {result.ShutdownReason}");
            StopBrowsing();
        }
        else
        {
            Debug.Log("[LobbyBrowserManager DEBUG] JoinSessionLobby SUCCEEDED — waiting for OnSessionListUpdated...");
        }
    }

    // Call this when the player closes the "Browse Games" panel (Back/Close button, or
    // right before actually joining a chosen game — see OnJoinRowClicked).
    public void StopBrowsing()
    {
        ClearRows();
        if (_browseRunner != null)
        {
            // Same lesson as NetworkBootstrap: Destroy, don't Shutdown(), a runner that
            // may not have fully connected — avoids the same "stuck" bug class.
            Destroy(_browseRunner.gameObject);
            _browseRunner = null;
        }
    }

    // Fusion calls this automatically and repeatedly while JoinSessionLobby is active —
    // any time a session is created, a player count changes, or a session closes.
    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
    {
        Debug.Log($"[LobbyBrowserManager DEBUG] OnSessionListUpdated fired — {sessionList.Count} total session(s) received.");
        foreach (var s in sessionList)
        {
            Debug.Log($"[LobbyBrowserManager DEBUG]   Session '{s.Name}' — Visible={s.IsVisible}, Open={s.IsOpen}, Players={s.PlayerCount}/{s.MaxPlayers}");
        }

        ClearRows();

        foreach (var session in sessionList)
        {
            if (!session.IsVisible || !session.IsOpen) continue; // skip hidden/full/closed rooms

            SessionListItem row = Instantiate(rowPrefab, listContent);
            row.Setup(session.Name, session.PlayerCount, session.MaxPlayers, OnJoinRowClicked);
            _spawnedRows.Add(row);
        }
    }

    private void OnJoinRowClicked(string sessionCode)
    {
        StopBrowsing();
        networkBootstrap.JoinSpecificGame(sessionCode);
    }

    private void ClearRows()
    {
        foreach (var row in _spawnedRows)
        {
            if (row != null) Destroy(row.gameObject);
        }
        _spawnedRows.Clear();
    }

    // --- Required interface stubs (this runner is browse-only, doesn't need these) ---
    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) { }
    public void OnInput(NetworkRunner runner, NetworkInput input) { }
    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }
    public void OnConnectedToServer(NetworkRunner runner) { }
    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
    public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
    public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
    public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
    public void OnSceneLoadDone(NetworkRunner runner) { }
    public void OnSceneLoadStart(NetworkRunner runner) { }
    public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
}
