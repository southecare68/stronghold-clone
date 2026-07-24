// Music.cs — the score, also generated rather than sourced.
//
// Split in two on purpose, and the split is the whole design:
//
//   Compose(mood) -> a list of Notes.  WHAT is played: pitches, when, how long,
//                    on which voice. Pure data, no audio, no floats that matter.
//   Render(mood)  -> PCM.              HOW it sounds: the instruments.
//
// Sound effects did not need this, but music does, because the interesting
// mistakes in music are musical. "Is every pitch actually in the mode?" and "does
// the harmony change on the bar line?" are questions you can only ask of notes,
// not of a waveform — by the time it is PCM, a wrong note is just a number. So
// tests/Audio asserts against Compose and never has to guess from a spectrum.
//
// Three moods, cross-faded at runtime by the game (see Scripts/Music.cs):
// Calm while you build, Tension when something of theirs is in sight, Battle
// when blows are landing.
//
// Everything LOOPS seamlessly, which is not a detail — a click every twenty
// seconds is the fastest way to make a soundtrack unbearable. Two things buy it:
// the loop length is an exact whole number of samples (the tempos are chosen so
// samples-per-beat divides evenly), and notes that run past the end WRAP round to
// the beginning rather than being cut off. A note that starts on the last beat
// finishes over the first, exactly as it would on the next repeat.
//
// Like everything else in Audio/, this is engine-agnostic and cannot touch the
// simulation.

using System;
using System.Collections.Generic;

namespace Audio
{
    public enum Mood { Calm, Tension, Battle }

    public enum Voice
    {
        Drone,    // the horizon: a low sustained fifth
        Pad,      // held chord tones
        Bass,     // the root, plucked low
        Pluck,    // melody and arpeggios (Karplus-Strong — a lute, near enough)
        Kick,
        Snare,
    }

    public readonly struct Note
    {
        public readonly int Start;      // samples from the top of the loop
        public readonly int Length;     // samples
        public readonly int Midi;       // ignored by Kick/Snare
        public readonly Voice Voice;
        public readonly float Gain;

        public Note(Voice voice, int start, int length, int midi, float gain)
        {
            Voice = voice; Start = start; Length = length; Midi = midi; Gain = gain;
        }
    }

    public static class Music
    {
        public const int SampleRate = 22050;
        public const int Bars = 8;
        public const int BeatsPerBar = 4;

        public static readonly Mood[] All = { Mood.Calm, Mood.Tension, Mood.Battle };

        // Chosen so SampleRate*60/Bpm is an EXACT integer. 22050*60 = 1,323,000,
        // and 72/100/140 all divide it cleanly. A tempo that does not (132, say)
        // leaves a fractional sample per beat, which accumulates across the loop
        // and puts the last bar out of step with the first — an audible stumble
        // once a cycle, and one that is maddening to track down later.
        public static int Bpm(Mood m) => m switch
        {
            Mood.Calm => 72,
            Mood.Tension => 100,
            _ => 140,
        };

        public static int SamplesPerBeat(Mood m) => SampleRate * 60 / Bpm(m);
        public static int LoopSamples(Mood m) => SamplesPerBeat(m) * BeatsPerBar * Bars;

        // D Dorian — the medieval mode, and the reason this does not sound like
        // film-score minor. Its raised sixth (B natural against a D root) is the
        // whole character. Battle drops to Aeolian: flattening that sixth to Bb
        // is a one-note change that takes the brightness straight out.
        static readonly int[] Dorian = { 0, 2, 3, 5, 7, 9, 10 };
        static readonly int[] Aeolian = { 0, 2, 3, 5, 7, 8, 10 };
        const int Tonic = 50;          // D3

        static int[] ScaleOf(Mood m) => m == Mood.Battle ? Aeolian : Dorian;

        // i - VII - III - IV, twice. A modal loop with no leading tone anywhere in
        // it, so it turns over forever without ever asking to resolve — which is
        // exactly what background music for a strategy game has to do.
        static readonly int[] Progression = { 0, 6, 2, 3, 0, 6, 2, 3 };

