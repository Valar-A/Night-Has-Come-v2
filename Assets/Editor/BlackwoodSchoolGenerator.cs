using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// ---------------------------------------------------------------------------
// BLACKWOOD SECONDARY SCHOOL — PROCEDURAL BLOCKOUT GENERATOR
// ---------------------------------------------------------------------------
// Builds the whole 5-level greybox from the room tables below, in one click:
//   Tools > Blackwood > Generate School Blockout
//
// This is deliberately a GENERATOR and not hand-placed geometry. Every room,
// corridor, wall, door gap, stair flight and task-node marker comes out of the
// data tables in this file, so changing the map means changing a number here
// and re-running the menu item — not dragging 500 cubes around by hand. It is
// fully re-runnable: it deletes the previous blockout root before rebuilding,
// so nothing accumulates or duplicates.
//
// LAYOUT (all measurements in metres, matching GDD section 6):
//
//   Main block            X 0..40, Z 0..30   (GDD: "approximately 40m x 30m")
//   Main corridor         X 0..40, Z 12..18  (the social hub — runs the full
//                                             length of the building)
//   South room block      X 0..40, Z 0..12
//   North room block      X 0..40, Z 18..30
//   Stair Tower B (west)  X -7..0,  Z 12..18  (projects out of the west wall)
//   Stair Tower A (east)  X 40..47, Z 12..18  (projects out of the east wall)
//
// WHY THE STAIR TOWERS PROJECT OUTSIDE THE MAIN BLOCK: a 4m floor-to-floor rise
// needs roughly 6m of horizontal run to stay walkable. Putting the stairs inside
// the 40x30 footprint would have meant either eating a room per floor or making
// the flights so steep the character controller fights them. Projecting stair
// towers is standard school architecture and it also gives each floor a clean,
// unobstructed corridor from end to end.
//
// STAIR MODULE (repeats identically at every level, in both towers):
//   - The FLIGHT sits in the south lane of the tower and always climbs AWAY from
//     the building. You step onto it straight off the corridor.
//   - The LANDING sits in the north lane at the arrival level and walks you back
//     toward the building, where it meets the corridor of the floor above.
//   - Because flights are stacked vertically in the same lane (one per storey)
//     and landings are stacked in the other lane, headroom is a constant 4m and
//     nothing ever intersects. One flight's top is never the next flight's
//     bottom — you always cross the landing between them.
//
// BASEMENT ACCESS: per GDD, the Basement is reachable via Stair Tower B ONLY.
// Tower A has no basement flight and its basement-level corridor opening is
// walled off, so the Boiler Room / Janitor's Wing really is a one-way-in,
// one-way-out death trap the way the GDD's "highest-risk floor" intends.
//
// WHAT THIS DOES NOT DO: no art, no lighting pass, no navmesh, no gameplay
// scripts. Task nodes and crafting benches are placed as clearly-named, colour-
// coded MARKERS at their GDD locations so the task/crafting systems can bind to
// real transforms instead of hard-coded coordinates later.
// ---------------------------------------------------------------------------
public static class BlackwoodSchoolGenerator
{
    private const string RootName = "BlackwoodSchool_Blockout";

    // ---- Core metrics -----------------------------------------------------
    private const float FloorHeight   = 4.0f;
    private const float SlabThickness = 0.3f;
    private const float WallThickness = 0.3f;
    private const float WallHeight    = FloorHeight - SlabThickness; // 3.7
    private const float DoorWidth     = 2.4f;

    // ---- Main block extents ----------------------------------------------
    private const float BX0 = 0f,  BX1 = 40f;
    private const float BZ0 = 0f,  BZ1 = 30f;

    // ---- Corridor band ----------------------------------------------------
    private const float CorrZ0 = 12f, CorrZ1 = 18f;

    // ---- Grounds ----------------------------------------------------------
    private const float LawnX0 = -27f, LawnX1 = 67f;
    private const float LawnZ0 = -20f, LawnZ1 = 50f;

    // ---- Stair towers -----------------------------------------------------
    private const float TowerAX0 = 40f, TowerAX1 = 47f;   // east
    private const float TowerBX0 = -7f, TowerBX1 = 0f;    // west

    // Lanes inside each tower (Z bands). Flight lane takes the corridor's south
    // half, landing lane the north half.
    //
    // These deliberately share edges and run flush to the corridor: an earlier
    // version inset them slightly (12.3..15.0 and 15.4..17.7), which read fine
    // in the Scene view but left three unfloored slivers at every level above
    // the base — 0.3m at the tower mouth, 0.2m before the first step, and 0.4m
    // between the two lanes — each one sitting directly over a 4m drop into the
    // stairwell. A 1m-wide character capsule usually bridges a gap like that,
    // which is exactly what makes it a nasty bug: it does not fail every time,
    // it fails occasionally, at speed, and always looks like a physics glitch
    // rather than a hole in the level. Sharing the edges removes the gaps by
    // construction instead of patching them with filler geometry.
    private const float FlightZ0 = 12.0f, FlightZ1 = 15.0f;
    private const float LandZ0   = 15.0f, LandZ1   = 18.0f;

