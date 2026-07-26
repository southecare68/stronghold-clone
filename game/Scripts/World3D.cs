// World3D.cs — the 3D renderer.
//
// The deterministic simulation (game/Sim) is reused untouched; this turns its
// state into a 3D scene each frame with the real POLYGON models — no baking, no
// sprites. Milestone 1: a local skirmish, a tilted camera, ground, and every
// unit and building as its actual model at the interpolated sim position, facing
// where it moves. Input, animation, height and netcode come in later milestones.

using Godot;
using Sim;
using System.Collections.Generic;

public partial class World3D : Node3D
{
    const int TicksPerSecond = 20;
    const double Step = 1.0 / TicksPerSecond;
    const int MaxTicksPerFrame = 8;
    const int MapSize = Skirmish.DefaultSize;

    const string Prefabs = "res://Assets/PolygonFantasyKingdom/Prefabs/";

    // Model scale: the Synty models are authored around 1 unit ≈ 1 metre, so a
    // character is ~1.8 units tall — too big for our 1-unit tiles. These bring
    // them down to size; tuned by eye.
    const float CharScale = 0.42f;

    // Wall assembly. The pack's pieces are 5-unit modules: Wall_01 is a solid
    // 5x5x0.5 body with a FLAT top; Battlements is the 5x1.38x0.5 crenellated
    // parapet that stands on it. Scaled to one tile wide, a rampart's height, with
    // a walkway men stand and walk on. All in local space; the run is local X.
    static readonly Vector3 WallBodyScale = new(0.202f, 0.34f, 1.5f);  // -> 1.01 wide x 1.7 tall x 0.75 deep
    const float WallTopY = 5f * 0.34f;                                 // 1.7 — flat walkway height
    static readonly Vector3 WallBatScale = new(0.202f, 0.32f, 0.7f);   // parapet, thinner, on the outer edge
    const float WallBatZ = 0.26f;                                      // parapet offset to one long edge
    const float WallWalkY = WallTopY;                                  // men stand on the flat top

    PackedScene _wallBody, _wallBat;
    readonly HashSet<(int, int)> _wallSet = new();

    Simulation _sim;
    Camera3D _cam;
    double _accum;
    float _alpha;

    readonly Dictionary<int, Node3D> _unitNodes = new();
    readonly Dictionary<int, Node3D> _buildingNodes = new();
    readonly Dictionary<int, Vector2> _prevPos = new();
    readonly Dictionary<int, float> _yaw = new();
    readonly Dictionary<int, Skeleton3D> _skel = new();
    readonly Dictionary<int, float> _phase = new();
    const float WalkCadence = 11f;   // how fast the legs cycle while marching

    // Climbing the wall: a garrisoned soldier is routed on foot from the ground, to
    // the stair, up it and along the walkway to its spot — a render path, since the
    // sim just treats it as garrisoned.
    sealed class Climb { public Vector3[] Pts; public float Dist; }
    readonly Dictionary<int, Climb> _climb = new();
    readonly HashSet<int> _onWall = new();
    Vector3 _stairBase, _stairTop;   // set by BuildStaircase
    const float ClimbSpeed = 2.6f;   // units per second up the path

    readonly Dictionary<BuildingType, PackedScene> _bldModel = new();
    readonly Dictionary<BuildingType, float> _bldScale = new();
    PackedScene _mSoldier, _mPeasant, _mRunner, _mBrute, _mArcher;

    // Camera orbit around a target on the ground.
    Vector3 _camTarget;
    float _camDist = 16f, _camYaw = 0.6f, _camPitch = 0.85f;   // radians

    // Selection & orders (local play; netcode comes in a later milestone).
    const int MyPlayer = 1;
    readonly HashSet<int> _selected = new();
    readonly List<Command> _pending = new();
    readonly Dictionary<int, MeshInstance3D> _rings = new();
    Mesh _ringMesh;
    Material _ringMine, _ringEnemy;

    bool _boxing;
    Vector2 _boxStart, _boxEnd;
    ColorRect _box;

