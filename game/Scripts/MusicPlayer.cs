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

    // The player's own track for the peaceful/tension moods. Battle stays the
    // generated combat track. Nothing else in the game cares which is which.
    const string FanfarePath = "res://Music/Stone_Hall_Fanfare.wav";

    public override void _Ready()
    {
        // Battle is always the generated combat track.
        _tracks[Mood.Battle] = Generated(Mood.Battle);

        // Calm and Tension play the supplied fanfare. Both point at the SAME
        // stream so moving between "building" and "enemy in sight" does not
        // restart the music (see SetMood). If the file cannot be loaded, fall
        // back to the generated tracks so there is always music.
        var fanfare = LoadFanfare();
        if (fanfare != null)
        {
            _tracks[Mood.Calm] = fanfare;
            _tracks[Mood.Tension] = fanfare;
            GD.Print("[music] peaceful/tension = Stone_Hall_Fanfare.wav, battle = generated");
        }
        else
        {
            _tracks[Mood.Calm] = Generated(Mood.Calm);
            _tracks[Mood.Tension] = Generated(Mood.Tension);
            GD.Print("[music] fanfare not found — using the generated tracks");
        }

        _a = new AudioStreamPlayer();
        _b = new AudioStreamPlayer();
        AddChild(_a);
        AddChild(_b);

        _bIsCurrent = false;
        _a.Stream = _tracks[_mood];
        _a.VolumeDb = Db(Volume);
        if (Enabled) _a.Play();
    }

    // A generated track, looped whole (the composed tracks wrap seamlessly).
    static AudioStreamWav Generated(Mood mood)
    {
        var pcm = Music.RenderBytes(mood);
        return new AudioStreamWav
        {
            Data = pcm,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Music.SampleRate,
            Stereo = false,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward,
            LoopBegin = 0,
            LoopEnd = pcm.Length / 2,
        };
    }

    // Load the supplied WAV straight from disk (LoadFromFile reads the raw file,
    // so it needs no Godot import step) and set it to loop. Returns null if the
    // file is missing or unreadable, so the caller can fall back.
    static AudioStreamWav LoadFanfare()
    {
        if (!Godot.FileAccess.FileExists(FanfarePath)) return null;
        var wav = AudioStreamWav.LoadFromFile(FanfarePath);
        if (wav == null) return null;

        int bytesPerFrame = (wav.Format == AudioStreamWav.FormatEnum.Format16Bits ? 2 : 1)
                          * (wav.Stereo ? 2 : 1);
        if (bytesPerFrame > 0)
        {
            wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            wav.LoopBegin = 0;
            wav.LoopEnd = wav.Data.Length / bytesPerFrame;
        }
        return wav;
    }

    public void SetMood(Mood mood)
    {
        if (mood == _mood || !Enabled) return;

        // If the new mood plays the SAME track as the current one — Calm and
        // Tension both being the fanfare — there is nothing to switch. Just adopt
        // the label, so an enemy wandering into view does not restart the music.
        if (_tracks[mood] == _tracks[_mood]) { _mood = mood; return; }

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
