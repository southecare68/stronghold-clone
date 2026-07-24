// MusicPlayer.cs — the Godot half of the score.
//
// Audio/Music.cs writes the piece; this plays it and decides nothing about the
// notes. Two jobs only:
//
//   1. Loop each track without a seam. The buffers are built to loop (see
//      Music.cs), so this just has to hand Godot the right loop points.
//   2. Cross-fade between moods. A hard cut between tracks is worse than no
//      adaptive music at all — it draws attention to the machinery instead of
//      the fight. Two players, one fading up while the other fades down.
//
// Non-positional, unlike the effects: music is not coming from anywhere on the
// map, so an AudioStreamPlayer rather than an AudioStreamPlayer2D. It is also
// mixed deliberately under the effects, because a soundtrack that buries the
// sound of your own army dying is worse than silence.

using Godot;
using System.Collections.Generic;
using Audio;

public sealed partial class MusicPlayer : Node
{
    // Long enough that the change reads as the situation shifting rather than a
    // track ending, short enough that battle music arrives while the battle is
    // still on. Two seconds is about the shortest that does not sound like a cut.
    const float FadeSeconds = 2.2f;

    readonly Dictionary<Mood, AudioStreamWav> _tracks = new();
    AudioStreamPlayer _a, _b;
    bool _bIsCurrent;

    Mood _mood = Mood.Calm;
    float _fade = 1f;            // 0..1, how far the incoming player has come up

    public bool Enabled = true;
    public float Volume = 0.55f;   // under the effects by default
    public Mood Current => _mood;

    AudioStreamPlayer Incoming => _bIsCurrent ? _b : _a;
    AudioStreamPlayer Outgoing => _bIsCurrent ? _a : _b;

    public override void _Ready()
    {
        foreach (var mood in Music.All)
        {
            var pcm = Music.RenderBytes(mood);
            int samples = pcm.Length / 2;
            _tracks[mood] = new AudioStreamWav
            {
                Data = pcm,
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = Music.SampleRate,
                Stereo = false,
                // Loop the WHOLE buffer. The tracks are composed so that notes
                // running past the end wrap round to the beginning, which is what
                // makes this seam inaudible rather than merely quiet.
                LoopMode = AudioStreamWav.LoopModeEnum.Forward,
                LoopBegin = 0,
                LoopEnd = samples,
            };
        }

        _a = new AudioStreamPlayer();
        _b = new AudioStreamPlayer();
        AddChild(_a);
        AddChild(_b);

        _bIsCurrent = false;
        _a.Stream = _tracks[_mood];
        _a.VolumeDb = Db(Volume);
        if (Enabled) _a.Play();

        GD.Print($"[music] {_tracks.Count} tracks composed: " +
                 string.Join(", ", System.Array.ConvertAll(Music.All,
                     m => $"{m} {Music.LoopSamples(m) / (float)Music.SampleRate:0.0}s@{Music.Bpm(m)}bpm")));
    }

    public void SetMood(Mood mood)
    {
        if (mood == _mood || !Enabled) return;

        // Swap which player is "current" and start the new one silent; _Process
        // walks the cross-fade from here.
        _bIsCurrent = !_bIsCurrent;
        _mood = mood;
        _fade = 0f;

        var incoming = Incoming;
        incoming.Stream = _tracks[mood];
        incoming.VolumeDb = -60f;
        incoming.Play();
    }

    public override void _Process(double delta)
    {
        if (!Enabled) return;

        if (_fade < 1f)
        {
            _fade = Mathf.Min(1f, _fade + (float)delta / FadeSeconds);
            // Equal-power rather than linear: two tracks cross-faded linearly dip
            // in loudness through the middle, because power goes as the square.
            float up = Mathf.Sin(_fade * Mathf.Pi * 0.5f);
            float down = Mathf.Cos(_fade * Mathf.Pi * 0.5f);

            Incoming.VolumeDb = Db(Volume * up);
            Outgoing.VolumeDb = Db(Volume * down);

            if (_fade >= 1f) Outgoing.Stop();
        }
        else
        {
            Incoming.VolumeDb = Db(Volume);
        }
    }

    public void SetEnabled(bool on)
    {
        Enabled = on;
        if (!on) { _a.Stop(); _b.Stop(); return; }
        _fade = 1f;
        var p = Incoming;
        p.Stream = _tracks[_mood];
        p.VolumeDb = Db(Volume);
        p.Play();
    }

    // Silence has to be actual silence, not a very quiet track — LinearToDb(0) is
    // negative infinity, which Godot does not take kindly to.
    static float Db(float linear) => linear <= 0.0005f ? -60f : Mathf.LinearToDb(linear);
}
