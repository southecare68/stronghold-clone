// Main.cs — The Godot engine layer: renders the sim, feeds input into commands.
//
// Runs in one of three modes, chosen by command line:
//
//   (no args)              LOCAL  — two clients in one window over
//                                   LoopbackTransport. The original vertical
//                                   slice: you drive player 1, and the HUD
//                                   compares two real simulations tick by tick.
//   --host[=PORT]          HOST   — player 1, listening for a joiner.
//   --join=ADDR[:PORT]     JOIN   — player 2, connecting to a host.
//   --code=XXXXX-XXXXX     JOIN   — same thing, endpoint spelled as a code.
//
// LOCAL mode is kept because it is the only mode that can prove sync without a
// second machine: two independent Simulations, same input, compared every tick.
// The networked modes cannot do that locally, so they rely on the checksum each
// peer piggybacks onto its turns — which is what a real desync detector has to
// look like anyway.

using Godot;
using System.Collections.Generic;
using Sim;
using Netcode;

// Godot ships its own TileMap node; in this file the name always means ours.
using TileMap = Sim.TileMap;

public partial class Main : Node2D
{
    const int TicksPerSecond = 20;
    const double Step = 1.0 / TicksPerSecond;
    const float PxPerUnit = 12f;     // world units -> screen pixels

    // The match map. Every client builds its OWN copy; TileMap.Demo is
    // deterministic, so all copies are identical and StateChecksum's map
    // fingerprint agrees. Sized to fit the window set in project.godot.
    const int DemoSize = 56;

    // Never simulate more than this many ticks in one frame. Without a cap, a
    // long stall followed by a burst of arriving turns would try to catch up all
    // at once, freeze the window, and look exactly like a crash.
    const int MaxTicksPerFrame = 8;

    ITransport _net;
    Client _me;      // the client we render and control
    Client _other;   // LOCAL mode only: the second in-process client
    EnetTransport _enet;   // networked modes only

    int _myPlayer = 1;
    string _mode = "LOCAL";
    string _joinHint = "";

    public Client LocalClient => _me;
    public Client RemoteClient => _other;

    double _accum;
    readonly HashSet<int> _selected = new();
    bool _boxing;
    Vector2 _boxStart;
    Vector2 _mouse;
    Label _hud;
    bool _desyncLogged;

    // ---- Visual interpolation ----------------------------------------------
    // The simulation advances 20 times a second; the display refreshes several
    // times more often than that. Drawing raw sim positions therefore shows
    // units stepping 20 times a second no matter how high the frame rate is. So
    // we remember where each unit was BEFORE the most recent tick and draw
    // between there and where it is now, according to how far the frame clock
    // has travelled toward the next tick.
    //
    // This is a rendering concern and nothing else. The interpolated value is a
    // float, it is never fed back, and no part of the simulation can observe
    // it — which is exactly why the sim can forbid floats while the renderer
    // uses them freely. Nothing in here can change a checksum.
    //
    // The cost is that the picture trails the simulation by up to one tick
    // (50 ms). The alternative, extrapolating ahead of the sim, has to guess,
    // and it guesses wrong every time a unit stops or changes direction — which
    // looks far worse than a small constant lag. With 150 ms of input delay
    // already in the protocol, 50 ms of render lag is not the thing anyone will
    // notice.
    readonly Dictionary<int, Vector2> _prevWorld = new();
    float _alpha;
    bool _debugInterp;

    public override void _Ready()
    {
        // --debug-interp shows where a unit is DRAWN next to where the sim
        // actually has it. The two differing, by less than one tick of travel,
        // is what "interpolation is running" looks like in a single frame.
        _debugInterp = HasFlag("--debug-interp");
        SetUpTransport();

        // Identical starting armies on EVERY machine (determinism starts here).
        foreach (var c in Clients())
        {
            c.Sim.SpawnUnit(1, 8, 8);
            c.Sim.SpawnUnit(1, 11, 8);
            c.Sim.SpawnUnit(1, 8, 11);
            c.Sim.SpawnUnit(2, 44, 40);
            c.Sim.SpawnUnit(2, 47, 40);
        }

        _hud = new Label { Position = new Vector2(8, 8) };
        AddChild(_hud);
    }

    void SetUpTransport()
    {
        var (mode, address, port) = ParseCommandLine();
        _mode = mode;

        if (mode == "LOCAL")
        {
            var loop = new LoopbackTransport();
            _net = loop;
            _me = new Client(1, loop, TileMap.Demo(DemoSize));
            _other = new Client(2, loop, TileMap.Demo(DemoSize));
            loop.Connect(_me);
            loop.Connect(_other);
            _myPlayer = 1;
            return;
        }

        _enet = mode == "HOST" ? EnetTransport.Host(port) : EnetTransport.Join(address, port);
        _net = _enet;
        _myPlayer = _enet.PlayerId;
        _me = new Client(_myPlayer, _enet, TileMap.Demo(DemoSize));
        _enet.Attach(_me);

        if (mode == "HOST")
        {
            string ip = LocalIPv4();
            _joinHint = ip == null ? $"port {port}" : MatchCode.Describe(ip, port);
        }
    }

