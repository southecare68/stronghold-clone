// Sound3D.cs — the Godot half of audio for the 3D renderer.
//
// The same split the 2D Sound used: Audio/Synth.cs is engine-agnostic and decides
// what each sound IS; this knows about Godot and decides when one is heard, how
// loud, and from where. Nothing here can affect the simulation — sound observes
// and never feeds back, exactly like interpolation and fog — so no checksum can
// move because of it.
//
// Positional audio is AudioStreamPlayer3D voices in world space. The listener is
// the Camera3D itself (a current Camera3D is Godot's 3D audio listener), so a
// fight on the far side of the map is quiet and one under the cursor is loud, for
// free, at any zoom. UI feedback plays through a plain non-positional player so it
// is centred — it happened to YOU, not somewhere on the map.

using Godot;
using System.Collections.Generic;
using Audio;

public sealed partial class Sound3D : Node3D
{
    // Enough for a busy melee without being able to drown the mix; the oldest is
    // stolen when they are all busy, so the most recent blows stay audible.
    const int Voices = 24;

    readonly Dictionary<Sfx, AudioStreamWav> _streams = new();
    readonly List<AudioStreamPlayer3D> _pool = new();
    AudioStreamPlayer _ui;                 // non-positional, for interface feedback
    readonly Dictionary<Sfx, ulong> _lastPlayed = new();
    int _next;

    public bool Muted;
    public float Volume = 0.85f;

    // --audio-log prints every voice that starts. Sound is the one part of this
    // renderer a screenshot cannot check, so this is how a headless session
    // confirms the right effects fire at the right moments.
    public bool LogPlays;
    readonly Dictionary<Sfx, int> _counts = new();
    public IReadOnlyDictionary<Sfx, int> Counts => _counts;

    // How soon the same effect may fire again. Without this, twenty soldiers
    // trading blows on one tick stack twenty identical impacts to the sample — a
    // loud crunch rather than a fight, and it eats the whole voice pool at once.
    static readonly Dictionary<Sfx, ulong> MinGap = new()
    {
        [Sfx.MeleeHit] = 55, [Sfx.BowShot] = 45, [Sfx.ArrowHit] = 45,
        [Sfx.UnitDeath] = 90, [Sfx.Deposit] = 120, [Sfx.BuildDone] = 120,
        [Sfx.Collapse] = 200, [Sfx.GateMove] = 200, [Sfx.Select] = 60,
        [Sfx.MoveOrder] = 60, [Sfx.AttackOrder] = 60, [Sfx.Denied] = 250,
        [Sfx.BuildPlace] = 80,
    };

    // Per-effect trim, in dB. Synth normalises every sound to the same peak, which
    // is right for the synthesizer and wrong for the mix: an order chirp must sit
    // well under a wall coming down.
    static readonly Dictionary<Sfx, float> Trim = new()
    {
        [Sfx.Deposit] = -13f, [Sfx.MoveOrder] = -9f, [Sfx.Select] = -8f,
        [Sfx.AttackOrder] = -7f, [Sfx.MeleeHit] = -5f, [Sfx.BowShot] = -6f,
        [Sfx.ArrowHit] = -7f, [Sfx.UnitDeath] = -4f, [Sfx.BuildPlace] = -3f,
        [Sfx.BuildDone] = -6f, [Sfx.GateMove] = -4f, [Sfx.Collapse] = 0f,
        [Sfx.Denied] = -6f,
    };

    public override void _Ready()
    {
        foreach (var kind in Synth.All)
            _streams[kind] = new AudioStreamWav
            {
                Data = Synth.RenderBytes(kind),
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = Synth.SampleRate,
                Stereo = false,
                LoopMode = AudioStreamWav.LoopModeEnum.Disabled,
            };

        for (int i = 0; i < Voices; i++)
        {
            // UnitSize is the radius over which the sound stays near full volume;
            // beyond it the roll-off begins. Sized to the map (~128 tiles) so a
            // fight in view is clearly heard and one across the map is faint.
            var p = new AudioStreamPlayer3D { UnitSize = 14f, MaxDb = 3f, MaxDistance = 140f };
            AddChild(p);
            _pool.Add(p);
        }

        _ui = new AudioStreamPlayer();
        AddChild(_ui);

        GD.Print($"[audio] {_streams.Count} effects synthesised, {Voices} 3D voices ready");
    }

    // A sound with a place in the world. The caller decides whether the player is
    // allowed to hear it at all (fog); this decides whether there is room for it.
    public void Play(Sfx kind, Vector3 worldPos)
    {
        if (Muted || Volume <= 0f || !Gate(kind)) return;

        var voice = Take();
        voice.Stream = _streams[kind];
        voice.Position = worldPos;
        voice.VolumeDb = Mathf.LinearToDb(Volume) + Trim.GetValueOrDefault(kind, 0f);
        voice.Play();
        Logged(kind);
    }

    // Interface feedback — selecting, ordering, being refused. Non-positional, so
    // it is centred and unattenuated.
    public void PlayUi(Sfx kind)
    {
        if (Muted || Volume <= 0f || _ui == null || !Gate(kind)) return;
        _ui.Stream = _streams[kind];
        _ui.VolumeDb = Mathf.LinearToDb(Volume) + Trim.GetValueOrDefault(kind, 0f);
        _ui.Play();
        Logged(kind);
    }

    // The rate-limit gate, shared by both play paths.
    bool Gate(Sfx kind)
    {
        ulong now = Time.GetTicksMsec();
        if (_lastPlayed.TryGetValue(kind, out ulong last) &&
            MinGap.TryGetValue(kind, out ulong gap) && now - last < gap) return false;
        _lastPlayed[kind] = now;
        return true;
    }

    void Logged(Sfx kind)
    {
        _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
        if (LogPlays) GD.Print($"[audio] {kind}  total={_counts[kind]}");
    }

    // Round-robin, preferring a free voice; steal the oldest otherwise so the most
    // RECENT events stay audible.
    AudioStreamPlayer3D Take()
    {
        for (int i = 0; i < _pool.Count; i++)
        {
            var p = _pool[(_next + i) % _pool.Count];
            if (!p.Playing) { _next = (_next + i + 1) % _pool.Count; return p; }
        }
        var stolen = _pool[_next];
        _next = (_next + 1) % _pool.Count;
        return stolen;
    }
}
