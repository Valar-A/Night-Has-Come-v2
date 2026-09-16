using System.Collections;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

// Two small locators that answer one question each:
//   - "where does player #n stand at the discussion table?"  -> DiscussionRoom
//   - "where does player #n start in the school?"            -> SchoolSpawns
//
// Both are static and both resolve themselves lazily by looking the objects up by
// name, which matters more than it looks: this project's scenes do NOT reliably
// stay separate Unity Scenes at runtime. Fusion's NetworkSceneManagerDefault
// merges additively-loaded scene content into a "[SceneName]" wrapper GameObject
// inside the runner's own scene, so SceneManager.GetSceneByName(x).isLoaded is
// often false even when the content is right there — the codebase already works
// around this in four other places. Finding the blockout root by NAME works in
// both the separate-scene case and the merged-wrapper case, and after a Host
// Migration too, so that is what these do.

public static class WorldAnchorUtil
{
    // Puts a pose on the ground instead of trusting a marker's own height.
    //
    // The school's Spawn_n markers sit at floor + 0.6m (they are little floating
    // spheres you are meant to SEE in the Scene view, not stand on), so teleporting
    // a player straight to a marker's transform drops them out of the air. Worse,
    // that 0.6m is baked into the generator and will change the day the marker
    // style changes, silently breaking every spawn.
    //
    // CRITICAL: this raycasts through runner.GetPhysicsScene(), NOT Physics.Raycast.
    // Fusion simulates in its OWN PhysicsScene. A default-scene raycast here would
    // report clean hits off geometry the character controller cannot actually stand
    // on, which is exactly the trap that hid the fall-through-the-lawn bug through
    // an entire debugging session: every static check passed while the game was
    // broken, because the checks and the player were looking at two different
    // physics scenes.
    // Reused buffer — this runs at most ten times per phase change, but allocating a
    // RaycastHit[] each call for no reason is still just litter.
    private static readonly RaycastHit[] _groundHits = new RaycastHit[16];

    public static Vector3 SnapToGround(NetworkRunner runner, Vector3 pose, float searchUp = 1.5f, float searchDown = 6f)
    {
        if (runner == null) return pose;

        PhysicsScene physics = runner.GetPhysicsScene();
        Vector3 origin = pose + Vector3.up * searchUp;
        float maxDistance = searchUp + searchDown;

        // CONFIRMED LIVE BUG this replaced: the first version used the single-hit
        // physics.Raycast(..., out hit, ...) and took whatever it returned. Measured
        // during a host-only run, with the player standing on school Spawn_1, that
        // call reported the ground at y = 2.05 instead of y = 0.05 — because the
        // nearest thing under the ray origin was the TOP OF THE PLAYER'S OWN 2m
        // capsule, not the floor.
        //
        // In that state every following Day would place a player 2m higher than the
        // last, stacking a little further into the air each round, and nothing would
        // log an error about it. A dedicated Player layer plus a layer mask would be
        // the tidy fix, but every collider in this project currently sits on Default
        // and changing that would also change what SimpleKCC collides with. So
        // instead: collect every hit and take the nearest one that is NOT part of a
        // NetworkObject. Level geometry is plain GameObjects; players (and anything
        // else spawned by Fusion) are not — which makes that a reliable test here
        // without touching layers at all.
        int count = physics.Raycast(origin, Vector3.down, _groundHits, maxDistance, ~0, QueryTriggerInteraction.Ignore);

        bool found = false;
        float nearest = float.MaxValue;
        Vector3 groundPoint = Vector3.zero;

        for (int i = 0; i < count; i++)
        {
            Collider hitCollider = _groundHits[i].collider;
            if (hitCollider == null) continue;
            if (hitCollider.GetComponentInParent<NetworkObject>() != null) continue; // a player, not the floor

            if (_groundHits[i].distance < nearest)
            {
                nearest = _groundHits[i].distance;
                groundPoint = _groundHits[i].point;
                found = true;
            }
        }

        if (found)
        {
            // 5cm of clearance so the capsule starts just above the surface rather
            // than exactly co-planar with it, which can make the first ground check
            // of the first tick go either way.
            return groundPoint + Vector3.up * 0.05f;
        }

        Debug.LogWarning($"[WorldAnchors] No ground found under {pose} in the runner's physics scene ({count} hit(s), all of them players or nothing at all) — using the marker height as-is. If players fall here, this is why.");
        return pose;
    }
}

