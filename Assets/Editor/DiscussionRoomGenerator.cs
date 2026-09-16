using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Procedural blockout for the Discussion chamber: a round room with one round
// table and ten chairs around it, one per player at the game's maximum lobby
// size.
//
// Same philosophy as BlackwoodSchoolGenerator: everything is generated from
// numbers at the top of this file, the whole thing is re-runnable (it deletes
// and rebuilds its own root, it never merges into whatever is already there),
// and every collider is normalised on the way out so a hand-edit or a stale
// serialised shape can never survive a regenerate.
//
// That last point is not paranoia. The school blockout shipped with exactly one
// collider out of 336 flattened to zero thickness (the lawn), and the result was
// players walking out of the building and falling forever — while every static
// raycast check reported the ground as perfectly solid, because a ray hits a
// zero-thickness plate just fine and only a swept capsule tunnels through it.
// ValidateColliders() at the bottom exists so that class of bug gets caught here
// instead of in a live test.
//
//   Tools/Blackwood/Create or Update Discussion Scene   <- makes/refreshes the scene asset
//   Tools/Blackwood/Generate Discussion Room            <- rebuilds into the ACTIVE scene
//   Tools/Blackwood/Delete Discussion Room              <- removes it from the ACTIVE scene
public static class DiscussionRoomGenerator
{
    // =======================================================================
    // DIMENSIONS — change these, re-run the menu item, done.
    // =======================================================================
    private const string RootName  = "DiscussionRoom_Blockout";
    private const string ScenePath = "Assets/Scenes/DiscussionScene.unity";

    // Ten seats because ten is the game's maximum lobby size. Fewer players just
    // leaves empty chairs, which reads correctly at a table like this one.
    private const int SeatCount = 10;

    // Room shell. The floor's top surface is y = 0, so everything above it is a
    // plain positive height and nothing needs an offset baked into it.
    private const float RoomRadius     = 7.5f;
    private const float WallHeight     = 4.5f;
    private const float WallThickness  = 0.3f;
    private const int   WallSegments   = 28;   // straight boxes faking a circle
    private const float FloorThickness = 0.5f;
    private const float CeilThickness  = 0.3f;

    // Table. TableTopY is the walking-height surface players' items/placards sit
    // on, which is why the pedestal stops just under it rather than at it.
    private const float TableTopRadius    = 3.2f;
    private const float TableTopThickness = 0.16f;
    private const float TableTopY         = 0.78f;
    private const float PedestalRadius    = 0.85f;
    private const float TableBaseRadius   = 1.8f;
    private const float TableBaseHeight   = 0.12f;

    // Chairs. SeatRingRadius is measured to the chair's centre, so the front edge
    // of a chair sits at SeatRingRadius - ChairHalfDepth and the gap to the table
    // edge works out at about 0.35m — close enough to lean on, far enough to walk
    // behind.
    private const float SeatRingRadius = 3.85f;
    private const float ChairHalfWidth = 0.30f;
    private const float ChairHalfDepth = 0.30f;
    private const float SeatHeight     = 0.44f;
    private const float SeatThickness  = 0.08f;
    private const float BackRestTop    = 1.12f;
    private const float LegThickness   = 0.07f;

    // Where a STANDING player goes, measured from the room centre. Deliberately
    // outside the chair rather than on it.
    //
    // CONFIRMED by probing the built room: a player capsule (radius 0.5, the
    // SimpleKCC setting on the Survivalist prefab) placed on a SeatPoint overlaps
    // that chair's seat, legs and backrest — all ten of them. That is correct for
    // a SEATED character, whose controller will be off, but a standing character
    // dropped there would get shoved sideways by the chair collider the moment
    // physics ticked. So there are two marker rings, not one, and which one you
    // use depends on whether the character is sitting.
    private const float StandRingRadius = 4.85f;

    private static Material _matFloor, _matWall, _matCeiling;
    private static Material _matTableTop, _matTableBase, _matChairFrame, _matChairSeat;
    private static Material _matPlacard, _matSeatMarker, _matStandMarker;

    // =======================================================================
    // MENU ITEMS
    // =======================================================================

