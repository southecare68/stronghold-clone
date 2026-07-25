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
using Audio;

// Godot ships its own TileMap node; in this file the name always means ours.
using TileMap = Sim.TileMap;

public partial class Main : Node2D
{
    const int TicksPerSecond = 20;
    const double Step = 1.0 / TicksPerSecond;
    const float PxPerUnit = 12f;     // world units -> screen pixels

    // The match map. Every client builds its OWN copy; TileMap.Skirmish is
    // deterministic (no RNG), so all copies are identical and StateChecksum's map
    // fingerprint agrees. Much larger than the window — that's what the camera and
    // minimap are for.
    const int MapSize = Skirmish.DefaultSize;

    // Never simulate more than this many ticks in one frame. Without a cap, a
    // long stall followed by a burst of arriving turns would try to catch up all
    // at once, freeze the window, and look exactly like a crash.
    const int MaxTicksPerFrame = 8;

    ITransport _net;
    Client _me;      // the client we render and control (null in replay mode)
    Client _other;   // LOCAL mode only: the second in-process client
    EnetTransport _enet;   // networked modes only

    // The simulation the renderer, HUD and input all read from. In live modes
    // this IS _me.Sim; in replay mode it is the reconstructed playback sim.
    Simulation _shown;

    // Replay: when playing one back, we drive this sim from the recorded command
    // stream instead of a client; when recording, _me carries a ReplayRecorder.
    bool _replayMode;
    Replay _replay;
    ReplayRecorder _recorder;   // records the live match (null in replay mode)
    int _replayIndex;
    string _replayPath = "user://last.shrep";

    int _myPlayer = 1;
    string _mode = "LOCAL";
    string _joinHint = "";

    // Which design a barracks trains when right-clicked. Chosen with number keys.
    int _trainDesign;

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

    // ---- Render-only unit separation ---------------------------------------
    // The simulation lets units share a tile (no collision — a deliberate scope
    // choice), so a clump of units would otherwise draw on one pixel. This
    // spreads them for display ONLY: the sim positions are untouched, nothing
    // here feeds back, and no checksum can change — exactly like interpolation.
    //
    // The layout is a stable function of the sim state, not a per-frame physics
    // relaxation, so it never jitters: units on the same tile are ranked by id
    // and placed at fixed sunflower offsets around the shared point. A stack of N
    // fans into a tight, steady cluster.
    readonly Dictionary<int, Vector2> _sepOffset = new();
    const float SepSpacing = 13f;      // ~ unit diameter, so circles just clear

    // ---- Camera (pan & zoom) -----------------------------------------------
    // A manual transform rather than a Camera2D node: _Draw applies it with
    // DrawSetTransform, and input inverts the SAME formula, so what you click is
    // exactly what you see at any zoom. The HUD is a separate Label node, so it
    // is unaffected and stays pinned to the screen. Works in replay mode too.
    Vector2 _camCenter;                // the world-pixel point shown at screen centre
    float _camZoom = 1f;
    bool _panning;
    const float MinZoom = 0.35f, MaxZoom = 3.5f;

    // ---- Projectiles (render-only) -----------------------------------------
    // A ranged blow becomes an arrow flying from shooter to target. Driven off
    // the sim's transient ShotsThisTick, so it's purely cosmetic — no sim state,
    // and it replays for free. Only long-range shots get an arrow; melee blows
    // are adjacent and show nothing.
    sealed class Projectile { public Vector2 From, To; public float Age; public float Life; }
    readonly List<Projectile> _projectiles = new();
    static readonly int RangedShotDist = Fixed.FromInt(2);   // shots longer than this get an arrow

    // ---- Minimap ------------------------------------------------------------
    // Drawn in SCREEN space (the camera transform is reset first), so it stays
    // pinned to the corner at any zoom. Shows the whole battlefield, the current
    // view as a rectangle, and jumps the camera when clicked.
    const float MiniSize = 160f, MiniMargin = 10f;
    // Terrain never changes, so it is baked into a one-pixel-per-tile texture
    // once and blitted each frame — far cheaper than drawing 16k rects.
    ImageTexture _miniTerrain;

    // ---- Baked art ----------------------------------------------------------
    // Sprites rendered offline from the 3D packs (tools/bake/), loaded at startup.
    // Every lookup can return null and the renderer falls back to its original
    // shapes, so the game is complete with or without art — see SpriteBank.
    SpriteBank _art;
    // A unit's heading changes tick to tick and would make the sprite flicker
    // between facings on the spot; this remembers the last committed facing per
    // unit and only switches when the heading has clearly moved on.
    readonly Dictionary<int, int> _facing = new();
    // Baked units face this screen direction at facing 0. Calibrated once against
    // the actual sprites; a wrong value just rotates every unit by a constant.
    const int FacingOffset = 0;
    // Per-unit walk-cycle phase, advanced by wall time while a unit is moving and
    // frozen while it stands. Render-only: it is a float driven by frame delta and
    // never touches the simulation, exactly like interpolation.
    readonly Dictionary<int, float> _animPhase = new();
    const float WalkCadence = 9f;      // walk frames per second at full stride
    const float AttackCadence = 8f;    // attack frames per second — one full swing per ~0.5s blow
    // Last-seen design per living unit, so a corpse knows which sprite set to use
    // after the simulation has removed the unit.
    readonly Dictionary<int, int> _lastDesign = new();

    // Render-only corpses. When a unit vanishes from the simulation we keep
    // drawing a death animation where it fell, then fade it out. Purely cosmetic:
    // the sim removed the unit already, nothing here is game state, and — like the
    // death SOUND — it only appears where the player could see the kill.
    sealed class Corpse { public int Design, Facing; public Vector2 Feet; public float Age; }
    readonly List<Corpse> _corpses = new();
    const float DeathPlaySec = 1.0f;   // play the topple slowly enough to watch
    const float DeathFadeSec = 1.4f;   // then let the body lie a while before fading

    // ---- Fog of war (the DRAWING half) --------------------------------------
    // The rule lives in the simulation (Sim/Vision.cs) — what you may attack,
    // gather and build on. This is only the picture of it: unexplored ground is
    // black, ground you have seen but cannot see now is dimmed and shows what you
    // remember (terrain, buildings, resource patches) but no live enemies, and
    // ground in sight is drawn in full.
    //
    // Deliberately NOT shown in a replay: a replay is watched from outside the
    // match, and re-fogging it to one player's view would hide the very thing
    // you replayed it to look at. Press F to check the other reading anyway.
    bool _fogView = true;
    bool FogOn => _shown != null && _shown.FogEnabled && _fogView && !_replayMode;

    bool Lit(int x, int y) => !FogOn || _shown.Fog.IsVisible(_myPlayer, x, y);
    bool Known(int x, int y) => !FogOn || _shown.Fog.IsExplored(_myPlayer, x, y);
    bool LitUnit(Unit u) => !FogOn || _shown.Fog.IsVisible(_myPlayer, Fixed.ToInt(u.X), Fixed.ToInt(u.Y));

    bool KnownBuilding(Building b)
    {
        if (!FogOn) return true;
        for (int y = b.Y; y < b.Y + b.H; y++)
            for (int x = b.X; x < b.X + b.W; x++)
                if (_shown.Fog.IsExplored(_myPlayer, x, y)) return true;
        return false;
    }

    // The minimap's fog layer. Rebuilt only when the sim ticks (20 Hz at most),
    // as one byte array handed to Godot in a single call — per-pixel SetPixel
    // over 16k tiles every frame would not be affordable.
    ImageTexture _miniFog;
    byte[] _fogPixels;
    int _fogBakedTick = -1;

    // ---- Sound ---------------------------------------------------------------
    // Audio is a rendering concern in exactly the sense interpolation is: it
    // OBSERVES the simulation and never feeds back, so nothing here can move a
    // checksum. There are no sound files — every effect is generated from
    // arithmetic at startup (Audio/Synth.cs).
    //
    // Most events are found by diffing the simulation between ticks rather than
    // by the sim telling us. That keeps the sim free of presentation hooks, and
    // it means a REPLAY makes exactly the same noises for free, because a replay
    // produces the same state transitions.
    Sound _sound;

    // ---- Music --------------------------------------------------------------
    // Adaptive, and driven off exactly the same observations the sound effects
    // use — no new hooks in the simulation. Three states: Calm while you build,
    // Tension the moment something of theirs is in sight, Battle while blows are
    // landing. The mood is deliberately STICKY on the way down (see
    // BattleHoldSeconds): flicking back to calm the instant a fight pauses makes
    // the music sound broken, and a skirmish with a lull in it is still a fight.
    MusicPlayer _music;
    double _lastCombatAt = -999;
    const double BattleHoldSeconds = 6.0;

