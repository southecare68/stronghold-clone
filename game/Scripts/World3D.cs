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
using System;
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
    // Garrison posts on the deck, out on the perimeter ring — clear of the central
    // hall in the middle and the round towers at the corners. Each faces outward.
    static readonly Vector3[] RoofOffsets =
    {
        new(0, 0, 1.2f), new(0, 0, -1.2f), new(1.2f, 0, 0), new(-1.2f, 0, 0),        // wall midpoints
        new(0.72f, 0, 1.2f), new(-0.72f, 0, 1.2f), new(0.72f, 0, -1.2f), new(-0.72f, 0, -1.2f),
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
    readonly Dictionary<int, MeshInstance3D> _carryProp = new();   // the load a peasant hauls, shown in front of it
    readonly Dictionary<int, Node3D> _buildingNodes = new();
    readonly Dictionary<int, int> _turretMask = new();   // a turret's rampart-neighbour bits, to rebuild its spurs
    readonly Dictionary<int, Node3D> _nodeNodes = new();   // resource nodes (trees, rock)
    PackedScene _mTree, _mRock, _mWheat;
    // A grain field's wheat bunches, so they can thin out as the field is reaped.
    readonly Dictionary<int, List<Node3D>> _fieldCrop = new();
    readonly Dictionary<int, int> _fieldPeak = new();      // most grain this field has held

    // Building selection drives the train panel: click your barracks to open it.
    Building _selectedBuilding;
    Control _trainPanel;
    Label _trainInfo;
    // Demolish asks first: a stray Del arms this confirm popup, and only a second
    // Del (or the Demolish button) actually razes. 0 = nothing pending.
    Control _confirmPanel;
    Label _confirmLabel;
    int _demolishId;
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
    // Each Steps building's foot (ground) and top (walkway), set when its node is
    // built — a wall/turret garrison climbs the nearest owned steps to get up.
    readonly Dictionary<int, (Vector3 foot, Vector3 top)> _stepsAccess = new();
    const float ClimbSpeed = 2.6f;   // units per second up the path

    // Idle peasants drift to the fire pit in front of their keep and wait there —
    // a render-only muster (the sim leaves an idle peasant standing where it is).
    readonly Dictionary<int, Vector3> _firePit = new();     // owner -> pit world position
    readonly Dictionary<int, Vector3> _loiterPos = new();   // unit id -> current drift position
    const float LoiterSpeed = 1.9f;                          // units per second, walking to/from the fire
    // Fire pits flicker each frame — render-only, so it may use wall-clock time.
    sealed class FireFx { public MeshInstance3D[] Flames; public OmniLight3D Light; public float Phase; }
    readonly List<FireFx> _fires = new();
    float _fireTime;

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
    // Placement facing, in quarter-turns (R rotates it). Cosmetic — footprints are
    // square, so it never changes which tiles a building occupies, which is why it
    // can live entirely in the renderer. _ghostModel previews the rotated building;
    // _pendingRot carries the choice to the node the sim creates a few ticks later.
    int _ghostRot;
    Node3D _ghostModel;
    BuildingType? _ghostModelType;
    readonly Dictionary<Vector2I, int> _pendingRot = new();

    // What the player can put down (not the Keep — you start with one). Order sets
    // the palette left to right.
    static readonly BuildingType[] Buildable =
    {
        BuildingType.Wall, BuildingType.Gatehouse, BuildingType.Steps, BuildingType.Turret,
        BuildingType.House, BuildingType.Barracks,
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

    // Territory overlay: a purely-visual border drawn along the edges of each
    // player's zone of influence (the union of discs around their buildings, the
    // keep reaching furthest). Render-only — the sim knows nothing of it, so it
    // cannot touch the checksum. Rebuilt only when the building set (or fog)
    // changes. Toggle with T.
    MeshInstance3D _territory;
    ImmediateMesh _territoryMesh;
    bool _showTerritory = true;
    long _terrSig = long.MinValue;
    int _terrTick;
    // My territory as a rectangle in tile coords, so fog can be cleared inside it.
    bool _myTerrValid;
    int _myMinX, _myMinY, _myMaxX, _myMaxY;
    const int TerrMargin = 4;          // tiles of breathing room around the claimed area
    const int TerrResourceReach = 18;  // a camp claims resource nodes this near its buildings (= the sim's work range)
    // One fixed colour per camp, by owner id — a camp keeps its colour whoever is
    // looking (blue is player 1, so it also matches your own selection ring in the
    // usual single-player game). More entries are ready for more than two players.
    static readonly Color[] CampColors =
    {
        new(0.35f, 0.75f, 1f),    // player 1 — blue
        new(1f, 0.45f, 0.35f),    // player 2 — red
        new(0.5f, 0.85f, 0.45f),  // player 3 — green
        new(0.95f, 0.8f, 0.3f),   // player 4 — gold
    };
    static Color TerrColor(int owner) => CampColors[Math.Clamp(owner - 1, 0, CampColors.Length - 1)];

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
        // --ai turns the other side over to the computer for a single-window
        // practice match, in place of the men-on-walls demo. The AI lives inside
        // the sim, so it is enabled on EVERY client's Simulation identically and
        // needs no network traffic — the two loopback sims each run the same bot
        // and stay in lockstep. It replaces the scaffold rather than joining it.
        bool ai = HasFlag("--ai") || FlagValue("--ai") != null;
        var aiLevel = AiLevelArg();
        // --no-fog reveals the whole map. FogEnabled is sim state (it gates orders
        // and is checksummed), so it must be flipped on EVERY client identically or
        // the two would desync — hence it lives in the same per-client setup loop.
        // In a networked match both machines must pass it, like the match seed.
        bool noFog = HasFlag("--no-fog");
        int aiOwner = MyPlayer == 2 ? 1 : 2;
        foreach (var c in Clients())
        {
            Skirmish.Setup(c.Sim, MapSize);
            if (noFog) c.Sim.FogEnabled = false;
            if (ai) c.Sim.EnableAi(aiOwner, aiLevel);
        }

        LoadModels();
        SetupEnvironment();
        SetupGround();
        SetupFog();
        SetupTerritory();
        SetupCombatFx();
        SetupSelectionUi();
        SetupHud();
        SetupBuild();
        SetupTrainPanel();
        SetupConfirmPanel();

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

        SnapshotPositions();
        SeedObservation();   // baseline so the starting world fires no sounds
        GD.Print("[3d] world ready — mode ", _mode, ", player ", MyPlayer,
                 ai ? ", vs " + aiLevel + " AI" : "", ", ",
                 _sim.Units.Count, " units, ", _sim.Buildings.Count, " buildings");
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

    // The value of a `--flag=value` argument, or null if it was not passed.
    static string FlagValue(string flag)
    {
        string pre = flag + "=";
        foreach (var a in OS.GetCmdlineUserArgs()) if (a.StartsWith(pre)) return a.Substring(pre.Length);
        foreach (var a in OS.GetCmdlineArgs()) if (a.StartsWith(pre)) return a.Substring(pre.Length);
        return null;
    }

    // Difficulty from `--ai`, `--ai=easy|normal|hard`. Unknown values fall to Normal.
    static Sim.AiLevel AiLevelArg() => (FlagValue("--ai") ?? "").ToLower() switch
    {
        "easy"  => Sim.AiLevel.Easy,
        "hard"  => Sim.AiLevel.Hard,
        _       => Sim.AiLevel.Normal,
    };

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

    // Scale a building model to fill ~90% of its (square) footprint, but never let
    // it stand taller than the keep — the Synty house models are big. Shared by the
    // placed building and its ghost preview so they match.
    static float BuildingScale(PackedScene scene, int footTiles)
    {
        var a = ModelAabb(scene);
        float horiz = Mathf.Max(Mathf.Max(a.Size.X, a.Size.Z), 0.1f);
        float fit = 0.9f * footTiles / horiz;
        float cap = KeepBldMaxH / Mathf.Max(a.Size.Y, 0.1f);
        return Mathf.Min(fit, cap);
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
        _mWheat = Load("Props/SM_Prop_Wheat_Bunch_01");   // standing crop for grain fields

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
        _fogPlane.Visible = true;   // re-show it when fog is toggled back ON (the F key)
        for (int y = 0; y < MapSize; y++)
            for (int x = 0; x < MapSize; x++)
            {
                int o = (y * MapSize + x) * 4;
                // Your own home zone is never veiled — you have standing awareness of
                // the land you hold and a band around it, so it reads as in the clear.
                (byte R, byte G, byte B, byte A) c =
                    InMyReveal(x, y) || _sim.CanSee(MyPlayer, x, y) ? default :
                    _sim.HasExplored(MyPlayer, x, y) ? FogExplored : FogUnexplored;
                _fogBytes[o] = c.R; _fogBytes[o + 1] = c.G; _fogBytes[o + 2] = c.B; _fogBytes[o + 3] = c.A;
            }
        _fogImg.SetData(MapSize, MapSize, false, Image.Format.Rgba8, _fogBytes);
        _fogTex.Update(_fogImg);
    }

    // ---- territory overlay -------------------------------------------------
    //
    // A player's territory is the axis-aligned rectangle that bounds their
    // buildings, with a little margin — so it reads as a clean square/rectangle of
    // straight lines, not an organic blob. Drawn in the owner's colour. Purely
    // visual, computed here from the building set; the sim knows nothing of it.

    void SetupTerritory()
    {
        _territoryMesh = new ImmediateMesh();
        _territory = new MeshInstance3D
        {
            Mesh = _territoryMesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // flat ribbon — draw both faces
            },
        };
        AddChild(_territory);
    }

    void UpdateTerritory()
    {
        _territory.Visible = _showTerritory;
        // Rebuild only when the building set (or explored ground) changes; my
        // territory rectangle drives the fog reveal, so keep it current even hidden.
        long sig = 0;
        foreach (var b in _sim.Buildings)
            if (b.Alive) sig = sig * 1000003 + (((long)b.Id << 3) | (uint)b.Owner) * 131 + b.CenterX * 719 + b.CenterY;
        bool periodic = (++_terrTick % 30) == 0;   // catch fog reveals without a per-tick scan
        if (sig == _terrSig && !periodic) return;
        _terrSig = sig;
        RebuildTerritory();
    }

    void RebuildTerritory()
    {
        bool fog = _sim.FogEnabled;
        // Bounding box of each owner's known buildings (footprint corners).
        var box = new SortedDictionary<int, int[]>();   // owner -> {minX,minY,maxX,maxY}, owner order
        foreach (var b in _sim.Buildings)
        {
            if (!b.Alive) continue;
            // An enemy box may only be drawn from buildings you have scouted, or it
            // would betray unseen ones — the maphack the fog exists to prevent.
            if (fog && b.Owner != MyPlayer && !_sim.HasExplored(MyPlayer, b.CenterX, b.CenterY)) continue;
            int lx = b.X, ly = b.Y, hx = b.X + b.W - 1, hy = b.Y + b.H - 1;
            if (box.TryGetValue(b.Owner, out var r))
            { r[0] = Math.Min(r[0], lx); r[1] = Math.Min(r[1], ly); r[2] = Math.Max(r[2], hx); r[3] = Math.Max(r[3], hy); }
            else box[b.Owner] = new[] { lx, ly, hx, hy };
        }

        // Then swallow the home resource patches: any node a camp's building can
        // reach (the sim's own work range) belongs to that camp, so the border ends
        // up OUTSIDE its wood and stone — a fairly large holding, not a tight ring
        // around the keep. The contested mid-map deposits sit far past any base's
        // buildings, so neither camp claims them until someone builds out to them.
        foreach (var n in _sim.NodeList)
        {
            if (n.Amount <= 0) continue;
            foreach (var b in _sim.Buildings)
            {
                if (!b.Alive || !box.ContainsKey(b.Owner)) continue;
                if (fog && b.Owner != MyPlayer && !_sim.HasExplored(MyPlayer, b.CenterX, b.CenterY)) continue;
                int dx = n.X - b.CenterX, dy = n.Y - b.CenterY;
                if (dx * dx + dy * dy > TerrResourceReach * TerrResourceReach) continue;
                var r = box[b.Owner];
                r[0] = Math.Min(r[0], n.X); r[1] = Math.Min(r[1], n.Y);
                r[2] = Math.Max(r[2], n.X); r[3] = Math.Max(r[3], n.Y);
            }
        }

        _myTerrValid = false;
        _territoryMesh.ClearSurfaces();
        if (box.Count == 0) return;   // nothing owned — an empty surface would warn

        const float yH = 0.14f;
        _territoryMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var kv in box)       // owner order, so it is stable frame to frame
        {
            int owner = kv.Key;
            var r = kv.Value;
            int minX = Math.Max(0, r[0] - TerrMargin), minY = Math.Max(0, r[1] - TerrMargin);
            int maxX = Math.Min(MapSize - 1, r[2] + TerrMargin), maxY = Math.Min(MapSize - 1, r[3] + TerrMargin);
            // Double the claimed rectangle about its own centre — twice as wide and
            // twice as deep, so each camp holds a much larger domain.
            int cx = (minX + maxX) / 2, cy = (minY + maxY) / 2, hw = maxX - minX, hh = maxY - minY;
            minX = Math.Max(0, cx - hw); maxX = Math.Min(MapSize - 1, cx + hw);
            minY = Math.Max(0, cy - hh); maxY = Math.Min(MapSize - 1, cy + hh);
            // Tile (x,y) is centred at world (x,y), so the rectangle's outer edge is
            // half a tile beyond the extreme tile centres.
            float x0 = minX - 0.5f, x1 = maxX + 0.5f, z0 = minY - 0.5f, z1 = maxY + 0.5f;
            Color col = TerrColor(owner);
            AddBorderLine(new Vector3(x0, yH, z0), new Vector3(x1, yH, z0), col);   // top
            AddBorderLine(new Vector3(x0, yH, z1), new Vector3(x1, yH, z1), col);   // bottom
            AddBorderLine(new Vector3(x0, yH, z0), new Vector3(x0, yH, z1), col);   // left
            AddBorderLine(new Vector3(x1, yH, z0), new Vector3(x1, yH, z1), col);   // right
            if (owner == MyPlayer) { _myMinX = minX; _myMinY = minY; _myMaxX = maxX; _myMaxY = maxY; _myTerrValid = true; }
        }
        _territoryMesh.SurfaceEnd();
    }

    // A straight, flat ribbon from a to b in the ground plane — the fat "line" of a
    // border edge. Widened sideways (perpendicular in XZ) so it reads at a distance.
    void AddBorderLine(Vector3 a, Vector3 b, Color col)
    {
        const float w = 0.14f;
        var d = (b - a); d.Y = 0;
        var perp = new Vector3(d.Z, 0, -d.X).Normalized() * w;
        Vector3[] quad = { a - perp, b - perp, b + perp, a - perp, b + perp, a + perp };
        foreach (var v in quad) { _territoryMesh.SurfaceSetColor(col); _territoryMesh.SurfaceAddVertex(v); }
    }

    // Is this tile within my fog-free home zone? That is the territory rectangle
    // plus a margin, so the fog begins WELL OUTSIDE the border — a band of open
    // ground around the holding, not fog right up against the line. (The border
    // itself is drawn from the un-expanded rectangle, so it stays put.)
    const int FogRevealMargin = 6;   // tiles of cleared ground beyond the territory border
    bool InMyReveal(int x, int y) =>
        _myTerrValid && x >= _myMinX - FogRevealMargin && x <= _myMaxX + FogRevealMargin
                     && y >= _myMinY - FogRevealMargin && y <= _myMaxY + FogRevealMargin;

    // Inside my territory rectangle itself (the border) — where I may build even on
    // ground my units have not scouted. Matches the sim's HomeRect, so the build
    // ghost and the actual Build command agree.
    bool InMyTerritoryRect(int x, int y) =>
        _myTerrValid && x >= _myMinX && x <= _myMaxX && y >= _myMinY && y <= _myMaxY;

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
        // Drop a demolish prompt whose target has vanished (razed by other means).
        if (_demolishId != 0 && BuildingById(_demolishId) == null) CancelDemolish();

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

        _selInfo.Text =
              _selected.Count == 1 ? DescribeUnit(_selected)
            : _selected.Count > 1 ? $"{_selected.Count} units selected"
            : _selectedBuilding != null && _selectedBuilding.Alive ? DescribeBuilding(_selectedBuilding)
            : "No selection";

        string state; Color tint;
        if (_me.Desync != null)     { state = $"DESYNC @ {_me.Desync.Tick}"; tint = new Color(0.95f, 0.4f, 0.35f); }
        else if (_me.Stalled)       { state = "waiting for peer…";           tint = new Color(0.92f, 0.78f, 0.35f); }
        else                        { state = "in sync";                     tint = new Color(0.5f, 0.8f, 0.55f); }
        _netInfo.Text = $"{_mode}  ·  tick {_sim.TickNumber}  ·  {state}";
        _netInfo.AddThemeColorOverride("font_color", tint);
    }

    // A one-line description of a selected building. The keep cannot be razed; any
    // other shows what the [Del] key would reclaim, so the demolish refund is
    // discoverable rather than hidden.
    string DescribeBuilding(Building b)
    {
        string head = $"{NameOf(b.Type)}  ·  {b.Hp} hp";
        if (b.Type == BuildingType.Keep) return head;
        string[] tag = { "w", "s", "f", "g" };
        var r = _sim.RefundOf(b.Type);
        var parts = new List<string>();
        for (int i = 0; i < r.Length; i++) if (r[i] > 0) parts.Add($"{r[i]}{tag[i]}");
        string refund = parts.Count == 0 ? "" : "  +" + string.Join(" ", parts);
        return $"{head}  ·  [Del] demolish{refund}";
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
        BuildingType.Mill => "Mill", BuildingType.Bakery => "Bakery",
        BuildingType.Steps => "Steps", BuildingType.Turret => "Turret", _ => t.ToString(),
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
        _ghostRot = 0;                 // each new pick starts facing the default way
        foreach (var kv in _buildButtons) kv.Value.ButtonPressed = kv.Key == t;
        _sound?.PlayUi(Sfx.Select);
    }

    void ExitBuild()
    {
        _buildType = null;
        _wallDragging = false;
        foreach (var kv in _buildButtons) kv.Value.ButtonPressed = false;
        foreach (var g in _ghosts) g.Visible = false;
        if (_ghostModel != null) _ghostModel.Visible = false;
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
        {
            _me.Issue(new Command { Type = CommandType.Build, TargetId = (int)t, X = ox, Y = oy });
            // Remember the facing for the building the sim will raise here in a few
            // ticks; SyncBuildings applies it when the node appears.
            if (_ghostRot != 0) _pendingRot[new Vector2I(ox, oy)] = _ghostRot;
        }
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
        // A turret or gatehouse may replace your own wall segment — that tile counts
        // as free, so it drops straight into a finished wall.
        bool swapWall = (t == BuildingType.Turret || t == BuildingType.Gatehouse)
            && _sim.OwnWallAt(MyPlayer, ox, oy) != null;
        if (!swapWall && !_sim.CanPlace(t, ox, oy)) return false;
        // Explored ground, or your own territory (buildable seen or not) — the same
        // rule the Build command applies, so the ghost tells the truth.
        var (w, h) = _sim.FootprintOf(t);
        for (int y = oy; y < oy + h; y++)
            for (int x = ox; x < ox + w; x++)
                if (!_sim.HasExplored(MyPlayer, x, y) && !InMyTerritoryRect(x, y)) return false;
        var cost = _sim.CostOf(t);
        for (int i = 0; i < cost.Count; i++)
            if (_sim.Stockpile(MyPlayer, (ResourceType)i) < cost[i]) return false;
        return true;
    }

    // The ghost(s) under the cursor, updated each frame while in build mode.
    void UpdateGhost()
    {
        if (_buildType is not BuildingType t)
        { foreach (var g in _ghosts) g.Visible = false; if (_ghostModel != null) _ghostModel.Visible = false; return; }

        var mouse = GetViewport().GetMousePosition();
        if (!GroundTile(mouse, out int cx, out int cy))
        { foreach (var g in _ghosts) g.Visible = false; if (_ghostModel != null) _ghostModel.Visible = false; return; }

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

        UpdateGhostModel(t, cx, cy, w, h);
    }

    // A translucent, rotated copy of the actual building model, so you can see the
    // facing R will give it. Walls orient themselves to their run, so they skip it.
    void UpdateGhostModel(BuildingType t, int cx, int cy, int w, int h)
    {
        bool showModel = t != BuildingType.Wall && !_wallDragging
                         && _bldModel.TryGetValue(t, out var scene) && scene != null;
        if (!showModel) { if (_ghostModel != null) _ghostModel.Visible = false; return; }

        if (_ghostModelType != t)   // rebuild only when the chosen type changes
        {
            _ghostModel?.QueueFree();
            _ghostModel = _bldModel[t].Instantiate<Node3D>();
            foreach (var mi in Descendants<MeshInstance3D>(_ghostModel)) mi.Transparency = 0.5f;
            _ghostModel.Scale = Vector3.One * BuildingScale(_bldModel[t], w);
            AddChild(_ghostModel);
            _ghostModelType = t;
        }
        int ox = cx - (w - 1) / 2, oy = cy - (h - 1) / 2;
        _ghostModel.Visible = true;
        _ghostModel.Position = new Vector3(ox + (w - 1) / 2f, 0.02f, oy + (h - 1) / 2f);
        _ghostModel.Rotation = new Vector3(0, _ghostRot * Mathf.Pi / 2f, 0);
    }

    // Every descendant of a given node type, depth-first.
    static IEnumerable<T> Descendants<T>(Node n) where T : Node
    {
        foreach (var c in n.GetChildren())
        {
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
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
        UpdateTerritory();   // before nodes/fog: my territory rect gates both
        SyncNodes();
        UpdateFog();
        UpdateRings();
        UpdateHud();
        UpdateFx(delta);
        UpdateFires(delta);
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
            bool idle = u.IsPeasant && u.Job == Sim.Job.None && u.GarrisonId == 0;
            if (idle && _firePit.TryGetValue(u.Owner, out var pit))
            {
                // Idle peasant: drift to a slot round the fire pit and wait, facing
                // the fire. Render-only — its sim tile is left where the sim put it.
                var slot = pit + LoiterSlot(u.Id);
                var cur = _loiterPos.TryGetValue(u.Id, out var lp) ? lp : new Vector3(draw.X, 0, draw.Y);
                cur = cur.MoveToward(slot, (float)delta * LoiterSpeed);
                _loiterPos[u.Id] = cur;
                pos = cur;
                var v = slot - cur;
                walking = v.LengthSquared() > 0.02f;
                face = walking ? v : pit - slot;   // arrived: turn to the flames
            }
            else if (wall != null)
            {
                // A garrison climbs to its post: the keep uses its own built-in
                // stair; a wall/gatehouse/turret garrison walks to the nearest owned
                // Steps, up them, and along the walkway. A turret stands one flight
                // higher still, on its open deck.
                Vector3 top, outward = Vector3.Zero;
                Vector3[] path;
                var ground0 = new Vector3(draw.X, 0, draw.Y);
                if (wall.Type == BuildingType.Keep)
                {
                    var off = RoofOffsets[(_keepIdx.TryGetValue(u.Id, out var ki) ? ki : 0) % RoofOffsets.Length];
                    top = new Vector3(wall.X + (wall.W - 1) / 2f, KeepRoofY, wall.Y + (wall.H - 1) / 2f) + off;
                    outward = new Vector3(off.X, 0, off.Z);
                    var st = _keepStair.TryGetValue(wall.Id, out var ks) ? ks : (top, top);
                    path = new[] { ground0, st.Item1, st.Item2, top };
                }
                else
                {
                    float cx = wall.X + (wall.W - 1) / 2f, cz = wall.Y + (wall.H - 1) / 2f;
                    var walk = new Vector3(cx, WallTopY, cz);          // this tile, at walkway height
                    var acc = NearestStepsAccess(wall);
                    if (wall.Type == BuildingType.Turret)
                    {
                        top = new Vector3(cx, TurretStandY, cz);       // up onto the deck
                        path = acc.HasValue
                            ? new[] { ground0, acc.Value.foot, acc.Value.top, walk, top }
                            : new[] { ground0, walk, top };
                    }
                    else
                    {
                        top = walk;
                        path = acc.HasValue
                            ? new[] { ground0, acc.Value.foot, acc.Value.top, top }
                            : new[] { ground0, top };
                    }
                }

                if (_onWall.Contains(u.Id))
                {
                    // Up and stood to. Keep archers facing out; on a wall, hold heading.
                    pos = top; face = outward; walking = false;
                }
                else
                {
                    // March to the steps, up them, and along the top to the spot.
                    if (!_climb.TryGetValue(u.Id, out var cl))
                        cl = _climb[u.Id] = new Climb { Pts = path };
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
                var ground = new Vector3(draw.X, 0, draw.Y);
                if (_loiterPos.TryGetValue(u.Id, out var lp))
                {
                    // Just left the fire pit (got a job) — walk back to the sim path
                    // instead of snapping, then hand back to normal field movement.
                    lp = lp.MoveToward(ground, (float)delta * LoiterSpeed * 1.6f);
                    if (lp.DistanceSquaredTo(ground) < 0.02f) { _loiterPos.Remove(u.Id); pos = ground; face = new Vector3(vel.X, 0, vel.Y); }
                    else { _loiterPos[u.Id] = lp; pos = lp; face = ground - lp; }
                    walking = true;
                }
                else
                {
                    pos = ground;
                    face = new Vector3(vel.X, 0, vel.Y);
                    walking = vel.LengthSquared() > 1e-5f;
                    attacking = !walking && u.TargetId != 0;
                }
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
            UpdateCarry(u, pos, node.Visible);
            _lastSeen[u.Id] = (pos, u.IsPeasant);
        }
        Prune(_unitNodes, live);
        foreach (var id in new List<int>(_carryProp.Keys))
            if (!live.Contains(id)) { _carryProp[id].QueueFree(); _carryProp.Remove(id); }
        foreach (var id in new List<int>(_skel.Keys))
            if (!live.Contains(id)) { _skel.Remove(id); _phase.Remove(id); _climb.Remove(id); _onWall.Remove(id); _loiterPos.Remove(id); }

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

    // The load a peasant is hauling, shown as a small prop held in front of it —
    // a log of wood, a chunk of stone, a sheaf of grain — so you can see the goods
    // move from the deposit to the drop-off, as in the 2D game. Hidden when its
    // hands are empty or it is out of sight.
    void UpdateCarry(Unit u, Vector3 pos, bool visible)
    {
        bool carrying = u.IsPeasant && u.CarryAmount > 0 && visible;
        if (!carrying)
        {
            if (_carryProp.TryGetValue(u.Id, out var hidden)) hidden.Visible = false;
            return;
        }
        if (!_carryProp.TryGetValue(u.Id, out var prop) || prop == null)
        {
            prop = new MeshInstance3D
            {
                Mesh = new BoxMesh(),
                MaterialOverride = new StandardMaterial3D { Roughness = 1f },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(prop);
            _carryProp[u.Id] = prop;
        }
        var (col, size) = CarryLook(u.CarryType);
        ((BoxMesh)prop.Mesh).Size = size;
        ((StandardMaterial3D)prop.MaterialOverride).AlbedoColor = col;
        float yaw = _yaw.TryGetValue(u.Id, out var yy) ? yy : 0f;
        var fwd = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
        prop.Position = pos + new Vector3(0, 0.7f, 0) + fwd * 0.26f;   // held at the chest, out front
        prop.Rotation = new Vector3(0, yaw, 0);
        prop.Visible = true;
    }

    // Colour and shape of a hauled load by kind.
    static (Color, Vector3) CarryLook(ResourceType t) => t switch
    {
        ResourceType.Wood  => (new Color(0.42f, 0.28f, 0.14f), new Vector3(0.5f, 0.16f, 0.16f)),   // a log, borne across
        ResourceType.Stone => (new Color(0.52f, 0.52f, 0.55f), new Vector3(0.28f, 0.24f, 0.28f)),   // a rough chunk
        ResourceType.Grain => (new Color(0.82f, 0.68f, 0.28f), new Vector3(0.30f, 0.30f, 0.24f)),   // a sheaf
        ResourceType.Flour => (new Color(0.86f, 0.83f, 0.76f), new Vector3(0.26f, 0.30f, 0.22f)),   // a sack
        _                  => (new Color(0.60f, 0.42f, 0.22f), new Vector3(0.28f, 0.20f, 0.24f)),   // a basket of bread
    };

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

        // A SOLID stone core fills the footprint out to the walls, so the keep can
        // never be seen through, and the textured Wall_01 faces are a THIN skin flush
        // on its sides — no thick curtain slab standing proud of the body.
        var core = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2 * d - 0.02f, KeepRoofY, 2 * d - 0.02f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.47f, 0.45f, 0.42f) },
            Position = new Vector3(0, KeepRoofY * 0.5f, 0),
        };
        root.AddChild(core);

        // The core's own flat faces are the walls — no separate curtain panels laid
        // over them (those read as slabs standing proud). A door on the front face.
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

        // A clean grey crenellated parapet around the roof edge, flush with the
        // body — grey merlons on a low lip, matching the stone rather than the pack's
        // tan battlement strip standing proud.
        GreyParapet(root, nw, ne); GreyParapet(root, sw, se);
        GreyParapet(root, nw, sw); GreyParapet(root, ne, se);

        // A ROUND tower at each corner, each crowned with a green conical spire and
        // a red flag, around a central hall under a green peaked roof — the fairy-
        // tale castle of keep1.
        foreach (var c in new[] { nw, ne, sw, se }) RoundKeepTower(root, c);
        CentralHall(root);

        // The hidden internal climb: the gate mouth (on the ground) and the inner
        // floor. From the floor the man rises straight up behind the walls onto the
        // roof — reads as climbing unseen stairs inside.
        _keepStair[b.Id] = (root.Position + new Vector3(0, 0, d), root.Position);

        // A fire pit on the ground in front of the gate — the muster where idle
        // peasants gather (see SyncUnits). Kept with the keep so it dies with it.
        var pit = new Vector3(0, 0, d + 1.8f);
        FirePit(root, pit);
        _firePit[b.Owner] = root.Position + pit;

        return root;
    }

    // A little fire pit: a ring of stones round crossed logs and orange flames, with
    // a warm glow. Purely decorative — the muster point for idle peasants.
    void FirePit(Node3D root, Vector3 at)
    {
        var stone = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.29f, 0.27f), Roughness = 1f };
        var log = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.19f, 0.11f), Roughness = 1f };
        var flame = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1f, 0.55f, 0.15f),
        };

        const int ring = 8;
        const float r = 0.42f;
        for (int i = 0; i < ring; i++)
        {
            float a = i * Mathf.Tau / ring;
            root.AddChild(KeepBox(stone, new Vector3(0.17f, 0.13f, 0.17f), at + new Vector3(Mathf.Cos(a) * r, 0.06f, Mathf.Sin(a) * r)));
        }
        var lA = KeepBox(log, new Vector3(0.55f, 0.1f, 0.14f), at + new Vector3(0, 0.08f, 0));
        var lB = KeepBox(log, new Vector3(0.14f, 0.1f, 0.55f), at + new Vector3(0, 0.11f, 0));
        root.AddChild(lA); root.AddChild(lB);
        var flames = new MeshInstance3D[3];
        for (int i = 0; i < 3; i++)
        {
            flames[i] = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.12f, Height = 0.36f + 0.12f * (i % 2), RadialSegments = 8 },
                MaterialOverride = flame,
                Position = at + new Vector3((i - 1) * 0.12f, 0.26f, 0),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            root.AddChild(flames[i]);
        }
        var light = new OmniLight3D
        {
            Position = at + new Vector3(0, 0.5f, 0),
            LightColor = new Color(1f, 0.62f, 0.28f),
            OmniRange = 3.6f, LightEnergy = 1.5f,
        };
        root.AddChild(light);
        _fires.Add(new FireFx { Flames = flames, Light = light, Phase = _fires.Count * 1.7f });
    }

    // Flicker every live fire pit — flame height and the warm glow, from a couple of
    // out-of-step waves so it reads as fire, not a pulse. Prunes pits whose keep fell.
    void UpdateFires(double delta)
    {
        _fireTime += (float)delta;
        for (int i = _fires.Count - 1; i >= 0; i--)
        {
            var f = _fires[i];
            if (!GodotObject.IsInstanceValid(f.Light)) { _fires.RemoveAt(i); continue; }
            float t = _fireTime + f.Phase;
            float lf = 0.6f * Mathf.Sin(t * 11f) + 0.4f * Mathf.Sin(t * 19f + 1.3f);   // ~[-1,1]
            f.Light.LightEnergy = 1.4f + 0.5f * lf;
            for (int j = 0; j < f.Flames.Length; j++)
            {
                var fl = f.Flames[j];
                if (!GodotObject.IsInstanceValid(fl)) continue;
                float ph = t * 13f + j * 2.1f;
                float h = 1f + 0.24f * Mathf.Sin(ph) + 0.1f * Mathf.Sin(ph * 1.7f + 0.5f);
                fl.Scale = new Vector3(1f - 0.06f * (h - 1f), h, 1f - 0.06f * (h - 1f));   // taller = a touch thinner
            }
        }
    }

    // A stable loitering slot around the fire pit, spread by unit id so the idle
    // peasants ring the fire rather than stack up.
    static Vector3 LoiterSlot(int id)
    {
        float a = id * 2.3999632f;                 // golden angle — an even spread
        float rad = 0.75f + (id % 3) * 0.32f;
        return new Vector3(Mathf.Cos(a) * rad, 0, Mathf.Sin(a) * rad);
    }

    // A round corner tower with a green conical spire and a red flag on top. Built
    // from a plain CYLINDER, not the pack's wall-tower model — that model ships with
    // wall stubs attached, which is what made the stone run out past the corners.
    // The four flat curtain walls now meet a clean round turret at each corner.
    void RoundKeepTower(Node3D root, Vector3 at)
    {
        float towerH = KeepRoofY + 1.0f;
        const float r = 0.5f;
        var stone = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.48f, 0.44f), Roughness = 1f };
        root.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = towerH, RadialSegments = 16 },
            MaterialOverride = stone,
            Position = at + new Vector3(0, towerH / 2f, 0),
        });

        // Conical spire — a cone (a cylinder tapered to a point), roof-green.
        var green = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.41f, 0.33f), Roughness = 1f };
        const float coneH = 1.2f;
        root.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = r + 0.1f, Height = coneH, RadialSegments = 16 },
            MaterialOverride = green,
            Position = at + new Vector3(0, towerH + coneH / 2f, 0),
        });
        KeepFlag(root, at + new Vector3(0, towerH + coneH, 0));
    }

    // A little red flag on a pole, flown from a spire top.
    void KeepFlag(Node3D root, Vector3 at)
    {
        var pole = new StandardMaterial3D { AlbedoColor = new Color(0.26f, 0.2f, 0.14f), Roughness = 1f };
        var red  = new StandardMaterial3D { AlbedoColor = new Color(0.68f, 0.11f, 0.10f), Roughness = 1f };
        const float poleH = 0.75f;
        root.AddChild(KeepBox(pole, new Vector3(0.05f, poleH, 0.05f), at + new Vector3(0, poleH / 2f, 0)));
        root.AddChild(KeepBox(red, new Vector3(0.02f, 0.26f, 0.38f), at + new Vector3(0, poleH - 0.16f, 0.21f)));
    }

    // The central hall (donjon top): a stone block rising from the deck under a
    // green peaked (gabled) roof — the tall middle of keep1.
    void CentralHall(Node3D root)
    {
        var stone = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.48f, 0.44f), Roughness = 1f };
        var green = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.41f, 0.33f), Roughness = 1f };
        const float hw = 0.9f, bodyH = 0.9f, roofH = 0.8f;
        float baseY = KeepRoofY;
        root.AddChild(KeepBox(stone, new Vector3(2 * hw, bodyH, 2 * hw), new Vector3(0, baseY + bodyH / 2f, 0)));
        // Gabled roof — a triangular prism (ridge runs along Z).
        root.AddChild(new MeshInstance3D
        {
            Mesh = new PrismMesh { Size = new Vector3(2 * hw + 0.16f, roofH, 2 * hw + 0.16f) },
            MaterialOverride = green,
            Position = new Vector3(0, baseY + bodyH + roofH / 2f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }


    // A shadow-free box helper for keep detail (hall, flags, cores).
    static MeshInstance3D KeepBox(Material m, Vector3 size, Vector3 pos)
    {
        var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m, Position = pos };
        mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        return mi;
    }

    // A grey crenellated parapet along one deck edge: a low lip with a row of merlons
    // on top, inset a touch so it sits flush with the body, not proud of it.
    void GreyParapet(Node3D root, Vector3 a, Vector3 c)
    {
        var stone = new StandardMaterial3D { AlbedoColor = new Color(0.52f, 0.5f, 0.46f), Roughness = 1f };
        var seg = c - a;
        float len = seg.Length();
        if (len < 0.05f) return;
        var dir = seg / len;
        var mid = (a + c) * 0.5f - mid_inset(a, c);      // flush with the body face
        float yaw = Mathf.Atan2(-seg.Z, seg.X);

        // Low continuous lip.
        var lip = KeepBox(stone, new Vector3(len, 0.16f, 0.1f), mid + new Vector3(0, KeepRoofY + 0.08f, 0));
        lip.Rotation = new Vector3(0, yaw, 0);
        root.AddChild(lip);

        // Merlon teeth: tooth width == gap.
        int teeth = Mathf.Max(2, Mathf.RoundToInt(len / 0.55f));
        float tw = len / (teeth * 2 - 1);
        for (int i = 0; i < teeth; i++)
        {
            var t = KeepBox(stone, new Vector3(tw, 0.24f, 0.1f),
                            mid + dir * (-len / 2 + tw / 2 + i * 2 * tw) + new Vector3(0, KeepRoofY + 0.28f, 0));
            t.Rotation = new Vector3(0, yaw, 0);
            root.AddChild(t);
        }
    }

    // Half-depth pull toward the keep centre, so an edge feature sits flush.
    static Vector3 mid_inset(Vector3 a, Vector3 c)
    {
        var mid = (a + c) * 0.5f;
        return mid.Normalized() * 0.06f;
    }

    // The front entrance: a wooden door sitting flush on the front wall's face — lit
    // and plainly a door, not a dark recessed hollow — with a centre seam and iron
    // braces. It sits right at the wall face, so it neither hides nor stands proud.
    // Centred at `at` on the front.
    void DoorLeaf(Node3D root, Vector3 at)
    {
        var wood = new StandardMaterial3D { AlbedoColor = new Color(0.46f, 0.30f, 0.16f), Roughness = 1f };
        var iron = new StandardMaterial3D { AlbedoColor = new Color(0.18f, 0.17f, 0.16f), Roughness = 1f };
        const float w = 0.86f, h = 1.7f, face = 0.03f;   // the (now thin) front wall's outer face is the edge

        MeshInstance3D Box(Material m, Vector3 size, Vector3 pos)
        {
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m, Position = at + pos };
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            return mi;
        }

        root.AddChild(Box(wood, new Vector3(w, h, 0.1f), new Vector3(0, h / 2, face)));                     // the door
        root.AddChild(Box(iron, new Vector3(0.05f, h, 0.12f), new Vector3(0, h / 2, face + 0.01f)));        // centre seam
        root.AddChild(Box(iron, new Vector3(w + 0.02f, 0.09f, 0.12f), new Vector3(0, h * 0.26f, face + 0.01f)));
        root.AddChild(Box(iron, new Vector3(w + 0.02f, 0.09f, 0.12f), new Vector3(0, h * 0.74f, face + 0.01f)));
    }

    void SyncBuildings()
    {
        // Rampart tiles, so a wall knows which way its run goes.
        _wallSet.Clear();
        foreach (var b in _sim.Buildings)
            if ((b.Type == BuildingType.Wall || b.Type == BuildingType.Gatehouse
                 || b.Type == BuildingType.Turret) && b.Alive)
                _wallSet.Add((b.X, b.Y));

        var live = new HashSet<int>();
        foreach (var b in _sim.Buildings)
        {
            live.Add(b.Id);
            // A turret or gatehouse's connecting spurs depend on which neighbours
            // are ramparts; if that changed since it was built (a wall raised against
            // it after the fact), rebuild it so the join stays flush.
            if ((b.Type == BuildingType.Turret || b.Type == BuildingType.Gatehouse)
                && _buildingNodes.TryGetValue(b.Id, out var tn))
            {
                int mask = TurretMask(b);
                if (!_turretMask.TryGetValue(b.Id, out var old) || old != mask)
                { tn.QueueFree(); _buildingNodes.Remove(b.Id); }
            }
            if (!_buildingNodes.TryGetValue(b.Id, out var node))
            {
                // Composed structures (built from primitives, no single model prefab).
                if (b.Type == BuildingType.Wall) node = MakeWall(b);
                else if (b.Type == BuildingType.Keep) node = MakeKeep(b);
                else if (b.Type == BuildingType.Steps) node = MakeSteps(b);
                else if (b.Type == BuildingType.Turret) { node = MakeTurret(b); _turretMask[b.Id] = TurretMask(b); }
                else if (b.Type == BuildingType.Gatehouse) { node = MakeGate(b); _turretMask[b.Id] = TurretMask(b); }
                else
                {
                    if (!_bldModel.TryGetValue(b.Type, out var scene) || scene == null) continue;
                    node = scene.Instantiate<Node3D>();
                    node.Scale = Vector3.One * BuildingScale(scene, b.W);
                    // Centre on the footprint. A tile at (x,y) is centred at (x,y),
                    // so a WxH footprint's centre is (x+(W-1)/2, y+(H-1)/2) — not W/2,
                    // which would sit half a tile off (unit positions are tile-centred).
                    node.Position = new Vector3(b.X + (b.W - 1) / 2f, 0, b.Y + (b.H - 1) / 2f);
                    // Apply the facing chosen at placement (render-only; footprint is
                    // square, so it never moved which tiles this occupies).
                    if (_pendingRot.TryGetValue(new Vector2I(b.X, b.Y), out int q))
                    {
                        node.Rotation = new Vector3(0, q * Mathf.Pi / 2f, 0);
                        _pendingRot.Remove(new Vector2I(b.X, b.Y));
                    }
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
            bool seen = !_sim.FogEnabled || _sim.HasExplored(MyPlayer, n.X, n.Y) || InMyReveal(n.X, n.Y);
            if (!_nodeNodes.TryGetValue(n.Id, out var node))
            {
                if (!seen) continue;
                if (n.Type == ResourceType.Grain)
                {
                    node = MakeGrainField(n);            // plowed soil + wheat, not a tree
                }
                else
                {
                    var scene = n.Type == ResourceType.Stone ? _mRock : _mTree;
                    node = scene.Instantiate<Node3D>();
                    float jitter = 1f + ((n.X * 13 + n.Y * 7) % 5) * 0.06f;
                    float baseS = n.Type == ResourceType.Stone ? 0.5f : 0.42f;
                    node.Scale = Vector3.One * baseS * jitter;
                    node.Rotation = new Vector3(0, ((n.X * 31 + n.Y * 17) % 360) * Mathf.Pi / 180f, 0);
                    node.Position = new Vector3(n.X, 0, n.Y);
                }
                AddChild(node);
                _nodeNodes[n.Id] = node;
            }
            if (n.Type == ResourceType.Grain) ReapField(n);   // thin the wheat as it is harvested
            node.Visible = seen;
        }
        Prune(_nodeNodes, live);
        // Forget the wheat bookkeeping for fields that have been reaped away.
        var goneFields = new List<int>();
        foreach (var id in _fieldCrop.Keys) if (!live.Contains(id)) goneFields.Add(id);
        foreach (var id in goneFields) { _fieldCrop.Remove(id); _fieldPeak.Remove(id); }
    }

    // A grain field: a patch of plowed soil planted with wheat bunches. The bunches
    // are kept in _fieldCrop so ReapField can hide them one by one as the node's
    // grain is carried off, so a field visibly empties as it is worked.
    Node3D MakeGrainField(ResourceNode n)
    {
        var root = new Node3D { Position = new Vector3(n.X, 0, n.Y) };

        // A patch of tilled earth, just proud of the grass. The pack's farm-row
        // prop is a single narrow furrow that reads as a stray cross on its own,
        // so the bed is a plain dark-earth slab under the crop instead.
        const float bed = 1.2f;
        var soil = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(bed, 0.06f, bed) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.21f, 0.13f), Roughness = 1f },
            Position = new Vector3(0, 0.03f, 0),
        };
        root.AddChild(soil);

        // Wheat bunches in rows across the bed, so the patch reads as a crop field.
        var wheatA = ModelAabb(_mWheat);
        float wheatH = Mathf.Max(wheatA.Size.Y, 0.1f);
        float wheatS = 0.55f / wheatH;                     // a bit over half a tile tall
        const int rows = 4;                                // 4x4 = a full-looking field
        float step = (bed - 0.24f) / (rows - 1);           // leave a margin inside the bed
        var crop = new List<Node3D>();
        // Fill in a serpentine order so ReapField empties the field row by row.
        for (int gy = 0; gy < rows; gy++)
        {
            int order = (gy % 2 == 0) ? 1 : -1;
            for (int c = 0; c < rows; c++)
            {
                int gx = order > 0 ? c : rows - 1 - c;
                int h = (n.X * 71 + n.Y * 53 + gx * 17 + gy * 29);
                float jx = ((h % 5) - 2) * 0.018f;
                float jz = (((h / 5) % 5) - 2) * 0.018f;
                var w = _mWheat.Instantiate<Node3D>();
                float js = wheatS * (1f + ((h % 5) * 0.06f));
                w.Scale = new Vector3(js, js, js);
                w.Position = new Vector3(-bed / 2 + 0.12f + gx * step + jx, 0.06f - wheatA.Position.Y * js,
                                         -bed / 2 + 0.12f + gy * step + jz);
                w.Rotation = new Vector3(0, (h % 360) * Mathf.Pi / 180f, 0);
                root.AddChild(w);
                crop.Add(w);
            }
        }
        _fieldCrop[n.Id] = crop;
        _fieldPeak[n.Id] = Mathf.Max(n.Amount, 1);
        return root;
    }

    // Show wheat bunches in proportion to the grain still standing, so a field
    // empties from full to bare as farmers carry it off.
    void ReapField(ResourceNode n)
    {
        if (!_fieldCrop.TryGetValue(n.Id, out var crop)) return;
        if (!_fieldPeak.TryGetValue(n.Id, out var peak) || n.Amount > peak)
            _fieldPeak[n.Id] = peak = Mathf.Max(n.Amount, 1);
        float frac = Mathf.Clamp((float)n.Amount / peak, 0f, 1f);
        int show = Mathf.CeilToInt(crop.Count * frac);     // at least one until truly empty
        if (n.Amount <= 0) show = 0;
        for (int i = 0; i < crop.Count; i++) crop[i].Visible = i < show;
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

    // A player-built Steps tile: a stone staircase from the ground up to the wall
    // walkway, turned so it climbs TOWARD the nearest rampart. A garrison ordered
    // onto a wall walks to the steps, up them, and along the top — no steps, no
    // climb (the sim refuses the garrison), matching the classic "you need stairs
    // to man your walls". Its foot/top are recorded so units know where to climb.
    const int StairSteps = 8;
    const float StepsRun = 1.5f;   // horizontal run of a steps tile (foot -> top)

    Node3D MakeSteps(Building b)
    {
        var dir = StepsDir(b);                          // axis-aligned, toward the rampart
        float yaw = Mathf.Atan2(dir.X, dir.Y);          // rotate local +z onto `dir`
        var root = new Node3D
        {
            Position = new Vector3(b.X, 0, b.Y),
            Rotation = new Vector3(0, yaw, 0),
        };

        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.56f, 0.52f, 0.47f), Roughness = 1f };
        float stepH = WallTopY / StairSteps;
        float stepDepth = StepsRun / StairSteps;
        for (int i = 0; i < StairSteps; i++)
        {
            float topY = stepH * (i + 1);
            float z = -StepsRun / 2f + (i + 0.5f) * stepDepth;   // lowest at the foot (-z), rising toward +z
            root.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.8f, topY, stepDepth + 0.02f) },
                MaterialOverride = mat,
                Position = new Vector3(0, topY * 0.5f, z),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }

        // World-space foot (ground) and top (walkway) so a climber has its path.
        var d3 = new Vector3(dir.X, 0, dir.Y);
        _stepsAccess[b.Id] = (
            new Vector3(b.X, 0, b.Y) - d3 * (StepsRun / 2f),
            new Vector3(b.X, WallTopY, b.Y) + d3 * (StepsRun / 2f));
        return root;
    }

    // Which way a steps tile should climb: toward the nearest rampart it serves
    // (wall / gatehouse / turret). With none yet, it faces away from the keep, the
    // way the walls do, so a lone steps still looks outward.
    Vector2I StepsDir(Building b)
    {
        Building near = null; int bestD = int.MaxValue;
        foreach (var r in _sim.Buildings)
            if (r.Alive && r.Owner == b.Owner &&
                (r.Type == BuildingType.Wall || r.Type == BuildingType.Gatehouse || r.Type == BuildingType.Turret))
            {
                int dx = r.X - b.X, dy = r.Y - b.Y, d = dx * dx + dy * dy;
                if (d > 0 && d < bestD) { bestD = d; near = r; }
            }

        int tx, ty;
        if (near != null) { tx = near.X - b.X; ty = near.Y - b.Y; }
        else
        {
            var keep = _sim.Buildings.Find(k => k.Type == BuildingType.Keep && k.Owner == b.Owner && k.Alive);
            if (keep == null) return new Vector2I(0, 1);
            tx = b.X - keep.CenterX; ty = b.Y - keep.CenterY;   // away from the keep
        }
        return Mathf.Abs(tx) >= Mathf.Abs(ty)
            ? new Vector2I(tx >= 0 ? 1 : -1, 0)
            : new Vector2I(0, ty >= 0 ? 1 : -1);
    }

    // A player-built Turret tile: a square stone tower that rises above the wall
    // walk, crenellated, with an open deck on top. Archers who reach the wall can
    // climb one step higher onto it and shoot from the highest point around.
    const float TurretStandY = WallTopY + 1.5f;   // deck height — where a garrison stands

    // Which of a turret/gatehouse's four cardinal neighbours are ramparts — the
    // spurs it grows toward — plus a gate's open/closed bit. Any change here means
    // the node must be rebuilt (new spurs, or the door raised/dropped).
    int TurretMask(Building b)
    {
        int mask = 0, bit = 1;
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            if (_wallSet.Contains((b.X + dx, b.Y + dy))) mask |= bit;
            bit <<= 1;
        }
        if (b.Type == BuildingType.Gatehouse && b.Open) mask |= 16;
        return mask;
    }

    Node3D MakeTurret(Building b)
    {
        var stone = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.48f, 0.44f), Roughness = 1f };
        var root = new Node3D { Position = new Vector3(b.X, 0, b.Y) };

        // Over a tile wide, so the shaft butts flush against the (now centred) walls
        // on either side with no grass showing between — a tower IN the line.
        const float w = 1.2f;
        root.AddChild(KeepBox(stone, new Vector3(w, TurretStandY, w), new Vector3(0, TurretStandY / 2f, 0)));  // shaft

        // Four corner merlons round the deck edge — a crenellated crown.
        const float m = 0.28f, mh = 0.38f;
        float e = w / 2f - m / 2f;
        foreach (var c in new[] { new Vector3(e, 0, e), new Vector3(-e, 0, e), new Vector3(e, 0, -e), new Vector3(-e, 0, -e) })
            root.AddChild(KeepBox(stone, new Vector3(m, mh, m), c + new Vector3(0, TurretStandY + mh / 2f, 0)));
        return root;
    }

    // A player-built Gatehouse: a stone gateway that rises ABOVE the wall walk, with
    // two jamb piers, a lintel, a crenellated crown, and a timber door across the
    // passage that shows when the gate is shut. Oriented to run with the wall line,
    // and spurred into its neighbours so it sits IN the wall, not beside it.
    const float GateH = WallTopY + 0.75f;   // a solid block, a little over the wall — chunky, not a tower

    Node3D MakeGate(Building b)
    {
        bool horiz = _wallSet.Contains((b.X + 1, b.Y)) || _wallSet.Contains((b.X - 1, b.Y));
        bool vert = _wallSet.Contains((b.X, b.Y + 1)) || _wallSet.Contains((b.X, b.Y - 1));
        float rot = (vert && !horiz) ? Mathf.Pi / 2f : 0f;   // passage runs across local X
        var stone = new StandardMaterial3D { AlbedoColor = new Color(0.6f, 0.58f, 0.53f), Roughness = 1f };
        var root = new Node3D { Position = new Vector3(b.X, 0, b.Y), Rotation = new Vector3(0, rot, 0) };

        // A solid stone block filling the tile, with an archway tunnelled through it
        // (the passage runs in Z). Built as two jambs + the wall over the arch, so
        // the tunnel stays open; a deck and battlements crown it.
        const float W = 1.34f, D = 0.98f, openW = 0.74f, jambW = (W - openW) / 2f;
        // The opening is the LOWER half of the block, so the raised portcullis has
        // solid gatehouse above it to hide in (rather than poking out the top).
        float openH = GateH * 0.5f;
        foreach (float s in new[] { 1f, -1f })                       // the two jamb side-walls
            root.AddChild(KeepBox(stone, new Vector3(jambW, GateH, D), new Vector3(s * (openW + jambW) / 2f, GateH / 2f, 0)));
        root.AddChild(KeepBox(stone, new Vector3(openW + 0.02f, GateH - openH, D),   // wall over the arch
            new Vector3(0, (GateH + openH) / 2f, 0)));
        // A stepped (corbelled) arch rounding the top of the opening.
        for (int s = 0; s < 3; s++)
        {
            float inset = 0.06f + s * 0.07f;             // narrows toward the top
            float y = openH - 0.06f - s * 0.08f;
            foreach (float side in new[] { 1f, -1f })
                root.AddChild(KeepBox(stone, new Vector3(inset, 0.1f, D),
                    new Vector3(side * (openW / 2f - inset / 2f), y, 0)));
        }

        // A timber deck on top, recessed within the battlements.
        var plank = new StandardMaterial3D { AlbedoColor = new Color(0.46f, 0.32f, 0.19f), Roughness = 1f };
        root.AddChild(KeepBox(plank, new Vector3(W - 0.3f, 0.08f, D - 0.3f), new Vector3(0, GateH + 0.04f, 0)));

        // Battlements around all four top edges — merlons at the corners and mid-edges.
        const float mw = 0.19f, mh = 0.34f;
        float ex = W / 2f - mw / 2f, ez = D / 2f - mw / 2f;
        var merlons = new List<Vector3>();
        foreach (float mz in new[] { ez, -ez })
            for (float mx = -ex; mx <= ex + 0.01f; mx += ex) merlons.Add(new Vector3(mx, 0, mz));
        foreach (float mx in new[] { ex, -ex }) merlons.Add(new Vector3(mx, 0, 0));   // side mid-edges
        foreach (var mp in merlons)
            root.AddChild(KeepBox(stone, new Vector3(mw, mh, mw), mp + new Vector3(0, GateH + mh / 2f, 0)));

        // The gate itself: an iron portcullis in the passage. Shut, it drops down
        // and bars the way; open, it is hauled UP into the gatehouse so the archway
        // is clear to walk straight through. The lift is the whole tell between the
        // two states, so it is deliberately large.
        var iron = new StandardMaterial3D { AlbedoColor = new Color(0.17f, 0.17f, 0.2f), Metallic = 0.35f, Roughness = 0.5f };
        var oak = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.22f, 0.12f), Roughness = 1f };
        float gW = openW - 0.06f, gH = openH - 0.1f;
        // Open: raise it just clear of the opening and up into the gatehouse's solid
        // upper half, so the archway below is fully clear and nothing pokes out top.
        float lift = b.Open ? GateH - gH - 0.06f : 0f;
        var grille = new Node3D { Position = new Vector3(0, lift, 0) };
        // A timber backing behind the bars, so a SHUT gate reads solid, not see-through.
        grille.AddChild(KeepBox(oak, new Vector3(gW, gH, 0.06f), new Vector3(0, gH / 2f, -0.04f)));
        for (int i = 0; i <= 4; i++)                                // vertical iron bars
            grille.AddChild(KeepBox(iron, new Vector3(0.05f, gH, 0.06f), new Vector3(-gW / 2f + i * gW / 4f, gH / 2f, 0.02f)));
        foreach (float hy in new[] { 0.14f, 0.52f, 0.9f })          // horizontal rails
            grille.AddChild(KeepBox(iron, new Vector3(gW, 0.05f, 0.06f), new Vector3(0, hy * gH, 0.02f)));
        root.AddChild(grille);

        return root;
    }

    // The foot/top of the nearest owned Steps to a rampart, so its garrison can
    // climb up. Null if none is built (the sim won't allow the garrison then).
    (Vector3 foot, Vector3 top)? NearestStepsAccess(Building rampart)
    {
        Building best = null; int bestD = int.MaxValue;
        foreach (var s in _sim.Buildings)
            if (s.Alive && s.Owner == rampart.Owner && s.Type == BuildingType.Steps)
            {
                int dx = s.X - rampart.X, dy = s.Y - rampart.Y, d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = s; }
            }
        if (best != null && _stepsAccess.TryGetValue(best.Id, out var acc)) return acc;
        return null;
    }

    // A wall tile: a solid body with a flat walkway top and a crenellated parapet
    // along the outer edge, turned to run with the wall line. Men stand on the top.
    Node3D MakeWall(Building b)
    {
        bool horiz = _wallSet.Contains((b.X + 1, b.Y)) || _wallSet.Contains((b.X - 1, b.Y));
        bool vert = _wallSet.Contains((b.X, b.Y + 1)) || _wallSet.Contains((b.X, b.Y - 1));

        // Face the crenellated parapet AWAY from the keep, so the wall shields it.
        // The parapet sits on the tile's +z edge (→ +x once a vertical run is turned
        // 90°); flip the tile 180° whenever that edge would point toward the keep.
        bool alongZ = vert && !horiz;
        float rot = alongZ ? Mathf.Pi / 2f : 0f;
        var keep = _sim.Buildings.Find(k => k.Type == BuildingType.Keep && k.Owner == b.Owner && k.Alive);
        if (keep != null && (alongZ ? keep.CenterX > b.X : keep.CenterY > b.Y))
            rot += Mathf.Pi;

        var root = new Node3D
        {
            Position = new Vector3(b.X + (b.W - 1) / 2f, 0, b.Y + (b.H - 1) / 2f),   // tile-centred like the units
            Rotation = new Vector3(0, rot, 0),
        };

        // The wall prefab's pivot sits at one END, so a placed wall renders half a
        // tile off-centre along its run. Shift it back so the wall is centred on its
        // tile — then wall-to-wall still tiles, and it meets a (centred) gate or
        // turret flush instead of one side poking in and the other gapping.
        const float WallXFix = -0.505f;
        var body = _wallBody.Instantiate<Node3D>();
        body.Scale = WallBodyScale;
        body.Position = new Vector3(WallXFix, 0, 0);
        root.AddChild(body);

        var parapet = _wallBat.Instantiate<Node3D>();
        parapet.Scale = WallBatScale;
        parapet.Position = new Vector3(WallXFix, WallTopY, WallBatZ);   // on top, along the outer edge
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
        // Toggle fullscreen. F11 is the universal key on Linux/Windows; on macOS
        // that keycode is the OS "Show Desktop" gesture and never reaches us, so
        // Cmd+Ctrl+F (the macOS-standard fullscreen chord) is offered as well.
        if (e is InputEventKey f && f.Pressed &&
            (f.Keycode == Key.F11 || (f.Keycode == Key.F && f.MetaPressed && f.CtrlPressed)))
        {
            var w = DisplayServer.WindowGetMode();
            bool full = w == DisplayServer.WindowMode.Fullscreen || w == DisplayServer.WindowMode.ExclusiveFullscreen;
            DisplayServer.WindowSetMode(full ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
            return;
        }

        // F toggles fog of war — solo only. FogEnabled is sim state, so it must be
        // flipped on EVERY client sim together to stay in lockstep; in LOCAL both
        // sit in this process and step in the same loop, so flipping them between
        // ticks is safe. In a networked match one player revealing the map would be
        // a maphack AND a desync, so the key is ignored there — use --no-fog (agreed
        // by both machines at launch) instead. No modifiers, so it never collides
        // with the Cmd+Ctrl+F fullscreen chord.
        if (e is InputEventKey fog && fog.Pressed && fog.Keycode == Key.F &&
            !fog.CtrlPressed && !fog.MetaPressed && !fog.AltPressed && _mode == "LOCAL")
        {
            bool on = !_sim.FogEnabled;
            foreach (var c in Clients()) c.Sim.FogEnabled = on;
            return;
        }

        // T shows/hides the territory border overlay. Purely visual and local, so
        // it needs no modifiers, no lockstep, and works in any mode.
        if (e is InputEventKey terr && terr.Pressed && terr.Keycode == Key.T &&
            !terr.CtrlPressed && !terr.MetaPressed && !terr.AltPressed)
        {
            _showTerritory = !_showTerritory;
            return;
        }

        // R rotates the building being placed a quarter-turn, so you can face it
        // whichever way looks best before committing.
        if (e is InputEventKey rot && rot.Pressed && rot.Keycode == Key.R &&
            !rot.CtrlPressed && !rot.MetaPressed && !rot.AltPressed && _buildType != null)
        {
            _ghostRot = (_ghostRot + 1) & 3;
            return;
        }

        // Delete / Backspace demolishes the selected building — but asks first. A
        // first press ARMS the confirm popup; a second (or the Demolish button, or
        // Enter) actually razes it, reclaiming half the cost and freeing its worker.
        // The failsafe is the point: a stray Del never loses a building outright.
        // The command itself goes down the normal lockstep path, so it stays fair
        // and networked. Not the keep, and not while placing.
        if (e is InputEventKey del && del.Pressed && _buildType == null &&
            (del.Keycode == Key.Delete || del.Keycode == Key.Backspace ||
             ((del.Keycode == Key.Enter || del.Keycode == Key.KpEnter) && _demolishId != 0)))
        {
            if (_demolishId != 0) ConfirmDemolish();
            else ArmDemolish(_selectedBuilding);
            return;
        }

        // Escape dismisses a pending demolish first, before it leaves build mode.
        if (e is InputEventKey esc && esc.Pressed && esc.Keycode == Key.Escape && _demolishId != 0)
        {
            CancelDemolish();
            return;
        }

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
        CancelDemolish();           // ...and drops any pending demolish confirmation

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
        float bestD = float.MaxValue;
        foreach (var b in _sim.Buildings)
        {
            if (b.Owner != MyPlayer || !b.Alive) continue;
            // Walls (now selectable, so they can be demolished) get a tighter pick
            // radius than a big building, so a click grabs the one you mean rather
            // than a neighbour in the run.
            float reach = b.Type == BuildingType.Wall ? 42f * 42f : 90f * 90f;
            // Test a couple of heights up the model, since a click can land low on
            // the body or high on the roof; take the nearer.
            var c = new Vector3(b.X + (b.W - 1) / 2f, 0f, b.Y + (b.H - 1) / 2f);
            float d = Mathf.Min(
                _cam.UnprojectPosition(c + Vector3.Up * 0.6f).DistanceSquaredTo(screen),
                _cam.UnprojectPosition(c + Vector3.Up * 1.6f).DistanceSquaredTo(screen));
            if (d < reach && d < bestD) { bestD = d; best = b; }
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

    // ---- demolish confirmation --------------------------------------------

    // A small centred popup that stands between a stray Del and losing a building.
    // It does NOT pause the game (a modal that froze the loop would stall lockstep
    // and risk a desync); the world keeps ticking behind it, and it simply holds
    // the demolish order until the player confirms or cancels.
    void SetupConfirmPanel()
    {
        var layer = new CanvasLayer();
        AddChild(layer);

        _confirmPanel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both, GrowVertical = Control.GrowDirection.Both,
            OffsetTop = -70, Visible = false,
        };
        ((PanelContainer)_confirmPanel).AddThemeStyleboxOverride("panel", Panel(new Color(0.13f, 0.09f, 0.10f, 0.96f)));
        layer.AddChild(_confirmPanel);

        var margin = new MarginContainer();
        foreach (var s in new[] { "left", "right" }) margin.AddThemeConstantOverride("margin_" + s, 18);
        foreach (var s in new[] { "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + s, 14);
        _confirmPanel.AddChild(margin);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        margin.AddChild(col);

        _confirmLabel = new Label { Text = "Demolish?", HorizontalAlignment = HorizontalAlignment.Center };
        _confirmLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.92f, 0.9f));
        _confirmLabel.AddThemeFontSizeOverride("font_size", 15);
        col.AddChild(_confirmLabel);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        col.AddChild(row);

        var yes = new Button { Text = "Demolish  ⌫", CustomMinimumSize = new Vector2(120, 0), FocusMode = Control.FocusModeEnum.None };
        yes.AddThemeColorOverride("font_color", new Color(0.98f, 0.55f, 0.5f));
        yes.AddThemeFontSizeOverride("font_size", 13);
        yes.Pressed += ConfirmDemolish;
        row.AddChild(yes);

        var no = new Button { Text = "Cancel  Esc", CustomMinimumSize = new Vector2(120, 0), FocusMode = Control.FocusModeEnum.None };
        no.AddThemeFontSizeOverride("font_size", 13);
        no.Pressed += CancelDemolish;
        row.AddChild(no);
    }

    // Arm the confirm popup for a building (the keep is never demolishable).
    void ArmDemolish(Building b)
    {
        if (b == null || !b.Alive || b.Type == BuildingType.Keep) return;
        _demolishId = b.Id;
        string[] tag = { "w", "s", "f", "g" };
        var r = _sim.RefundOf(b.Type);
        var parts = new List<string>();
        for (int i = 0; i < r.Length; i++) if (r[i] > 0) parts.Add($"{r[i]}{tag[i]}");
        string refund = parts.Count == 0 ? "" : $"   refund +{string.Join(" ", parts)}";
        _confirmLabel.Text = $"Demolish {NameOf(b.Type)}?{refund}";
        _confirmPanel.Visible = true;
        _sound?.PlayUi(Sfx.Select);
    }

    // Confirm: issue the actual command down the normal lockstep path, then dismiss.
    void ConfirmDemolish()
    {
        if (_demolishId != 0 && BuildingById(_demolishId) != null)
        {
            _me.Issue(new Command { Type = CommandType.Demolish, TargetId = _demolishId });
            _selectedBuilding = null;
            _sound?.PlayUi(Sfx.Select);
        }
        CancelDemolish();
    }

    void CancelDemolish()
    {
        _demolishId = 0;
        if (_confirmPanel != null) _confirmPanel.Visible = false;
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

    // Zoom sets the tilt too: at the default distance the angle is unchanged;
    // zooming IN drops toward a near ground-level view, zooming OUT rises toward a
    // top-down overview. So "get down to the ground" is simply "zoom all the way in".
    const float CamMinDist = 6f, CamDefDist = 16f, CamMaxDist = 90f;
    void UpdateCamera()
    {
        if (_cam == null) return;
        _camPitch = _camDist <= CamDefDist
            ? Mathf.Lerp(0.28f, 0.85f, (_camDist - CamMinDist) / (CamDefDist - CamMinDist))
            : Mathf.Lerp(0.85f, 1.2f, (_camDist - CamDefDist) / (CamMaxDist - CamDefDist));
        var offset = new Vector3(
            Mathf.Sin(_camYaw) * Mathf.Cos(_camPitch),
            Mathf.Sin(_camPitch),
            Mathf.Cos(_camYaw) * Mathf.Cos(_camPitch)) * _camDist;
        _cam.Position = _camTarget + offset;
        _cam.LookAt(_camTarget, Vector3.Up);
    }
}