    // Creates Assets/Scenes/DiscussionScene.unity if it does not exist, or
    // refreshes the blockout inside it if it does.
    //
    // Deliberately opens/creates the scene ADDITIVELY rather than with
    // NewSceneMode.Single. Single would blow away whatever scene is currently
    // open in the editor — which, when this is being driven remotely, is very
    // likely GameplayScene with unsaved changes in it. Additive leaves the
    // user's work exactly where it was and just adds a second scene to the
    // hierarchy that they can close whenever they like.
    [MenuItem("Tools/Blackwood/Create or Update Discussion Scene", false, 40)]
    public static void CreateOrUpdateDiscussionScene()
    {
        EnsureFolder("Assets", "Scenes");

        Scene scene;
        SceneAsset existing = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);

        if (existing != null)
        {
            scene = SceneManager.GetSceneByPath(ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }
        else
        {
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        }

        BuildInto(scene);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.Refresh();

        Debug.Log("[DiscussionRoom] Scene written to " + ScenePath +
                  " — it is open additively in the hierarchy; close it whenever you're done looking.");
    }

    // Rebuilds the blockout into whatever scene is currently active. Useful when
    // you already have DiscussionScene open and just want to iterate on the
    // numbers at the top of this file.
    [MenuItem("Tools/Blackwood/Generate Discussion Room", false, 41)]
    public static void GenerateIntoActiveScene()
    {
        Scene active = EditorSceneManager.GetActiveScene();
        BuildInto(active);
        EditorSceneManager.MarkSceneDirty(active);
    }

    [MenuItem("Tools/Blackwood/Delete Discussion Room", false, 42)]
    public static void DeleteFromActiveScene()
    {
        Scene active = EditorSceneManager.GetActiveScene();
        GameObject existing = FindRootIn(active);
        if (existing == null)
        {
            Debug.Log("[DiscussionRoom] Nothing to delete in '" + active.name + "'.");
            return;
        }

        Object.DestroyImmediate(existing);
        EditorSceneManager.MarkSceneDirty(active);
        Debug.Log("[DiscussionRoom] Deleted '" + RootName + "' from '" + active.name + "'.");
    }

    // =======================================================================
    // BUILD
    // =======================================================================
    private static void BuildInto(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            Debug.LogError("[DiscussionRoom] Target scene is not valid/loaded — aborting.");
            return;
        }

        BuildMaterials();

        // Full teardown first. Regenerating on top of the old geometry is how you
        // end up with two overlapping sets of colliders and a room that mysteriously
        // gets slower and buggier every time you press the menu item.
        GameObject stale = FindRootIn(scene);
        if (stale != null) Object.DestroyImmediate(stale);

        GameObject root = new GameObject(RootName);
        SceneManager.MoveGameObjectToScene(root, scene);
        root.transform.position = Vector3.zero;

        BuildShell(root.transform);
        BuildTable(root.transform);
        BuildChairsAndSeats(root.transform);
        BuildLighting(root.transform, scene);
        BuildCameraAnchor(root.transform);

        ValidateColliders(root);