    readonly HashSet<int> _prevUnitIds = new();
    readonly HashSet<int> _prevBuildingIds = new();
    readonly Dictionary<int, bool> _prevGateOpen = new();
    readonly Dictionary<int, Vector2> _prevBuildingWhere = new();
    int _prevStockTotal;

    public override void _Ready()
    {
        // --debug-interp shows where a unit is DRAWN next to where the sim
        // actually has it. The two differing, by less than one tick of travel,
        // is what "interpolation is running" looks like in a single frame.
        _debugInterp = HasFlag("--debug-interp");

        // --replay=<path> watches a recorded match instead of playing.
        string replayArg = FlagValue("--replay");
        if (replayArg != null)
        {
            StartReplay(replayArg);
            StartArt();
            StartSound();
            _hud = new Label { Position = new Vector2(8, 8) };
            AddChild(_hud);
            return;
        }

        SetUpTransport();

        // Identical starting state on EVERY machine (determinism starts here):
        // same armies, same drop-offs, same resource nodes in the same order.
        // Armies, keeps, stockpiles, nodes and the unit roster — see Sim/Skirmish.cs.
        // It lives in the sim so the headless tests can place the same start and
        // check it is actually playable.
        foreach (var c in Clients()) Skirmish.Setup(c.Sim, MapSize);

        _shown = _me.Sim;
        CenterCamera();
        BuildMinimapTerrain();

        // Record the match we render, so it can be saved and watched back. Started
        // now, after setup, so the recording's initial snapshot is the real
        // starting world (tick 0). Costs nothing but a per-tick command copy.
        _recorder = new ReplayRecorder(_me.Sim);
        _me.Recorder = _recorder;

        StartArt();
        StartSound();
        _hud = new Label { Position = new Vector2(8, 8) };
        AddChild(_hud);
    }

    // ---- Sound: setup and the observer --------------------------------------

    void StartArt()
    {
        _art = new SpriteBank();
    }

    void StartSound()
    {
        _sound = new Sound { LogPlays = HasFlag("--audio-log") };
        AddChild(_sound);
        _music = new MusicPlayer();
        AddChild(_music);
        PrimeSoundObserver();
    }

    // Record the world as it stands WITHOUT making any noise. Without this the
    // first diff would see every starting unit and building as brand new and open
    // the match with a fanfare of construction.
    void PrimeSoundObserver()
    {
        _prevUnitIds.Clear();
        _prevBuildingIds.Clear();
        _prevGateOpen.Clear();
        _prevBuildingWhere.Clear();
        foreach (var u in _shown.Units) _prevUnitIds.Add(u.Id);
        foreach (var b in _shown.Buildings)
        {
            _prevBuildingIds.Add(b.Id);
            _prevGateOpen[b.Id] = b.Open;
            _prevBuildingWhere[b.Id] = new Vector2(b.CenterX, b.CenterY);
        }
        _prevStockTotal = StockTotal();
    }

    // Pick the track from what is actually happening. Read straight off the same
    // simulation state everything else observes — the sim is not told that music
    // exists, and a replay therefore scores itself.
    void UpdateMood()
    {
        if (_music == null) return;
        _music.SetMood(DecideMood());
    }

    Mood DecideMood()
    {
        // Blows landing, or still ringing. The hold is what stops the score
        // lurching between battle and calm through a pause in the melee.
        if (Time.GetTicksMsec() / 1000.0 - _lastCombatAt < BattleHoldSeconds) return Mood.Battle;

        // Committed to a fight but not yet in contact — the march up is Battle
        // too, because that is when it is about to matter.
        foreach (var u in _shown.Units)
            if (u.Owner == _myPlayer && (u.TargetId != 0 || u.TargetBuildingId != 0))
                return Mood.Battle;

        // Anything of theirs in sight. Under fog this is genuinely informative:
        // the music tells you something is out there at the same moment you could
        // have seen it, and never before — it can only read what you can see.
        foreach (var u in _shown.Units)
            if (u.Owner != _myPlayer && LitUnit(u)) return Mood.Tension;

        return Mood.Calm;
    }

    void SetVolume(float delta)
    {
        if (_sound == null) return;
        _sound.Volume = Mathf.Clamp(_sound.Volume + delta, 0f, 1f);
        _sound.Muted = false;               // reaching for the volume means "let me hear it"
        _sound.PlayUi(Sfx.Deposit);         // a tick at the new level, so you can judge it
    }

    string SoundLine() =>
        _sound == null ? "off" : _sound.Muted ? "MUTED" : $"{Mathf.RoundToInt(_sound.Volume * 100)}%";

    string MusicLine() =>
        _music == null || !_music.Enabled ? "off" : _music.Current.ToString().ToLower();

    // Keep the ears where the eyes are. The audible radius is tied to what is on
    // screen rather than being a fixed number of world pixels: zoomed out you are
    // looking at the whole battlefield and should hear it, zoomed in on your own
    // base a fight across the map should not be shouting in your ear.
    void UpdateListener()
    {
        if (_sound == null) return;
        float halfWidth = GetViewportRect().Size.X / (2f * _camZoom);
        _sound.Listen(_camCenter, Mathf.Max(240f, halfWidth * 1.7f));
    }

    int StockTotal() =>
        _shown.Stockpile(_myPlayer, ResourceType.Wood) +
        _shown.Stockpile(_myPlayer, ResourceType.Stone) +
        _shown.Stockpile(_myPlayer, ResourceType.Food);

    // World units -> the pixel space the audio voices live in, which is the same
    // space the renderer draws in before the camera transform is applied.
    static Vector2 Aud(float tileX, float tileY) => new Vector2(tileX, tileY) * PxPerUnit;

    // Should the player HEAR something happening there? The same rule as seeing
    // it. Letting a fight on the far side of the ridge be audible would hand back
    // exactly the information the fog was put in to withhold.
    bool Audible(int tileX, int tileY) => Lit(tileX, tileY);

    // Find this tick's events by comparing the world to how it was. Deliberately
    // a diff rather than hooks inside the simulation: the sim stays free of
    // presentation concerns, and a replay makes the same noises for nothing,
    // because it reproduces the same transitions.
    void ObserveForSound()
    {
        if (_sound == null) return;

        // Units gone: someone fell. Their last drawn position is still in the
        // interpolation history, which is the only place it survives.
        foreach (int id in _prevUnitIds)
        {
            if (_shown.Units.Find(u => u.Id == id) != null) continue;
            if (!_prevWorld.TryGetValue(id, out var where)) continue;
            if (!Audible(Mathf.RoundToInt(where.X), Mathf.RoundToInt(where.Y))) continue;

            _sound.Play(Sfx.UnitDeath, Aud(where.X, where.Y));

            // Leave a corpse to play the death topple where it fell. Render-only:
            // the sim has already removed the unit; this just animates its exit.
            if (_art != null && _art.AnyLoaded && _art.FrameCount(_lastDesign.GetValueOrDefault(id), SpriteBank.Anim.Death) > 0)
                _corpses.Add(new Corpse
                {
                    Design = _lastDesign.GetValueOrDefault(id),
                    Facing = _facing.GetValueOrDefault(id),
                    Feet = where,
                    Age = 0f,
                });
        }

        // Units arrived: a barracks finished one. Only your own — an enemy
        // reinforcement is not something you would hear across the map.
        foreach (var u in _shown.Units)
        {
            if (_prevUnitIds.Contains(u.Id)) continue;
            if (u.Owner != _myPlayer) continue;
            _sound.Play(Sfx.BuildDone, Aud(u.X / (float)Fixed.One, u.Y / (float)Fixed.One));
        }

        foreach (var b in _shown.Buildings)
        {
            if (_prevBuildingIds.Contains(b.Id)) continue;
            if (Audible(b.CenterX, b.CenterY)) _sound.Play(Sfx.BuildPlace, Aud(b.CenterX, b.CenterY));
        }

        // Buildings gone: something was brought down. The position has to come
        // from the remembered footprint, since the building itself is no longer
        // in the list to ask.
        foreach (int id in _prevBuildingIds)
        {
            if (_shown.Buildings.Find(x => x.Id == id) != null) continue;
            if (!_prevBuildingWhere.TryGetValue(id, out var where)) continue;
            if (Audible(Mathf.RoundToInt(where.X), Mathf.RoundToInt(where.Y)))
                _sound.Play(Sfx.Collapse, Aud(where.X, where.Y));
        }

        foreach (var b in _shown.Buildings)
        {
            if (b.Type != BuildingType.Gatehouse) continue;
            // No previous state means the gate was only just built — that is
            // BuildPlace's event, not a gate moving. Reading "unknown" as
            // "changed" made every new gatehouse groan open the moment it landed.
            if (!_prevGateOpen.TryGetValue(b.Id, out bool was) || was == b.Open) continue;
            if (Audible(b.CenterX, b.CenterY)) _sound.Play(Sfx.GateMove, Aud(b.CenterX, b.CenterY));
        }

        // A load banked. Heard at your own drop-off, which is where it happened.
        int stock = StockTotal();
        if (stock > _prevStockTotal && _shown.DropOffs.TryGetValue(_myPlayer, out var drop))
            _sound.Play(Sfx.Deposit, Aud(drop.X, drop.Y));

        // Roll the record forward for the next tick.
        _prevUnitIds.Clear();
        foreach (var u in _shown.Units) _prevUnitIds.Add(u.Id);
        _prevBuildingIds.Clear();
        _prevGateOpen.Clear();
        _prevBuildingWhere.Clear();
        foreach (var b in _shown.Buildings)
        {
            _prevBuildingIds.Add(b.Id);
            _prevGateOpen[b.Id] = b.Open;
            _prevBuildingWhere[b.Id] = new Vector2(b.CenterX, b.CenterY);
        }
        _prevStockTotal = stock;
    }