    IEnumerable<Client> Clients()
    {
        yield return _me;
        if (_other != null) yield return _other;
    }

    // --- Command line ------------------------------------------------------
    // Godot swallows its own flags; anything after a bare `--` arrives via
    // GetCmdlineUserArgs. We check both lists so the flags work whether or not
    // the launcher passed the separator.
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
                int port = EnetTransport.DefaultPort;
                int colon = addr?.LastIndexOf(':') ?? -1;
                if (colon > 0)
                {
                    port = ParsePort(addr.Substring(colon + 1), EnetTransport.DefaultPort);
                    addr = addr.Substring(0, colon);
                }
                return ("JOIN", addr, port);
            }

            if (arg.StartsWith("--code="))
            {
                if (MatchCode.TryDecode(value, out string ip, out int codePort))
                    return ("JOIN", ip, codePort);
                GD.PrintErr($"[net] '{value}' is not a valid match code — starting in LOCAL mode");
            }
        }
        return ("LOCAL", null, EnetTransport.DefaultPort);
    }

    static int ParsePort(string s, int fallback) =>
        int.TryParse(s, out int p) && p > 0 && p < 65536 ? p : fallback;

    static bool HasFlag(string flag)
    {
        foreach (var a in OS.GetCmdlineUserArgs()) if (a == flag) return true;
        foreach (var a in OS.GetCmdlineArgs()) if (a == flag) return true;
        return false;
    }

    static string LocalIPv4()
    {
        foreach (string a in IP.GetLocalAddresses())
            if (a.Contains('.') && !a.StartsWith("127.")) return a;
        return null;
    }

    // --- Tick loop ---------------------------------------------------------
    public override void _Process(double delta)
    {
        _net.Poll();

        // Nothing may run before every player is present. Not one tick: a client
        // that opened the match alone would send turns nobody receives, and its
        // peer would stall on tick 0 for the rest of the session. Wall-clock time
        // is discarded rather than banked, so connecting doesn't trigger a
        // fast-forward through the ticks spent waiting.
        if (_enet != null && (_enet.Failed || !_enet.ReadyToPlay))
        {
            _accum = 0;
            _alpha = 0f;
            LogDesyncOnce();
            _hud.Text = BuildHud();
            QueueRedraw();
            return;
        }

        // Fixed-timestep loop: the sim advances a whole number of times per
        // second, independent of frame rate, so every machine covers the same
        // ground. A tick only runs when every player's input for it has arrived.
        _accum += delta;
        int ran = 0;

        while (_accum >= Step && ran < MaxTicksPerFrame)
        {
            foreach (var c in Clients()) c.SendInput();

            // Where everything is now becomes "where everything was" the instant
            // the tick lands. Taken every pass, so several ticks in one frame
            // still interpolate from the position before the LAST of them.
            SnapshotPositions();

            bool advanced = _me.TryStep();
            foreach (var c in Clients()) if (c != _me) c.TryStep();

            if (!advanced)
            {
                // Stalled on a peer. Hold at the tick boundary instead of banking
                // the wall-clock time — otherwise a five-second stall is followed
                // by a hundred-tick fast-forward. Holding here also pins the
                // interpolation at a full step, so units settle on their true
                // positions and wait rather than sliding past them.
                _accum = Step;
                break;
            }
            _accum -= Step;
            ran++;
        }

        // How far this frame sits between the last tick and the next. Clamped,
        // because a frame long enough to overrun the catch-up cap must not push
        // units beyond where the simulation has actually placed them.
        _alpha = (float)Mathf.Clamp(_accum / Step, 0.0, 1.0);

        LogDesyncOnce();
        _hud.Text = BuildHud();
        QueueRedraw();
    }

    void LogDesyncOnce()
    {
        if (_desyncLogged || _me.Desync == null) return;
        _desyncLogged = true;
        GD.PrintErr($"[sim] {_me.Desync}");
        GD.PrintErr("[sim] the two machines no longer agree — everything after this tick is meaningless");
    }

    string BuildHud() => Head() + WinnerLine() + InterpLine();

    // Announced once a side has no units left. The sim keeps ticking (harmless —
    // nobody is fighting), so this just reads the current verdict each frame.
    string WinnerLine()
    {
        int w = _me.Sim.MatchWinner();
        if (w < 0) return "";                              // still contested
        if (w == 0) return "\n— DRAW — both armies destroyed";
        return $"\n★ PLAYER {w} WINS ★" + (w == _myPlayer ? "  (you)" : "");
    }

    string InterpLine()
    {
        if (!_debugInterp) return "";

        var u = _me.Sim.Units.Count > 0 ? _me.Sim.Units[0] : null;
        if (u == null) return $"\ninterp a={_alpha:0.000}";

        var sim = SimWorld(u);
        var drawn = DrawWorld(u);
        var was = _prevWorld.TryGetValue(u.Id, out var w) ? w : sim;
        return $"\ninterp a={_alpha:0.000}   unit {u.Id}  was ({was.X:0.0000}, {was.Y:0.0000})" +
               $"  drawn ({drawn.X:0.0000}, {drawn.Y:0.0000})  sim ({sim.X:0.0000}, {sim.Y:0.0000})";
    }

    string Head()
    {
        string head = $"[{_mode}] tick {_me.Sim.TickNumber}   checksum 0x{_me.Sim.Checksum():X8}";

        if (_me.Desync != null)
            return head + "   DESYNC ✗\n" + _me.Desync;

        if (_enet != null)
        {
            if (_enet.Failed) return head + $"\nNETWORK ERROR: {_enet.Status}";
            if (!_enet.ReadyToPlay)
                return head + $"\n{_enet.Status.ToUpper()}" +
                       (_mode == "HOST" && _joinHint != "" ? $"\njoin with:  {_joinHint}" : "");
            if (_me.Stalled) return head + "   WAITING FOR PEER …";
            return head + "   IN SYNC ✓   (peer agrees through tick " + (_me.Sim.TickNumber - 1) + ")";
        }

        // LOCAL mode: two real simulations to compare directly.
        uint a = _me.Sim.Checksum();
        uint b = _other.Sim.Checksum();
        return head + "   " + (a == b ? "IN SYNC ✓" : "DESYNC ✗");
    }

    // ---- Input: mouse produces COMMANDS, never direct state changes ---------
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            _mouse = mm.Position;
            if (_boxing) QueueRedraw();
        }
        else if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                _boxing = true;
                _boxStart = mb.Position;
            }
            else if (mb.ButtonIndex == MouseButton.Right && _selected.Count > 0)
            {
                // Right-click an enemy to attack it, empty ground to move there.
                var enemy = EnemyUnitAt(mb.Position);
                if (enemy != null)
                {
                    _me.Issue(new Command
                    {
                        Type = CommandType.Attack,
                        UnitIds = new List<int>(_selected).ToArray(),
                        TargetId = enemy.Id,
                    });
                }
                else
                {
                    var w = ScreenToWorld(mb.Position);
                    _me.Issue(new Command
                    {
                        Type = CommandType.Move,
                        UnitIds = new List<int>(_selected).ToArray(),
                        X = Mathf.RoundToInt(w.X),
                        Y = Mathf.RoundToInt(w.Y),
                    });
                }
            }
        }
        else if (e is InputEventMouseButton up && !up.Pressed &&
                 up.ButtonIndex == MouseButton.Left && _boxing)
        {
            _boxing = false;
            SelectInBox(_boxStart, up.Position);
        }
    }

    // The nearest enemy unit under the cursor, or null. Uses the drawn position,
    // so clicking what you see hits what you meant even mid-move.
    Unit EnemyUnitAt(Vector2 screen)
    {
        Unit best = null;
        float bestD2 = 12f * 12f;      // within ~one unit radius of the click
        foreach (var u in _me.Sim.Units)
        {
            if (u.Owner == _myPlayer) continue;
            float d2 = WorldToScreen(u).DistanceSquaredTo(screen);
            if (d2 < bestD2) { bestD2 = d2; best = u; }
        }
        return best;
    }

    void SelectInBox(Vector2 p0, Vector2 p1)
    {
        _selected.Clear();
        var rect = new Rect2(p0, Vector2.Zero).Expand(p1).Abs();
        foreach (var u in _me.Sim.Units)
        {
            if (u.Owner != _myPlayer) continue;
            if (rect.HasPoint(WorldToScreen(u))) _selected.Add(u.Id);
        }
        QueueRedraw();
    }

    // ---- Rendering (float is fine HERE — this is not the sim) ----------------
    // Terrain palette. Ground is the background; only the rest is drawn per tile.
    static readonly Color GroundColor = new(0.17f, 0.21f, 0.15f);
    static readonly Color WaterColor = new(0.12f, 0.24f, 0.40f);
    static readonly Color RockColor = new(0.34f, 0.33f, 0.31f);
    static readonly Color MarshColor = new(0.24f, 0.25f, 0.11f);

    public override void _Draw()
    {
        DrawTerrain();
        DrawPaths();

        foreach (var u in _me.Sim.Units)
        {
            var p = WorldToScreen(u);
            var color = u.Owner == 1 ? new Color(0.3f, 0.7f, 1f) : new Color(1f, 0.45f, 0.35f);
            DrawCircle(p, 6f, color);
            if (_selected.Contains(u.Id))
                DrawArc(p, 9f, 0, Mathf.Tau, 24, Colors.White, 1.5f);
            if (u.MaxHp > 0 && u.Hp < u.MaxHp)
                DrawHealthBar(p, u.Hp, u.MaxHp);
        }
        if (_boxing)
        {
            var r = new Rect2(_boxStart, Vector2.Zero).Expand(_mouse).Abs();
            DrawRect(r, new Color(1, 1, 1, 0.15f), true);
            DrawRect(r, new Color(1, 1, 1, 0.6f), false, 1f);
        }
    }

    // Ground is one background rect; only water/rock/marsh are drawn per tile, so
    // this stays cheap even though terrain never changes. Tiles are centred on
    // their integer coordinate, matching where a unit standing on that tile draws.
    void DrawTerrain()
    {
        var map = _me.Sim.Map;
        DrawRect(new Rect2(TileCorner(0, 0),
                           new Vector2(map.Width * PxPerUnit, map.Height * PxPerUnit)),
                 GroundColor);

        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                var t = map.At(x, y);
                if (t == Terrain.Ground) continue;
                DrawRect(new Rect2(TileCorner(x, y), new Vector2(PxPerUnit, PxPerUnit)),
                         t switch
                         {
                             Terrain.Water => WaterColor,
                             Terrain.Rock => RockColor,
                             _ => MarshColor,
                         });
            }
    }

    // The remaining route of each selected unit, so string-pulling is visible:
    // on open ground it is a single straight line to the goal; around the wall or
    // lake it kinks only at the corners it must round.
    void DrawPaths()
    {
        var line = new Color(1f, 0.9f, 0.4f, 0.55f);
        foreach (var u in _me.Sim.Units)
        {
            if (!_selected.Contains(u.Id) || !u.HasPath) continue;

            var prev = DrawWorld(u) * PxPerUnit;
            for (int i = u.PathIndex; i < u.Path.Count; i++)
            {
                var wp = new Vector2(u.Path[i].X, u.Path[i].Y) * PxPerUnit;
                DrawLine(prev, wp, line, 1.5f);
                DrawCircle(wp, 2.5f, line);
                prev = wp;
            }
        }
    }

    // A small health bar above a damaged unit: red track, green fill.
    void DrawHealthBar(Vector2 center, int hp, int maxHp)
    {
        const float w = 14f, h = 2.5f;
        var topLeft = center + new Vector2(-w / 2f, -12f);
        float frac = Mathf.Clamp((float)hp / maxHp, 0f, 1f);
        DrawRect(new Rect2(topLeft, new Vector2(w, h)), new Color(0.5f, 0.1f, 0.1f));
        DrawRect(new Rect2(topLeft, new Vector2(w * frac, h)), new Color(0.3f, 0.85f, 0.35f));
    }

    // Top-left corner of a tile in screen space. Tiles are centred on the integer
    // coordinate, so tile (x,y) spans half a tile either side of (x,y)*Px.
    static Vector2 TileCorner(int x, int y) =>
        new Vector2((x - 0.5f) * PxPerUnit, (y - 0.5f) * PxPerUnit);

    public override void _ExitTree() => _enet?.Close();

    // The unit's true position, straight out of the sim.
    static Vector2 SimWorld(Unit u) =>
        new Vector2(u.X / (float)Fixed.One, u.Y / (float)Fixed.One);

    void SnapshotPositions()
    {
        foreach (var u in _me.Sim.Units) _prevWorld[u.Id] = SimWorld(u);
    }

    // Where the unit is DRAWN this frame: between its position before the last
    // tick and its position now. A unit with no history yet — the first frames
    // of a match — simply draws where it is.
    Vector2 DrawWorld(Unit u)
    {
        var now = SimWorld(u);
        return _prevWorld.TryGetValue(u.Id, out var was) ? was.Lerp(now, _alpha) : now;
    }

    // Everything on screen goes through here, hit-testing included, so a
    // box-select catches the units the player can actually see rather than the
    // invisible positions the sim is holding up to a tick ahead of the picture.
    Vector2 WorldToScreen(Unit u) => DrawWorld(u) * PxPerUnit;

    Vector2 ScreenToWorld(Vector2 screen) => screen / PxPerUnit;
}