        int colliders = root.GetComponentsInChildren<Collider>(true).Length;
        int renderers = root.GetComponentsInChildren<Renderer>(true).Length;
        Debug.Log("[DiscussionRoom] Built into '" + scene.name + "': " + SeatCount +
                  " seats, " + renderers + " renderers, " + colliders + " colliders.");
    }

    // -----------------------------------------------------------------------
    // Room shell: round floor, a ring of wall segments, round ceiling.
    // -----------------------------------------------------------------------
    private static void BuildShell(Transform parent)
    {
        GameObject shell = Child(parent, "Shell");

        float discRadius = RoomRadius + WallThickness;

        // Visible round floor. Its top surface is y = 0.
        Disc(shell.transform, "Floor",
             new Vector3(0f, -FloorThickness * 0.5f, 0f), discRadius, FloorThickness, _matFloor);

        // Invisible box slab directly under the floor disc.
        //
        // This is on purpose and it is not redundant. The visible floor is a
        // 20-sided cylinder mesh, so the only collider that fits it properly is a
        // MeshCollider — and a MeshCollider is exactly the kind of collider a
        // fast-moving swept capsule is most likely to squeeze through at a seam,
        // which in this project has already cost a full debugging session once
        // (the lawn). A plain BoxCollider one metre thick underneath it means
        // that even in the worst case a player lands on something solid instead
        // of falling out of the world. It renders nothing, so it costs nothing
        // visually.
        GameObject safety = LBox(shell.transform, "Floor_SafetyPlate",
                                 -discRadius, -1.0f, -discRadius,
                                  discRadius,  0.0f,  discRadius, _matFloor);
        Renderer safetyRenderer = safety.GetComponent<Renderer>();
        if (safetyRenderer != null) safetyRenderer.enabled = false;

        // Wall ring. Each segment is a straight box rotated to sit tangent to the
        // circle; segWidth is the chord length for one segment plus a small
        // overlap so neighbouring boxes always intersect rather than leaving a
        // hairline gap a capsule could catch on.
        GameObject walls = Child(shell.transform, "Walls");
        float segWidth = 2f * RoomRadius * Mathf.Tan(Mathf.PI / WallSegments) + 0.08f;

        for (int i = 0; i < WallSegments; i++)
        {
            float angle = (360f / WallSegments) * i;
            Quaternion rot = Quaternion.Euler(0f, angle, 0f);
            Vector3 outward = rot * Vector3.forward;

            GameObject seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seg.name = "Wall_" + i.ToString("D2");
            seg.transform.SetParent(walls.transform, false);
            seg.transform.localPosition = outward * (RoomRadius + WallThickness * 0.5f)
                                        + Vector3.up * (WallHeight * 0.5f);
            seg.transform.localRotation = rot;
            seg.transform.localScale = new Vector3(segWidth, WallHeight, WallThickness);
            NormaliseBoxCollider(seg);
            SetMat(seg, _matWall);
        }

        // Ceiling. Sits on top of the walls rather than overlapping them.
        Disc(shell.transform, "Ceiling",
             new Vector3(0f, WallHeight + CeilThickness * 0.5f, 0f), discRadius, CeilThickness, _matCeiling);
    }

    // -----------------------------------------------------------------------
    // The table: round top on a single central pedestal, so no legs get in the
    // way of the ten chairs.
    // -----------------------------------------------------------------------
    private static void BuildTable(Transform parent)
    {
        GameObject table = Child(parent, "RoundTable");

        // Empty at the exact centre of the table top — a stable thing for a
        // camera, a vote-result VFX or a spotlight to aim at later, so nothing
        // downstream has to hardcode Vector3.zero plus a magic height.
        GameObject centre = Child(table.transform, "TableCentre");
        centre.transform.localPosition = new Vector3(0f, TableTopY, 0f);

        Disc(table.transform, "Table_Base",
             new Vector3(0f, TableBaseHeight * 0.5f, 0f), TableBaseRadius, TableBaseHeight, _matTableBase);

        float pedestalBottom = TableBaseHeight;
        float pedestalTop    = TableTopY - TableTopThickness;
        Disc(table.transform, "Table_Pedestal",
             new Vector3(0f, (pedestalBottom + pedestalTop) * 0.5f, 0f),
             PedestalRadius, pedestalTop - pedestalBottom, _matTableBase);

        Disc(table.transform, "Table_Top",
             new Vector3(0f, TableTopY - TableTopThickness * 0.5f, 0f),
             TableTopRadius, TableTopThickness, _matTableTop);

        // A name placard on the table in front of each seat. Purely a blockout
        // readability thing: without them a round table with ten identical chairs
        // is genuinely hard to count in the Scene view, and these also mark
        // exactly where a player nameplate / vote token would go later.
        GameObject placards = Child(table.transform, "Placards");
        for (int i = 0; i < SeatCount; i++)
        {
            float angle = (360f / SeatCount) * i;
            Quaternion rot = Quaternion.Euler(0f, angle, 0f);
            Vector3 outward = rot * Vector3.forward;

            GameObject p = GameObject.CreatePrimitive(PrimitiveType.Cube);
            p.name = "Placard_" + (i + 1);
            p.transform.SetParent(placards.transform, false);
            p.transform.localPosition = outward * (TableTopRadius - 0.5f) + Vector3.up * (TableTopY + 0.01f);
            p.transform.localRotation = rot;
            p.transform.localScale = new Vector3(0.36f, 0.02f, 0.24f);

            // Placards are decoration sitting ON the table, which already has a
            // collider. Leaving colliders on them would give the table a bumpy
            // rim for no reason, so they come off entirely.
            Collider pc = p.GetComponent<Collider>();
            if (pc != null) Object.DestroyImmediate(pc);

            SetMat(p, _matPlacard);
        }
    }

    // -----------------------------------------------------------------------
    // Ten chairs, evenly spaced, each facing the centre of the table — plus one
    // SeatPoint marker per chair.
    // -----------------------------------------------------------------------
    private static void BuildChairsAndSeats(Transform parent)
    {
        GameObject chairs = Child(parent, "Chairs");
        GameObject seats  = Child(parent, "SeatPoints");
        GameObject stands = Child(parent, "StandPoints");

        for (int i = 0; i < SeatCount; i++)
        {
            float angle = (360f / SeatCount) * i;
            Quaternion outwardRot = Quaternion.Euler(0f, angle, 0f);
            Vector3 outward = outwardRot * Vector3.forward;
            Vector3 chairPos = outward * SeatRingRadius;

            // The chair is modelled in local space with +Z as its FACING
            // direction, so rotating the root to look at the table centre
            // (-outward) automatically turns the backrest outwards.
            Quaternion facing = Quaternion.LookRotation(-outward, Vector3.up);

            GameObject chair = Child(chairs.transform, "Chair_" + (i + 1));
            chair.transform.localPosition = chairPos;
            chair.transform.localRotation = facing;
            BuildOneChair(chair.transform);

            // SeatPoint is an EMPTY at floor level with the same facing as the
            // chair. Floor level rather than seat-surface level on purpose: this
            // is the transform a player character's root gets placed at, and a
            // character root lives at its feet. If we later want them visibly
            // seated, that is a sit animation plus a small local offset — not a
            // different marker position, which would silently break every
            // standing pose.
            GameObject seat = Child(seats.transform, "SeatPoint_" + (i + 1));
            seat.transform.localPosition = chairPos;
            seat.transform.localRotation = facing;

            // Small visible knob so the markers can actually be found in the
            // Scene view. No collider — players must never bump into a marker.
            GameObject knob = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            knob.name = "Marker";
            knob.transform.SetParent(seat.transform, false);
            knob.transform.localPosition = new Vector3(0f, 1.35f, 0f);
            knob.transform.localScale = Vector3.one * 0.22f;
            Collider kc = knob.GetComponent<Collider>();
            if (kc != null) Object.DestroyImmediate(kc);
            SetMat(knob, _matSeatMarker);

            // Matching standing position, directly behind the same chair and
            // facing the same way. Clear of the chair, clear of the wall, and far
            // enough from its neighbours that two player capsules never overlap
            // (2*pi*4.85 / 10 = 3.05m apart, against a 1.0m capsule diameter).
            GameObject stand = Child(stands.transform, "StandPoint_" + (i + 1));
            stand.transform.localPosition = outward * StandRingRadius;
            stand.transform.localRotation = facing;

            GameObject standKnob = GameObject.CreatePrimitive(PrimitiveType.Cube);
            standKnob.name = "Marker";
            standKnob.transform.SetParent(stand.transform, false);
            standKnob.transform.localPosition = new Vector3(0f, 0.08f, 0f);
            standKnob.transform.localScale = new Vector3(0.5f, 0.05f, 0.5f);
            Collider skc = standKnob.GetComponent<Collider>();
            if (skc != null) Object.DestroyImmediate(skc);
            SetMat(standKnob, _matStandMarker);
        }
    }

    // One chair, built in local space. +Z is the direction the sitter faces.
    private static void BuildOneChair(Transform chair)
    {
        float legTop  = SeatHeight;
        float halfLeg = LegThickness * 0.5f;
        float inset   = 0.06f; // keeps the legs just inside the seat's footprint

        float lx = ChairHalfWidth - inset;
        float lz = ChairHalfDepth - inset;

        LBox(chair, "Leg_FL", -lx - halfLeg, 0f,  lz - halfLeg, -lx + halfLeg, legTop,  lz + halfLeg, _matChairFrame);
        LBox(chair, "Leg_FR",  lx - halfLeg, 0f,  lz - halfLeg,  lx + halfLeg, legTop,  lz + halfLeg, _matChairFrame);
        LBox(chair, "Leg_BL", -lx - halfLeg, 0f, -lz - halfLeg, -lx + halfLeg, legTop, -lz + halfLeg, _matChairFrame);
        LBox(chair, "Leg_BR",  lx - halfLeg, 0f, -lz - halfLeg,  lx + halfLeg, legTop, -lz + halfLeg, _matChairFrame);

        LBox(chair, "Seat",
             -ChairHalfWidth, legTop, -ChairHalfDepth,
              ChairHalfWidth, legTop + SeatThickness, ChairHalfDepth, _matChairSeat);

        // Backrest sits at -Z, i.e. behind the sitter, and its two uprights run
        // all the way down to the seat so the whole thing is one solid piece.
        float backZ0 = -ChairHalfDepth;
        float backZ1 = -ChairHalfDepth + 0.08f;
        float backBottom = legTop + SeatThickness;

        LBox(chair, "Back_Upright_L", -ChairHalfWidth, backBottom, backZ0,
                                      -ChairHalfWidth + 0.08f, BackRestTop, backZ1, _matChairFrame);
        LBox(chair, "Back_Upright_R",  ChairHalfWidth - 0.08f, backBottom, backZ0,
                                       ChairHalfWidth, BackRestTop, backZ1, _matChairFrame);
        LBox(chair, "Back_Panel", -ChairHalfWidth, backBottom + 0.18f, backZ0,
                                   ChairHalfWidth, BackRestTop - 0.08f, backZ1, _matChairSeat);
    }

    // -----------------------------------------------------------------------
    // Lighting. A pendant over the table plus a soft fill, so the room reads as
    // an interior instead of a black void.
    // -----------------------------------------------------------------------
    private static void BuildLighting(Transform parent, Scene scene)
    {
        GameObject lights = Child(parent, "Lighting");

        // Pendant shade hanging over the centre of the table.
        GameObject shade = Disc(lights.transform, "Pendant_Shade",
                                new Vector3(0f, 3.05f, 0f), 0.9f, 0.25f, _matTableBase);
        Collider shadeCollider = shade.GetComponent<Collider>();
        if (shadeCollider != null) Object.DestroyImmediate(shadeCollider);

        GameObject cord = LBox(lights.transform, "Pendant_Cord",
                               -0.04f, 3.2f, -0.04f, 0.04f, WallHeight, 0.04f, _matTableBase);
        Collider cordCollider = cord.GetComponent<Collider>();
        if (cordCollider != null) Object.DestroyImmediate(cordCollider);

        GameObject keyGO = Child(lights.transform, "Pendant_Light");
        keyGO.transform.localPosition = new Vector3(0f, 2.85f, 0f);
        Light key = keyGO.AddComponent<Light>();
        key.type = LightType.Point;
        key.range = 14f;
        key.intensity = 2.6f;
        key.color = new Color(1f, 0.93f, 0.80f);
        key.shadows = LightShadows.Soft;

        // Fill lights, so the walls and the outward-facing side of each chair back
        // are not completely black.
        //
        // These are POINT lights on purpose, and that is not a style choice. The
        // first version used a single dim Directional here, which looked fine in
        // isolation — but a Directional light has no position: it lights every
        // renderer in the whole world no matter where it sits. This room is loaded
        // additively ALONGSIDE the school and parked 300m above it, so a
        // Directional in here would have quietly washed a second, bluish light
        // over the entire school map for the whole match, including during Night.
        // Point lights have a range, so everything this room does to the lighting
        // stops at its own walls.
        //
        // Related consequence worth knowing: RenderSettings (ambient colour) belong
        // to the ACTIVE scene, and the active scene during a match is GameplayScene,
        // not this one — so this room gets none of its own ambient at runtime and
        // has to be lit entirely by these lamps.
        for (int i = 0; i < 4; i++)
        {
            float angle = 90f * i + 45f;
            Vector3 outward = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;

            GameObject fillGO = Child(lights.transform, "Fill_Light_" + (i + 1));
            fillGO.transform.localPosition = outward * (RoomRadius - 1.2f) + Vector3.up * (WallHeight - 0.6f);
            Light fill = fillGO.AddComponent<Light>();
            fill.type = LightType.Point;
            fill.range = 11f;
            fill.intensity = 1.1f;
            fill.color = new Color(0.70f, 0.74f, 0.88f);
            fill.shadows = LightShadows.None;
        }

        // Ambient is a per-SCENE render setting, and RenderSettings always writes
        // to the ACTIVE scene — which, because this generator works additively,
        // is usually not the scene being built. So make the target active just
        // long enough to write the settings, then hand it back. Skipping this
        // would silently scribble the discussion room's ambient colour onto
        // whatever scene the user happened to have open.
        Scene previouslyActive = EditorSceneManager.GetActiveScene();
        bool switched = previouslyActive != scene && EditorSceneManager.SetActiveScene(scene);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.20f, 0.20f, 0.24f);
        RenderSettings.fog = false;

        if (switched) EditorSceneManager.SetActiveScene(previouslyActive);
    }

    // A ready-made camera pose looking across the table. DISABLED on purpose:
    // this scene is going to be loaded additively next to the gameplay scene, and
    // a second enabled Camera would fight the player's camera for the display
    // with no warning. It is here as an anchor to position from, not as a camera
    // that runs.
    private static void BuildCameraAnchor(Transform parent)
    {
        GameObject camGO = Child(parent, "DiscussionCameraAnchor");
        camGO.transform.localPosition = new Vector3(0f, 3.4f, -8.2f);
        camGO.transform.localRotation = Quaternion.Euler(22f, 0f, 0f);

        Camera cam = camGO.AddComponent<Camera>();
        cam.fieldOfView = 55f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 60f;
        camGO.SetActive(false);
    }

    // =======================================================================
    // VALIDATION
    // =======================================================================

    // Catches the zero-thickness collider class of bug at build time. A collider
    // thinner than 5cm in any world axis answers raycasts perfectly while letting
    // a swept character capsule pass straight through it — which is exactly how
    // the lawn bug hid from every static check that was thrown at it.
    private static void ValidateColliders(GameObject root)
    {
        int problems = 0;

        foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger) continue;

            Vector3 size;
            BoxCollider box = c as BoxCollider;
            if (box != null)
            {
                Vector3 s = box.transform.lossyScale;
                size = new Vector3(Mathf.Abs(box.size.x * s.x),
                                   Mathf.Abs(box.size.y * s.y),
                                   Mathf.Abs(box.size.z * s.z));
            }
            else
            {
                size = c.bounds.size;
            }

            float thinnest = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
            if (thinnest < 0.05f)
            {
                Debug.LogError("[DiscussionRoom] DEGENERATE COLLIDER on '" + Path(c.transform) +
                               "' — thinnest world axis is " + thinnest.ToString("F4") +
                               "m. A character capsule will tunnel through this.", c.gameObject);
                problems++;
            }
        }

        if (problems == 0)
            Debug.Log("[DiscussionRoom] Collider validation passed — no degenerate colliders.");
    }

    private static string Path(Transform t)
    {
        string p = t.name;
        while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
        return p;
    }

    // =======================================================================
    // PRIMITIVE HELPERS
    // =======================================================================
    private static GameObject Child(Transform parent, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    // Axis-aligned box from two opposite corners, in the PARENT's local space —
    // which is what makes the chair work: the chair is modelled around its own
    // origin and then the root transform is rotated to face the table.
    private static GameObject LBox(Transform parent, string name,
                                   float x0, float y0, float z0,
                                   float x1, float y1, float z1, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = new Vector3(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0), Mathf.Abs(z1 - z0));
        NormaliseBoxCollider(go);
        SetMat(go, mat);
        return go;
    }

    // Flat cylinder — table top, base, floor, ceiling, pendant shade.
    //
    // The default Cylinder primitive is 2 units tall and 1 unit across, so the
    // scale is (diameter, thickness/2, diameter). Its stock collider is a
    // CapsuleCollider, which is completely wrong for a flat disc — a capsule
    // around a 6.4m-wide, 16cm-thick plate is a 6.4m SPHERE. That has to be
    // swapped for a MeshCollider or the table becomes an invisible dome nobody
    // can walk near.
    private static GameObject Disc(Transform parent, string name, Vector3 localCentre,
                                   float radius, float thickness, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localCentre;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = new Vector3(radius * 2f, Mathf.Max(thickness, 0.02f) * 0.5f, radius * 2f);

        CapsuleCollider capsule = go.GetComponent<CapsuleCollider>();
        if (capsule != null) Object.DestroyImmediate(capsule);

        MeshFilter mf = go.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            MeshCollider mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.sharedMesh;
        }

        SetMat(go, mat);
        return go;
    }

    // Force the BoxCollider back to a unit cube so its shape is driven ONLY by
    // the transform scale. Same guard BlackwoodSchoolGenerator uses, for the same
    // confirmed reason.
    private static void NormaliseBoxCollider(GameObject go)
    {
        BoxCollider bc = go.GetComponent<BoxCollider>();
        if (bc != null) { bc.size = Vector3.one; bc.center = Vector3.zero; }
    }

    private static void SetMat(GameObject go, Material mat)
    {
        Renderer r = go.GetComponent<Renderer>();
        if (r != null && mat != null) r.sharedMaterial = mat;
    }

    private static GameObject FindRootIn(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return null;
        foreach (GameObject go in scene.GetRootGameObjects())
            if (go.name == RootName) return go;
        return null;
    }

    private static void EnsureFolder(string parent, string child)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + child))
            AssetDatabase.CreateFolder(parent, child);
    }

    // =======================================================================
    // MATERIALS
    // =======================================================================
    private static void BuildMaterials()
    {
        const string dir = "Assets/Materials/Blockout";
        EnsureFolder("Assets", "Materials");
        EnsureFolder("Assets/Materials", "Blockout");

        _matFloor      = Mat(dir, "DR_Floor",      new Color(0.30f, 0.27f, 0.25f));
        _matWall       = Mat(dir, "DR_Wall",       new Color(0.40f, 0.39f, 0.43f));
        _matCeiling    = Mat(dir, "DR_Ceiling",    new Color(0.18f, 0.18f, 0.21f));
        _matTableTop   = Mat(dir, "DR_TableTop",   new Color(0.47f, 0.31f, 0.18f));
        _matTableBase  = Mat(dir, "DR_TableBase",  new Color(0.27f, 0.19f, 0.12f));
        _matChairFrame = Mat(dir, "DR_ChairFrame", new Color(0.34f, 0.24f, 0.16f));
        _matChairSeat  = Mat(dir, "DR_ChairSeat",  new Color(0.52f, 0.17f, 0.17f));
        _matPlacard    = Mat(dir, "DR_Placard",    new Color(0.88f, 0.86f, 0.78f));
        _matSeatMarker  = Mat(dir, "DR_SeatMarker",  new Color(0.20f, 0.90f, 0.75f));
        _matStandMarker = Mat(dir, "DR_StandMarker", new Color(0.95f, 0.72f, 0.15f));
    }

    // Picks the shader that actually matches the project's render pipeline.
    // Hard-coding "Standard" gives solid magenta on URP, which is the classic way
    // a blockout ends up looking broken for no real reason.
    private static Material Mat(string dir, string name, Color colour)
    {
        string path = dir + "/" + name + ".mat";
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null)
        {
            existing.color = colour;
            if (existing.HasProperty("_BaseColor")) existing.SetColor("_BaseColor", colour);
            return existing;
        }

        Shader shader = null;
        if (UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline != null)
            shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Diffuse");

        Material m = new Material(shader);
        m.color = colour;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }
}