    // Load a replay file and stand up the playback simulation.
    void StartReplay(string path)
    {
        var bytes = Godot.FileAccess.GetFileAsBytes(path);
        if (bytes == null || bytes.Length == 0)
        {
            GD.PrintErr($"[replay] could not read {path}");
            return;
        }
        _replay = Replay.Deserialize(bytes);
        if (_replay == null)
        {
            GD.PrintErr($"[replay] {path} is not a valid replay");
            return;
        }
        _replayMode = true;
        _mode = "REPLAY";
        _shown = _replay.Reconstruct();
        CenterCamera();
        BuildMinimapTerrain();
        GD.Print($"[replay] playing {path}: {_replay.Commands.Count} ticks, " +
                 $"expecting final checksum 0x{_replay.FinalChecksum:X8}");
    }

    // Write the match so far to a replay file.
    void SaveReplay()
    {
        if (_recorder == null || _me == null) return;
        var replay = _recorder.Finish(_me.Sim);
        using var f = Godot.FileAccess.Open(_replayPath, Godot.FileAccess.ModeFlags.Write);
        if (f == null) { GD.PrintErr($"[replay] could not write {_replayPath}"); return; }
        f.StoreBuffer(replay.Serialize());
        GD.Print($"[replay] saved {_replayPath}: {replay.Commands.Count} ticks, " +
                 $"final checksum 0x{replay.FinalChecksum:X8}. Watch with  --replay={_replayPath}");
    }

    void SetUpTransport()
    {
        var (mode, address, port) = ParseCommandLine();
        _mode = mode;

        if (mode == "LOCAL")
        {
            var loop = new LoopbackTransport();
            _net = loop;
            _me = new Client(1, loop, TileMap.Skirmish(MapSize));
            _other = new Client(2, loop, TileMap.Skirmish(MapSize));
            loop.Connect(_me);
            loop.Connect(_other);
            _myPlayer = 1;
            return;
        }

        _enet = mode == "HOST" ? EnetTransport.Host(port) : EnetTransport.Join(address, port);
        _net = _enet;
        _myPlayer = _enet.PlayerId;
        _me = new Client(_myPlayer, _enet, TileMap.Skirmish(MapSize));
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

    // The value of a --flag=value argument, or null if absent.
    static string FlagValue(string flag)
    {
        string prefix = flag + "=";
        foreach (var a in OS.GetCmdlineUserArgs()) if (a.StartsWith(prefix)) return a.Substring(prefix.Length);
        foreach (var a in OS.GetCmdlineArgs()) if (a.StartsWith(prefix)) return a.Substring(prefix.Length);
        return null;
    }

    static string LocalIPv4()
    {
        foreach (string a in IP.GetLocalAddresses())
            if (a.Contains('.') && !a.StartsWith("127.")) return a;
        return null;
    }

    // Advance the playback at the same fixed 20 Hz as a live match, feeding the
    // recorded commands one tick at a time. Interpolation and separation use the
    // same code as live play, so a replay looks exactly like the game.
    void StepReplay(double delta)
    {
        _accum += delta;
        int ran = 0;
        while (_accum >= Step && ran < MaxTicksPerFrame && _replayIndex < _replay.Commands.Count)
        {
            SnapshotPositions();
            _shown.Tick(_replay.Commands[_replayIndex++]);
            CaptureShots();
            _accum -= Step;
            ran++;
        }
        _alpha = _replayIndex < _replay.Commands.Count
            ? (float)Mathf.Clamp(_accum / Step, 0.0, 1.0) : 1f;

        AgeProjectiles(delta);
        ComputeSeparation();
        _hud.Text = BuildHud();
        QueueRedraw();
    }

    // --- Tick loop ---------------------------------------------------------
    public override void _Process(double delta)
    {
        if (_replayMode) { StepReplay(delta); return; }

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
            UpdateMinimapFog();
            UpdateListener();
            UpdateMood();
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
            if (advanced) { CaptureShots(); ObserveForSound(); }

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

        AgeProjectiles(delta);
        ComputeSeparation();
        AdvanceAnimation(delta);
        UpdateMinimapFog();
        UpdateListener();
        UpdateMood();
        LogDesyncOnce();
        _hud.Text = BuildHud();
        QueueRedraw();
    }

    // Recompute the display offsets. Group units by the tile their sim position
    // rounds to; each group of more than one fans out. Done once per frame so
    // both drawing and hit-testing (which share WorldToScreen) see the same
    // layout. Computed from sim state each frame, so it needs no history and
    // cannot drift.
    void ComputeSeparation()
    {
        _sepOffset.Clear();
        var groups = new Dictionary<(int, int), List<int>>();
        foreach (var u in _shown.Units)
        {
            var cell = (Mathf.RoundToInt(u.X / (float)Fixed.One), Mathf.RoundToInt(u.Y / (float)Fixed.One));
            if (!groups.TryGetValue(cell, out var list)) { list = new List<int>(); groups[cell] = list; }
            list.Add(u.Id);
        }

        foreach (var list in groups.Values)
        {
            if (list.Count < 2) continue;      // a lone unit needs no offset
            list.Sort();                       // rank by id so the layout is stable
            for (int i = 0; i < list.Count; i++) _sepOffset[list[i]] = Sunflower(i);
        }
    }

    // The i-th point of a sunflower (phyllotaxis) packing: rank 0 at the centre,
    // the rest spiralling out with roughly even spacing. Stable per rank, so the
    // cluster holds still.
    static Vector2 Sunflower(int i)
    {
        if (i == 0) return Vector2.Zero;
        const float golden = 2.399963f;        // golden angle, radians
        float a = i * golden;
        float r = SepSpacing * Mathf.Sqrt(i);
        return new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
    }

    void LogDesyncOnce()
    {
        if (_desyncLogged || _me?.Desync == null) return;
        _desyncLogged = true;
        GD.PrintErr($"[sim] {_me.Desync}");
        GD.PrintErr("[sim] the two machines no longer agree — everything after this tick is meaningless");
    }

    string BuildHud()
    {
        if (_replayMode) return ReplayHud();
        return Head() + StockLine() + WinnerLine() + InterpLine();
    }

    // A replay shows progress and, once finished, whether it reproduced the
    // recording — the same self-check the headless test runs, on screen.
    string ReplayHud()
    {
        int done = _replayIndex, total = _replay.Commands.Count;
        string head = $"[REPLAY] tick {_shown.TickNumber}   {done}/{total}   " +
                      $"checksum 0x{_shown.StateChecksum():X8}";
        if (done < total) return head + WinnerLine();
        bool ok = _shown.StateChecksum() == _replay.FinalChecksum;
        return head + (ok ? "   ✓ reproduced exactly" : "   ✗ DIVERGED from recording") + WinnerLine();
    }

    // Your own stockpile. Reads straight from the sim each frame.
    string StockLine()
    {
        int wood = _shown.Stockpile(_myPlayer, ResourceType.Wood);
        int stone = _shown.Stockpile(_myPlayer, ResourceType.Stone);
        int food = _shown.Stockpile(_myPlayer, ResourceType.Food);
        var d = _shown.DesignOf(_trainDesign);
        string name = _trainDesign < Skirmish.DesignNames.Length ? Skirmish.DesignNames[_trainDesign] : $"#{_trainDesign}";
        return $"\nwood {wood}   stone {stone}   food {food}" +
               $"\ntrain: [{_trainDesign + 1}] {name}  (hp {d.Hp} dmg {d.Damage} spd {d.SpeedStat} rng {d.RangeStat} cd {d.Cooldown}, {d.PointCost}/{Simulation.MaxDesignPoints}pts)" +
               "\n[1/2/3/4] pick design  [B/K/W/G] build at cursor  (wheel zoom, mid-drag/arrows pan)" +
               "\nright-click your barracks to train, gate to open/close, enemy to attack" +
               (_shown.FogEnabled ? $"\nfog of war ON  [F] {(_fogView ? "reveal map" : "back to your view")}" +
                                    "  — you cannot attack, gather or build where you cannot see"
                                  : "") +
               $"\nsound {SoundLine()}  [M] mute  [-/=] volume   music {MusicLine()}  [N] on/off";
    }

    // Announced once a side has no units left. The sim keeps ticking (harmless —
    // nobody is fighting), so this just reads the current verdict each frame.
    string WinnerLine()
    {
        int w = _shown.MatchWinner();
        if (w < 0) return "";                              // still contested
        if (w == 0) return "\n— DRAW — both armies destroyed";
        return $"\n★ PLAYER {w} WINS ★" + (w == _myPlayer ? "  (you)" : "");
    }

    string InterpLine()
    {
        if (!_debugInterp) return "";

        var u = _shown.Units.Count > 0 ? _shown.Units[0] : null;
        if (u == null) return $"\ninterp a={_alpha:0.000}";

        var sim = SimWorld(u);
        var drawn = DrawWorld(u);
        var was = _prevWorld.TryGetValue(u.Id, out var w) ? w : sim;
        return $"\ninterp a={_alpha:0.000}   unit {u.Id}  was ({was.X:0.0000}, {was.Y:0.0000})" +
               $"  drawn ({drawn.X:0.0000}, {drawn.Y:0.0000})  sim ({sim.X:0.0000}, {sim.Y:0.0000})";
    }

    string Head()
    {
        string head = $"[{_mode}] tick {_shown.TickNumber}   checksum 0x{_shown.Checksum():X8}";

        if (_me.Desync != null)
            return head + "   DESYNC ✗\n" + _me.Desync;

        if (_enet != null)
        {
            if (_enet.Failed) return head + $"\nNETWORK ERROR: {_enet.Status}";
            if (!_enet.ReadyToPlay)
                return head + $"\n{_enet.Status.ToUpper()}" +
                       (_mode == "HOST" && _joinHint != "" ? $"\njoin with:  {_joinHint}" : "");
            if (_me.Stalled) return head + "   WAITING FOR PEER …";
            return head + "   IN SYNC ✓   (peer agrees through tick " + (_shown.TickNumber - 1) + ")";
        }

        // LOCAL mode: two real simulations to compare directly.
        uint a = _shown.Checksum();
        uint b = _other.Sim.Checksum();
        return head + "   " + (a == b ? "IN SYNC ✓" : "DESYNC ✗");
    }

    // ---- Input: mouse produces COMMANDS, never direct state changes ---------
    // Camera controls (wheel zoom, middle-drag / arrow-key pan) work in EVERY
    // mode, replay included. Gameplay orders are gated behind the replay check.
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            _mouse = ScreenToCanvas(mm.Position);
            if (_panning) { _camCenter -= mm.Relative / _camZoom; ClampCamera(); QueueRedraw(); }
            else if (_boxing) QueueRedraw();
            return;
        }