        // Scale degree -> MIDI, carrying octaves so degree 9 is degree 2 an octave
        // up rather than an index out of range.
        static int Pitch(int[] scale, int degree, int octave = 0)
        {
            int oct = octave + (int)Math.Floor(degree / (double)scale.Length);
            int idx = ((degree % scale.Length) + scale.Length) % scale.Length;
            return Tonic + scale[idx] + 12 * oct;
        }

        // ---- composition ----------------------------------------------------

        public static List<Note> Compose(Mood mood)
        {
            var notes = new List<Note>();
            var scale = ScaleOf(mood);
            int spb = SamplesPerBeat(mood);
            int loop = LoopSamples(mood);
            int barLen = spb * BeatsPerBar;

            // Seeded per mood, so a given mood is the same piece every run. As
            // with the effects this is not a determinism requirement — music can
            // never reach the simulation — but a score that differed run to run
            // could not be tested and could not be revised.
            var rng = new Rng(0x3B0B0000u + (uint)mood);

            // The drone spans the entire loop. Its envelope is one full sine
            // cycle over that span, which is periodic BY CONSTRUCTION — so the
            // level at the loop point matches on both sides and the seam is
            // inaudible. An ordinary swell would step here every time round.
            if (mood != Mood.Battle)
                notes.Add(new Note(Voice.Drone, 0, loop, Tonic - 12, 0.32f));
            else
                notes.Add(new Note(Voice.Drone, 0, loop, Tonic - 12, 0.22f));

            int prevDegree = 4;      // start the melody on the fifth

            for (int bar = 0; bar < Bars; bar++)
            {
                int barStart = bar * barLen;
                int chord = Progression[bar % Progression.Length];

                // Pad: the triad, held for the bar. Voiced above the bass so the
                // two do not fight for the same octave.
                foreach (int d in new[] { 0, 2, 4 })
                    notes.Add(new Note(Voice.Pad, barStart, barLen,
                                       Pitch(scale, chord + d, 1), 0.15f));

                // Bass: root on the strong beats. Battle takes every beat, which
                // is most of why it feels like it is moving faster than it is.
                int[] bassBeats = mood == Mood.Battle
                    ? new[] { 0, 1, 2, 3 }
                    : mood == Mood.Tension ? new[] { 0, 2 } : new[] { 0 };
                foreach (int beat in bassBeats)
                    notes.Add(new Note(Voice.Bass, barStart + beat * spb, spb,
                                       Pitch(scale, chord, 0), 0.30f));

                // Melody. Chord tones on the strong beats and stepwise motion in
                // between: the two rules that separate a tune from noodling. The
                // RNG chooses WHICH chord tone and WHICH direction, so it varies
                // without ever wandering out of the harmony.
                int notesInBar = mood switch { Mood.Calm => 2, Mood.Tension => 4, _ => 8 };
                int step = barLen / notesInBar;

                for (int i = 0; i < notesInBar; i++)
                {
                    bool strong = i % 2 == 0;
                    int degree;
                    if (strong)
                    {
                        // A tone of the current chord, in the melody's octave.
                        int[] tones = { chord, chord + 2, chord + 4 };
                        degree = tones[rng.NextInt(tones.Length)] + 7;
                    }
                    else
                    {
                        // Move by a step, and turn back if we have drifted high or
                        // low — a melody that only ever walks one way runs off the
                        // end of the register.
                        int dir = rng.NextInt(2) == 0 ? -1 : 1;
                        if (prevDegree > 13) dir = -1;
                        if (prevDegree < 7) dir = 1;
                        degree = prevDegree + dir;
                    }
                    prevDegree = degree;

                    // Let it ring past its slot. Overlapping tails are what makes
                    // a plucked instrument sound like an instrument rather than a
                    // sequence of separate events.
                    int len = step * 2;
                    float gain = strong ? 0.26f : 0.19f;
                    notes.Add(new Note(Voice.Pluck, barStart + i * step, len,
                                       Pitch(scale, degree), gain));
                }

                // Percussion: battle only. It is the single clearest signal that
                // the situation has changed, which is the point of having moods.
                if (mood == Mood.Battle)
                {
                    notes.Add(new Note(Voice.Kick, barStart, spb, 0, 0.55f));
                    notes.Add(new Note(Voice.Kick, barStart + 2 * spb, spb, 0, 0.45f));
                    notes.Add(new Note(Voice.Snare, barStart + spb, spb, 0, 0.34f));
                    notes.Add(new Note(Voice.Snare, barStart + 3 * spb, spb, 0, 0.34f));
                }
                else if (mood == Mood.Tension)
                {
                    // No kit — just a heartbeat on the downbeat. Enough to feel
                    // hurried without announcing a fight that has not started.
                    notes.Add(new Note(Voice.Kick, barStart, spb, 0, 0.26f));
                }
            }

            return notes;
        }