    // ---- Stairs -----------------------------------------------------------
    // 16 steps x 0.25 rise = exactly one 4m storey. Tread works out at ~0.41m.
    // SimpleKCC on the player prefab is configured with StepHeight 0.5 and
    // StepDepth 0.2, so both numbers sit comfortably inside what the controller
    // will actually climb — verified against the prefab's own settings, not
    // guessed.
    private const int   StepCount = 16;
    private const float StepRise  = FloorHeight / StepCount; // 0.25

    // ---- Levels -----------------------------------------------------------
    // Y is the TOP surface of that level's floor slab — i.e. what the player
    // actually stands on.
    private struct Level
    {
        public string Name;
        public float  Y;
        public Level(string n, float y) { Name = n; Y = y; }
    }

    private static readonly Level[] Levels =
    {
        new Level("Basement", -4f),
        new Level("Ground",    0f),
        new Level("First",     4f),
        new Level("Second",    8f),
        new Level("Roof",     12f),
    };

    // ---- Rooms ------------------------------------------------------------
    // Side: true = north block (Z 18..30, door in its south wall)
    //       false = south block (Z 0..12, door in its north wall)
    private struct Room
    {
        public string Name;
        public float  X0, X1;
        public bool   North;
        public Room(string name, float x0, float x1, bool north)
        {
            Name = name; X0 = x0; X1 = x1; North = north;
        }
    }

    // BASEMENT — highest risk, highest reward. Boiler Room and the Janitor's
    // Wing are the two biggest resource yields in the game and they are both
    // down here behind a single staircase.
    private static readonly Room[] BasementRooms =
    {
        new Room("Boiler Room",              0f,  14f, false),
        new Room("Janitors Storage Corridor",14f, 26f, false),
        new Room("Dead End Alcoves",         26f, 34f, false),
        new Room("Electrical Cupboard",      34f, 40f, false),
        new Room("Basement Store A",          0f, 14f, true),
        new Room("Basement Store B",         14f, 27f, true),
        new Room("Archive Store",            27f, 40f, true),
    };

    // GROUND — primary social hub. Gymnasium takes the east wing as a single
    // wide-open hall with nowhere to hide, exactly as the GDD describes it.
    private static readonly Room[] GroundRooms =
    {
        new Room("Science Prep Room",         0f, 12f, false),  // Crafting Bench A
        new Room("South Store",              12f, 22f, false),
        new Room("Gymnasium",                22f, 40f, false),
        new Room("Reception and Main Lobby",  0f, 14f, true),
        new Room("Ground Floor Washrooms",   14f, 24f, true),
        new Room("Changing Rooms",           24f, 30f, true),
        new Room("Gym Equipment Store",      30f, 40f, true),
    };

    // FIRST — four classrooms literally lining the corridor, per GDD.
    private static readonly Room[] FirstRooms =
    {
        new Room("Common Study Area",  0f, 14f, false),
        new Room("Prep Store",        14f, 24f, false),
        new Room("Science Lab",       24f, 40f, false),   // vent shaft at east end
        new Room("Classroom 1",        0f, 10f, true),
        new Room("Classroom 2",       10f, 20f, true),
        new Room("Classroom 3",       20f, 30f, true),
        new Room("Classroom 4",       30f, 40f, true),
    };

    // SECOND — longer traversal, more isolation. Headmaster's Office stays
    // sealed (no door gap) — the GDD is explicit that no key has ever been found.
    private static readonly Room[] SecondRooms =
    {
        new Room("Art Supply Closet",  0f,  7f, false),   // Crafting Bench B
        new Room("Art Room",           7f, 18f, false),
        new Room("AV Room",           18f, 28f, false),
        new Room("Second Floor Archive",28f,40f, false),
        new Room("Staff Room",         0f, 14f, true),
        new Room("School Library",    14f, 30f, true),
        new Room("Headmasters Office",30f, 40f, true),    // sealed, see below
    };

    // Rooms that get NO door — locked forever, environmental storytelling only.
    private static readonly HashSet<string> SealedRooms = new HashSet<string>
    {
        "Headmasters Office",
    };

    // ---- Task nodes -------------------------------------------------------
    // These are the anchors the task system will bind to. Placing them now,
    // with the exact names the design uses, means the task implementation never
    // has to hard-code a coordinate.
    private struct Node
    {
        public string Name;
        public int    LevelIndex;
        public float  X, Z;
        public Node(string n, int lvl, float x, float z) { Name = n; LevelIndex = lvl; X = x; Z = z; }
    }