        if (e is InputEventMouseButton mb)
        {
            // Camera: wheel zooms toward the cursor, middle-button drags to pan.
            if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)   { ZoomAt(1.1f, mb.Position);       QueueRedraw(); return; }
            if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown) { ZoomAt(1f / 1.1f, mb.Position);  QueueRedraw(); return; }
            if (mb.ButtonIndex == MouseButton.Middle)                 { _panning = mb.Pressed;            return; }

            // Clicking the minimap jumps the camera there — checked in SCREEN
            // space (the panel doesn't move with the camera) and before any
            // gameplay click, so a click on the panel never orders units.
            if (mb.Pressed && MinimapRect().HasPoint(mb.Position))
            {
                var r = MinimapRect();
                var rel = (mb.Position - r.Position) / r.Size;      // 0..1 across the panel
                _camCenter = new Vector2(rel.X * _shown.Map.Width, rel.Y * _shown.Map.Height) * PxPerUnit;
                ClampCamera();
                QueueRedraw();
                return;
            }

            if (_replayMode) return;   // no orders while watching a replay
            var at = ScreenToCanvas(mb.Position);

            if (mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                _boxing = true;
                _boxStart = at;
            }
            else if (mb.Pressed && mb.ButtonIndex == MouseButton.Right)
            {
                // Right-click resolves to the most specific thing under the
                // cursor. Acting on your own building (train / work the gate)
                // needs no unit selected; orders to units do.
                var mine = OwnBuildingAt(at);
                if (mine != null && mine.Type == BuildingType.Barracks)
                {
                    _me.Issue(new Command { Type = CommandType.Train, TargetId = mine.Id, X = _trainDesign });
                    _sound?.PlayUi(CanAffordTraining() ? Sfx.MoveOrder : Sfx.Denied);
                }
                else if (mine != null && mine.Type == BuildingType.Gatehouse)
                    _me.Issue(new Command { Type = CommandType.ToggleGate, TargetId = mine.Id });
                else if (_selected.Count > 0)
                {
                    var ids = new List<int>(_selected).ToArray();
                    var enemy = EnemyUnitAt(at);
                    var enemyBuilding = EnemyBuildingAt(at);
                    var node = NodeAt(at);
                    if (enemy != null)
                    {
                        _me.Issue(new Command { Type = CommandType.Attack, UnitIds = ids, TargetId = enemy.Id });
                        // With the map revealed by F you can click an enemy the
                        // SIMULATION will refuse. Say so, rather than leaving the
                        // order to vanish silently.
                        _sound?.PlayUi(_shown.CanSeeUnit(_myPlayer, enemy) ? Sfx.AttackOrder : Sfx.Denied);
                    }
                    else if (enemyBuilding != null)
                    {
                        _me.Issue(new Command { Type = CommandType.AttackBuilding, UnitIds = ids, TargetId = enemyBuilding.Id });
                        _sound?.PlayUi(Sfx.AttackOrder);
                    }
                    else if (node != null)
                    {
                        _me.Issue(new Command { Type = CommandType.Gather, UnitIds = ids, TargetId = node.Id });
                        _sound?.PlayUi(Sfx.MoveOrder);
                    }
                    else
                    {
                        var w = ScreenToWorld(at);
                        _me.Issue(new Command
                        {
                            Type = CommandType.Move, UnitIds = ids,
                            X = Mathf.RoundToInt(w.X), Y = Mathf.RoundToInt(w.Y),
                        });
                        _sound?.PlayUi(Sfx.MoveOrder);
                    }
                }
            }
            else if (!mb.Pressed && mb.ButtonIndex == MouseButton.Left && _boxing)
            {
                _boxing = false;
                SelectInBox(_boxStart, at);
            }
            return;
        }

        if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            // Arrow keys pan (all modes). Step is constant in SCREEN space.
            float step = 40f / _camZoom;
            switch (k.Keycode)
            {
                case Key.Left:  _camCenter += new Vector2(-step, 0); ClampCamera(); QueueRedraw(); return;
                case Key.Right: _camCenter += new Vector2(step, 0);  ClampCamera(); QueueRedraw(); return;
                case Key.Up:    _camCenter += new Vector2(0, -step);  ClampCamera(); QueueRedraw(); return;
                case Key.Down:  _camCenter += new Vector2(0, step);   ClampCamera(); QueueRedraw(); return;
                // Reveal the whole map. A DISPLAY switch only — it cannot show
                // you anything the simulation would let you act on, because the
                // orders are gated in Sim/Vision.cs, not here.
                case Key.F: _fogView = !_fogView; _fogBakedTick = -1; QueueRedraw(); return;
                // Mute, and volume by steps. All modes, replay included.
                case Key.M: if (_sound != null) _sound.Muted = !_sound.Muted; return;
                case Key.N: _music?.SetEnabled(!_music.Enabled); return;
                case Key.Minus: SetVolume(-0.1f); return;
                case Key.Equal: SetVolume(0.1f); return;
            }

            if (_replayMode) return;
            // B / K / W / G place a building with its top-left at the cursor tile.
            if (k.Keycode == Key.B) PlaceAtCursor(BuildingType.Barracks);
            else if (k.Keycode == Key.K) PlaceAtCursor(BuildingType.Keep);
            else if (k.Keycode == Key.W) PlaceAtCursor(BuildingType.Wall);
            else if (k.Keycode == Key.G) PlaceAtCursor(BuildingType.Gatehouse);
            // 1 / 2 / 3 / 4 choose which design a barracks trains.
            else if (k.Keycode == Key.Key1) _trainDesign = 0;
            else if (k.Keycode == Key.Key2) _trainDesign = 1;
            else if (k.Keycode == Key.Key3) _trainDesign = 2;
            else if (k.Keycode == Key.Key4) _trainDesign = 3;
            // F5 saves the match so far as a replay.
            else if (k.Keycode == Key.F5) SaveReplay();
        }
    }

    // The nearest enemy unit under the cursor, or null. Uses the drawn position,
    // so clicking what you see hits what you meant even mid-move.
    Unit EnemyUnitAt(Vector2 screen)
    {
        Unit best = null;
        float bestD2 = 12f * 12f;      // within ~one unit radius of the click
        foreach (var u in _shown.Units)
        {
            if (u.Owner == _myPlayer || !LitUnit(u)) continue;
            float d2 = WorldToScreen(u).DistanceSquaredTo(screen);
            if (d2 < bestD2) { bestD2 = d2; best = u; }
        }
        return best;
    }

    // One of your own buildings whose footprint is under the cursor, or null.
    Building OwnBuildingAt(Vector2 screen) => BuildingAt(screen, mine: true);

    // An ENEMY building under the cursor, or null — a siege target.
    Building EnemyBuildingAt(Vector2 screen) => BuildingAt(screen, mine: false);

    Building BuildingAt(Vector2 screen, bool mine)
    {
        var w = ScreenToWorld(screen);
        int tx = Mathf.RoundToInt(w.X), ty = Mathf.RoundToInt(w.Y);
        foreach (var b in _shown.Buildings)
        {
            if ((b.Owner == _myPlayer) != mine) continue;
            if (!mine && !KnownBuilding(b)) continue;
            if (tx >= b.X && tx < b.X + b.W && ty >= b.Y && ty < b.Y + b.H) return b;
        }
        return null;
    }

    // Issue a Build order with the building's top-left at the cursor tile. The
    // sim validates footprint and cost and refuses quietly if it cannot place.
    void PlaceAtCursor(BuildingType type)
    {
        var w = ScreenToWorld(_mouse);
        int x = Mathf.RoundToInt(w.X), y = Mathf.RoundToInt(w.Y);
        _me.Issue(new Command
        {
            Type = CommandType.Build, TargetId = (int)type, X = x, Y = y,
        });

        // A refused build is silent in the simulation — it simply places nothing
        // and spends nothing — which from the outside is indistinguishable from
        // the click not registering. Predict the common refusals and SAY so. The
        // sound only for the acceptance case is left to the observer, so it lands
        // when the structure actually appears rather than three ticks early.
        if (!WouldBuild(type, x, y)) _sound?.PlayUi(Sfx.Denied);
    }

    // The same three tests Simulation.Apply makes for a Build order. A local
    // prediction, used ONLY to pick a sound — the simulation remains the sole
    // authority on whether anything is actually placed.
    bool WouldBuild(BuildingType type, int x, int y)
    {
        if (!_shown.CanPlace(type, x, y)) return false;
        var cost = _shown.CostOf(type);
        for (int i = 0; i < Sim.Resources.Count; i++)
            if (_shown.Stockpile(_myPlayer, (ResourceType)i) < cost[i]) return false;
        return Known(x, y);
    }

    bool CanAffordTraining() =>
        _shown.Stockpile(_myPlayer, ResourceType.Wood) >= 15;

    // The resource node under the cursor, or null.
    ResourceNode NodeAt(Vector2 screen)
    {
        ResourceNode best = null;
        float bestD2 = 12f * 12f;
        foreach (var n in _shown.Nodes)
        {
            if (!Known(n.X, n.Y)) continue;
            float d2 = (new Vector2(n.X, n.Y) * PxPerUnit).DistanceSquaredTo(screen);
            if (d2 < bestD2) { bestD2 = d2; best = n; }
        }
        return best;
    }

    void SelectInBox(Vector2 p0, Vector2 p1)
    {
        _selected.Clear();
        var rect = new Rect2(p0, Vector2.Zero).Expand(p1).Abs();
        foreach (var u in _shown.Units)
        {
            if (u.Owner != _myPlayer) continue;
            if (rect.HasPoint(WorldToScreen(u))) _selected.Add(u.Id);
        }
        if (_selected.Count > 0) _sound?.PlayUi(Sfx.Select);
        QueueRedraw();
    }

    // ---- Rendering (float is fine HERE — this is not the sim) ----------------
    // Terrain palette. Ground is the background; only the rest is drawn per tile.
    static readonly Color GroundColor = new(0.17f, 0.21f, 0.15f);
    static readonly Color WaterColor = new(0.12f, 0.24f, 0.40f);
    static readonly Color RockColor = new(0.34f, 0.33f, 0.31f);
    static readonly Color MarshColor = new(0.24f, 0.25f, 0.11f);

    static readonly Color WoodColor = new(0.45f, 0.32f, 0.16f);
    static readonly Color StoneColor = new(0.62f, 0.62f, 0.66f);
    static readonly Color FoodColor = new(0.85f, 0.75f, 0.25f);

    public override void _Draw()
    {
        ApplyCameraTransform();   // everything below is drawn in world-pixel space

        DrawTerrain();
        DrawNodes();
        DrawBuildings();
        DrawDropOffs();
        DrawPaths();
        DrawProjectiles();
        DrawCorpses();   // under the living, on the ground

        foreach (var u in _shown.Units)
        {
            // An enemy in fog simply is not there as far as the screen is
            // concerned. Your own units always draw — you always know where your
            // own army is, whatever it is standing in.
            if (u.Owner != _myPlayer && !LitUnit(u)) continue;

            var p = WorldToScreen(u);
            var color = u.Owner == 1 ? new Color(0.3f, 0.7f, 1f) : new Color(1f, 0.45f, 0.35f);
            // Radius scales with the design's HP, so a Brute reads as bigger than
            // a Runner at a glance.
            float r = Mathf.Clamp(4f + u.MaxHp * 0.03f, 4f, 9f);

            var state = UnitState(u);
            var sprite = _art?.Unit(u.DesignId, UnitFacing(u), state, UnitFrame(u, state));
            if (sprite != null)
            {
                // A team-coloured disc UNDER the feet — the sprite carries no
                // colour, so this is what says whose unit it is, and it doubles as
                // the shadow that anchors the sprite to the ground.
                DrawCircle(p + new Vector2(0, 2f), r, new Color(color.R, color.G, color.B, 0.5f));
                DrawUnitSprite(sprite, p, r, Colors.White);
                if (_selected.Contains(u.Id))
                    DrawArc(p + new Vector2(0, 2f), r + 3f, 0, Mathf.Tau, 24, Colors.White, 1.5f);
            }
            else
            {
                DrawCircle(p, r, color);
                if (_selected.Contains(u.Id))
                    DrawArc(p, r + 3f, 0, Mathf.Tau, 24, Colors.White, 1.5f);
            }

            if (u.MaxHp > 0 && u.Hp < u.MaxHp)
                DrawHealthBar(p, u.Hp, u.MaxHp);
            // A worker hauling a load shows a small dot of the resource's colour.
            if (u.CarryAmount > 0)
                DrawCircle(p + new Vector2(0, -8f), 2f, ResourceColor(u.CarryType));
        }
        // Last, so it darkens everything drawn above it — terrain, buildings and
        // any unit that happens to be standing in remembered ground.
        DrawFog();

        if (_boxing)
        {
            var r = new Rect2(_boxStart, Vector2.Zero).Expand(_mouse).Abs();
            DrawRect(r, new Color(1, 1, 1, 0.15f), true);
            DrawRect(r, new Color(1, 1, 1, 0.6f), false, 1f);
        }

        // Back to screen space for anything pinned to the display.
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        DrawMinimap();
    }

    // Ground is one background rect; only water/rock/marsh are drawn per tile, so
    // this stays cheap even though terrain never changes. Tiles are centred on
    // their integer coordinate, matching where a unit standing on that tile draws.
    void DrawTerrain()
    {
        var map = _shown.Map;
        var (x0, y0, x1, y1) = VisibleTiles();

        // Textured path: draw a ground texture under everything, then only the
        // non-ground tiles on top. Each tile samples a DIFFERENT sub-region of
        // its texture keyed by (x,y), so a 128-texture tiled across the map does
        // not repeat visibly every tile — it reads as one continuous surface.
        var ground = _art?.Terrain("ground");
        if (ground != null)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    DrawTerrainTile(map.At(x, y), x, y, ground);
            return;
        }

        // Fallback: flat colours, exactly as before the art existed.
        DrawRect(new Rect2(TileCorner(0, 0),
                           new Vector2(map.Width * PxPerUnit, map.Height * PxPerUnit)),
                 GroundColor);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
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

    void DrawTerrainTile(Terrain t, int x, int y, Texture2D ground)
    {
        var tex = t switch
        {
            Terrain.Rock => _art.Terrain("rock"),
            Terrain.Marsh => _art.Terrain("marsh"),
            Terrain.Water => _art.Terrain("water"),
            _ => ground,
        } ?? ground;

        var dst = new Rect2(TileCorner(x, y), new Vector2(PxPerUnit, PxPerUnit));

        // Water has no texture in the pack, so tint the ground blue for it rather
        // than leaving a hole; every other type has its own.
        if (t == Terrain.Water && _art.Terrain("water") == null)
        {
            DrawTextureRectRegion(ground, dst, TileRegion(ground, x, y), WaterColor.Lerp(Colors.White, 0.15f));
            return;
        }
        DrawTextureRectRegion(tex, dst, TileRegion(tex, x, y));
    }

    // A small, deterministic window into the texture chosen by tile coordinate,
    // so adjacent tiles show different parts of the source and the surface does
    // not look stamped. 4x4 windows across the texture give sixteen variations,
    // shuffled by a cheap hash of (x,y) so the pattern has no visible period.
    static Rect2 TileRegion(Texture2D tex, int x, int y)
    {
        var size = tex.GetSize();
        float w = size.X / 4f, h = size.Y / 4f;
        int hash = (x * 73856093) ^ (y * 19349663);
        int gx = ((hash & 3) + 4) % 4;
        int gy = (((hash >> 2) & 3) + 4) % 4;
        return new Rect2(gx * w, gy * h, w, h);
    }

    // The tile range actually on screen. On a 128x128 map that is a few hundred
    // tiles instead of sixteen thousand — worth having for the terrain, and
    // essential for the fog, which is drawn tile by tile every frame.
    (int, int, int, int) VisibleTiles()
    {
        var map = _shown.Map;
        var half = GetViewportRect().Size / (2f * _camZoom * PxPerUnit);
        var c = _camCenter / PxPerUnit;
        return (Mathf.Max(0, Mathf.FloorToInt(c.X - half.X) - 1),
                Mathf.Max(0, Mathf.FloorToInt(c.Y - half.Y) - 1),
                Mathf.Min(map.Width - 1, Mathf.CeilToInt(c.X + half.X) + 1),
                Mathf.Min(map.Height - 1, Mathf.CeilToInt(c.Y + half.Y) + 1));
    }

    // Two shades, and the difference between them is the whole point: solid black
    // is ground you have never laid eyes on, and the softer veil is ground you
    // scouted once — you still see the lie of the land and any building you found
    // there, but not what is moving through it now.
    static readonly Color Unexplored = new(0.04f, 0.04f, 0.06f, 1f);
    static readonly Color Remembered = new(0.04f, 0.04f, 0.06f, 0.55f);

    void DrawFog()
    {
        if (!FogOn) return;
        var (x0, y0, x1, y1) = VisibleTiles();
        var tile = new Vector2(PxPerUnit, PxPerUnit);

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (_shown.Fog.IsVisible(_myPlayer, x, y)) continue;
                DrawRect(new Rect2(TileCorner(x, y), tile),
                         _shown.Fog.IsExplored(_myPlayer, x, y) ? Remembered : Unexplored);
            }
    }

    // The remaining route of each selected unit, so string-pulling is visible:
    // on open ground it is a single straight line to the goal; around the wall or
    // lake it kinks only at the corners it must round.
    void DrawPaths()
    {
        var line = new Color(1f, 0.9f, 0.4f, 0.55f);
        foreach (var u in _shown.Units)
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

    static Color ResourceColor(ResourceType t) => t switch
    {
        ResourceType.Wood => WoodColor,
        ResourceType.Stone => StoneColor,
        _ => FoodColor,
    };

    // Resource nodes: a square coloured by type, sized by how much is left, so a
    // depleting node visibly shrinks.
    void DrawNodes()
    {
        foreach (var n in _shown.Nodes)
        {
            // A patch you have found stays on your map even when nobody is
            // watching it — its remaining amount is what you last saw, which is
            // close enough and is how the genre has always played it.
            if (!Known(n.X, n.Y)) continue;
            var center = new Vector2(n.X, n.Y) * PxPerUnit;
            float s = Mathf.Lerp(4f, 11f, Mathf.Clamp(n.Amount / 300f, 0.15f, 1f));
            DrawRect(new Rect2(center - new Vector2(s / 2f, s / 2f), new Vector2(s, s)),
                     ResourceColor(n.Type));
        }
    }

    // Buildings: a filled footprint in the owner's colour, keeps darker and
    // walled, barracks lighter with a production bar when a unit is queued.
    void DrawBuildings()
    {
        var stone = new Color(0.55f, 0.55f, 0.58f);
        foreach (var b in _shown.Buildings)
        {
            // Structures do not move, so scouting one is knowledge you keep — the
            // fog veil drawn over it afterwards is what marks it as remembered
            // rather than observed.
            if (b.Owner != _myPlayer && !KnownBuilding(b)) continue;
            var owner = b.Owner == 1 ? new Color(0.3f, 0.7f, 1f) : new Color(1f, 0.45f, 0.35f);
            var rect = new Rect2(TileCorner(b.X, b.Y),
                                 new Vector2(b.W * PxPerUnit, b.H * PxPerUnit));

            // Baked sprite if we have one for this type. Drawn wider than the
            // footprint and anchored at the footprint's BOTTOM, so the building
            // rises up out of its tiles the way an isometric sprite should, and a
            // thin owner-tinted ring on the ground says whose it is (the sprite
            // itself carries no team colour).
            var sprite = _art?.Building(b.Type);
            if (sprite != null)
            {
                DrawRect(rect, new Color(owner.R, owner.G, owner.B, 0.18f));
                DrawRect(rect, owner, false, 1.5f);
                DrawBuildingSprite(sprite, rect);
                DrawBuildingBars(b, rect);
                continue;
            }

            switch (b.Type)
            {
                case BuildingType.Wall:
                    // Stone masonry with a thin tint of the owner's colour.
                    DrawRect(rect, stone.Lerp(owner, 0.2f));
                    DrawRect(rect, stone.Darkened(0.3f), false, 1f);
                    break;

                case BuildingType.Gatehouse:
                    // Closed: filled stone. Open: just the jambs, so the gap reads
                    // as walkable.
                    if (b.Open)
                    {
                        DrawRect(rect, owner, false, 2f);
                        var jamb = new Vector2(3f, rect.Size.Y);
                        DrawRect(new Rect2(rect.Position, jamb), stone);
                        DrawRect(new Rect2(rect.Position + new Vector2(rect.Size.X - 3f, 0), jamb), stone);
                    }
                    else
                    {
                        DrawRect(rect, stone.Lerp(owner, 0.3f));
                        DrawRect(rect, owner, false, 2f);
                    }
                    break;

                default:  // Keep, Barracks
                    var fill = b.Type == BuildingType.Keep ? owner.Darkened(0.35f) : owner.Darkened(0.15f);
                    DrawRect(rect, fill);
                    DrawRect(rect, owner, false, 2f);
                    break;
            }

            DrawBuildingBars(b, rect);
        }
    }

    // Damage and production bars, above a building's footprint. Shared by the
    // sprite and the shape path so both read the same.
    void DrawBuildingBars(Building b, Rect2 rect)
    {
        if (b.MaxHp > 0 && b.Hp < b.MaxHp)
        {
            float frac = Mathf.Clamp((float)b.Hp / b.MaxHp, 0f, 1f);
            var barTop = rect.Position + new Vector2(0, -4f);
            DrawRect(new Rect2(barTop, new Vector2(rect.Size.X, 3f)), new Color(0.5f, 0.1f, 0.1f));
            DrawRect(new Rect2(barTop, new Vector2(rect.Size.X * frac, 3f)), new Color(0.3f, 0.85f, 0.35f));
        }

        // Production progress (BuildTimer counts DOWN from TrainTime=60), above
        // the damage bar so both are visible.
        if (b.Type == BuildingType.Barracks && b.TrainQueue.Count > 0)
        {
            float frac = 1f - Mathf.Clamp(b.BuildTimer / 60f, 0f, 1f);
            var barTop = rect.Position + new Vector2(0, -8f);
            DrawRect(new Rect2(barTop, new Vector2(rect.Size.X, 3f)), new Color(0, 0, 0, 0.5f));
            DrawRect(new Rect2(barTop, new Vector2(rect.Size.X * frac, 3f)), new Color(0.9f, 0.8f, 0.3f));
        }
    }

    // Draw a building sprite anchored at the BOTTOM-CENTRE of its footprint. A
    // sprite is a tall picture of something standing ON the tiles, so its base
    // belongs on the ground and its height rises up the screen. Width tracks the
    // footprint (with a little overhang); height follows the sprite's own aspect,
    // so a tower stays tall and a wall stays low.
    void DrawBuildingSprite(Texture2D sprite, Rect2 footprint)
    {
        var texSize = sprite.GetSize();
        float drawW = footprint.Size.X * 1.35f;
        float drawH = drawW * texSize.Y / texSize.X;
        float bottom = footprint.Position.Y + footprint.Size.Y;
        float cx = footprint.Position.X + footprint.Size.X * 0.5f;
        var dst = new Rect2(cx - drawW * 0.5f, bottom - drawH, drawW, drawH);
        DrawTextureRect(sprite, dst, false);
    }

    // A ring at each player's drop-off, in that player's colour.
    void DrawDropOffs()
    {
        foreach (var kv in _shown.DropOffs)
        {
            if (kv.Key != _myPlayer && !Known(kv.Value.X, kv.Value.Y)) continue;
            var p = new Vector2(kv.Value.X, kv.Value.Y) * PxPerUnit;
            var c = kv.Key == 1 ? new Color(0.3f, 0.7f, 1f, 0.8f) : new Color(1f, 0.45f, 0.35f, 0.8f);
            DrawArc(p, 13f, 0, Mathf.Tau, 28, c, 2f);
        }
    }

    // Advance each unit's clip phase — faster for walking, slower for a swing,
    // frozen when idle — and age the corpses. Render-only, driven by frame time.
    void AdvanceAnimation(double delta)
    {
        if (_art == null || !_art.AnyLoaded) return;

        foreach (var u in _shown.Units)
        {
            _lastDesign[u.Id] = u.DesignId;
            var st = UnitState(u);
            if (st == SpriteBank.Anim.Walk)
                _animPhase[u.Id] = _animPhase.GetValueOrDefault(u.Id) + (float)delta * WalkCadence;
            else if (st == SpriteBank.Anim.Attack)
                _animPhase[u.Id] = _animPhase.GetValueOrDefault(u.Id) + (float)delta * AttackCadence;
        }

        for (int i = _corpses.Count - 1; i >= 0; i--)
        {
            _corpses[i].Age += (float)delta;
            if (_corpses[i].Age > DeathPlaySec + DeathFadeSec) _corpses.RemoveAt(i);
        }
    }

    // What a unit is doing, for animation. Chasing counts as walking; standing
    // with a target (a unit or a building) is attacking; anything else is idle.
    SpriteBank.Anim UnitState(Unit u)
    {
        if (Moving(u)) return SpriteBank.Anim.Walk;
        if (u.TargetId != 0 || u.TargetBuildingId != 0) return SpriteBank.Anim.Attack;
        return SpriteBank.Anim.Idle;
    }

    int UnitFrame(Unit u, SpriteBank.Anim state)
    {
        int n = _art.FrameCount(u.DesignId, state);
        if (n <= 0) return 0;
        return Mathf.PosMod((int)_animPhase.GetValueOrDefault(u.Id), n);
    }

    // Is the unit travelling toward a waypoint (as opposed to standing)? The sim
    // sets Tx=X, Ty=Y on arrival, so a gap means motion.
    static bool Moving(Unit u)
    {
        int dx = u.Tx - u.X, dy = u.Ty - u.Y;
        return dx * dx + dy * dy > (Fixed.One / 8) * (Fixed.One / 8);
    }

    // Which of the eight baked facings to show for a unit, from the direction it
    // is heading toward its current waypoint. A stationary unit keeps whatever it
    // last faced, so an idle army does not all snap to face south. The chosen
    // facing is remembered and only changed when the heading has clearly moved to
    // a new octant, so a unit crossing a boundary does not strobe between two
    // sprites.
    int UnitFacing(Unit u)
    {
        int dx = u.Tx - u.X, dy = u.Ty - u.Y;
        if (dx * dx + dy * dy < (Fixed.One / 8) * (Fixed.One / 8))
            return _facing.TryGetValue(u.Id, out var held) ? held : 0;

        // Screen angle: atan2(dy, dx) with +y downward, mapped to 8 octants.
        float ang = Mathf.Atan2(dy, dx);                       // -pi..pi
        int oct = Mathf.PosMod(Mathf.RoundToInt(ang / (Mathf.Tau / 8f)) + FacingOffset, 8);
        _facing[u.Id] = oct;
        return oct;
    }

    // A unit sprite, anchored at the FEET (bottom-centre on the unit's point) so
    // it stands on the tile rather than floating. Sized to a few multiples of the
    // shape radius it replaces, so the scene keeps the same visual density.
    void DrawUnitSprite(Texture2D sprite, Vector2 feet, float r, Color modulate)
    {
        var texSize = sprite.GetSize();
        float drawH = r * 4.6f;
        float drawW = drawH * texSize.X / texSize.Y;
        var dst = new Rect2(feet.X - drawW * 0.5f, feet.Y - drawH + r * 0.6f, drawW, drawH);
        DrawTextureRect(sprite, dst, false, modulate);
    }

    // The fallen, mid-topple or settled-and-fading. Drawn on the ground under the
    // living. A corpse in ground that has slipped back into fog is hidden, the
    // same rule the death sound and every enemy sprite follow.
    void DrawCorpses()
    {
        if (_art == null) return;
        foreach (var c in _corpses)
        {
            int tx = Mathf.RoundToInt(c.Feet.X), ty = Mathf.RoundToInt(c.Feet.Y);
            if (!Lit(tx, ty)) continue;

            int n = _art.FrameCount(c.Design, SpriteBank.Anim.Death);
            if (n <= 0) continue;

            // Play through the topple frames over DeathPlaySec and hold the last;
            // then fade that settled body out over DeathFadeSec.
            float playT = Mathf.Clamp(c.Age / DeathPlaySec, 0f, 1f);
            int frame = Mathf.Min(n - 1, (int)(playT * n));
            float alpha = c.Age <= DeathPlaySec ? 1f
                        : 1f - Mathf.Clamp((c.Age - DeathPlaySec) / DeathFadeSec, 0f, 1f);

            var sprite = _art.Unit(c.Design, c.Facing, SpriteBank.Anim.Death, frame);
            if (sprite != null)
                DrawUnitSprite(sprite, c.Feet * PxPerUnit, 6f, new Color(1, 1, 1, alpha));
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

    public override void _ExitTree()
    {
        if (!_replayMode && _recorder != null) SaveReplay();   // always leave a recording behind
        _enet?.Close();
    }

    // The unit's true position, straight out of the sim.
    static Vector2 SimWorld(Unit u) =>
        new Vector2(u.X / (float)Fixed.One, u.Y / (float)Fixed.One);

    void SnapshotPositions()
    {
        foreach (var u in _shown.Units) _prevWorld[u.Id] = SimWorld(u);
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
    // The separation offset is added here too, so clicks land on the unit drawn
    // under the cursor, not the stacked sim position it was spread from.
    Vector2 WorldToScreen(Unit u) =>
        DrawWorld(u) * PxPerUnit + (_sepOffset.TryGetValue(u.Id, out var o) ? o : Vector2.Zero);

    Vector2 ScreenToWorld(Vector2 screen) => screen / PxPerUnit;

    // ---- Camera helpers ----------------------------------------------------
    // The world-pixel point under a screen point, inverting the exact transform
    // _Draw applies. All input goes through this, so hit-testing matches the view.
    Vector2 ScreenToCanvas(Vector2 screen)
    {
        var vp = GetViewportRect().Size;
        return _camCenter + (screen - vp / 2f) / _camZoom;
    }

    // The transform _Draw applies: world-pixel -> screen. Kept identical to the
    // inverse above.
    void ApplyCameraTransform()
    {
        var vp = GetViewportRect().Size;
        DrawSetTransform(vp / 2f - _camCenter * _camZoom, 0f, new Vector2(_camZoom, _camZoom));
    }

    // Open on your own base if you have one — on a map this size the geometric
    // centre is a long way from anything you own. Falls back to the map centre
    // (which is what a replay wants, having no "your" side).
    void CenterCamera()
    {
        foreach (var b in _shown.Buildings)
        {
            if (b.Owner != _myPlayer || b.Type != BuildingType.Keep) continue;
            _camCenter = new Vector2(b.CenterX, b.CenterY) * PxPerUnit;
            ClampCamera();
            return;
        }
        _camCenter = new Vector2(_shown.Map.Width, _shown.Map.Height) * (PxPerUnit / 2f);
    }

    // Turn this tick's long-range blows into flying arrows. Short (melee) blows
    // are skipped — the attacker is already next to the target.
    void CaptureShots()
    {
        foreach (var s in _shown.ShotsThisTick)
        {
            int dist = Fixed.VLen(s.ToX - s.FromX, s.ToY - s.FromY);
            int fx = Fixed.ToInt(s.FromX), fy = Fixed.ToInt(s.FromY);
            int tx = Fixed.ToInt(s.ToX), ty = Fixed.ToInt(s.ToY);

            // An arrow gives away a fight you cannot see. Show and sound it only
            // if an END of it is in sight — you do hear the shaft land next to
            // you even when whatever loosed it is still hidden.
            bool sawShooter = Lit(fx, fy), sawTarget = Lit(tx, ty);
            if (!sawShooter && !sawTarget) continue;

            var to = new Vector2(s.ToX, s.ToY) / (float)Fixed.One * PxPerUnit;

            _lastCombatAt = Time.GetTicksMsec() / 1000.0;

            if (dist < RangedShotDist)
            {
                // Close quarters: no arrow to draw, but very much something to
                // hear, and it belongs at the unit being hit.
                _sound?.Play(Sfx.MeleeHit, to);
                continue;
            }

            // A ranged exchange is two sounds in two places: the release where
            // the archer stands, the impact where it arrives. Each is only heard
            // if that END is visible.
            if (sawShooter)
                _sound?.Play(Sfx.BowShot, new Vector2(s.FromX, s.FromY) / (float)Fixed.One * PxPerUnit);
            if (sawTarget) _sound?.Play(Sfx.ArrowHit, to);

            _projectiles.Add(new Projectile
            {
                From = new Vector2(s.FromX, s.FromY) / (float)Fixed.One * PxPerUnit,
                To = to,
                Age = 0f,
                Life = 0.14f,          // fast — an arrow, not a lob
            });
        }
    }

    void AgeProjectiles(double delta)
    {
        for (int i = _projectiles.Count - 1; i >= 0; i--)
        {
            _projectiles[i].Age += (float)delta;
            if (_projectiles[i].Age >= _projectiles[i].Life) _projectiles.RemoveAt(i);
        }
    }

    // The minimap panel, in screen coordinates, pinned to the bottom-right.
    // One pixel per tile. Terrain is sealed for the life of the match, so this
    // runs once at startup and the minimap is a single blit thereafter.
    void BuildMinimapTerrain()
    {
        var map = _shown.Map;
        var img = Image.CreateEmpty(map.Width, map.Height, false, Image.Format.Rgba8);
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
                img.SetPixel(x, y, map.At(x, y) switch
                {
                    Terrain.Water => WaterColor,
                    Terrain.Rock => RockColor,
                    Terrain.Marsh => MarshColor,
                    _ => GroundColor,
                });
        _miniTerrain = ImageTexture.CreateFromImage(img);
    }

    // The minimap's fog layer: one pixel per tile, rebuilt only when the sim has
    // actually ticked. Filling a byte array and handing it over in one call keeps
    // this off the frame budget — 16k per-pixel calls at 60 fps would not.
    void UpdateMinimapFog()
    {
        if (!FogOn) { _miniFog = null; _fogBakedTick = -1; return; }
        if (_shown.TickNumber == _fogBakedTick && _miniFog != null) return;
        _fogBakedTick = _shown.TickNumber;

        var map = _shown.Map;
        _fogPixels ??= new byte[map.Width * map.Height * 4];

        for (int i = 0, y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++, i += 4)
            {
                _fogPixels[i] = 10; _fogPixels[i + 1] = 10; _fogPixels[i + 2] = 15;
                _fogPixels[i + 3] = _shown.Fog.IsVisible(_myPlayer, x, y) ? (byte)0
                                  : _shown.Fog.IsExplored(_myPlayer, x, y) ? (byte)140
                                  : (byte)255;
            }

        var img = Image.CreateFromData(map.Width, map.Height, false, Image.Format.Rgba8, _fogPixels);
        if (_miniFog == null) _miniFog = ImageTexture.CreateFromImage(img);
        else _miniFog.Update(img);
    }

    Rect2 MinimapRect()
    {
        var vp = GetViewportRect().Size;
        return new Rect2(vp.X - MiniSize - MiniMargin, vp.Y - MiniSize - MiniMargin, MiniSize, MiniSize);
    }

    // The whole battlefield at a glance. Called AFTER the camera transform has
    // been reset, so all of this is screen-space and never pans or zooms.
    void DrawMinimap()
    {
        var map = _shown.Map;
        var r = MinimapRect();
        float sx = r.Size.X / map.Width, sy = r.Size.Y / map.Height;
        var cell = new Vector2(Mathf.Ceil(sx), Mathf.Ceil(sy));   // ceil so tiles leave no gaps

        Vector2 At(float tileX, float tileY) => r.Position + new Vector2(tileX * sx, tileY * sy);

        // Terrain: baked once into a tile-per-pixel texture (see _miniTerrain)
        // and stretched to the panel, rather than thousands of rects a frame.
        if (_miniTerrain != null) DrawTextureRect(_miniTerrain, r, false);
        else DrawRect(r, GroundColor);

        // Fog, over the terrain but UNDER the markers, so your own units still
        // read clearly against dark ground. Rebuilt at the tick rate, not the
        // frame rate — see UpdateMinimapFog.
        if (FogOn && _miniFog != null) DrawTextureRect(_miniFog, r, false);

        foreach (var n in _shown.Nodes)
        {
            if (!Known(n.X, n.Y)) continue;
            DrawRect(new Rect2(At(n.X, n.Y), cell), ResourceColor(n.Type));
        }

        foreach (var b in _shown.Buildings)
        {
            if (b.Owner != _myPlayer && !KnownBuilding(b)) continue;
            DrawRect(new Rect2(At(b.X, b.Y), new Vector2(b.W * sx, b.H * sy)),
                     b.Owner == 1 ? new Color(0.3f, 0.7f, 1f) : new Color(1f, 0.45f, 0.35f));
        }

        // The minimap is the easiest place to leak the enemy's whole position, so
        // it gets the same rule as the main view: hidden means not drawn.
        foreach (var u in _shown.Units)
        {
            if (u.Owner != _myPlayer && !LitUnit(u)) continue;
            var p = At(u.X / (float)Fixed.One, u.Y / (float)Fixed.One);
            DrawRect(new Rect2(p - Vector2.One, new Vector2(3, 3)),
                     u.Owner == 1 ? new Color(0.45f, 0.8f, 1f) : new Color(1f, 0.55f, 0.45f));
        }

        // Where the camera is looking, in tiles.
        var vp = GetViewportRect().Size;
        var halfTiles = vp / (2f * _camZoom * PxPerUnit);
        var centreTiles = _camCenter / PxPerUnit;
        var view = new Rect2(At(centreTiles.X - halfTiles.X, centreTiles.Y - halfTiles.Y),
                             new Vector2(halfTiles.X * 2f * sx, halfTiles.Y * 2f * sy));
        // Clipped to the panel: near a map edge the view runs past the map, and an
        // outline spilling outside the minimap looks like a rendering bug.
        view = view.Intersection(r);
        if (view.Size.X > 0 && view.Size.Y > 0)
            DrawRect(view, new Color(1, 1, 1, 0.85f), false, 1f);

        DrawRect(r, new Color(1, 1, 1, 0.35f), false, 1f);   // frame, on top
    }

    // Drawn in world-pixel space (under the camera transform): a small head with a
    // short tail, moving from shooter to target.
    void DrawProjectiles()
    {
        var color = new Color(1f, 0.95f, 0.6f);
        foreach (var p in _projectiles)
        {
            float t = Mathf.Clamp(p.Age / p.Life, 0f, 1f);
            var pos = p.From.Lerp(p.To, t);
            var tail = p.From.Lerp(p.To, Mathf.Max(0f, t - 0.12f));
            DrawLine(tail, pos, color, 1.5f);
            DrawCircle(pos, 2f, color);
        }
    }

    // Zoom keeping the world point under the cursor fixed — the expected feel.
    void ZoomAt(float factor, Vector2 screen)
    {
        var before = ScreenToCanvas(screen);
        _camZoom = Mathf.Clamp(_camZoom * factor, MinZoom, MaxZoom);
        var after = ScreenToCanvas(screen);
        _camCenter += before - after;
        ClampCamera();
    }

    // Keep the centre within the map so the battlefield can't be lost off-screen.
    // Keep the VIEW on the map, not merely the centre. Clamping the centre alone
    // was harmless when the whole map fit in the window, but on a 128-tile map it
    // lets you scroll half a screen of void into frame. Along an axis too short
    // to fill the window there is nothing to scroll to, so centre it instead.
    void ClampCamera()
    {
        var map = new Vector2(_shown.Map.Width, _shown.Map.Height) * PxPerUnit;
        var half = GetViewportRect().Size / (2f * _camZoom);

        _camCenter = new Vector2(
            map.X > half.X * 2f ? Mathf.Clamp(_camCenter.X, half.X, map.X - half.X) : map.X / 2f,
            map.Y > half.Y * 2f ? Mathf.Clamp(_camCenter.Y, half.Y, map.Y - half.Y) : map.Y / 2f);
    }
}