    public override void _Ready()
    {
        _sim = new Simulation(Sim.TileMap.Skirmish(MapSize));
        Skirmish.Setup(_sim, MapSize);

        LoadModels();
        SetupEnvironment();
        SetupGround();
        SetupSelectionUi();

        _cam = new Camera3D { Current = true };
        AddChild(_cam);
        // Aim at the starting party (soldiers spawn a few tiles east of the keep).
        _camTarget = new Vector3(Skirmish.West(MapSize) + 9, 0, MapSize / 2f);   // the wall
        UpdateCamera();

        // A stretch of wall at the base, with the starting soldiers already manning
        // it so men-on-the-walls is visible the moment you launch. You can still
        // garrison by hand too: select soldiers and right-click a wall. (A proper
        // build UI comes in M5.)
        int wy = MapSize / 2, wx = Skirmish.West(MapSize) + 6;
        var walls = new List<Building>();
        for (int i = 0; i < 6; i++) walls.Add(_sim.PlaceBuilding(BuildingType.Wall, 1, wx + i, wy));
        // A stone stair up to the walkway on the inner face, near the west end.
        BuildStaircase(wx + 1, wy);

        int k = 1;   // spread them along the wall, leaving the near tiles clear
        foreach (var u in _sim.Units)
        {
            if (u.Owner != 1 || u.IsPeasant || k >= walls.Count) continue;
            var w = walls[k]; k += 2;
            if (w != null) u.GarrisonId = w.Id;   // no snap — they walk to the stair and climb up
        }

        SnapshotPositions();
        GD.Print("[3d] world ready — ", _sim.Units.Count, " units, ", _sim.Buildings.Count, " buildings");
    }

    // The union AABB of a model's meshes in its own space — used to size and place
    // the wall pieces and to know how high the ramparts stand.
    static Aabb ModelAabb(PackedScene scene)
    {
        var inst = scene.Instantiate<Node3D>();
        var a = new Aabb();
        bool first = true;
        Walk(inst, ref a, ref first);
        inst.QueueFree();
        return a;

        static void Walk(Node n, ref Aabb acc, ref bool first)
        {
            if (n is VisualInstance3D vi)
            {
                var box = vi.GetAabb();
                acc = first ? box : acc.Merge(box);
                first = false;
            }
            foreach (var c in n.GetChildren()) Walk(c, ref acc, ref first);
        }
    }

    // ---- setup -------------------------------------------------------------

    void LoadModels()
    {
        _mSoldier = Load("Characters/SM_Chr_Soldier_Male_01");
        _mPeasant = Load("Characters/SM_Chr_Peasant_Male_01");
        _mRunner  = Load("Characters/SM_Chr_Soldier_Female_01");
        _mBrute   = Load("Characters/SM_Chr_Rider_01");
        _mArcher  = Load("Characters/SM_Chr_King_01");

        void B(BuildingType t, string rel, float s) { _bldModel[t] = Load(rel); _bldScale[t] = s; }
        B(BuildingType.Keep,          "Castle/SM_Bld_Castle_Wall_Tower_L_01", 0.5f);
        B(BuildingType.Barracks,      "Buildings/Preset_Houses/SM_Bld_Preset_House_01_A_Optimized", 0.5f);
        B(BuildingType.WoodcutterHut, "Buildings/Preset_Houses/SM_Bld_Preset_House_03_Optimized", 0.5f);
        B(BuildingType.Storehouse,    "Buildings/Preset_Houses/SM_Bld_Preset_Blacksmith_01_Optimized", 0.5f);
        B(BuildingType.Quarry,        "Buildings/Preset_Houses/SM_Bld_Preset_House_08_Optimized", 0.5f);
        B(BuildingType.Farm,          "Buildings/Preset_Houses/SM_Bld_Preset_Stables_01_Optimized", 0.5f);
        B(BuildingType.Mill,          "Buildings/Preset_Houses/SM_Bld_Preset_House_Windmill_01_Optimized", 0.5f);
        B(BuildingType.Bakery,        "Buildings/Preset_Houses/SM_Bld_Preset_House_04_Optimized", 0.5f);
        B(BuildingType.House,         "Buildings/Preset_Houses/SM_Bld_Preset_House_02_A_Optimized", 0.5f);
        B(BuildingType.Gatehouse,     "Castle/SM_Bld_Castle_Wall_Gate_01", 0.5f);
        B(BuildingType.Wall,          "Castle/SM_Bld_Castle_Wall_01", 0.5f);   // (composed in MakeWall)

        _wallBody = Load("Castle/SM_Bld_Castle_Wall_01");
        _wallBat  = Load("Castle/SM_Bld_Castle_Battlements_01");
    }