    private static readonly Node[] TaskNodes =
    {
        // Basement (level 0)
        new Node("Repair Generator",            0,  7f,  6f),
        new Node("Bleed Boiler Pressure Valve", 0, 11f,  3f),
        new Node("Fix Gas Leakage",             0, 20f,  6f),
        new Node("Restock Chemical Store",      0, 23f,  3f),
        new Node("Restore Electrical Panel",    0, 37f,  6f),
        // Ground (level 1)
        new Node("Repair Water System",         1, 19f, 24f),
        new Node("Repair Security Doors West",  1,  2f, 15f),
        new Node("Repair Security Doors East",  1, 38f, 15f),
        new Node("Restore Reception Terminal",  1,  7f, 24f),
        new Node("Re-hang Gym Floodlight",      1, 31f,  6f),
        new Node("Repair Locker Row",           1, 20f, 13f),
        // First (level 2)
        new Node("Pressurize Ventilation",      2, 38f,  6f),
        new Node("Neutralize Chemical Spill",   2, 30f,  6f),
        new Node("Replace Fluorescent Tubes 1", 2,  5f, 24f),
        new Node("Replace Fluorescent Tubes 2", 2, 15f, 24f),
        new Node("Replace Fluorescent Tubes 3", 2, 25f, 24f),
        new Node("Replace Fluorescent Tubes 4", 2, 35f, 24f),
        // Second (level 3)
        new Node("Restart Communication Tower", 3, 23f,  6f),
        new Node("Restore Staff Room Breaker",  3,  7f, 24f),
        new Node("Re-catalogue Library Archive",3, 22f, 24f),
        new Node("Sort Art Room Supplies",      3, 12f,  6f),
        // Roof (level 4)
        new Node("Calibrate Antenna Array",     4, 30f,  6f),
        new Node("Drain Water Tank Sediment",   4, 12f, 24f),
        // Stair towers, ground level
        new Node("Fix Emergency Stair Light A", 1, 43f, 16f),
        new Node("Fix Emergency Stair Light B", 1, -4f, 16f),
    };

    // Common tasks — every player gets one of these each round. Deliberately in
    // high-traffic, highly visible places: their whole point is that people can
    // see whether you actually turned up to do them.
    private static readonly Node[] CommonTaskNodes =
    {
        new Node("COMMON Sign Attendance Register", 1,  7f, 28f),
        new Node("COMMON Swipe Staff ID",           1, 36f, 16f),
        new Node("COMMON Log Maintenance Sheet",    1, 10f, 20f),
    };

    private static readonly Node[] Benches =
    {
        new Node("Crafting Bench A", 1, 6f, 6f),   // Science Prep Room, Ground
        new Node("Crafting Bench B", 3, 3f, 6f),   // Art Supply Closet, Second
    };

    // ---- Materials --------------------------------------------------------
    private static Material _matStructure, _matCorridor, _matStairs, _matGrass;
    private static Material _matTask, _matCommon, _matBench, _matSpawn, _matProp;
    private static Material[] _matLevel;