        // ---- rendering ------------------------------------------------------

        const float Peak = 0.55f;      // below the effects, so they always cut through

        public static short[] Render(Mood mood)
        {
            int loop = LoopSamples(mood);
            var buf = new float[loop];

            foreach (var n in Compose(mood))
            {
                switch (n.Voice)
                {
                    case Voice.Drone: RenderDrone(buf, n); break;
                    case Voice.Pad: RenderPad(buf, n); break;
                    case Voice.Bass:
                    case Voice.Pluck: RenderPluck(buf, n); break;
                    case Voice.Kick: RenderKick(buf, n); break;
                    default: RenderSnare(buf, n); break;
                }
            }

            Normalise(buf);
            return ToPcm(buf);
        }

        public static byte[] RenderBytes(Mood mood)
        {
            var s = Render(mood);
            var bytes = new byte[s.Length * 2];
            for (int i = 0; i < s.Length; i++)
            {
                bytes[i * 2] = (byte)(s[i] & 0xff);
                bytes[i * 2 + 1] = (byte)((s[i] >> 8) & 0xff);
            }
            return bytes;
        }

        // The one line that makes the loop seamless: everything is written with a
        // wrapping index, so a note's tail continues over the top of the buffer
        // instead of being chopped off at it.
        static void Add(float[] buf, int at, float v) => buf[((at % buf.Length) + buf.Length) % buf.Length] += v;

        static float Freq(int midi) => 440f * MathF.Pow(2f, (midi - 69) / 12f);

        // A low fifth, barely moving. Its amplitude traces exactly one sine cycle
        // over the loop, so the value either side of the seam agrees.
        static void RenderDrone(float[] buf, Note n)
        {
            float f = Freq(n.Midi);
            for (int i = 0; i < n.Length; i++)
            {
                float t = i / (float)SampleRate;
                float env = 0.75f + 0.25f * MathF.Sin(2f * MathF.PI * i / n.Length);
                float v = MathF.Sin(2f * MathF.PI * f * t) * 0.7f
                        + MathF.Sin(2f * MathF.PI * f * 1.5f * t) * 0.3f;   // the fifth
                Add(buf, n.Start + i, v * env * n.Gain);
            }
        }

        // Three slightly detuned partials through a slow attack and release —
        // enough to read as a sustained ensemble rather than an organ.
        static void RenderPad(float[] buf, Note n)
        {
            float f = Freq(n.Midi);
            float[] detune = { 0.997f, 1f, 1.004f };
            float attack = SampleRate * 0.25f;
            float release = SampleRate * 0.5f;

            for (int i = 0; i < n.Length; i++)
            {
                float t = i / (float)SampleRate;
                float env = MathF.Min(1f, i / attack);
                float left = n.Length - i;
                if (left < release) env *= left / release;

                float v = 0f;
                foreach (float d in detune) v += MathF.Sin(2f * MathF.PI * f * d * t);
                Add(buf, n.Start + i, v / detune.Length * env * n.Gain);
            }
        }