// ---------------------------------------------------------------------------
// The round-table room players are moved into for Discussion + Voting.
// ---------------------------------------------------------------------------
public static class DiscussionRoom
{
    public const string SceneName = "DiscussionScene";
    private const string RootName = "DiscussionRoom_Blockout";

    // Where the room is parked in world space, well clear of the school.
    //
    // It has to be moved somewhere: the room is generated around the origin, and
    // the origin is INSIDE Blackwood (the building footprint is X 0..40, Z 0..30
    // and the lawn runs X -27..67, Z -20..50). Left at zero, the discussion room
    // and the school's ground floor would be occupying the same cubic metres.
    //
    // 300m straight up rather than sideways because it keeps the X/Z numbers
    // readable when debugging, and because the room is fully sealed — floor, walls
    // and ceiling — so there is nothing to fall off. Every peer applies this same
    // constant to its own copy of the scene, which is what makes the host's
    // computed seat positions land in the same place on every client.
    public static readonly Vector3 WorldOrigin = new Vector3(0f, 300f, 0f);

    private static Transform _root;
    private static Transform[] _standPoints;
    private static Transform[] _seatPoints;

    public static bool IsReady { get { return Resolve(); } }
    public static int StandPointCount { get { return Resolve() ? _standPoints.Length : 0; } }

    // Loads the scene (if it isn't already up), hands its contents to the runner's
    // physics scene, and parks it at WorldOrigin. Safe to call more than once.
    public static IEnumerator LoadAndPlace(NetworkRunner runner)
    {
        Forget();

        bool alreadyPresent = SceneManager.GetSceneByName(SceneName).isLoaded
                              || GameObject.Find("[" + SceneName + "]") != null
                              || GameObject.Find(RootName) != null;

        if (!alreadyPresent)
        {
            AsyncOperation load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
            if (load == null)
            {
                Debug.LogError($"[DiscussionRoom] LoadSceneAsync('{SceneName}') returned null — the scene is almost certainly missing from Build Settings. Discussion will have nowhere to put the players.");
                yield break;
            }
            yield return load;
        }

        // Same confirmed reason as GameplayScene (see LobbyToGameplayTransition's
        // long comment): a plain SceneManager.LoadSceneAsync leaves every collider
        // in Unity's DEFAULT physics scene, which SimpleKCC never queries. Without
        // this move, players teleported into this room would drop straight through
        // its floor — and, being 300m up, would keep falling a very long way.
        if (runner != null)
        {
            Scene simulationScene = runner.SimulationUnityScene;
            Scene loaded = SceneManager.GetSceneByName(SceneName);

            if (simulationScene.IsValid() && loaded.IsValid() && loaded.isLoaded && loaded != simulationScene)
            {
                GameObject[] roots = loaded.GetRootGameObjects();
                foreach (GameObject r in roots)
                {
                    SceneManager.MoveGameObjectToScene(r, simulationScene);
                }
                Debug.Log($"[DiscussionRoom] Moved {roots.Length} root object(s) into the runner's simulation scene '{simulationScene.name}'.");
            }
        }
        else
        {
            Debug.LogError("[DiscussionRoom] No NetworkRunner — the room's colliders will stay in the default physics scene and players will fall through its floor.");
        }

        GameObject root = GameObject.Find(RootName);
        if (root == null)
        {
            Debug.LogError($"[DiscussionRoom] '{SceneName}' loaded but '{RootName}' isn't in it. Re-run Tools/Blackwood/Create or Update Discussion Scene.");
            yield break;
        }

        // .position, not .localPosition — when Fusion merges the scene the root
        // ends up parented under a "[DiscussionScene]" wrapper, and only the world
        // position is meaningful in both layouts.
        root.transform.position = WorldOrigin;

        // The colliders just moved 300m. Physics keeps a cached copy of every
        // collider transform and only refreshes it at its own sync points, so
        // without this a query made before the next physics step would still see
        // the room down at the origin.
        Physics.SyncTransforms();

        if (!Resolve())
        {
            Debug.LogError("[DiscussionRoom] Room is loaded but its StandPoints/SeatPoints groups are missing.");
            yield break;
        }

        Debug.Log($"[DiscussionRoom] Ready at {WorldOrigin} with {_standPoints.Length} stand point(s) and {_seatPoints.Length} seat point(s).");
    }

    public static void Forget()
    {
        _root = null;
        _standPoints = null;
        _seatPoints = null;
    }