    // =======================================================================
    [MenuItem("Tools/Blackwood/Generate School Blockout")]
    public static void Generate()
    {
        BuildMaterials();

        // Re-runnable: nuke the previous blockout so repeated runs never stack
        // duplicate geometry on top of each other.
        GameObject existing = GameObject.Find(RootName);
        if (existing != null) Object.DestroyImmediate(existing);

        GameObject root = new GameObject(RootName);

        BuildGrounds(root.transform);

        for (int i = 0; i < Levels.Length; i++)
        {
            Level lvl = Levels[i];
            GameObject levelGO = new GameObject("Level_" + lvl.Name);
            levelGO.transform.SetParent(root.transform, false);

            if (lvl.Name == "Roof") BuildRoof(levelGO.transform, lvl);
            else                    BuildFloor(levelGO.transform, lvl, RoomsFor(i));
        }

        BuildStairTower(root.transform, "StairTower_A_East", TowerAX0, TowerAX1, true);
        BuildStairTower(root.transform, "StairTower_B_West", TowerBX0, TowerBX1, false);

        BuildMarkers(root.transform);

        // The old flat test plane, if it's still in the scene, sits right through
        // the Ground floor. Switch it off rather than deleting it — it may still
        // be wired to something.
        GameObject oldPlane = GameObject.Find("Plane");
        if (oldPlane != null && oldPlane.transform.root != root.transform)
        {
            oldPlane.SetActive(false);
            Debug.Log("[BlackwoodSchoolGenerator] Disabled the old flat 'Plane' — it intersected the Ground floor slab.");
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        ValidateColliders(root);

        int pieces = root.GetComponentsInChildren<Transform>().Length;
        Debug.Log("[BlackwoodSchoolGenerator] Blackwood Secondary School blockout generated — "
                  + pieces + " transforms across " + Levels.Length + " levels, "
                  + TaskNodes.Length + " task nodes, " + CommonTaskNodes.Length
                  + " common task nodes, " + Benches.Length + " crafting benches.");
    }

    [MenuItem("Tools/Blackwood/Delete School Blockout")]
    public static void DeleteBlockout()
    {
        GameObject existing = GameObject.Find(RootName);
        if (existing == null) { Debug.Log("[BlackwoodSchoolGenerator] Nothing to delete."); return; }
        Object.DestroyImmediate(existing);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[BlackwoodSchoolGenerator] Blockout deleted.");
    }

    // Every solid piece must be thick enough in all three axes for a swept
    // capsule to collide against reliably. A collider that is paper-thin in one
    // axis still passes raycast-based checks while letting a moving character
    // tunnel clean through it — which is exactly how the Lawn shipped as an
    // invisible hole in the world and cost a debugging session to find. Running
    // this on every generate turns that whole class of bug into a console error
    // at build time instead of a "why am I falling through the floor" report.
    private static void ValidateColliders(GameObject root)
    {
        const float minThickness = 0.05f;
        int bad = 0;
        BoxCollider[] boxes = root.GetComponentsInChildren<BoxCollider>(true);
        foreach (BoxCollider bc in boxes)
        {
            if (bc.isTrigger) continue;              // markers are meant to be non-solid
            Vector3 s = bc.bounds.size;
            if (s.x >= minThickness && s.y >= minThickness && s.z >= minThickness) continue;
            bad++;
            Debug.LogError("[BlackwoodSchoolGenerator] Collider on '" + bc.gameObject.name
                + "' is too thin to collide against reliably (world size " + s.ToString("F3")
                + "). A character controller will tunnel straight through it.", bc.gameObject);
        }

        if (bad == 0)
            Debug.Log("[BlackwoodSchoolGenerator] Collider validation passed — " + boxes.Length + " solid colliders, none degenerate.");
        else
            Debug.LogError("[BlackwoodSchoolGenerator] Collider validation FAILED — " + bad + " degenerate collider(s). See the errors above.");
    }

    private static Room[] RoomsFor(int levelIndex)
    {
        switch (levelIndex)
        {
            case 0: return BasementRooms;
            case 1: return GroundRooms;
            case 2: return FirstRooms;
            case 3: return SecondRooms;
            default: return new Room[0];
        }
    }

    // =======================================================================
    // FLOOR CONSTRUCTION
    // =======================================================================
    private static void BuildFloor(Transform parent, Level lvl, Room[] rooms)
    {
        float y = lvl.Y;
        int levelIndex = System.Array.IndexOf(Levels, lvl);
        Material accent = _matLevel[Mathf.Clamp(levelIndex, 0, _matLevel.Length - 1)];

        // ---- Slabs --------------------------------------------------------
        GameObject slabs = Child(parent, "Slabs");
        Slab(slabs.transform, "Slab_South",    BX0, BZ0,    BX1, CorrZ0, y, accent);
        Slab(slabs.transform, "Slab_Corridor", BX0, CorrZ0, BX1, CorrZ1, y, _matCorridor);
        Slab(slabs.transform, "Slab_North",    BX0, CorrZ1, BX1, BZ1,    y, accent);

        // ---- Building perimeter -------------------------------------------
        // The corridor band (Z 12..18) is deliberately left open at both ends —
        // that is where the stair towers plug in.
        GameObject walls = Child(parent, "Walls");
        WallZ(walls.transform, "Perimeter_West_South", BX0, BZ0,    CorrZ0, y, _matStructure);
        WallZ(walls.transform, "Perimeter_West_North", BX0, CorrZ1, BZ1,    y, _matStructure);
        WallZ(walls.transform, "Perimeter_East_South", BX1, BZ0,    CorrZ0, y, _matStructure);
        WallZ(walls.transform, "Perimeter_East_North", BX1, CorrZ1, BZ1,    y, _matStructure);
        WallX(walls.transform, "Perimeter_South",      BZ0, BX0, BX1, y, _matStructure);

        // Main entrance is a gap in the north wall at Ground level only.
        if (lvl.Name == "Ground")
            WallXGapped(walls.transform, "Perimeter_North", BZ1, BX0, BX1, y, _matStructure,
                        new[] { new Vector2(8f, 12f) });
        else
            WallX(walls.transform, "Perimeter_North", BZ1, BX0, BX1, y, _matStructure);

        // Basement is reachable through Stair Tower B only — seal the east
        // corridor mouth so Tower A genuinely does not serve this floor.
        if (lvl.Name == "Basement")
            WallZ(walls.transform, "Basement_TowerA_Sealed", BX1, CorrZ0, CorrZ1, y, _matStructure);

        // ---- Corridor walls, with a door gap per room ----------------------
        List<Vector2> southDoors = new List<Vector2>();
        List<Vector2> northDoors = new List<Vector2>();
        foreach (Room r in rooms)
        {
            if (SealedRooms.Contains(r.Name)) continue;   // locked room, no door
            float centre = (r.X0 + r.X1) * 0.5f;
            Vector2 gap = new Vector2(centre - DoorWidth * 0.5f, centre + DoorWidth * 0.5f);
            if (r.North) northDoors.Add(gap); else southDoors.Add(gap);
        }

        WallXGapped(walls.transform, "Corridor_Wall_South", CorrZ0, BX0, BX1, y, _matStructure, southDoors.ToArray());
        WallXGapped(walls.transform, "Corridor_Wall_North", CorrZ1, BX0, BX1, y, _matStructure, northDoors.ToArray());

        // ---- Room dividers -------------------------------------------------
        // One wall per internal boundary. Built from the boundary list rather
        // than per-room, so adjacent rooms never produce two coplanar walls
        // fighting each other in the depth buffer.
        BuildDividers(walls.transform, rooms, true,  CorrZ1, BZ1,    y);
        BuildDividers(walls.transform, rooms, false, BZ0,    CorrZ0, y);

        // ---- Room label markers -------------------------------------------
        GameObject labels = Child(parent, "Rooms");
        foreach (Room r in rooms)
        {
            float cz = r.North ? (CorrZ1 + BZ1) * 0.5f : (BZ0 + CorrZ0) * 0.5f;
            GameObject marker = new GameObject("Room_" + r.Name);
            marker.transform.SetParent(labels.transform, false);
            marker.transform.position = new Vector3((r.X0 + r.X1) * 0.5f, y + 0.05f, cz);
        }
    }

    private static void BuildDividers(Transform parent, Room[] rooms, bool north, float z0, float z1, float y)
    {
        SortedSet<float> boundaries = new SortedSet<float>();
        foreach (Room r in rooms)
        {
            if (r.North != north) continue;
            boundaries.Add(r.X0);
            boundaries.Add(r.X1);
        }
        foreach (float x in boundaries)
        {
            // The outer edges are already covered by the building perimeter.
            if (Mathf.Approximately(x, BX0) || Mathf.Approximately(x, BX1)) continue;
            WallZ(parent, (north ? "Divider_N_" : "Divider_S_") + x.ToString("F0"), x, z0, z1, y, _matStructure);
        }
    }

    // =======================================================================
    // ROOF
    // =======================================================================
    private static void BuildRoof(Transform parent, Level lvl)
    {
        float y = lvl.Y;
        GameObject slabs = Child(parent, "Slabs");
        Slab(slabs.transform, "Roof_Slab", BX0, BZ0, BX1, BZ1, y, _matLevel[4]);

        // Parapet — low enough to see over, high enough that the rooftop reads
        // as a real place rather than a floating plate.
        GameObject walls = Child(parent, "Parapet");
        Box(walls.transform, "Parapet_South", BX0, y, BZ0 - WallThickness, BX1, y + 1.1f, BZ0, _matStructure);
        Box(walls.transform, "Parapet_North", BX0, y, BZ1, BX1, y + 1.1f, BZ1 + WallThickness, _matStructure);
        // BOTH side parapets are broken across the corridor band (Z 13..17).
        //
        // CONFIRMED BUG this replaced: the west parapet was originally a single
        // unbroken run along X=0 for the full depth of the building. That is
        // exactly where the west stair tower's roof hut opens onto the deck, so
        // the parapet sealed the door — you could climb all five storeys, reach
        // the top landing, and simply not be able to get out onto the roof. The
        // east side happened to work only by accident: its parapet was already
        // split to make room for the fire-escape ladder, and that gap lines up
        // with the east hut's door. Caught by collision-probing both doorways;
        // west came back blocked, east clear. Now both are split on purpose.
        Box(walls.transform, "Parapet_West_A", BX0 - WallThickness, y, BZ0, BX0, y + 1.1f, 13f,  _matStructure);
        Box(walls.transform, "Parapet_West_B", BX0 - WallThickness, y, 17f, BX0, y + 1.1f, BZ1, _matStructure);
        // East parapet gap doubles as the fire-escape ladder arrival point.
        Box(walls.transform, "Parapet_East_A", BX1, y, BZ0,   BX1 + WallThickness, y + 1.1f, 13f,  _matStructure);
        Box(walls.transform, "Parapet_East_B", BX1, y, 17f,   BX1 + WallThickness, y + 1.1f, BZ1, _matStructure);

        GameObject props = Child(parent, "Props");
        Cylinder(props.transform, "Water_Tank_1", new Vector3(11f, y + 1.6f, 24f), 1.8f, 1.6f, _matProp);
        Cylinder(props.transform, "Water_Tank_2", new Vector3(15f, y + 1.6f, 24f), 1.8f, 1.6f, _matProp);

        // Satellite dish aerial — mast plus a tilted plate.
        Box(props.transform, "Aerial_Mast", 29.8f, y, 5.8f, 30.2f, y + 3f, 6.2f, _matProp);
        GameObject dish = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        dish.name = "Satellite_Dish";
        dish.transform.SetParent(props.transform, false);
        dish.transform.position = new Vector3(30f, y + 3.2f, 6f);
        dish.transform.localScale = new Vector3(2.4f, 0.12f, 2.4f);
        dish.transform.rotation = Quaternion.Euler(35f, 0f, 0f);
        SetMat(dish, _matProp);

        // Fire escape ladder on the east exterior — GDD lists it as the rooftop's
        // only alternative to Stairwell A.
        Box(props.transform, "Fire_Escape_Ladder", BX1, 0f, 14.6f, BX1 + 0.6f, y, 15.4f, _matProp);
    }

    // =======================================================================
    // STAIR TOWERS
    // =======================================================================
    private static void BuildStairTower(Transform parent, string name, float x0, float x1, bool east)
    {
        GameObject tower = Child(parent, name);

        // Tower B is the only route to the Basement; Tower A starts at Ground.
        int startLevel = east ? 1 : 0;

        // Outer shell, from the lowest level it serves up to the roof.
        float baseY = Levels[startLevel].Y - SlabThickness;
        float topY  = Levels[Levels.Length - 1].Y + 2.6f;   // includes the roof hut

        GameObject shell = Child(tower.transform, "Shell");
        Box(shell.transform, "Tower_Wall_South", x0, baseY, CorrZ0 - WallThickness, x1, topY, CorrZ0, _matStructure);
        Box(shell.transform, "Tower_Wall_North", x0, baseY, CorrZ1, x1, topY, CorrZ1 + WallThickness, _matStructure);
        float outerX = east ? x1 : x0;
        Box(shell.transform, "Tower_Wall_End",
            east ? outerX : outerX - WallThickness, baseY, CorrZ0,
            east ? outerX + WallThickness : outerX, topY, CorrZ1, _matStructure);

        // Base slab so the lowest flight has something to stand on.
        Slab(shell.transform, "Tower_Base", x0, CorrZ0, x1, CorrZ1, Levels[startLevel].Y, _matCorridor);

        GameObject flights  = Child(tower.transform, "Flights");
        GameObject landings = Child(tower.transform, "Landings");

        for (int i = startLevel; i < Levels.Length - 1; i++)
        {
            float fromY = Levels[i].Y;
            float toY   = Levels[i + 1].Y;

            // FLIGHT — south lane, always climbing away from the building. The
            // run starts exactly at the tower's inner edge so the first step is
            // a clean 0.25m rise straight off the corridor slab, with no lip and
            // no gap to fall down.
            float runStart = east ? x0 : x1;
            float tread    = Mathf.Abs(x1 - x0) / StepCount;
            GameObject flight = Child(flights.transform, "Flight_" + Levels[i].Name + "_to_" + Levels[i + 1].Name);

            for (int s = 0; s < StepCount; s++)
            {
                float sx0 = east ? runStart + s * tread : runStart - (s + 1) * tread;
                float sx1 = sx0 + tread;
                float stepTop = fromY + (s + 1) * StepRise;

                // Each step is a TREAD of fixed thickness, not a solid column
                // running down to the flight's base.
                //
                // CONFIRMED BUG this replaced: the original version built every
                // step as a solid block from the flight's base up to that step's
                // top surface. Near the top of a flight that block is nearly a
                // full storey tall — step 15 spanned the entire 4m rise. Since
                // every flight in a tower stacks in the same lane, that column
                // completely engulfed the top of the flight BELOW it: you would
                // climb a staircase and arrive inside solid geometry, with the
                // route back down sealed off. A floor-hole sweep caught it as 48
                // sample points inside the west tower where a downward ray found
                // nothing because its origin was buried in stair mass. Treads of
                // fixed thickness stack cleanly instead — flight N's treads sit a
                // clear 3.7m above flight N-1's arrival point.
                Box(flight.transform, "Step_" + s, sx0, stepTop - 0.3f, FlightZ0, sx1, stepTop, FlightZ1, _matStairs);

                // Balustrade along the open (north) edge of the flight, stepping
                // up with the treads. Without it the flight's north side is a
                // sheer drop back down onto the landing below — up to a full
                // storey. Deliberately stops two treads short of the top so the
                // player can step sideways off the flight onto the arrival
                // landing, which is the only way out of the stairwell.
                if (s < StepCount - 2)
                    Box(flight.transform, "Balustrade_" + s, sx0, stepTop, FlightZ1 - 0.15f, sx1, stepTop + 1.1f, FlightZ1 + 0.15f, _matStructure);
            }

            // LANDING — north lane at the arrival level, walking you back to the
            // corridor of the floor above.
            Slab(landings.transform, "Landing_" + Levels[i + 1].Name, x0, LandZ0, x1, LandZ1, toY, _matCorridor);
        }

        // Roof access hut over the top landing.
        float roofY = Levels[Levels.Length - 1].Y;
        GameObject hut = Child(tower.transform, "Roof_Hut");
        Box(hut.transform, "Hut_Roof", x0 - 0.2f, roofY + 2.6f, CorrZ0 - 0.2f, x1 + 0.2f, roofY + 2.9f, CorrZ1 + 0.2f, _matStructure);
        // Doorway from the hut onto the roof deck, facing the building.
        float doorX = east ? x0 : x1;
        Box(hut.transform, "Hut_Wall_Inner_A",
            doorX - (east ? 0f : WallThickness), roofY, CorrZ0,
            doorX + (east ? WallThickness : 0f), roofY + 2.6f, 14f, _matStructure);
        Box(hut.transform, "Hut_Wall_Inner_B",
            doorX - (east ? 0f : WallThickness), roofY, 16.4f,
            doorX + (east ? WallThickness : 0f), roofY + 2.6f, CorrZ1, _matStructure);
    }

    // =======================================================================
    // GROUNDS
    // =======================================================================
    private static void BuildGrounds(Transform parent)
    {
        GameObject grounds = Child(parent, "Grounds");

        // ~20m of lawn all the way around, per GDD 6.1. Sits just below the
        // Ground floor slab so the building reads as standing on it.
        //
        // Deliberately 2m thick rather than the 0.3m it needs to be visually.
        // Ground planes are the one piece of geometry a falling or fast-moving
        // character controller can tunnel through, and thickness is the cheapest
        // insurance against that — there is nothing under the lawn for the extra
        // depth to intersect, so it costs nothing.
        const float lawnTop = -SlabThickness;
        Box(grounds.transform, "Lawn", LawnX0, lawnTop - 2f, LawnZ0, LawnX1, lawnTop, LawnZ1, _matGrass);

        // Boundary wall around the grounds. Without it the lawn is a finite
        // plate in empty space: walk off the edge and you fall forever with no
        // way back, which is an unrecoverable state in a match with no respawn.
        // The GDD describes the grounds as a perimeter traversal zone that locks
        // at sunset, so a school boundary wall is the honest way to close it.
        GameObject bounds = Child(grounds.transform, "Boundary");
        Box(bounds.transform, "Boundary_South", LawnX0, lawnTop, LawnZ0 - 1f, LawnX1, lawnTop + 3f, LawnZ0, _matStructure);
        Box(bounds.transform, "Boundary_North", LawnX0, lawnTop, LawnZ1, LawnX1, lawnTop + 3f, LawnZ1 + 1f, _matStructure);
        Box(bounds.transform, "Boundary_West",  LawnX0 - 1f, lawnTop, LawnZ0 - 1f, LawnX0, lawnTop + 3f, LawnZ1 + 1f, _matStructure);
        Box(bounds.transform, "Boundary_East",  LawnX1, lawnTop, LawnZ0 - 1f, LawnX1 + 1f, lawnTop + 3f, LawnZ1 + 1f, _matStructure);

        // Courtyard marker — pure transit and observation space, no tasks,
        // and it locks at sunset (anyone caught outside is killed by the
        // environment, per GDD 6.2).
        GameObject courtyard = new GameObject("Courtyard_Zone");
        courtyard.transform.SetParent(grounds.transform, false);
        courtyard.transform.position = new Vector3(20f, 0f, -10f);
    }

    // =======================================================================
    // MARKERS
    // =======================================================================
    private static void BuildMarkers(Transform parent)
    {
        GameObject taskRoot   = Child(parent, "TaskNodes");
        GameObject commonRoot = Child(parent, "CommonTaskNodes");
        GameObject benchRoot  = Child(parent, "CraftingBenches");
        GameObject spawnRoot  = Child(parent, "SpawnPoints");

        foreach (Node n in TaskNodes)
            Marker(taskRoot.transform, "TaskNode_" + n.Name, new Vector3(n.X, Levels[n.LevelIndex].Y + 0.6f, n.Z), 0.55f, _matTask);

        foreach (Node n in CommonTaskNodes)
            Marker(commonRoot.transform, "CommonTask_" + n.Name, new Vector3(n.X, Levels[n.LevelIndex].Y + 0.6f, n.Z), 0.7f, _matCommon);

        // Benches are boxes rather than markers — they are physical objects the
        // player walks up to and stands at for 5 seconds.
        foreach (Node n in Benches)
        {
            GameObject b = Child(benchRoot.transform, n.Name);
            Box(b.transform, "Bench_Body", n.X - 1.1f, Levels[n.LevelIndex].Y, n.Z - 0.5f,
                                           n.X + 1.1f, Levels[n.LevelIndex].Y + 0.95f, n.Z + 0.5f, _matBench);
        }

        // Ten spawn points in the Reception / Main Lobby, spread far enough
        // apart that players do not start inside each other's capsules.
        for (int i = 0; i < 10; i++)
        {
            float sx = 2f + (i % 5) * 2.5f;
            float sz = 21f + (i / 5) * 3f;
            Marker(spawnRoot.transform, "Spawn_" + (i + 1), new Vector3(sx, 0.6f, sz), 0.45f, _matSpawn);
        }
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

    // Axis-aligned box from two opposite corners. Everything in this generator
    // ultimately funnels through here, which is why the room tables can stay as
    // plain min/max numbers instead of centres and extents.
    private static GameObject Box(Transform parent, string name, float x0, float y0, float z0, float x1, float y1, float z1, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position   = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
        go.transform.localScale = new Vector3(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0), Mathf.Abs(z1 - z0));

        // Force the BoxCollider back to a unit cube so its shape is driven ONLY
        // by the transform scale above.
        //
        // CONFIRMED BUG this guards against: the Lawn shipped with a collider
        // whose size had been flattened to (1, 0, 1) with center (0, -0.5, 0) —
        // an infinitely thin plate, while the mesh still rendered a full 0.3m
        // slab. Players walked out of the building onto the grass and fell
        // straight through it, forever. What made it so hard to spot is that a
        // zero-thickness collider still answers raycasts perfectly, so the
        // floor-hole sweep reported the lawn as solid; only a character
        // controller, which sweeps a capsule rather than a ray, tunnels through
        // it. Normalising the collider here means a hand-edited or stale shape
        // can never survive a regenerate.
        BoxCollider bc = go.GetComponent<BoxCollider>();
        if (bc != null) { bc.size = Vector3.one; bc.center = Vector3.zero; }

        SetMat(go, mat);
        return go;
    }

    private static void Slab(Transform parent, string name, float x0, float z0, float x1, float z1, float topY, Material mat)
    {
        Box(parent, name, x0, topY - SlabThickness, z0, x1, topY, z1, mat);
    }

    // Wall running along X at a fixed Z.
    private static void WallX(Transform parent, string name, float z, float x0, float x1, float baseY, Material mat)
    {
        if (x1 - x0 < 0.01f) return;
        Box(parent, name, x0, baseY, z - WallThickness * 0.5f, x1, baseY + WallHeight, z + WallThickness * 0.5f, mat);
    }

    // Wall running along Z at a fixed X.
    private static void WallZ(Transform parent, string name, float x, float z0, float z1, float baseY, Material mat)
    {
        if (z1 - z0 < 0.01f) return;
        Box(parent, name, x - WallThickness * 0.5f, baseY, z0, x + WallThickness * 0.5f, baseY + WallHeight, z1, mat);
    }

    // Wall along X with doorway gaps punched out of it. Gaps arrive unsorted and
    // may overlap or hang off the ends, so they are clamped and merged before
    // the solid segments between them are emitted.
    private static void WallXGapped(Transform parent, string name, float z, float x0, float x1, float baseY, Material mat, Vector2[] gaps)
    {
        List<Vector2> clean = new List<Vector2>();
        foreach (Vector2 g in gaps)
        {
            float a = Mathf.Max(x0, Mathf.Min(g.x, g.y));
            float b = Mathf.Min(x1, Mathf.Max(g.x, g.y));
            if (b > a) clean.Add(new Vector2(a, b));
        }
        clean.Sort((p, q) => p.x.CompareTo(q.x));

        float cursor = x0;
        int seg = 0;
        foreach (Vector2 g in clean)
        {
            if (g.x > cursor) WallX(parent, name + "_" + seg++, z, cursor, g.x, baseY, mat);
            cursor = Mathf.Max(cursor, g.y);
        }
        if (cursor < x1) WallX(parent, name + "_" + seg, z, cursor, x1, baseY, mat);
    }

    private static void Cylinder(Transform parent, string name, Vector3 centre, float diameter, float height, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = centre;
        go.transform.localScale = new Vector3(diameter, height * 0.5f, diameter);
        SetMat(go, mat);
    }

    // Markers are trigger-only: they must never block movement, they exist so a
    // designer can see the node and so gameplay code has a transform to bind to.
    private static void Marker(Transform parent, string name, Vector3 pos, float size, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * size;
        Collider col = go.GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
        SetMat(go, mat);
    }

    private static void SetMat(GameObject go, Material mat)
    {
        Renderer r = go.GetComponent<Renderer>();
        if (r != null && mat != null) r.sharedMaterial = mat;
    }

    // =======================================================================
    // MATERIALS
    // =======================================================================
    private static void BuildMaterials()
    {
        const string dir = "Assets/Materials/Blockout";
        if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
        if (!AssetDatabase.IsValidFolder(dir))                AssetDatabase.CreateFolder("Assets/Materials", "Blockout");

        _matStructure = Mat(dir, "BO_Structure", new Color(0.72f, 0.72f, 0.70f));
        _matCorridor  = Mat(dir, "BO_Corridor",  new Color(0.58f, 0.58f, 0.60f));
        _matStairs    = Mat(dir, "BO_Stairs",    new Color(0.45f, 0.46f, 0.50f));
        _matGrass     = Mat(dir, "BO_Grass",     new Color(0.26f, 0.36f, 0.22f));
        _matProp      = Mat(dir, "BO_Prop",      new Color(0.38f, 0.38f, 0.40f));

        // Level colour-coding — the whole point of a blockout is being able to
        // tell at a glance which floor you are looking at in the Scene view.
        _matLevel = new[]
        {
            Mat(dir, "BO_Level_Basement", new Color(0.45f, 0.28f, 0.26f)),
            Mat(dir, "BO_Level_Ground",   new Color(0.78f, 0.76f, 0.72f)),
            Mat(dir, "BO_Level_First",    new Color(0.60f, 0.70f, 0.80f)),
            Mat(dir, "BO_Level_Second",   new Color(0.62f, 0.76f, 0.62f)),
            Mat(dir, "BO_Level_Roof",     new Color(0.40f, 0.40f, 0.44f)),
        };

        _matTask   = Mat(dir, "BO_TaskNode",   new Color(0.95f, 0.78f, 0.15f));
        _matCommon = Mat(dir, "BO_CommonTask", new Color(0.95f, 0.45f, 0.10f));
        _matBench  = Mat(dir, "BO_Bench",      new Color(0.30f, 0.65f, 0.90f));
        _matSpawn  = Mat(dir, "BO_Spawn",      new Color(0.20f, 0.90f, 0.75f));
    }

    private static Material Mat(string dir, string name, Color colour)
    {
        string path = dir + "/" + name + ".mat";
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) { existing.color = colour; return existing; }

        // Pick the shader that actually matches the project's render pipeline.
        // Hard-coding "Standard" would give solid magenta on URP, which is the
        // classic way a blockout ends up looking broken for no real reason.
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
