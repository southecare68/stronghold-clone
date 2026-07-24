// Sound.cs — the Godot half of audio: turn Synth's buffers into playable voices.
//
// The split is the same one used everywhere else in this project. Audio/Synth.cs
// is engine-agnostic and decides what each sound IS; this file knows about Godot
// and decides when one is heard, how loud, and from where. Nothing here can
// affect the simulation — sound observes and never feeds back, exactly like
// interpolation and the minimap — so no checksum can move because of it.
//
// Positional audio is done with AudioStreamPlayer2D against an AudioListener2D
// parked at the camera centre. The game draws through a manual transform rather
// than a Camera2D node, so there is no listener for Godot to infer; supplying one
// explicitly is what makes a fight on the far side of the map sound like it is
// over there instead of in your ear.

using Godot;
using System.Collections.Generic;
using Audio;

public sealed partial class Sound : Node2D
{
    // Enough for a busy melee without being able to drown the mix. When they are
    // all busy the oldest is stolen, which is the right failure: a battle should
    // sound like a battle, not like a queue.
    const int Voices = 24;

    readonly Dictionary<Sfx, AudioStreamWav> _streams = new();
    readonly List<AudioStreamPlayer2D> _pool = new();
    readonly Dictionary<Sfx, ulong> _lastPlayed = new();
    AudioListener2D _listener;
    int _next;

    public bool Muted;
    public float Volume = 0.85f;

    // --audio-log prints every voice that starts. Sound is the one part of this
    // project that cannot be checked from a screenshot, so this is how a headless
    // session confirms the right effects fire at the right moments.
    public bool LogPlays;
    readonly Dictionary<Sfx, int> _counts = new();
    public IReadOnlyDictionary<Sfx, int> Counts => _counts;

    // How soon the same effect may fire again. Without this, twenty soldiers
    // trading blows on the same tick produce twenty identical impacts stacked to
    // the sample — which sums to a loud crunch rather than a fight, and eats the
    // whole voice pool for one instant of combat.
    static readonly Dictionary<Sfx, ulong> MinGap = new()
    {
        [Sfx.MeleeHit] = 55,
        [Sfx.BowShot] = 45,
        [Sfx.ArrowHit] = 45,
        [Sfx.UnitDeath] = 90,
        [Sfx.Deposit] = 120,
        [Sfx.BuildDone] = 120,
        [Sfx.Collapse] = 200,
        [Sfx.GateMove] = 200,
        [Sfx.Select] = 60,
        [Sfx.MoveOrder] = 60,
        [Sfx.AttackOrder] = 60,
        [Sfx.Denied] = 250,
        [Sfx.BuildPlace] = 80,
    };

    // Per-effect trim, in dB. Synth normalises every sound to the same peak,
    // which is right for the synthesizer and wrong for the mix: a deposit tick
    // that fires every few seconds must sit well under a wall coming down.
    static readonly Dictionary<Sfx, float> Trim = new()
    {
        [Sfx.Deposit] = -13f,
        [Sfx.MoveOrder] = -9f,
        [Sfx.Select] = -8f,
        [Sfx.AttackOrder] = -7f,
        [Sfx.MeleeHit] = -5f,
        [Sfx.BowShot] = -6f,
        [Sfx.ArrowHit] = -7f,
        [Sfx.UnitDeath] = -4f,
        [Sfx.BuildPlace] = -3f,
        [Sfx.BuildDone] = -6f,
        [Sfx.GateMove] = -4f,
        [Sfx.Collapse] = 0f,
        [Sfx.Denied] = -6f,
    };

    public override void _Ready()
    {
        foreach (var kind in Synth.All)
        {
            var wav = new AudioStreamWav
            {
                Data = Synth.RenderBytes(kind),
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = Synth.SampleRate,
                Stereo = false,
                LoopMode = AudioStreamWav.LoopModeEnum.Disabled,
            };
            _streams[kind] = wav;
        }

        for (int i = 0; i < Voices; i++)
        {
            var p = new AudioStreamPlayer2D { Attenuation = 1f, MaxDistance = 2000f };
            AddChild(p);
            _pool.Add(p);
        }

        _listener = new AudioListener2D();
        AddChild(_listener);
        _listener.MakeCurrent();

        GD.Print($"[audio] {_streams.Count} effects synthesised, {Voices} voices ready");
    }

    // Follow the camera. Called every frame by Main, because what you can hear
    // should match what you are looking at — including at zoom, where the same
    // world distance covers far more of the screen and ought to stay audible.
    public void Listen(Vector2 cameraWorldPx, float audibleRadiusPx)
    {
        if (_listener == null) return;
        _listener.Position = cameraWorldPx;
        foreach (var p in _pool) p.MaxDistance = audibleRadiusPx;
    }

    // A sound with a place in the world. Main decides whether the player is
    // allowed to hear it at all (fog); this decides whether there is room for it.
    public void Play(Sfx kind, Vector2 worldPx)
    {
        if (Muted || Volume <= 0f) return;

        ulong now = Time.GetTicksMsec();
        if (_lastPlayed.TryGetValue(kind, out ulong last) &&
            MinGap.TryGetValue(kind, out ulong gap) && now - last < gap) return;
        _lastPlayed[kind] = now;

        var voice = Take();
        voice.Stream = _streams[kind];
        voice.Position = worldPx;
        voice.VolumeDb = Mathf.LinearToDb(Volume) + Trim.GetValueOrDefault(kind, 0f);
        voice.Play();

        _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
        if (LogPlays)
            GD.Print($"[audio] {kind} at ({worldPx.X:0},{worldPx.Y:0})  " +
                     $"{voice.VolumeDb:0.0} dB  playing={voice.Playing}  total={_counts[kind]}");
    }

    // Interface feedback — selecting, ordering, being refused. Played at the
    // listener so it is centred and unattenuated: it happened to YOU, not
    // somewhere on the map.
    public void PlayUi(Sfx kind) => Play(kind, _listener?.Position ?? Vector2.Zero);

    // Round-robin, preferring a free voice. Stealing the oldest rather than
    // dropping the sound keeps the most RECENT events audible, which is what a
    // player is reacting to.
    AudioStreamPlayer2D Take()
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