    static PackedScene Load(string rel) => GD.Load<PackedScene>(Prefabs + rel + ".tscn");

    void SetupEnvironment()
    {
        var sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-52, -46, 0),
            LightEnergy = 1.15f,
            ShadowEnabled = true,
        };
        AddChild(sun);

        var we = new WorldEnvironment();
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.53f, 0.62f, 0.74f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.55f, 0.57f, 0.6f),
            AmbientLightEnergy = 0.9f,
        };
        we.Environment = env;
        AddChild(we);
    }

    void SetupGround()
    {
        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(MapSize, MapSize) },
            Position = new Vector3(MapSize / 2f, 0, MapSize / 2f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.45f, 0.28f) },
        };
        AddChild(ground);
    }

    void SetupSelectionUi()
    {
        // A flat ground ring under a selected unit, team-coloured and unshaded so
        // it reads at any camera angle.
        _ringMesh = new TorusMesh { InnerRadius = 0.42f, OuterRadius = 0.55f, Rings = 6, RingSegments = 20 };
        _ringMine = Unshaded(new Color(0.35f, 0.75f, 1f));
        _ringEnemy = Unshaded(new Color(1f, 0.45f, 0.35f));

        // A 2D marquee for box-select, on its own layer above the 3D view.
        var layer = new CanvasLayer();
        AddChild(layer);
        _box = new ColorRect { Color = new Color(0.4f, 0.8f, 1f, 0.18f), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(_box);
    }

    static Material Unshaded(Color c) => new StandardMaterial3D
    {
        AlbedoColor = c,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
    };

    // ---- per-frame ---------------------------------------------------------

    public override void _Process(double delta)
    {
        _accum += delta;
        int ran = 0;
        while (_accum >= Step && ran < MaxTicksPerFrame)
        {
            SnapshotPositions();
            // This frame's orders ride the FIRST tick only, then it is empty.
            _sim.Tick(ran == 0 ? _pending : (IReadOnlyList<Command>)System.Array.Empty<Command>());
            if (ran == 0) _pending.Clear();
            _accum -= Step;
            ran++;
        }
        _alpha = (float)Mathf.Clamp(_accum / Step, 0.0, 1.0);

        SyncUnits(delta);
        SyncBuildings();
        UpdateRings();
        CameraInput(delta);
    }

    void SnapshotPositions()
    {
        foreach (var u in _sim.Units) _prevPos[u.Id] = SimXZ(u);
    }

    static Vector2 SimXZ(Unit u) => new Vector2(u.X / (float)Fixed.One, u.Y / (float)Fixed.One);

    void SyncUnits(double delta)
    {
        var live = new HashSet<int>();
        foreach (var u in _sim.Units)
        {
            live.Add(u.Id);
            if (!_unitNodes.TryGetValue(u.Id, out var node))
            {
                node = ModelFor(u).Instantiate<Node3D>();
                node.Scale = Vector3.One * CharScale;
                AddChild(node);
                _unitNodes[u.Id] = node;
                DisableBakedAnimation(node);            // the prefab's AnimationPlayer would clobber our posing
                var sk = Anim3D.Find(node);
                if (sk != null) BindToSkeleton(node, sk);   // the modular meshes ship unbound — bind them so posing shows
                _skel[u.Id] = sk;
            }

            var now = SimXZ(u);
            var prev = _prevPos.TryGetValue(u.Id, out var p) ? p : now;
            var draw = prev.Lerp(now, _alpha);
            var vel = now - prev;

            Vector3 pos, face;
            bool walking, attacking = false;

            var wall = u.GarrisonId != 0 ? BuildingById(u.GarrisonId) : null;
            if (wall != null)
            {
                var top = new Vector3(wall.X + (wall.W - 1) / 2f, WallTopY, wall.Y + (wall.H - 1) / 2f);
                if (_onWall.Contains(u.Id))
                {
                    // Up and stood to. Hold the spot; keep the heading it arrived on.
                    pos = top; face = Vector3.Zero; walking = false;
                }
                else
                {
                    // March to the stair, up it, and along the walkway to the spot.
                    if (!_climb.TryGetValue(u.Id, out var cl))
                        cl = _climb[u.Id] = new Climb { Pts = new[] { new Vector3(draw.X, 0, draw.Y), _stairBase, _stairTop, top } };
                    cl.Dist += (float)delta * ClimbSpeed;
                    pos = SamplePath(cl.Pts, cl.Dist, out face, out bool done);
                    walking = true;
                    if (done) { _onWall.Add(u.Id); _climb.Remove(u.Id); pos = top; walking = false; }
                }
            }
            else
            {
                // Field unit: on the ground, moving where the sim moves it.
                _onWall.Remove(u.Id); _climb.Remove(u.Id);
                pos = new Vector3(draw.X, 0, draw.Y);
                face = new Vector3(vel.X, 0, vel.Y);
                walking = vel.LengthSquared() > 1e-5f;
                attacking = !walking && u.TargetId != 0;
            }

            node.Position = pos;
            if (face.LengthSquared() > 1e-5f) _yaw[u.Id] = Mathf.Atan2(face.X, face.Z);
            node.Rotation = new Vector3(0, _yaw.TryGetValue(u.Id, out var yy) ? yy : 0f, 0);

            if (_skel.TryGetValue(u.Id, out var s) && s != null)
            {
                if (walking)
                {
                    _phase[u.Id] = (_phase.TryGetValue(u.Id, out var ph) ? ph : 0f) + (float)delta * WalkCadence;
                    Anim3D.Walk(s, _phase[u.Id]);
                }
                else if (attacking)
                {
                    var d = _sim.DesignOf(u.DesignId);
                    float prog = d.Cooldown > 0 ? 1f - u.AttackTimer / (float)d.Cooldown : 0f;
                    Anim3D.Attack(s, Mathf.Clamp((int)(prog * Anim3D.AttackFrames), 0, Anim3D.AttackFrames - 1));
                }
                else Anim3D.Idle(s);
            }
        }
        Prune(_unitNodes, live);
        foreach (var id in new List<int>(_skel.Keys))
            if (!live.Contains(id)) { _skel.Remove(id); _phase.Remove(id); _climb.Remove(id); _onWall.Remove(id); }
    }

    // Point at distance `dist` along a polyline, with the segment direction and
    // whether the end has been reached.
    static Vector3 SamplePath(Vector3[] pts, float dist, out Vector3 dir, out bool done)
    {
        done = false;
        float rem = dist;
        for (int i = 0; i < pts.Length - 1; i++)
        {
            var seg = pts[i + 1] - pts[i];
            float len = seg.Length();
            bool last = i == pts.Length - 2;
            if (rem <= len || last)
            {
                dir = len > 1e-4f ? seg / len : Vector3.Forward;
                if (last && rem >= len) done = true;
                return pts[i] + seg * (len > 1e-4f ? Mathf.Clamp(rem / len, 0f, 1f) : 1f);
            }
            rem -= len;
        }
        dir = Vector3.Forward;
        done = true;
        return pts[^1];
    }

    void SyncBuildings()
    {
        // Rampart tiles, so a wall knows which way its run goes.
        _wallSet.Clear();
        foreach (var b in _sim.Buildings)
            if ((b.Type == BuildingType.Wall || b.Type == BuildingType.Gatehouse) && b.Alive)
                _wallSet.Add((b.X, b.Y));

        var live = new HashSet<int>();
        foreach (var b in _sim.Buildings)
        {
            live.Add(b.Id);
            if (!_buildingNodes.TryGetValue(b.Id, out var node))
            {
                if (!_bldModel.TryGetValue(b.Type, out var scene) || scene == null) continue;
                node = scene.Instantiate<Node3D>();

                if (b.Type == BuildingType.Wall)
                {
                    node.QueueFree();          // the generic instance isn't used for walls
                    node = MakeWall(b);
                }
                else
                {
                    node.Scale = Vector3.One * _bldScale[b.Type];
                    // Centre on the footprint. A tile at (x,y) is centred at (x,y),
                    // so a WxH footprint's centre is (x+(W-1)/2, y+(H-1)/2) — not W/2,
                    // which would sit half a tile off (unit positions are tile-centred).
                    node.Position = new Vector3(b.X + (b.W - 1) / 2f, 0, b.Y + (b.H - 1) / 2f);
                }
                AddChild(node);
                _buildingNodes[b.Id] = node;
            }
        }
        Prune(_buildingNodes, live);
    }

    // A stone staircase from the ground up to the walkway, on the inner (south)
    // face of the wall at column `tileX`. Built from stacked steps so a unit
    // climbing it reads as walking up.
    const int StairSteps = 8;
    const float StairRun = 2.0f;   // how far south of the wall the stair reaches
    void BuildStaircase(float tileX, float wallZ)
    {
        _stairBase = new Vector3(tileX, 0, wallZ + StairRun);   // foot of the stair, on the ground
        _stairTop = new Vector3(tileX, WallTopY, wallZ);        // where it meets the walkway
        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.56f, 0.52f, 0.47f) };
        float stepH = WallTopY / StairSteps;
        float stepDepth = StairRun / StairSteps;
        for (int i = 0; i < StairSteps; i++)
        {
            float topY = stepH * (i + 1);
            float z = wallZ + StairRun - (i + 0.5f) * stepDepth;   // nearest step is furthest from the wall
            var step = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.9f, topY, stepDepth + 0.02f) },
                MaterialOverride = mat,
                Position = new Vector3(tileX, topY * 0.5f, z),   // grow up from the ground
            };
            AddChild(step);
        }
    }

    // A wall tile: a solid body with a flat walkway top and a crenellated parapet
    // along the outer edge, turned to run with the wall line. Men stand on the top.
    Node3D MakeWall(Building b)
    {
        bool horiz = _wallSet.Contains((b.X + 1, b.Y)) || _wallSet.Contains((b.X - 1, b.Y));
        bool vert = _wallSet.Contains((b.X, b.Y + 1)) || _wallSet.Contains((b.X, b.Y - 1));

        var root = new Node3D
        {
            Position = new Vector3(b.X + (b.W - 1) / 2f, 0, b.Y + (b.H - 1) / 2f),   // tile-centred like the units
            Rotation = new Vector3(0, vert && !horiz ? Mathf.Pi / 2f : 0f, 0),
        };

        var body = _wallBody.Instantiate<Node3D>();
        body.Scale = WallBodyScale;
        root.AddChild(body);

        var parapet = _wallBat.Instantiate<Node3D>();
        parapet.Scale = WallBatScale;
        parapet.Position = new Vector3(0, WallTopY, WallBatZ);   // on top, along the outer edge
        root.AddChild(parapet);

        return root;
    }

    static void Prune(Dictionary<int, Node3D> nodes, HashSet<int> live)
    {
        var gone = new List<int>();
        foreach (var kv in nodes) if (!live.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone) { nodes[id].QueueFree(); nodes.Remove(id); }
    }

    // Remove any AnimationPlayer the prefab ships with — it drives the skeleton to
    // its bind pose every frame and would overwrite the poses we set.
    static void DisableBakedAnimation(Node n)
    {
        var kill = new List<Node>();
        Collect(n, kill);
        foreach (var ap in kill) { ap.GetParent().RemoveChild(ap); ap.QueueFree(); }

        static void Collect(Node node, List<Node> into)
        {
            if (node is AnimationPlayer) into.Add(node);
            foreach (var c in node.GetChildren()) Collect(c, into);
        }
    }

    // Synty modular characters ship every body mesh under one skeleton with its
    // skeleton binding left empty, so they don't follow the pose. Point each skinned
    // mesh at the skeleton so our posing actually deforms it.
    static void BindToSkeleton(Node n, Skeleton3D skel)
    {
        if (n is MeshInstance3D mi && mi.Skin != null)
            mi.Skeleton = mi.GetPathTo(skel);
        foreach (var c in n.GetChildren()) BindToSkeleton(c, skel);
    }

    PackedScene ModelFor(Unit u)
    {
        if (u.IsPeasant) return _mPeasant;
        return u.DesignId switch { 1 => _mRunner, 2 => _mBrute, 3 => _mArcher, _ => _mSoldier };
    }

    // ---- camera ------------------------------------------------------------

    void CameraInput(double delta)
    {
        float pan = 24f * (float)delta * (_camDist / 34f);
        var fwd = new Vector3(Mathf.Sin(_camYaw), 0, Mathf.Cos(_camYaw));
        var right = new Vector3(fwd.Z, 0, -fwd.X);
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up))    _camTarget -= fwd * pan;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down))  _camTarget += fwd * pan;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left))  _camTarget -= right * pan;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) _camTarget += right * pan;
        if (Input.IsKeyPressed(Key.Q)) _camYaw -= 1.2f * (float)delta;
        if (Input.IsKeyPressed(Key.E)) _camYaw += 1.2f * (float)delta;
        UpdateCamera();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)   { _camDist = Mathf.Max(6f, _camDist * 0.9f); UpdateCamera(); }
            if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) { _camDist = Mathf.Min(90f, _camDist * 1.1f); UpdateCamera(); }

            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed) { _boxing = true; _boxStart = _boxEnd = mb.Position; }
                else if (_boxing) { _boxing = false; _box.Visible = false; FinishSelect(mb.Position); }
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
            {
                RightClick(mb.Position);
            }
        }
        else if (e is InputEventMouseMotion mm && _boxing)
        {
            _boxEnd = mm.Position;
            var tl = new Vector2(Mathf.Min(_boxStart.X, _boxEnd.X), Mathf.Min(_boxStart.Y, _boxEnd.Y));
            var sz = (_boxEnd - _boxStart).Abs();
            _box.Position = tl; _box.Size = sz;
            _box.Visible = sz.Length() > 6f;   // hide until it is actually a drag
        }
    }

    // Left-release: a small movement is a click (pick one unit); a real drag is a
    // marquee (every own unit inside it).
    void FinishSelect(Vector2 end)
    {
        bool additive = Input.IsKeyPressed(Key.Shift);
        if (!additive) _selected.Clear();

        if ((end - _boxStart).Length() <= 6f)
        {
            var u = UnitAtScreen(_boxStart, mine: true);
            if (u != null) _selected.Add(u.Id);
        }
        else
        {
            var tl = new Vector2(Mathf.Min(_boxStart.X, end.X), Mathf.Min(_boxStart.Y, end.Y));
            var br = new Vector2(Mathf.Max(_boxStart.X, end.X), Mathf.Max(_boxStart.Y, end.Y));
            foreach (var kv in _unitNodes)
            {
                var unit = FindUnit(kv.Key);
                if (unit == null || unit.Owner != MyPlayer) continue;
                var sp = _cam.UnprojectPosition(kv.Value.Position + Vector3.Up * 0.6f);
                if (sp.X >= tl.X && sp.X <= br.X && sp.Y >= tl.Y && sp.Y <= br.Y) _selected.Add(kv.Key);
            }
        }
    }

    // Right-click: attack an enemy under the cursor, else march the selection to
    // the ground point.
    void RightClick(Vector2 screen)
    {
        if (_selected.Count == 0) return;
        var ids = new List<int>(_selected).ToArray();

        var enemy = UnitAtScreen(screen, mine: false);
        if (enemy != null)
        {
            _pending.Add(new Command { Owner = MyPlayer, Type = CommandType.Attack, UnitIds = ids, TargetId = enemy.Id });
            return;
        }

        // Clicking on your own rampart mans it — tested against the wall's RAISED
        // body on screen, not the ground behind it, so clicking the wall itself
        // works rather than reading as the tile beyond it.
        var wall = WallUnderCursor(screen);
        if (wall != null)
        {
            _pending.Add(new Command { Owner = MyPlayer, Type = CommandType.Garrison, UnitIds = ids, TargetId = wall.Id });
            return;
        }
        if (GroundTile(screen, out int tx, out int ty))
            _pending.Add(new Command { Owner = MyPlayer, Type = CommandType.Move, UnitIds = ids, X = tx, Y = ty });
    }

    // The friendly rampart whose body sits nearest the cursor — projected at mid
    // height so clicking the wall (not the ground behind it) picks it.
    Building WallUnderCursor(Vector2 screen)
    {
        Building best = null;
        float bestD = 34f * 34f;
        foreach (var b in _sim.Buildings)
        {
            if (b.Owner != MyPlayer || !b.Alive ||
                (b.Type != BuildingType.Wall && b.Type != BuildingType.Gatehouse)) continue;
            var mid = new Vector3(b.X + 0.5f, WallWalkY, b.Y + 0.5f);
            float d = _cam.UnprojectPosition(mid).DistanceSquaredTo(screen);
            if (d < bestD) { bestD = d; best = b; }
        }
        return best;
    }

    Building BuildingById(int id)
    {
        foreach (var b in _sim.Buildings) if (b.Id == id) return b;
        return null;
    }

    // The unit whose model sits nearest the cursor, of the wanted side, within a
    // small pixel radius. Projection-based, so no colliders are needed.
    Unit UnitAtScreen(Vector2 screen, bool mine)
    {
        Unit best = null;
        float bestD = 26f * 26f;
        foreach (var kv in _unitNodes)
        {
            var u = FindUnit(kv.Key);
            if (u == null) continue;
            if (mine ? u.Owner != MyPlayer : u.Owner == MyPlayer) continue;
            var sp = _cam.UnprojectPosition(kv.Value.Position + Vector3.Up * 0.6f);
            float d = sp.DistanceSquaredTo(screen);
            if (d < bestD) { bestD = d; best = u; }
        }
        return best;
    }

    // Where the cursor ray meets the ground, as a whole sim tile.
    bool GroundTile(Vector2 screen, out int tx, out int ty)
    {
        tx = ty = 0;
        var o = _cam.ProjectRayOrigin(screen);
        var n = _cam.ProjectRayNormal(screen);
        if (Mathf.Abs(n.Y) < 1e-4f) return false;
        float t = -o.Y / n.Y;
        if (t < 0) return false;
        var p = o + n * t;
        tx = Mathf.RoundToInt(p.X);
        ty = Mathf.RoundToInt(p.Z);
        return true;
    }

    Unit FindUnit(int id)
    {
        foreach (var u in _sim.Units) if (u.Id == id) return u;
        return null;
    }

    // A ring under each selected unit; created and freed as the selection changes.
    void UpdateRings()
    {
        foreach (var id in _selected)
        {
            if (!_unitNodes.TryGetValue(id, out var node)) continue;
            if (!_rings.TryGetValue(id, out var ring))
            {
                var u = FindUnit(id);
                ring = new MeshInstance3D { Mesh = _ringMesh, MaterialOverride = u != null && u.Owner == MyPlayer ? _ringMine : _ringEnemy };
                AddChild(ring);
                _rings[id] = ring;
            }
            ring.Position = node.Position + new Vector3(0, 0.06f, 0);
        }
        // Drop rings for anything no longer selected or gone.
        var stale = new List<int>();
        foreach (var kv in _rings)
            if (!_selected.Contains(kv.Key) || !_unitNodes.ContainsKey(kv.Key)) stale.Add(kv.Key);
        foreach (var id in stale) { _rings[id].QueueFree(); _rings.Remove(id); if (!_unitNodes.ContainsKey(id)) _selected.Remove(id); }
    }

    void UpdateCamera()
    {
        if (_cam == null) return;
        var offset = new Vector3(
            Mathf.Sin(_camYaw) * Mathf.Cos(_camPitch),
            Mathf.Sin(_camPitch),
            Mathf.Cos(_camYaw) * Mathf.Cos(_camPitch)) * _camDist;
        _cam.Position = _camTarget + offset;
        _cam.LookAt(_camTarget, Vector3.Up);
    }
}