    // Where a STANDING player goes: behind their own chair, facing the table.
    //
    // Deliberately NOT the SeatPoint. Probing the built room showed a player
    // capsule (radius 0.5, matching SimpleKCC on the Survivalist prefab) placed on
    // a SeatPoint overlaps that chair's seat, legs and backrest on all ten seats —
    // so the very first physics tick would shove the player sideways off their own
    // chair. SeatPoints stay in the scene, correctly placed, for the day there is a
    // sit animation and the controller is switched off; until then this is the pose
    // that actually works.
    public static bool TryGetStandPose(int index, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!Resolve()) return false;
        if (index < 0 || index >= _standPoints.Length) return false;

        Transform t = _standPoints[index];
        if (t == null) return false;

        position = t.position;
        rotation = t.rotation;
        return true;
    }

    public static bool TryGetSeatPose(int index, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!Resolve()) return false;
        if (_seatPoints == null || index < 0 || index >= _seatPoints.Length) return false;

        Transform t = _seatPoints[index];
        if (t == null) return false;

        position = t.position;
        rotation = t.rotation;
        return true;
    }

    private static bool Resolve()
    {
        if (_root != null && _standPoints != null && _standPoints.Length > 0) return true;

        GameObject root = GameObject.Find(RootName);
        if (root == null) return false;

        Transform stands = root.transform.Find("StandPoints");
        Transform seats = root.transform.Find("SeatPoints");
        if (stands == null) return false;

        _root = root.transform;
        _standPoints = OrderedNumberedChildren(stands, "StandPoint_");
        _seatPoints = seats != null ? OrderedNumberedChildren(seats, "SeatPoint_") : new Transform[0];
        return _standPoints.Length > 0;
    }

    // Hierarchy order happens to match the numbering today, but relying on that
    // means one drag in the Hierarchy silently reshuffles who sits where. Sort by
    // the number in the name instead, so seat 3 is always the one labelled 3 —
    // which is what makes a placard line up with the P3 button in the voting UI.
    private static Transform[] OrderedNumberedChildren(Transform parent, string prefix)
    {
        var list = new System.Collections.Generic.List<Transform>();
        foreach (Transform child in parent)
        {
            if (child.name.StartsWith(prefix)) list.Add(child);
        }

        list.Sort((a, b) => ParseIndex(a.name, prefix).CompareTo(ParseIndex(b.name, prefix)));
        return list.ToArray();
    }

    private static int ParseIndex(string name, string prefix)
    {
        int parsed;
        return int.TryParse(name.Substring(prefix.Length), out parsed) ? parsed : int.MaxValue;
    }
}

// ---------------------------------------------------------------------------
// The school's own spawn markers, used when players return to the map for Day.
// ---------------------------------------------------------------------------
public static class SchoolSpawns
{
    private const string RootName = "BlackwoodSchool_Blockout";

    private static Transform[] _points;

    public static bool IsReady { get { return Resolve(); } }
    public static int Count { get { return Resolve() ? _points.Length : 0; } }

    public static void Forget()
    {
        _points = null;
    }

    // Returns the marker's X/Z with the height resolved against the actual floor —
    // see WorldAnchorUtil.SnapToGround for why the marker's own Y is not usable.
    public static bool TryGetSpawnPose(NetworkRunner runner, int index, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!Resolve()) return false;
        if (index < 0 || index >= _points.Length) return false;

        Transform t = _points[index];
        if (t == null) return false;

        position = WorldAnchorUtil.SnapToGround(runner, t.position);

        // The markers are plain spheres with no meaningful rotation of their own,
        // so face everyone the same way (down the corridor, +Z) rather than
        // inheriting an arbitrary identity rotation and calling it a decision.
        rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        return true;
    }

    private static bool Resolve()
    {
        if (_points != null && _points.Length > 0) return true;

        GameObject root = GameObject.Find(RootName);
        if (root == null) return false;

        Transform group = root.transform.Find("SpawnPoints");
        if (group == null) return false;

        var list = new System.Collections.Generic.List<Transform>();
        foreach (Transform child in group)
        {
            if (child.name.StartsWith("Spawn_")) list.Add(child);
        }

        list.Sort((a, b) =>
        {
            int ai, bi;
            if (!int.TryParse(a.name.Substring("Spawn_".Length), out ai)) ai = int.MaxValue;
            if (!int.TryParse(b.name.Substring("Spawn_".Length), out bi)) bi = int.MaxValue;
            return ai.CompareTo(bi);
        });

        _points = list.ToArray();
        return _points.Length > 0;
    }
}
