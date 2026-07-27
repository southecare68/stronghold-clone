// World3D.cs — the 3D renderer.
//
// The deterministic simulation (game/Sim) is reused untouched; this turns its
// state into a 3D scene each frame with the real POLYGON models — no baking, no
// sprites. Milestone 1: a local skirmish, a tilted camera, ground, and every
// unit and building as its actual model at the interpolated sim position, facing
// where it moves. Input, animation, height and netcode come in later milestones.

using Godot;
using Sim;
using Netcode;
using Audio;
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
    PackedScene _keepWall, _keepTurret;   // the keep is composed from castle pieces

    // The keep is a flat-topped fighting platform (Stronghold-style): troops climb a
    // stair onto its crenellated roof and fire from it. These carry the roof height,
    // each keep's stair foot/crown for the climb, and the spots troops stand at.
    const float KeepRoofY = 2.6f;
    const float KeepBldMaxH = 1.9f;   // cap other buildings below the keep's roofline
    readonly Dictionary<int, (Vector3 Base, Vector3 Top)> _keepStair = new();   // keep id -> stair
    readonly Dictionary<int, int> _keepIdx = new();                            // unit id -> roof-spot index
    static readonly Vector3[] RoofOffsets =
    {
        new(0, 0, 1.05f), new(1.05f, 0, 0), new(0, 0, -1.05f), new(-1.05f, 0, 0),   // edge posts, facing out
        new(0.85f, 0, 0.85f), new(-0.85f, 0, 0.85f), new(0.85f, 0, -0.85f), new(-0.85f, 0, -0.85f),
        new(0, 0, 0),   // the last-stand spot, dead centre — the lord's post
    };
    readonly HashSet<(int, int)> _wallSet = new();

    // Netcode. The renderer no longer owns a bare Simulation; it drives a lockstep
    // Client (see Lockstep.cs), exactly as the 2D Main did. LOCAL mode runs two
    // in-process clients over a LoopbackTransport so the whole lockstep path — turn
    // exchange, input delay, checksum agreement — runs even in a single window;
    // --host / --join swap in the real socket transport. `_sim` is just the render
    // view onto the client we control (`_me.Sim`).
    Client _me;                 // the client we render and command
    Client _other;             // LOCAL only: the second in-process client
    ITransport _net;
    EnetTransport _enet;       // networked modes only
    string _mode = "LOCAL";
    Simulation _sim;           // == _me.Sim, kept for the rendering code to read

    // --desync-dump: on the first desync, write the DIVERGING tick's full state to
    // a file, so two machines' dumps can be diffed to the exact unit/building/tile.
    // Only while enabled do we keep the ring of recent snapshots it reads from.
    bool _dumpDesync, _dumpDone;
    readonly Dictionary<int, MatchSnapshot> _dumpRing = new();
    const int DumpRingTicks = 12;
    Camera3D _cam;
    double _accum;
    float _alpha;

    readonly Dictionary<int, Node3D> _unitNodes = new();
    readonly Dictionary<int, Node3D> _buildingNodes = new();
    readonly Dictionary<int, Node3D> _nodeNodes = new();   // resource nodes (trees, rock)
    PackedScene _mTree, _mRock;

    // Building selection drives the train panel: click your barracks to open it.
    Building _selectedBuilding;
    Control _trainPanel;
    Label _trainInfo;
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
    int MyPlayer = 1;   // which side we command; 2 when we joined a host
    readonly HashSet<int> _selected = new();
    readonly Dictionary<int, MeshInstance3D> _rings = new();
    Mesh _ringMesh;
    Material _ringMine, _ringEnemy;

    bool _boxing;
    Vector2 _boxStart, _boxEnd;
    ColorRect _box;

    // Build mode. A chosen type places a translucent ghost that follows the cursor,
    // green where it can go and red where it can't; a click issues a Build order
    // through lockstep, and a wall can be dragged out as a straight run. Orders are
    // validated again by the sim, so the ghost is a guide, not the authority.
    BuildingType? _buildType;
    bool _wallDragging;
    Vector2I _wallStart;
    readonly List<MeshInstance3D> _ghosts = new();
    BoxMesh _ghostBox;
    Material _ghostOk, _ghostBad;
    readonly Dictionary<BuildingType, Button> _buildButtons = new();

    // What the player can put down (not the Keep — you start with one). Order sets
    // the palette left to right.
    static readonly BuildingType[] Buildable =
    {
        BuildingType.Wall, BuildingType.Gatehouse, BuildingType.House, BuildingType.Barracks,
        BuildingType.WoodcutterHut, BuildingType.Quarry, BuildingType.Storehouse,
        BuildingType.Farm, BuildingType.Mill, BuildingType.Bakery,
    };

    // HUD: a live status bar over the 3D view. Read-only view of the sim's
    // stockpiles and headcounts, rebuilt each frame — no state of its own.
    readonly Label[] _stat = new Label[StatCount];
    Label _selInfo;
    Label _netInfo;
    const int StatCount = 7;   // wood, stone, food, grain, flour, pop, army

    // Fog of war, drawn from the sim's per-player vision (see Vision.cs). A veil
    // over the ground — near-black where we've never been, a dim haze over ground
    // we've seen but can't see now, clear where a unit or building has eyes right
    // now. Enemy units and unseen enemy buildings are hidden outright. Purely a
    // read of _sim.CanSee / HasExplored; the sim owns the actual visibility.
    MeshInstance3D _fogPlane;
    Image _fogImg;
    ImageTexture _fogTex;
    byte[] _fogBytes;
    static readonly (byte R, byte G, byte B, byte A) FogUnexplored = (6, 8, 11, 236);
    static readonly (byte R, byte G, byte B, byte A) FogExplored = (14, 18, 26, 120);

    // Combat feedback: a floating health bar over a hurt unit, a spark where a
    // blow lands, a tracer for a ranged shot, a puff when a unit dies. All of it
    // is transient candy read from _sim.ShotsThisTick and per-unit hp — nothing is
    // fed back into the sim, so determinism is untouched. Effects in fogged tiles
    // are suppressed, so a fight you cannot see makes no sparks.
    sealed class Bar { public Node3D Root; public MeshInstance3D Fill; public StandardMaterial3D FillMat; }
    readonly Dictionary<int, Bar> _bars = new();
    sealed class Fx { public Node3D Node; public float Age, Life; public System.Action<Fx> Step; }
    readonly List<Fx> _fx = new();
    readonly Dictionary<int, (Vector3 Pos, bool Peasant)> _lastSeen = new();
    QuadMesh _quad;
    BoxMesh _bit;
    StandardMaterial3D _barBgMat, _bitMat;
    const float BarW = 0.9f, BarH = 0.13f, BarLift = 1.05f;

    // Audio: positional SFX (Camera3D is the listener) plus adaptive music. Both
    // observe the sim and never feed back. `_battle` counts down from the last blow
    // heard; while it is running the score is Battle, otherwise Calm.
    Sound3D _sound;
    MusicPlayer _music;
    float _battle;
    const float BattleHold = 5f;   // seconds of quiet before the music stands down

    // Sim-event observation for the economy/structure sounds: the sim emits no
    // events (it must stay render-agnostic and deterministic), so we diff its state
    // tick to tick and infer what happened — a load banked, a unit trained, a wall
    // raised or felled, a gate worked. Run once per advanced tick, seeded from the
    // starting world so nothing fires on launch.
    readonly HashSet<int> _prevUnitIds = new();
    readonly HashSet<int> _prevBuildingIds = new();
    readonly Dictionary<int, Vector3> _prevBuildingWhere = new();
    readonly Dictionary<int, bool> _prevGateOpen = new();
    int _prevStockTotal;

    public override void _Ready()
    {
        SetUpTransport();          // builds the client(s); _sim = _me.Sim
        // The starting world is built identically on every client, before tick 0,
        // like Skirmish.Setup itself. The men-on-walls scaffold is a single-window
        // DEMO convenience — a networked match starts clean (no free walls for the
        // host) and, crucially, a joiner ADOPTS the host's tick-0 snapshot, so any
        // pre-placed state has to survive the snapshot round-trip. Keeping the
        // scaffold to LOCAL sidesteps that and is the correct competitive start.
        bool demo = _mode == "LOCAL";
        foreach (var c in Clients()) { Skirmish.Setup(c.Sim, MapSize); if (demo) ScaffoldWall(c.Sim); }

        LoadModels();
        SetupEnvironment();
        SetupGround();
        SetupFog();
        SetupCombatFx();
        SetupSelectionUi();
        SetupHud();
        SetupBuild();
        SetupTrainPanel();

        // Audio. A current Camera3D is the 3D audio listener, so the SFX player
        // needs no explicit listener. --audio-log prints each voice for headless
        // checks; --mute silences everything.
        _dumpDesync = HasFlag("--desync-dump");
        bool mute = HasFlag("--mute");
        _sound = new Sound3D { LogPlays = HasFlag("--audio-log"), Muted = mute };
        AddChild(_sound);
        _music = new MusicPlayer { Enabled = !mute };
        AddChild(_music);

        _cam = new Camera3D { Current = true };
        AddChild(_cam);
        // Aim at OUR base — the host looks at the west start, a joiner at the east —
        // so each player opens on their own keep rather than a fog bank.
        int baseX = MyPlayer == 2 ? Skirmish.East(MapSize) - 9 : Skirmish.West(MapSize) + 9;
        _camTarget = new Vector3(baseX, 0, MapSize / 2f);
        _camYaw = MyPlayer == 2 ? 0.6f + Mathf.Pi : 0.6f;   // face in off the enemy side
        UpdateCamera();

        // The staircase serves the demo scaffold wall, so it too is LOCAL-only.
        if (demo) BuildStaircase(Skirmish.West(MapSize) + 7, MapSize / 2);

        SnapshotPositions();
        SeedObservation();   // baseline so the starting world fires no sounds
        GD.Print("[3d] world ready — mode ", _mode, ", player ", MyPlayer, ", ",
                 _sim.Units.Count, " units, ", _sim.Buildings.Count, " buildings");
    }

    // A stretch of wall at the base with the starting soldiers already manning it,
    // so men-on-the-walls shows the moment you launch. Sim state, so it is applied
    // to EVERY client identically (walls get ids in the same order on each).
    static void ScaffoldWall(Simulation sim)
    {
        int wy = MapSize / 2, wx = Skirmish.West(MapSize) + 6;
        var walls = new List<Building>();
        for (int i = 0; i < 6; i++) walls.Add(sim.PlaceBuilding(BuildingType.Wall, 1, wx + i, wy));

        var keep = sim.Buildings.Find(b => b.Type == BuildingType.Keep && b.Owner == 1);
        int k = 1;              // spread the rest along the wall, leaving the near tiles clear
        bool lordSet = false;
        foreach (var u in sim.Units)
        {
            if (u.Owner != 1 || u.IsPeasant) continue;
            if (!lordSet && keep != null) { u.GarrisonId = keep.Id; lordSet = true; continue; }  // the lord mans the keep
            if (k >= walls.Count) continue;
            var w = walls[k]; k += 2;
            if (w != null) u.GarrisonId = w.Id;   // no snap — they walk to the stair and climb up
        }
    }

    // Build the lockstep client(s) and the transport under them, mirroring the 2D
    // Main. LOCAL runs both sides in-process over a loopback; --host / --join use
    // the real socket. Afterwards `_sim` points at the client we control.
    void SetUpTransport()
    {
        var (mode, address, port) = ParseCommandLine();
        _mode = mode;

        if (mode == "LOCAL")
        {
            var loop = new LoopbackTransport();
            _net = loop;
            _me = new Client(1, loop, Sim.TileMap.Skirmish(MapSize));
            _other = new Client(2, loop, Sim.TileMap.Skirmish(MapSize));
            loop.Connect(_me);
            loop.Connect(_other);
            MyPlayer = 1;
        }
        else
        {
            _enet = mode == "HOST" ? EnetTransport.Host(port) : EnetTransport.Join(address, port);
            _net = _enet;
            MyPlayer = _enet.PlayerId;
            _me = new Client(MyPlayer, _enet, Sim.TileMap.Skirmish(MapSize));
            _enet.Attach(_me);
        }
        _sim = _me.Sim;
    }

    IEnumerable<Client> Clients()
    {
        yield return _me;
        if (_other != null) yield return _other;
    }

    // Godot swallows its own flags; anything after a bare `--` arrives via
    // GetCmdlineUserArgs. Check both lists. `--host`, `--join=addr[:port]`, or a
    // `--code=` match code; otherwise a single-window LOCAL game.
    static (string Mode, string Address, int Port) ParseCommandLine()
    {
        var args = new List<string>(OS.GetCmdlineUserArgs());
        args.AddRange(OS.GetCmdlineArgs());
        foreach (var arg in args)
        {
            string value = arg.Contains('=') ? arg.Substring(arg.IndexOf('=') + 1) : null;
            if (arg == "--host" || arg.StartsWith("--host="))
                return ("HOST", null, ParsePort(value, EnetTransport.DefaultPort));
            if (arg.StartsWith("--join="))
            {
                string addr = value;
                int p = EnetTransport.DefaultPort, colon = addr?.LastIndexOf(':') ?? -1;
                if (colon > 0) { p = ParsePort(addr.Substring(colon + 1), EnetTransport.DefaultPort); addr = addr.Substring(0, colon); }
                return ("JOIN", addr, p);
            }
            if (arg.StartsWith("--code=") && MatchCode.TryDecode(value, out string ip, out int codePort))
                return ("JOIN", ip, codePort);
        }
        return ("LOCAL", null, EnetTransport.DefaultPort);
    }

    static int ParsePort(string s, int fallback) =>
        int.TryParse(s, out int p) && p > 0 && p < 65536 ? p : fallback;

    // Whether a bare flag was passed (either arg list — Godot splits them at `--`).
    static bool HasFlag(string flag)
    {
        foreach (var a in OS.GetCmdlineUserArgs()) if (a == flag) return true;
        foreach (var a in OS.GetCmdlineArgs()) if (a == flag) return true;
        return false;
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

        _mTree = Load("Environments/SM_Env_Tree_Round_01");
        _mRock = Load("Environments/SM_Env_Rock_Chunk_02");

        // Keep pieces — a central donjon, corner turrets, conical roofs, and a wall
        // body to skirt them, assembled in MakeKeep into a small castle.
        _keepWall   = Load("Castle/SM_Bld_Castle_Wall_01");
        _keepTurret = Load("Castle/SM_Bld_Castle_Wall_Tower_S_01");   // round corner tower
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

    // Terrain colours, by tile type. The ground plane is painted with these; rock
    // also gets raised into relief so the ridge and outcrops have real shape.
    static readonly Color TerrGround = new(0.36f, 0.45f, 0.28f);   // grass
    static readonly Color TerrMarsh  = new(0.33f, 0.34f, 0.21f);   // boggy, browner
    static readonly Color TerrWater  = new(0.17f, 0.30f, 0.45f);   // deep water
    static readonly Color TerrRock   = new(0.44f, 0.42f, 0.39f);   // stone

    void SetupGround()
    {
        var map = _sim.Map;

        // The map, painted a tile at a time onto one texture on the ground plane.
        // Terrain never changes (see TileMap), so this is built once.
        var img = Image.CreateEmpty(MapSize, MapSize, false, Image.Format.Rgba8);
        for (int y = 0; y < MapSize; y++)
            for (int x = 0; x < MapSize; x++)
                img.SetPixel(x, y, ColorFor(map.At(x, y)));
        var tex = ImageTexture.CreateFromImage(img);

        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(MapSize, MapSize) },
            Position = new Vector3(MapSize / 2f, 0, MapSize / 2f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = tex,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,   // crisp tiles
            },
        };
        AddChild(ground);

        // Rock raised into relief — the central ridge and the outcrops become a
        // real barrier rather than a painted one. One MultiMesh, so all the blocks
        // are a single draw. Nothing stands on rock (it's impassable), so height
        // here never fights a unit's footing. Height is a cheap deterministic hash
        // of the tile so the ridge reads as rugged, not a smooth wall.
        var rock = new List<(int, int)>();
        for (int y = 0; y < MapSize; y++)
            for (int x = 0; x < MapSize; x++)
                if (map.At(x, y) == Sim.Terrain.Rock) rock.Add((x, y));

        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new BoxMesh { Size = Vector3.One },
            InstanceCount = rock.Count,
        };
        for (int i = 0; i < rock.Count; i++)
        {
            var (x, y) = rock[i];
            float h = 0.8f + ((x * 7 + y * 13) % 5) * 0.17f;   // 0.80 .. 1.48
            mm.SetInstanceTransform(i, new Transform3D(
                new Basis(Quaternion.Identity).Scaled(new Vector3(0.98f, h, 0.98f)),
                new Vector3(x, h * 0.5f, y)));
        }
        AddChild(new MultiMeshInstance3D
        {
            Multimesh = mm,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.46f, 0.44f, 0.41f) },
        });
    }

    static Color ColorFor(Sim.Terrain t) => t switch
    {
        Sim.Terrain.Water => TerrWater,
        Sim.Terrain.Rock  => TerrRock,
        Sim.Terrain.Marsh => TerrMarsh,
        _                     => TerrGround,
    };

    void SetupFog()
    {
        _fogImg = Image.CreateEmpty(MapSize, MapSize, false, Image.Format.Rgba8);
        _fogTex = ImageTexture.CreateFromImage(_fogImg);
        _fogBytes = new byte[MapSize * MapSize * 4];

        var mat = new StandardMaterial3D
        {
            AlbedoTexture = _fogTex,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,   // crisp tile blocks
            AlbedoColor = Colors.White,
        };
        _fogPlane = new MeshInstance3D
        {
            // A hair above the ground so it veils terrain without z-fighting, and
            // below the ramparts (1.7) so a manned wall still reads through haze.
            Mesh = new PlaneMesh { Size = new Vector2(MapSize, MapSize) },
            Position = new Vector3(MapSize / 2f, 0.06f, MapSize / 2f),
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_fogPlane);
        _fogPlane.Visible = _sim.FogEnabled;
    }

    // Repaint the veil from current vision. Cheap: one buffer fill + one upload.
    void UpdateFog()
    {
        if (!_sim.FogEnabled) { _fogPlane.Visible = false; return; }
        for (int y = 0; y < MapSize; y++)
            for (int x = 0; x < MapSize; x++)
            {
                int o = (y * MapSize + x) * 4;
                (byte R, byte G, byte B, byte A) c =
                    _sim.CanSee(MyPlayer, x, y) ? default :
                    _sim.HasExplored(MyPlayer, x, y) ? FogExplored : FogUnexplored;
                _fogBytes[o] = c.R; _fogBytes[o + 1] = c.G; _fogBytes[o + 2] = c.B; _fogBytes[o + 3] = c.A;
            }
        _fogImg.SetData(MapSize, MapSize, false, Image.Format.Rgba8, _fogBytes);
        _fogTex.Update(_fogImg);
    }

    // ---- combat feedback ---------------------------------------------------

    void SetupCombatFx()
    {
        _quad = new QuadMesh { Size = Vector2.One };
        _barBgMat = BarMat(new Color(0.05f, 0.05f, 0.06f, 0.85f), 1);
        _bit = new BoxMesh { Size = Vector3.One * 0.11f };
        _bitMat = (StandardMaterial3D)Unshaded(Colors.White);
        _bitMat.VertexColorUseAsAlbedo = true;   // let each particle's Color tint the bit
        _bit.Material = _bitMat;
    }

    static StandardMaterial3D BarMat(Color c, int priority) => new()
    {
        AlbedoColor = c,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = true,          // a bar reads even when the body is behind a wall
        RenderPriority = priority,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,   // always face the camera
        BillboardKeepScale = true,
    };

    // A world-space value colour, green through amber to red as it falls.
    static Color HpColor(float f) =>
        f > 0.5f ? new Color(1f - (f - 0.5f) * 2f * 0.15f, 0.8f, 0.2f).Lerp(new Color(0.35f, 0.82f, 0.30f), (f - 0.5f) * 2f)
                 : new Color(0.85f, 0.20f, 0.16f).Lerp(new Color(0.95f, 0.72f, 0.18f), f * 2f);

    // The health bar over a hurt unit. Hidden at full health and when the unit is
    // fogged; freed when the unit heals up or dies.
    void UpdateBar(Unit u, Vector3 pos, bool visible)
    {
        float frac = u.MaxHp > 0 ? Mathf.Clamp(u.Hp / (float)u.MaxHp, 0f, 1f) : 1f;
        if (!visible || !u.Alive || frac >= 0.999f)
        {
            if (_bars.Remove(u.Id, out var gone)) gone.Root.QueueFree();
            return;
        }
        if (!_bars.TryGetValue(u.Id, out var bar))
        {
            var root = new Node3D();
            var bg = new MeshInstance3D { Mesh = _quad, MaterialOverride = _barBgMat };
            bg.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            bg.Scale = new Vector3(BarW, BarH, 1);
            var fillMat = BarMat(Colors.Green, 2);
            var fill = new MeshInstance3D { Mesh = _quad, MaterialOverride = fillMat };
            fill.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            root.AddChild(bg);
            root.AddChild(fill);
            AddChild(root);
            bar = _bars[u.Id] = new Bar { Root = root, Fill = fill, FillMat = fillMat };
        }
        // Both quads billboard to the camera; the fill shrinks about its centre and
        // its higher render priority (with no depth test) keeps it over the ground.
        bar.Root.Position = pos + new Vector3(0, BarLift, 0);
        bar.Fill.Scale = new Vector3(BarW * frac, BarH * 0.68f, 1);
        bar.FillMat.AlbedoColor = HpColor(frac);
    }

    // Drain this tick's blows into sparks and tracers. Called right after each
    // Tick, before the next one clears the list. Fogged impacts are skipped.
    void SpawnShots()
    {
        foreach (var s in _sim.ShotsThisTick)
        {
            int tx = s.ToX >> 16, ty = s.ToY >> 16;
            if (_sim.FogEnabled && !_sim.CanSee(MyPlayer, tx, ty)) continue;
            var to = new Vector3(s.ToX / (float)Fixed.One, 0.7f, s.ToY / (float)Fixed.One);
            var from = new Vector3(s.FromX / (float)Fixed.One, 0.7f, s.FromY / (float)Fixed.One);
            bool ranged = (to - from).LengthSquared() > 1.6f * 1.6f;
            if (ranged) { Tracer(from, to); _sound.Play(Sfx.BowShot, from); _sound.Play(Sfx.ArrowHit, to); }
            else _sound.Play(Sfx.MeleeHit, to);
            Spark(to, new Color(0.9f, 0.25f, 0.2f), 7, 2.6f);                 // blood/impact
            _battle = BattleHold;   // a blow was heard; keep the score on Battle
        }
    }

    // Economy and structure sounds, inferred by diffing the sim tick to tick. Unit
    // deaths are handled in SyncUnits (its interpolation history holds the last
    // position), so this covers the rest: a unit trained, a wall raised or felled,
    // a gate worked, a load banked. Runs once per advanced tick.
    void ObserveEconomy()
    {
        if (_sound == null) return;

        // A unit of ours appeared that wasn't here last tick — a barracks finished
        // one. Only ours; an enemy reinforcement isn't ours to hear.
        foreach (var u in _sim.Units)
            if (!_prevUnitIds.Contains(u.Id) && u.Owner == MyPlayer)
                _sound.Play(Sfx.BuildDone, Aud(u.X / (float)Fixed.One, u.Y / (float)Fixed.One));

        // A building appeared — set down on an audible tile.
        foreach (var b in _sim.Buildings)
            if (!_prevBuildingIds.Contains(b.Id) && Audible(b.CenterX, b.CenterY))
                _sound.Play(Sfx.BuildPlace, Aud(b.CenterX, b.CenterY));

        // A building we knew about is gone — it came down. Its footprint is only
        // in the remembered record now, since it's no longer in the list to ask.
        foreach (var id in _prevBuildingIds)
            if (BuildingById(id) == null && _prevBuildingWhere.TryGetValue(id, out var where)
                && Audible(Mathf.RoundToInt(where.X), Mathf.RoundToInt(where.Z)))
                _sound.Play(Sfx.Collapse, where);

        // A gatehouse changed state. A gate with no previous state was only just
        // built — that's BuildPlace's event, not a gate moving.
        foreach (var b in _sim.Buildings)
            if (b.Type == BuildingType.Gatehouse
                && _prevGateOpen.TryGetValue(b.Id, out bool was) && was != b.Open
                && Audible(b.CenterX, b.CenterY))
                _sound.Play(Sfx.GateMove, Aud(b.CenterX, b.CenterY));

        // A load was banked: our gathered stock (wood/stone/food) rose. Heard at
        // our drop-off, which is where it happened.
        int stock = StockTotal();
        if (stock > _prevStockTotal && _sim.DropOffs.TryGetValue(MyPlayer, out var drop))
            _sound.Play(Sfx.Deposit, Aud(drop.X, drop.Y));

        RollObservation(stock);
    }

    // Record the current world as the baseline for the next diff.
    void SeedObservation() => RollObservation(StockTotal());

    void RollObservation(int stock)
    {
        _prevUnitIds.Clear();
        foreach (var u in _sim.Units) _prevUnitIds.Add(u.Id);
        _prevBuildingIds.Clear();
        _prevGateOpen.Clear();
        _prevBuildingWhere.Clear();
        foreach (var b in _sim.Buildings)
        {
            _prevBuildingIds.Add(b.Id);
            _prevGateOpen[b.Id] = b.Open;
            _prevBuildingWhere[b.Id] = Aud(b.CenterX, b.CenterY);
        }
        _prevStockTotal = stock;
    }

    // The gathered stock we would hear banked — production intermediates (grain,
    // flour) are not deliveries, so they're left out and don't trip Deposit.
    int StockTotal() =>
        _sim.Stockpile(MyPlayer, ResourceType.Wood) +
        _sim.Stockpile(MyPlayer, ResourceType.Stone) +
        _sim.Stockpile(MyPlayer, ResourceType.Food);

    // Should the player HEAR something there? The same rule as seeing it — a sound
    // from a fogged tile would hand back the information the fog exists to withhold.
    bool Audible(int tx, int ty) => !_sim.FogEnabled || _sim.CanSee(MyPlayer, tx, ty);

    // Sim tile coordinates to an audio world position, a little off the ground.
    static Vector3 Aud(float x, float z) => new Vector3(x, 0.5f, z);

    // A one-shot burst of little bits at a point.
    void Spark(Vector3 at, Color col, int count, float speed)
    {
        var p = new CpuParticles3D
        {
            Position = at, Emitting = true, OneShot = true, Explosiveness = 1f,
            Amount = count, Lifetime = 0.5, Mesh = _bit, Color = col,
            Direction = Vector3.Up, Spread = 85f, InitialVelocityMin = speed * 0.6f,
            InitialVelocityMax = speed, Gravity = new Vector3(0, -9f, 0),
            ScaleAmountMin = 0.6f, ScaleAmountMax = 1.2f,
        };
        AddChild(p);
        _fx.Add(new Fx { Node = p, Life = 1.0f });
    }

    // A brief bright streak from shooter to target — an arrow's flight, collapsed
    // to a fading line.
    void Tracer(Vector3 a, Vector3 b)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-3f) return;
        var mat = (StandardMaterial3D)Unshaded(new Color(1f, 0.95f, 0.7f));
        var m = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.05f, len) },
            MaterialOverride = mat,
            Position = (a + b) * 0.5f,
        };
        m.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        var fwd = d / len;
        var right = Vector3.Up.Cross(fwd);
        right = right.LengthSquared() < 1e-6f ? Vector3.Right : right.Normalized();
        m.Basis = new Basis(right, fwd.Cross(right).Normalized(), fwd);
        AddChild(m);
        _fx.Add(new Fx { Node = m, Life = 0.16f, Step = f => mat.AlbedoColor = new Color(1f, 0.95f, 0.7f, 1f - f.Age / f.Life) });
    }

    // Age every transient effect, run its per-frame step, free the expired.
    void UpdateFx(double delta)
    {
        for (int i = _fx.Count - 1; i >= 0; i--)
        {
            var f = _fx[i];
            f.Age += (float)delta;
            f.Step?.Invoke(f);
            if (f.Age >= f.Life)
            {
                f.Node.QueueFree();
                _fx.RemoveAt(i);
            }
        }
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

    // The resource/headcount bar and the selection readout. Each stat is a swatch
    // (so it reads at a glance) plus a label we refresh every frame in UpdateHud.
    static readonly (string Name, Color Swatch)[] StatDefs =
    {
        ("Wood",  new Color(0.62f, 0.44f, 0.24f)),
        ("Stone", new Color(0.60f, 0.62f, 0.66f)),
        ("Food",  new Color(0.86f, 0.66f, 0.24f)),
        ("Grain", new Color(0.80f, 0.72f, 0.34f)),
        ("Flour", new Color(0.88f, 0.86f, 0.80f)),
        ("Pop",   new Color(0.42f, 0.78f, 0.44f)),
        ("Army",  new Color(0.86f, 0.40f, 0.36f)),
    };

    void SetupHud()
    {
        var layer = new CanvasLayer();
        AddChild(layer);

        var bar = new PanelContainer
        {
            OffsetLeft = 12, OffsetTop = 10,
            AnchorLeft = 0, AnchorTop = 0,
        };
        bar.AddThemeStyleboxOverride("panel", Panel(new Color(0.09f, 0.11f, 0.14f, 0.86f)));
        layer.AddChild(bar);

        var margin = new MarginContainer();
        foreach (var s in new[] { "left", "right" }) margin.AddThemeConstantOverride("margin_" + s, 14);
        foreach (var s in new[] { "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + s, 8);
        bar.AddChild(margin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        margin.AddChild(row);

        for (int i = 0; i < StatCount; i++)
        {
            var cell = new HBoxContainer();
            cell.AddThemeConstantOverride("separation", 6);

            var dot = new ColorRect { Color = StatDefs[i].Swatch, CustomMinimumSize = new Vector2(11, 11) };
            dot.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            cell.AddChild(dot);

            var lab = new Label { Text = StatDefs[i].Name + " –" };
            lab.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 0.97f));
            lab.AddThemeFontSizeOverride("font_size", 15);
            _stat[i] = lab;
            cell.AddChild(lab);

            row.AddChild(cell);
        }

        // Selection readout, bottom-left.
        var selPanel = new PanelContainer
        {
            AnchorLeft = 0, AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = 12, OffsetTop = -44, OffsetBottom = -12,
        };
        selPanel.AddThemeStyleboxOverride("panel", Panel(new Color(0.09f, 0.11f, 0.14f, 0.86f)));
        layer.AddChild(selPanel);
        var selMargin = new MarginContainer();
        foreach (var s in new[] { "left", "right" }) selMargin.AddThemeConstantOverride("margin_" + s, 12);
        foreach (var s in new[] { "top", "bottom" }) selMargin.AddThemeConstantOverride("margin_" + s, 6);
        selPanel.AddChild(selMargin);
        _selInfo = new Label { Text = "No selection" };
        _selInfo.AddThemeColorOverride("font_color", new Color(0.82f, 0.86f, 0.92f));
        _selInfo.AddThemeFontSizeOverride("font_size", 14);
        selMargin.AddChild(_selInfo);

        // Net status, top-right: the match mode and whether lockstep is healthy —
        // in sync, stalled waiting on a peer, or desynced.
        var netPanel = new PanelContainer { AnchorLeft = 1, AnchorRight = 1, AnchorTop = 0, OffsetLeft = -220, OffsetRight = -12, OffsetTop = 10 };
        netPanel.AddThemeStyleboxOverride("panel", Panel(new Color(0.09f, 0.11f, 0.14f, 0.86f)));
        layer.AddChild(netPanel);
        var netMargin = new MarginContainer();
        foreach (var s in new[] { "left", "right" }) netMargin.AddThemeConstantOverride("margin_" + s, 12);
        foreach (var s in new[] { "top", "bottom" }) netMargin.AddThemeConstantOverride("margin_" + s, 8);
        netPanel.AddChild(netMargin);
        _netInfo = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _netInfo.AddThemeFontSizeOverride("font_size", 14);
        netMargin.AddChild(_netInfo);
    }

    static StyleBoxFlat Panel(Color bg) => new()
    {
        BgColor = bg,
        CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
        CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
    };

    void UpdateHud()
    {
        int me = MyPlayer;
        int[] res =
        {
            _sim.Stockpile(me, ResourceType.Wood),
            _sim.Stockpile(me, ResourceType.Stone),
            _sim.Stockpile(me, ResourceType.Food),
            _sim.Stockpile(me, ResourceType.Grain),
            _sim.Stockpile(me, ResourceType.Flour),
        };
        for (int i = 0; i < res.Length; i++) _stat[i].Text = $"{StatDefs[i].Name} {res[i]}";

        int idle = _sim.IdlePeasantCount(me);
        _stat[5].Text = $"Pop {_sim.PeasantCount(me)}/{_sim.PopulationCap(me)}" + (idle > 0 ? $" ({idle} idle)" : "");
        _stat[6].Text = $"Army {_sim.ArmySize(me)}";

        _selInfo.Text = _selected.Count == 0 ? "No selection"
            : _selected.Count == 1 ? DescribeUnit(_selected) : $"{_selected.Count} units selected";

        string state; Color tint;
        if (_me.Desync != null)     { state = $"DESYNC @ {_me.Desync.Tick}"; tint = new Color(0.95f, 0.4f, 0.35f); }
        else if (_me.Stalled)       { state = "waiting for peer…";           tint = new Color(0.92f, 0.78f, 0.35f); }
        else                        { state = "in sync";                     tint = new Color(0.5f, 0.8f, 0.55f); }
        _netInfo.Text = $"{_mode}  ·  tick {_sim.TickNumber}  ·  {state}";
        _netInfo.AddThemeColorOverride("font_color", tint);
    }

    // A one-line description of a single selected unit.
    string DescribeUnit(IEnumerable<int> ids)
    {
        foreach (var id in ids)
            foreach (var u in _sim.Units)
                if (u.Id == id && u.Alive)
                {
                    string kind = u.IsPeasant ? "Peasant"
                        : u.DesignId >= 0 && u.DesignId < Skirmish.DesignNames.Length ? Skirmish.DesignNames[u.DesignId]
                        : "Soldier";
                    string where = u.GarrisonId != 0 ? ", on the wall" : "";
                    return $"{kind}  ·  {u.Hp} hp{where}";
                }
        return "No selection";
    }

    // ---- build UI ----------------------------------------------------------

    void SetupBuild()
    {
        // A translucent box per footprint tile, green where it can go, red where it
        // can't. One shared unit cube, scaled and coloured per ghost.
        _ghostBox = new BoxMesh { Size = Vector3.One };
        _ghostOk = Ghost(new Color(0.35f, 0.85f, 0.45f, 0.38f));
        _ghostBad = Ghost(new Color(0.9f, 0.32f, 0.28f, 0.38f));

        // The palette, bottom-centre: one button per buildable type, name over cost.
        var layer = new CanvasLayer();
        AddChild(layer);
        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1, AnchorBottom = 1,
            OffsetTop = -70, OffsetBottom = -12, GrowHorizontal = Control.GrowDirection.Both,
        };
        panel.AddThemeStyleboxOverride("panel", Panel(new Color(0.09f, 0.11f, 0.14f, 0.9f)));
        layer.AddChild(panel);
        var margin = new MarginContainer();
        foreach (var s in new[] { "left", "right" }) margin.AddThemeConstantOverride("margin_" + s, 8);
        foreach (var s in new[] { "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + s, 6);
        panel.AddChild(margin);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 5);
        margin.AddChild(row);

        foreach (var t in Buildable)
        {
            var b = new Button
            {
                ToggleMode = true,
                Text = $"{NameOf(t)}\n{CostText(t)}",
                CustomMinimumSize = new Vector2(78, 0),
                FocusMode = Control.FocusModeEnum.None,   // never steal keyboard from the game
            };
            b.AddThemeFontSizeOverride("font_size", 12);
            var type = t;                              // capture per iteration
            b.Pressed += () => SelectBuild(type);
            row.AddChild(b);
            _buildButtons[t] = b;
        }
    }

    static Material Ghost(Color c) => new StandardMaterial3D
    {
        AlbedoColor = c,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    static string NameOf(BuildingType t) => t switch
    {
        BuildingType.Wall => "Wall", BuildingType.Gatehouse => "Gate", BuildingType.House => "House",
        BuildingType.Barracks => "Barracks", BuildingType.WoodcutterHut => "Woodcutter",
        BuildingType.Quarry => "Quarry", BuildingType.Storehouse => "Store", BuildingType.Farm => "Farm",
        BuildingType.Mill => "Mill", BuildingType.Bakery => "Bakery", _ => t.ToString(),
    };

    // Cost as a compact string: nonzero amounts with a resource initial.
    string CostText(BuildingType t)
    {
        var cost = _sim.CostOf(t);
        string[] tag = { "w", "s", "f", "g" };
        var parts = new List<string>();
        for (int i = 0; i < cost.Count; i++) if (cost[i] > 0) parts.Add($"{cost[i]}{tag[i]}");
        return parts.Count == 0 ? "free" : string.Join(" ", parts);
    }

    // Toggle a build type. Picking the active one (or its button again) leaves build
    // mode; picking another switches to it.
    void SelectBuild(BuildingType t)
    {
        if (_buildType == t) { ExitBuild(); return; }
        _buildType = t;
        _wallDragging = false;
        foreach (var kv in _buildButtons) kv.Value.ButtonPressed = kv.Key == t;
        _sound?.PlayUi(Sfx.Select);
    }

    void ExitBuild()
    {
        _buildType = null;
        _wallDragging = false;
        foreach (var kv in _buildButtons) kv.Value.ButtonPressed = false;
        foreach (var g in _ghosts) g.Visible = false;
    }

    // Left/right clicks while a type is chosen. A wall drags out a straight run;
    // everything else places one per click. Right-click leaves build mode.
    void BuildClick(InputEventMouseButton mb)
    {
        if (mb.ButtonIndex == MouseButton.Right && mb.Pressed) { ExitBuild(); return; }
        if (mb.ButtonIndex != MouseButton.Left) return;
        bool wall = _buildType == BuildingType.Wall;

        if (mb.Pressed)
        {
            if (!GroundTile(mb.Position, out int tx, out int ty)) return;
            if (wall) { _wallDragging = true; _wallStart = new Vector2I(tx, ty); }
            else PlaceOne(_buildType.Value, tx, ty);
        }
        else if (wall && _wallDragging)
        {
            _wallDragging = false;
            if (GroundTile(mb.Position, out int tx, out int ty))
                foreach (var p in WallLine(_wallStart, new Vector2I(tx, ty)))
                    PlaceOne(BuildingType.Wall, p.X, p.Y);
        }
    }

    // Issue a Build for a footprint centred on the cursor tile, if it can go there
    // and be paid for; a refused spot chirps rather than sending a dead order.
    void PlaceOne(BuildingType t, int cx, int cy)
    {
        var (w, h) = _sim.FootprintOf(t);
        int ox = cx - (w - 1) / 2, oy = cy - (h - 1) / 2;
        if (Placeable(t, ox, oy))
            _me.Issue(new Command { Type = CommandType.Build, TargetId = (int)t, X = ox, Y = oy });
        else
            _sound.PlayUi(Sfx.Denied);
    }

    // A straight orthogonal run between two tiles, along the longer axis — the way
    // a wall is dragged out. Inclusive of both ends.
    static IEnumerable<Vector2I> WallLine(Vector2I a, Vector2I b)
    {
        int dx = b.X - a.X, dy = b.Y - a.Y;
        if (Mathf.Abs(dx) >= Mathf.Abs(dy))
        {
            int step = dx >= 0 ? 1 : -1;
            for (int x = a.X; x != b.X + step; x += step) yield return new Vector2I(x, a.Y);
        }
        else
        {
            int step = dy >= 0 ? 1 : -1;
            for (int y = a.Y; y != b.Y + step; y += step) yield return new Vector2I(a.X, y);
        }
    }

    // Can this footprint legally sit here AND be afforded AND be on explored ground
    // — the same three gates the Build command applies, so the ghost tells the truth.
    bool Placeable(BuildingType t, int ox, int oy)
    {
        if (!_sim.CanPlace(t, ox, oy)) return false;
        var (w, h) = _sim.FootprintOf(t);
        for (int y = oy; y < oy + h; y++)
            for (int x = ox; x < ox + w; x++)
                if (!_sim.HasExplored(MyPlayer, x, y)) return false;
        var cost = _sim.CostOf(t);
        for (int i = 0; i < cost.Count; i++)
            if (_sim.Stockpile(MyPlayer, (ResourceType)i) < cost[i]) return false;
        return true;
    }

    // The ghost(s) under the cursor, updated each frame while in build mode.
    void UpdateGhost()
    {
        if (_buildType is not BuildingType t) { foreach (var g in _ghosts) g.Visible = false; return; }

        var mouse = GetViewport().GetMousePosition();
        if (!GroundTile(mouse, out int cx, out int cy)) { foreach (var g in _ghosts) g.Visible = false; return; }

        // While dragging a wall, one ghost per tile of the run; otherwise a single
        // footprint under the cursor.
        var tiles = new List<Vector2I>();
        if (t == BuildingType.Wall && _wallDragging)
            tiles.AddRange(WallLine(_wallStart, new Vector2I(cx, cy)));
        else
            tiles.Add(new Vector2I(cx, cy));

        var (w, h) = _sim.FootprintOf(t);
        int i = 0;
        foreach (var c in tiles)
        {
            int ox = c.X - (w - 1) / 2, oy = c.Y - (h - 1) / 2;
            var g = GhostAt(i++);
            g.Visible = true;
            g.Mesh = _ghostBox;
            g.Scale = new Vector3(w * 0.96f, 0.8f, h * 0.96f);
            g.Position = new Vector3(ox + (w - 1) / 2f, 0.4f, oy + (h - 1) / 2f);
            g.MaterialOverride = Placeable(t, ox, oy) ? _ghostOk : _ghostBad;
        }
        for (; i < _ghosts.Count; i++) _ghosts[i].Visible = false;
    }

    MeshInstance3D GhostAt(int i)
    {
        while (_ghosts.Count <= i)
        {
            var g = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(g);
            _ghosts.Add(g);
        }
        return _ghosts[i];
    }

    // ---- per-frame ---------------------------------------------------------

    public override void _Process(double delta)
    {
        // Fixed-timestep lockstep: a tick runs only when every player's input for
        // it is in hand. Each client publishes its turn (InputDelay ahead), then we
        // try to step. A stall — a peer's turn hasn't arrived — holds at the tick
        // boundary rather than banking wall-clock time to fast-forward through later.
        _accum += delta;
        int ran = 0;
        while (_accum >= Step && ran < MaxTicksPerFrame)
        {
            foreach (var c in Clients()) c.SendInput();
            SnapshotPositions();

            bool advanced = _me.TryStep();
            foreach (var c in Clients()) if (c != _me) c.TryStep();
            if (advanced) { SpawnShots(); ObserveEconomy(); if (_dumpDesync) RecordDumpRing(); }   // per-tick: blows, sounds, dump ring

            if (!advanced) { _accum = Step; break; }
            _accum -= Step;
            ran++;
        }
        _alpha = (float)Mathf.Clamp(_accum / Step, 0.0, 1.0);

        SyncUnits(delta);
        SyncBuildings();
        SyncNodes();
        UpdateFog();
        UpdateRings();
        UpdateHud();
        UpdateFx(delta);
        UpdateMusic(delta);
        UpdateGhost();
        UpdateTrainPanel();
        if (_dumpDesync && !_dumpDone && _me.Desync != null) { WriteDesyncDump(_me.Desync); _dumpDone = true; }
        CameraInput(delta);
    }

    void SnapshotPositions()
    {
        foreach (var u in _sim.Units) _prevPos[u.Id] = SimXZ(u);
    }

    // Adaptive score: SpawnShots refreshes _battle whenever a blow is heard. While
    // it runs the music is Battle; when the fighting stops for BattleHold seconds it
    // stands back down to Calm. MusicPlayer cross-fades the change.
    void UpdateMusic(double delta)
    {
        if (_music == null) return;
        if (_battle > 0f) _battle -= (float)delta;
        _music.SetMood(_battle > 0f ? Mood.Battle : Mood.Calm);
    }

    // ---- desync dump -------------------------------------------------------

    // Keep the just-completed tick's full state, so if a peer later reports that
    // this tick diverged we can dump the EXACT state that produced our checksum.
    void RecordDumpRing()
    {
        int k = _sim.TickNumber - 1;
        if (k < 0) return;
        _dumpRing[k] = _me.Sim.Snapshot();
        _dumpRing.Remove(k - DumpRingTicks);
    }

    // On the first desync, write the diverging tick's state to user:// as plain,
    // line-per-entity text. Run with --desync-dump on BOTH machines and diff the
    // two files: identical lines cancel, the first difference is the divergence.
    void WriteDesyncDump(DesyncReport d)
    {
        bool exact = _dumpRing.TryGetValue(d.Tick, out var snap);
        if (!exact) snap = _me.Sim.Snapshot();   // fell out of the ring; current state, flagged below

        string path = $"user://desync-{_mode}-p{MyPlayer}-tick{d.Tick}.txt";
        using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
        if (f == null) { GD.PrintErr("[desync] could not open ", path); return; }
        f.StoreString(DumpText(d, snap, exact));
        GD.Print("[desync] wrote ", ProjectSettings.GlobalizePath(path));
    }

    string DumpText(DesyncReport d, MatchSnapshot s, bool exact)
    {
        var b = new System.Text.StringBuilder();
        void L(string line) => b.Append(line).Append('\n');

        L($"DESYNC @ tick {d.Tick}: local 0x{d.LocalChecksum:X8} != player {d.RemotePlayer} 0x{d.RemoteChecksum:X8}");
        L($"mode {_mode} player {MyPlayer}");
        L(exact ? "# state AT the diverging tick (from the ring)"
                : "# WARNING: diverging tick fell out of the ring — this is the CURRENT state");
        L($"snapshot.tick {s.Tick} checksum 0x{s.Checksum:X8}");
        L($"map.fingerprint 0x{_me.Sim.Map.Fingerprint:X8}");
        L($"nextIds {s.NextUnitId}/{s.NextNodeId}/{s.NextBuildingId}");
        L($"rng 0x{s.RngState:X8}");
        for (int i = 0; i < s.Designs.Length; i++)
        {
            var g = s.Designs[i];
            L($"design {i} {g.Hp}/{g.Damage}/{g.SpeedStat}/{g.RangeStat}/{g.Cooldown}");
        }
        foreach (var u in s.Units)   // id order
            L($"unit {u.Id} o{u.Owner} d{u.DesignId} ({u.X},{u.Y}) t({u.Tx},{u.Ty}) hp{u.Hp}/{u.MaxHp} " +
              $"tgt{u.TargetId}/{u.TargetBuildingId} at{u.AttackTimer} job{(int)u.Job} gn{u.GatherNodeId} " +
              $"c{(int)u.CarryType}:{u.CarryAmount} gt{u.GatherTimer} p{(u.IsPeasant ? 1 : 0)} gar{u.GarrisonId} " +
              $"path{(u.HasPath ? u.Path.Count - u.PathIndex : 0)}");
        foreach (var bld in s.Buildings)   // id order
            L($"bldg {bld.Id} o{bld.Owner} t{(int)bld.Type} ({bld.X},{bld.Y}) {bld.W}x{bld.H} " +
              $"hp{bld.Hp}/{bld.MaxHp} q[{string.Join(",", bld.TrainQueue)}] bt{bld.BuildTimer} " +
              $"open{(bld.Open ? 1 : 0)} wkr{bld.WorkerId}");
        foreach (var n in s.Nodes)   // id order
            L($"node {n.Id} t{(int)n.Type} ({n.X},{n.Y}) amt{n.Amount}");
        foreach (int o in Sorted(s.Stock.Keys))
            L($"stock o{o} [{string.Join(",", s.Stock[o])}]");
        foreach (int o in Sorted(s.DropOffs.Keys))
            L($"drop o{o} ({s.DropOffs[o].X},{s.DropOffs[o].Y})");
        L($"fog {(s.FogEnabled ? 1 : 0)}");
        foreach (int o in Sorted(s.Explored.Keys))
        {
            var w = s.Explored[o];
            int bits = 0; uint h = 0x811c9dc5;
            foreach (uint word in w) { bits += System.Numerics.BitOperations.PopCount(word); h = (h ^ word) * 0x01000193; }
            L($"fog o{o} bits{bits} hash0x{h:X8}");
        }
        return b.ToString();
    }

    static List<int> Sorted(IEnumerable<int> keys)
    {
        var list = new List<int>(keys);
        list.Sort();
        return list;
    }

    static Vector2 SimXZ(Unit u) => new Vector2(u.X / (float)Fixed.One, u.Y / (float)Fixed.One);

    // Give each unit garrisoned on a keep a distinct roof-spot index, so a group
    // spreads around the parapet instead of stacking. Stable order (by id) means the
    // same men take the same posts each frame.
    void AssignKeepRoofSpots()
    {
        _keepIdx.Clear();
        var byKeep = new Dictionary<int, List<int>>();
        foreach (var u in _sim.Units)
        {
            if (u.GarrisonId == 0 || !u.Alive) continue;
            var b = BuildingById(u.GarrisonId);
            if (b == null || b.Type != BuildingType.Keep) continue;
            if (!byKeep.TryGetValue(b.Id, out var list)) byKeep[b.Id] = list = new List<int>();
            list.Add(u.Id);
        }
        foreach (var list in byKeep.Values)
        {
            list.Sort();
            for (int i = 0; i < list.Count; i++) _keepIdx[list[i]] = i;
        }
    }

    void SyncUnits(double delta)
    {
        AssignKeepRoofSpots();
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

            // Fog: an enemy is on screen only while one of ours can actually see its
            // tile. Ours are always drawn. (Buildings persist once explored; a unit
            // does not — it has moved on.)
            if (u.Owner != MyPlayer && _sim.FogEnabled)
            {
                bool seen = _sim.CanSee(MyPlayer, u.X >> 16, u.Y >> 16);
                node.Visible = seen;
                if (!seen)
                {
                    // Out of sight: no body, no bar, and forget it — so if it dies in
                    // the fog there is no tell-tale puff where we last saw it.
                    UpdateBar(u, Vector3.Zero, false);
                    _lastSeen.Remove(u.Id);
                    continue;
                }
            }
            else node.Visible = true;

            var now = SimXZ(u);
            var prev = _prevPos.TryGetValue(u.Id, out var p) ? p : now;
            var draw = prev.Lerp(now, _alpha);
            var vel = now - prev;

            Vector3 pos, face;
            bool walking, attacking = false;

            var wall = u.GarrisonId != 0 ? BuildingById(u.GarrisonId) : null;
            if (wall != null)
            {
                // A wall garrison stands on the walkway; a keep garrison takes a spot
                // on the roof deck and faces outward. Each uses its own stair.
                Vector3 top, sbase, stop, outward = Vector3.Zero;
                if (wall.Type == BuildingType.Keep)
                {
                    var off = RoofOffsets[(_keepIdx.TryGetValue(u.Id, out var ki) ? ki : 0) % RoofOffsets.Length];
                    top = new Vector3(wall.X + (wall.W - 1) / 2f, KeepRoofY, wall.Y + (wall.H - 1) / 2f) + off;
                    outward = new Vector3(off.X, 0, off.Z);
                    var st = _keepStair.TryGetValue(wall.Id, out var ks) ? ks : (top, top);
                    sbase = st.Item1; stop = st.Item2;
                }
                else
                {
                    top = new Vector3(wall.X + (wall.W - 1) / 2f, WallTopY, wall.Y + (wall.H - 1) / 2f);
                    sbase = _stairBase; stop = _stairTop;
                }

                if (_onWall.Contains(u.Id))
                {
                    // Up and stood to. Keep archers facing out; on a wall, hold heading.
                    pos = top; face = outward; walking = false;
                }
                else
                {
                    // March to the stair, up it, and along the top to the spot.
                    if (!_climb.TryGetValue(u.Id, out var cl))
                        cl = _climb[u.Id] = new Climb { Pts = new[] { new Vector3(draw.X, 0, draw.Y), sbase, stop, top } };
                    cl.Dist += (float)delta * ClimbSpeed;
                    pos = SamplePath(cl.Pts, cl.Dist, out face, out bool done);
                    walking = true;
                    if (done) { _onWall.Add(u.Id); _climb.Remove(u.Id); pos = top; walking = false; face = outward; }
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

            UpdateBar(u, pos, node.Visible);
            _lastSeen[u.Id] = (pos, u.IsPeasant);
        }
        Prune(_unitNodes, live);
        foreach (var id in new List<int>(_skel.Keys))
            if (!live.Contains(id)) { _skel.Remove(id); _phase.Remove(id); _climb.Remove(id); _onWall.Remove(id); }

        // A unit that was here last frame and is gone now has died (or, for a
        // peasant, been trained away). Soldiers fall with a puff; drop its bar.
        foreach (var id in new List<int>(_lastSeen.Keys))
        {
            if (live.Contains(id)) continue;
            var (at, peasant) = _lastSeen[id];
            _lastSeen.Remove(id);
            if (_bars.Remove(id, out var b)) b.Root.QueueFree();
            if (!peasant) { Spark(at + Vector3.Up * 0.5f, new Color(0.7f, 0.16f, 0.13f), 16, 3.2f); _sound.Play(Sfx.UnitDeath, at); }
        }
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

    // The keep: a flat-topped stone stronghold (Stronghold-style), not a roofed
    // tower. Four clean faces box the 3x3 footprint up to a crenellated roof deck the
    // troops stand and fight on; a round tower stands at each corner, a gate breaks
    // the front — and that gate is the ONLY way up: there is no outside stair. The
    // garrison walks in through it and climbs unseen inside the walls to the roof.
    // Placed once; a keep never moves.
    Node3D MakeKeep(Building b)
    {
        var root = new Node3D { Position = new Vector3(b.X + (b.W - 1) / 2f, 0, b.Y + (b.H - 1) / 2f) };
        const float d = 1.5f;   // half the 3x3 footprint
        var nw = new Vector3(-d, 0, -d); var ne = new Vector3(d, 0, -d);
        var sw = new Vector3(-d, 0, d);  var se = new Vector3(d, 0, d);

        // A SOLID stone core fills the footprint, so the keep can never be seen
        // through or into — no openings on any side. The textured Wall_01 faces are
        // the outer skin over it, and a single closed door marks the front entrance.
        var core = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2 * d - 0.15f, KeepRoofY, 2 * d - 0.15f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.44f, 0.42f, 0.39f) },
            Position = new Vector3(0, KeepRoofY * 0.5f, 0),
        };
        root.AddChild(core);

        KeepFace(root, nw, ne);   // north (back)
        KeepFace(root, nw, sw);   // west
        KeepFace(root, ne, se);   // east
        // South (front): a solid wall with a stone-framed doorway and a wooden door
        // built proud of it — the marked entrance. Solid core behind, so no opening.
        KeepFace(root, sw, se);
        DoorLeaf(root, new Vector3(0, 0, d));

        // The flat roof deck the garrison stands on.
        var deck = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2 * d, 0.2f, 2 * d) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.48f, 0.44f) },
            Position = new Vector3(0, KeepRoofY - 0.1f, 0),
        };
        deck.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        root.AddChild(deck);

        // Crenellated parapet around the roof edge, and a round tower at each corner.
        Parapet(root, nw, ne); Parapet(root, sw, se); Parapet(root, nw, sw); Parapet(root, ne, se);
        foreach (var c in new[] { nw, ne, sw, se }) RoundTurret(root, c);

        // The hidden internal climb: the gate mouth (on the ground) and the inner
        // floor. From the floor the man rises straight up behind the walls onto the
        // roof — reads as climbing unseen stairs inside.
        _keepStair[b.Id] = (root.Position + new Vector3(0, 0, d), root.Position);

        return root;
    }

    // One tall keep face — a Wall_01 (native 5x5x0.5, length on local X) scaled to
    // span the edge and rise to the roof, turned to run along it.
    void KeepFace(Node3D root, Vector3 a, Vector3 c)
    {
        var seg = c - a;
        float len = seg.Length();
        if (len < 0.05f) return;
        var w = _keepWall.Instantiate<Node3D>();
        w.Scale = new Vector3(len / 5f, KeepRoofY / 5f, 1.0f);
        w.Position = (a + c) * 0.5f;
        w.Rotation = new Vector3(0, Mathf.Atan2(-seg.Z, seg.X), 0);
        root.AddChild(w);
    }

    // A round tower at a keep corner, rising from the ground to a little above the
    // roofline — its own crenellations crown it.
    void RoundTurret(Node3D root, Vector3 at)
    {
        var aabb = ModelAabb(_keepTurret);
        float scale = (KeepRoofY + 0.7f) / Mathf.Max(0.1f, aabb.Size.Y);
        var t = _keepTurret.Instantiate<Node3D>();
        t.Scale = new Vector3(scale * 0.72f, scale, scale * 0.72f);   // slimmer so it frames the corner, not the deck
        t.Position = at - new Vector3(0, aabb.Position.Y * scale, 0);   // seat its base on the ground
        root.AddChild(t);
    }

    // The Battlements strip laid along a roof edge, at deck height.
    void Parapet(Node3D root, Vector3 a, Vector3 c)
    {
        var seg = c - a;
        float len = seg.Length();
        var p = _wallBat.Instantiate<Node3D>();
        p.Scale = new Vector3(len / 5f, 0.3f, 0.7f);
        p.Position = (a + c) * 0.5f + new Vector3(0, KeepRoofY, 0);
        p.Rotation = new Vector3(0, Mathf.Atan2(-seg.Z, seg.X), 0);
        root.AddChild(p);
    }

    // The front entrance: a stone porch standing well proud of the wall with a
    // wooden plank door in it, so it reads as a clear gateway and can't be lost
    // against the wall or hidden by the corner towers. Centred at `at` on the front.
    void DoorLeaf(Node3D root, Vector3 at)
    {
        var stone = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.6f, 0.55f), Roughness = 1f };
        var wood  = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.27f, 0.14f), Roughness = 1f };
        var iron  = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.15f, 0.14f), Roughness = 1f };

        const float w = 0.92f, h = 1.8f, out_ = 0.34f;   // door width, height, how far the porch stands out

        MeshInstance3D Box(Material m, Vector3 size, Vector3 pos)
        {
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m, Position = at + pos };
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            return mi;
        }

        // Porch: two jambs and a lintel, running from the wall face out to `out_`.
        float jz = out_ * 0.5f, jd = out_ + 0.1f;   // jamb centre z and depth
        root.AddChild(Box(stone, new Vector3(0.2f, h + 0.24f, jd), new Vector3(-w / 2 - 0.13f, (h + 0.24f) / 2, jz)));
        root.AddChild(Box(stone, new Vector3(0.2f, h + 0.24f, jd), new Vector3(w / 2 + 0.13f, (h + 0.24f) / 2, jz)));
        root.AddChild(Box(stone, new Vector3(w + 0.46f, 0.22f, jd), new Vector3(0, h + 0.11f, jz)));

        // The wooden door, hung at the front of the porch, with two iron braces.
        root.AddChild(Box(wood, new Vector3(w, h, 0.12f), new Vector3(0, h / 2, out_ + 0.02f)));
        root.AddChild(Box(iron, new Vector3(w + 0.04f, 0.1f, 0.15f), new Vector3(0, h * 0.28f, out_ + 0.05f)));
        root.AddChild(Box(iron, new Vector3(w + 0.04f, 0.1f, 0.15f), new Vector3(0, h * 0.72f, out_ + 0.05f)));
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
                else if (b.Type == BuildingType.Keep)
                {
                    node.QueueFree();          // composed from castle pieces, not one model
                    node = MakeKeep(b);
                }
                else
                {
                    // Size the model to its tile footprint rather than a fixed scale,
                    // and never let it stand taller than the keep — the Synty house
                    // models are big, so a flat 0.5 made them overshoot everything.
                    var a = ModelAabb(scene);
                    float horiz = Mathf.Max(Mathf.Max(a.Size.X, a.Size.Z), 0.1f);
                    float fit = 0.9f * b.W / horiz;             // fill ~90% of the footprint
                    float cap = KeepBldMaxH / Mathf.Max(a.Size.Y, 0.1f);   // stay under the keep
                    node.Scale = Vector3.One * Mathf.Min(fit, cap);
                    // Centre on the footprint. A tile at (x,y) is centred at (x,y),
                    // so a WxH footprint's centre is (x+(W-1)/2, y+(H-1)/2) — not W/2,
                    // which would sit half a tile off (unit positions are tile-centred).
                    node.Position = new Vector3(b.X + (b.W - 1) / 2f, 0, b.Y + (b.H - 1) / 2f);
                }
                AddChild(node);
                _buildingNodes[b.Id] = node;
            }

            // Fog: an enemy building stays once we've laid eyes on its tile — you
            // remember a keep is there even after your scout leaves. Ours always show.
            if (b.Owner != MyPlayer && _sim.FogEnabled)
                node.Visible = _sim.HasExplored(MyPlayer, b.X + (b.W - 1) / 2, b.Y + (b.H - 1) / 2);
        }
        Prune(_buildingNodes, live);
    }

    // Resource nodes as trees (wood/grain) and rock (stone). Shown only on ground
    // we've explored — a forest across the ridge stays hidden until scouted — and
    // pruned as the deposit is worked out. A deterministic yaw and scale jitter
    // keeps a cluster from looking stamped.
    void SyncNodes()
    {
        var live = new HashSet<int>();
        foreach (var n in _sim.NodeList)
        {
            live.Add(n.Id);
            bool seen = !_sim.FogEnabled || _sim.HasExplored(MyPlayer, n.X, n.Y);
            if (!_nodeNodes.TryGetValue(n.Id, out var node))
            {
                if (!seen) continue;
                var scene = n.Type == ResourceType.Stone ? _mRock : _mTree;
                node = scene.Instantiate<Node3D>();
                float jitter = 1f + ((n.X * 13 + n.Y * 7) % 5) * 0.06f;
                float baseS = n.Type == ResourceType.Stone ? 0.5f : (n.Type == ResourceType.Grain ? 0.28f : 0.42f);
                node.Scale = Vector3.One * baseS * jitter;
                node.Rotation = new Vector3(0, ((n.X * 31 + n.Y * 17) % 360) * Mathf.Pi / 180f, 0);
                node.Position = new Vector3(n.X, 0, n.Y);
                AddChild(node);
                _nodeNodes[n.Id] = node;
            }
            node.Visible = seen;
        }
        Prune(_nodeNodes, live);
    }

    // The resource node whose model sits nearest the cursor, within a small radius.
    ResourceNode NodeAtScreen(Vector2 screen)
    {
        ResourceNode best = null;
        float bestD = 26f * 26f;
        foreach (var n in _sim.NodeList)
        {
            if (!_nodeNodes.TryGetValue(n.Id, out var node) || !node.Visible) continue;
            float d = _cam.UnprojectPosition(new Vector3(n.X, 0.5f, n.Y)).DistanceSquaredTo(screen);
            if (d < bestD) { bestD = d; best = n; }
        }
        return best;
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
        // Escape leaves build mode (or, harmlessly, does nothing).
        if (e is InputEventKey k && k.Pressed && k.Keycode == Key.Escape && _buildType != null)
        {
            ExitBuild();
            return;
        }

        // Trackpad pinch. Spreading the fingers (Factor > 1) zooms in and
        // pinching zooms out, matching the OS gesture; same clamp as the wheel.
        if (e is InputEventMagnifyGesture mag)
        {
            _camDist = Mathf.Clamp(_camDist / Mathf.Max(mag.Factor, 0.01f), 6f, 90f);
            UpdateCamera();
            return;
        }

        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)   { _camDist = Mathf.Max(6f, _camDist * 0.9f); UpdateCamera(); }
            if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) { _camDist = Mathf.Min(90f, _camDist * 1.1f); UpdateCamera(); }

            // In build mode the left/right buttons place and cancel instead of
            // selecting and ordering. Wheel-zoom above still works either way.
            if (_buildType != null && (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right))
            {
                BuildClick(mb);
                return;
            }

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
        _selectedBuilding = null;   // a fresh selection closes any open building panel

        if ((end - _boxStart).Length() <= 6f)
        {
            var u = UnitAtScreen(_boxStart, mine: true);
            if (u != null) _selected.Add(u.Id);
            else _selectedBuilding = BuildingAtScreen(_boxStart);   // click a building of ours to inspect it
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
        if (_selected.Count > 0) _sound.PlayUi(Sfx.Select);
    }

    // Right-click: attack an enemy under the cursor, else march the selection to
    // the ground point.
    void RightClick(Vector2 screen)
    {
        if (_selected.Count == 0) return;
        var ids = new List<int>(_selected).ToArray();

        // Orders go through the lockstep client: Issue queues them, and this
        // frame's SendInput publishes them to run InputDelay ticks from now, on
        // every machine at once. Issue stamps the owner, so we don't set it here.
        var enemy = UnitAtScreen(screen, mine: false);
        if (enemy != null)
        {
            _me.Issue(new Command { Type = CommandType.Attack, UnitIds = ids, TargetId = enemy.Id });
            _sound.PlayUi(Sfx.AttackOrder);
            return;
        }

        // Clicking on your own rampart mans it — tested against the wall's RAISED
        // body on screen, not the ground behind it, so clicking the wall itself
        // works rather than reading as the tile beyond it.
        var wall = WallUnderCursor(screen);
        if (wall != null)
        {
            _me.Issue(new Command { Type = CommandType.Garrison, UnitIds = ids, TargetId = wall.Id });
            _sound.PlayUi(Sfx.MoveOrder);
            return;
        }

        // A resource node: put the selected workers on it. A hut/quarry hires its
        // own peasant automatically, so this is the manual "gather here" for spare
        // hands — the sim ignores it for non-peasants.
        var res = NodeAtScreen(screen);
        if (res != null)
        {
            _me.Issue(new Command { Type = CommandType.Gather, UnitIds = ids, TargetId = res.Id });
            _sound.PlayUi(Sfx.MoveOrder);
            return;
        }

        if (GroundTile(screen, out int tx, out int ty))
        {
            _me.Issue(new Command { Type = CommandType.Move, UnitIds = ids, X = tx, Y = ty });
            _sound.PlayUi(Sfx.MoveOrder);
        }
        else _sound.PlayUi(Sfx.Denied);
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

    // One of our footprint buildings under the cursor — walls excluded (those are
    // manned by right-click, not inspected). Tested against the model's mid-height
    // centre (not its ground tile, which the tall body hides behind it), nearest
    // within a generous radius so clicking anywhere on the building picks it.
    Building BuildingAtScreen(Vector2 screen)
    {
        Building best = null;
        float bestD = 90f * 90f;
        foreach (var b in _sim.Buildings)
        {
            if (b.Owner != MyPlayer || !b.Alive || b.Type == BuildingType.Wall) continue;
            // Test a couple of heights up the model, since a click can land low on
            // the body or high on the roof; take the nearer.
            var c = new Vector3(b.X + (b.W - 1) / 2f, 0f, b.Y + (b.H - 1) / 2f);
            float d = Mathf.Min(
                _cam.UnprojectPosition(c + Vector3.Up * 0.6f).DistanceSquaredTo(screen),
                _cam.UnprojectPosition(c + Vector3.Up * 1.6f).DistanceSquaredTo(screen));
            if (d < bestD) { bestD = d; best = b; }
        }
        return best;
    }

    // ---- train panel -------------------------------------------------------

    void SetupTrainPanel()
    {
        var layer = new CanvasLayer();
        AddChild(layer);

        // Centre-left, clear of the build bar and the selection readout.
        _trainPanel = new PanelContainer
        {
            AnchorLeft = 0, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = 12, OffsetTop = -70, Visible = false,
        };
        ((PanelContainer)_trainPanel).AddThemeStyleboxOverride("panel", Panel(new Color(0.09f, 0.11f, 0.14f, 0.9f)));
        layer.AddChild(_trainPanel);

        var margin = new MarginContainer();
        foreach (var s in new[] { "left", "right" }) margin.AddThemeConstantOverride("margin_" + s, 12);
        foreach (var s in new[] { "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + s, 10);
        _trainPanel.AddChild(margin);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        margin.AddChild(col);

        _trainInfo = new Label { Text = "Barracks" };
        _trainInfo.AddThemeColorOverride("font_color", new Color(0.9f, 0.92f, 0.96f));
        _trainInfo.AddThemeFontSizeOverride("font_size", 14);
        col.AddChild(_trainInfo);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 5);
        col.AddChild(row);
        // One button per registered design. All cost the same wood (TrainCost).
        for (int i = 0; i < _sim.DesignList.Count; i++)
        {
            string name = i < Skirmish.DesignNames.Length ? Skirmish.DesignNames[i] : $"Unit {i}";
            var b = new Button { Text = $"{name}\n15w", CustomMinimumSize = new Vector2(70, 0), FocusMode = Control.FocusModeEnum.None };
            b.AddThemeFontSizeOverride("font_size", 12);
            int design = i;
            b.Pressed += () => TrainAt(design);
            row.AddChild(b);
        }
    }

    // Queue one of `design` at the selected barracks. The sim re-checks wood and a
    // spare peasant; a refused order simply queues nothing.
    void TrainAt(int design)
    {
        if (_selectedBuilding == null || _selectedBuilding.Type != BuildingType.Barracks) return;
        _me.Issue(new Command { Type = CommandType.Train, TargetId = _selectedBuilding.Id, X = design });
        _sound.PlayUi(Sfx.Select);
    }

    // Show the panel while a live barracks of ours is selected, with its queue and
    // whether a spare peasant is on hand to fill it.
    void UpdateTrainPanel()
    {
        if (_selectedBuilding != null && BuildingById(_selectedBuilding.Id) == null)
            _selectedBuilding = null;   // it was destroyed out from under us

        bool show = _selectedBuilding != null && _selectedBuilding.Alive
                    && _selectedBuilding.Type == BuildingType.Barracks;
        _trainPanel.Visible = show;
        if (!show) return;

        int queued = _selectedBuilding.TrainQueue.Count;
        int idle = _sim.IdlePeasantCount(MyPlayer);
        _trainInfo.Text = $"Barracks — queue {queued}"
            + (idle > queued ? $"   ({idle - queued} spare)" : "   (no spare peasant)");
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