        // Karplus-Strong. A buffer of noise one period long, read round and round
        // while each pass is averaged with its neighbour: the high partials die
        // first and the fundamental survives, which is exactly what a plucked
        // string does. Ten lines for a lute.
        static void RenderPluck(float[] buf, Note n)
        {
            float f = Freq(n.Midi);
            int period = Math.Max(2, (int)(SampleRate / f));
            var line = new float[period];

            // Seeded from the note itself, so the same note always plucks the
            // same way and the piece is reproducible.
            var rng = new Rng(0x9E3779B9u ^ (uint)(n.Midi * 2654435761u) ^ (uint)n.Start);
            for (int i = 0; i < period; i++) line[i] = rng.NextFloat() * 2f - 1f;

            // Longer notes need a slower decay or they vanish before their slot
            // ends; bass notes hold longer than melody notes.
            float damp = n.Voice == Voice.Bass ? 0.998f : 0.996f;
            int idx = 0;

            for (int i = 0; i < n.Length; i++)
            {
                float cur = line[idx];
                float next = line[(idx + 1) % period];
                float v = (cur + next) * 0.5f * damp;
                line[idx] = v;
                idx = (idx + 1) % period;

                // A short fade at the very end so a note that is still ringing
                // when its slot expires does not stop on a step.
                float tail = n.Length - i;
                float env = tail < 400 ? tail / 400f : 1f;
                Add(buf, n.Start + i, cur * env * n.Gain);
            }
        }

        static void RenderKick(float[] buf, Note n)
        {
            int len = Math.Min(n.Length, (int)(SampleRate * 0.18f));
            float phase = 0f;
            for (int i = 0; i < len; i++)
            {
                float k = i / (float)len;
                float f = 115f * MathF.Exp(-3.2f * k) + 42f;      // the drop that makes it a kick
                phase += 2f * MathF.PI * f / SampleRate;
                float env = MathF.Exp(-5.5f * k);
                Add(buf, n.Start + i, MathF.Sin(phase) * env * n.Gain);
            }
        }

        static void RenderSnare(float[] buf, Note n)
        {
            int len = Math.Min(n.Length, (int)(SampleRate * 0.13f));
            var rng = new Rng(0x5EED0001u ^ (uint)n.Start);
            float hp = 0f, prev = 0f;
            for (int i = 0; i < len; i++)
            {
                float k = i / (float)len;
                float x = rng.NextFloat() * 2f - 1f;
                hp = 0.82f * (hp + x - prev);                      // brighten it
                prev = x;
                float env = MathF.Exp(-7f * k);
                Add(buf, n.Start + i, (hp * 0.8f + MathF.Sin(2f * MathF.PI * 185f * i / SampleRate) * 0.2f)
                                      * env * n.Gain);
            }
        }

        static void Normalise(float[] b)
        {
            float max = 0f;
            foreach (float v in b) max = MathF.Max(max, MathF.Abs(v));
            if (max < 1e-6f) return;
            float g = Peak / max;
            for (int i = 0; i < b.Length; i++) b[i] *= g;
        }

        static short[] ToPcm(float[] b)
        {
            var outp = new short[b.Length];
            for (int i = 0; i < b.Length; i++)
                outp[i] = (short)Math.Clamp(MathF.Round(b[i] * 32767f), -32768f, 32767f);
            return outp;
        }

        // The same private generator the effects use, for the same reason: keeping
        // it well away from Sim.Rng means nobody has to wonder whether writing a
        // tune could move the dice that decide a damage roll.
        sealed class Rng
        {
            uint _s;
            public Rng(uint seed) { _s = seed == 0 ? 1u : seed; }
            uint Next() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
            public float NextFloat() => Next() / 4294967296f;
            public int NextInt(int max) => (int)(Next() % (uint)max);
        }
    }
}
