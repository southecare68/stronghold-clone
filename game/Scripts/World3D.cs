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

    Simulation _sim;
    Camera3D _cam;
    double _accum;
    float _alpha;

    readonly Dictionary<int, Node3D> _unitNodes = new();
    readonly Dictionary<int, Node3D> _buildingNodes = new();
    readonly Dictionary<int, Vector2> _prevPos = new();
    readonly Dictionary<int, float> _yaw = new();

    readonly Dictionary<BuildingType, PackedScene> _bldModel = new();
    readonly Dictionary<BuildingType, float> _bldScale = new();
    PackedScene _mSoldier, _mPeasant, _mRunner, _mBrute, _mArcher;

    // Camera orbit around a target on the ground.
    Vector3 _camTarget;
    float _camDist = 16f, _camYaw = 0.6f, _camPitch = 0.85f;   // radians

    public override void _Ready()
    {
        _sim = new Simulation(Sim.TileMap.Skirmish(MapSize));
        Skirmish.Setup(_sim, MapSize);

        LoadModels();
        SetupEnvironment();
        SetupGround();

        _cam = new Camera3D { Current = true };
        AddChild(_cam);
        // Aim at the starting party (soldiers spawn a few tiles east of the keep).
        _camTarget = new Vector3(Skirmish.West(MapSize) + 4, 0, MapSize / 2f);
        UpdateCamera();

        SnapshotPositions();
        GD.Print("[3d] world ready — ", _sim.Units.Count, " units, ", _sim.Buildings.Count, " buildings");
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
        B(BuildingType.Wall,          "Castle/SM_Bld_Castle_Battlements_01", 0.5f);
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

    // ---- per-frame ---------------------------------------------------------

    public override void _Process(double delta)
    {
        _accum += delta;
        int ran = 0;
        while (_accum >= Step && ran < MaxTicksPerFrame)
        {
            SnapshotPositions();
            _sim.Tick(System.Array.Empty<Command>());
            _accum -= Step;
            ran++;
        }
        _alpha = (float)Mathf.Clamp(_accum / Step, 0.0, 1.0);

        SyncUnits();
        SyncBuildings();
        CameraInput(delta);
    }

    void SnapshotPositions()
    {
        foreach (var u in _sim.Units) _prevPos[u.Id] = SimXZ(u);
    }

    static Vector2 SimXZ(Unit u) => new Vector2(u.X / (float)Fixed.One, u.Y / (float)Fixed.One);

    void SyncUnits()
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
            }

            var now = SimXZ(u);
            var prev = _prevPos.TryGetValue(u.Id, out var p) ? p : now;
            var draw = prev.Lerp(now, _alpha);
            node.Position = new Vector3(draw.X, 0, draw.Y);

            // Face the way it is moving; hold the last heading when standing.
            var vel = now - prev;
            if (vel.LengthSquared() > 1e-5f)
                _yaw[u.Id] = Mathf.Atan2(vel.X, vel.Y);
            node.Rotation = new Vector3(0, _yaw.TryGetValue(u.Id, out var y) ? y : 0f, 0);
        }
        Prune(_unitNodes, live);
    }

    void SyncBuildings()
    {
        var live = new HashSet<int>();
        foreach (var b in _sim.Buildings)
        {
            live.Add(b.Id);
            if (!_buildingNodes.TryGetValue(b.Id, out var node))
            {
                if (!_bldModel.TryGetValue(b.Type, out var scene) || scene == null) continue;
                node = scene.Instantiate<Node3D>();
                node.Scale = Vector3.One * _bldScale[b.Type];
                // Centre the model on the footprint.
                node.Position = new Vector3(b.X + b.W / 2f, 0, b.Y + b.H / 2f);
                AddChild(node);
                _buildingNodes[b.Id] = node;
            }
        }
        Prune(_buildingNodes, live);
    }

    static void Prune(Dictionary<int, Node3D> nodes, HashSet<int> live)
    {
        var gone = new List<int>();
        foreach (var kv in nodes) if (!live.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone) { nodes[id].QueueFree(); nodes.Remove(id); }
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
        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp)   { _camDist = Mathf.Max(8f, _camDist * 0.9f); UpdateCamera(); }
            if (mb.ButtonIndex == MouseButton.WheelDown) { _camDist = Mathf.Min(90f, _camDist * 1.1f); UpdateCamera(); }
        }
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
