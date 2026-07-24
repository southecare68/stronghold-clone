// Synth.cs — every sound in the game, generated from arithmetic.
//
// There are no audio files in this repo, and that is deliberate rather than a
// shortcut. The project's premise is its own art, and a handful of short RTS
// effects — a bowstring, a sword landing, a gate dragging open — are cheap to
// describe as noise and envelopes and expensive to store as binary blobs nobody
// can diff or review. Generating them means the "assets" are source code: you
// can read what a sound IS, change one number to retune it, and a test can make
// assertions about it.
//
// Engine-agnostic on purpose, exactly like Net/: this file knows nothing about
// Godot, produces plain 16-bit PCM, and so `tests/Audio` can check every sound
// without a display or an audio device. The Godot layer (Scripts/Sound.cs) only
// wraps these buffers in an AudioStreamWAV and decides when to play them.
//
// Nothing here is game state. Sound is a rendering concern in exactly the sense
// interpolation and the minimap are: it observes the simulation and never feeds
// back into it, so no checksum can move because of anything in this file.

using System;

namespace Audio
{
    public enum Sfx
    {
        Select,         // units acknowledged a box-select
        MoveOrder,      // a move was issued
        AttackOrder,    // an attack was issued
        MeleeHit,       // a blow landed at close quarters
        BowShot,        // a bowstring released
        ArrowHit,       // an arrow arrived
        UnitDeath,      // a unit fell
        BuildPlace,     // a structure was set down
        BuildDone,      // a unit finished training
        Deposit,        // a load was banked at the drop-off
        GateMove,       // a gatehouse opened or closed
        Collapse,       // a building came down
        Denied,         // an order the game would not accept
    }

    public static class Synth
    {
        // 22.05 kHz mono is ample for short effects and a quarter of the memory
        // of stereo 44.1. Nothing here has content near the Nyquist limit.
        public const int SampleRate = 22050;

        public static readonly Sfx[] All =
        {
            Sfx.Select, Sfx.MoveOrder, Sfx.AttackOrder, Sfx.MeleeHit, Sfx.BowShot,
            Sfx.ArrowHit, Sfx.UnitDeath, Sfx.BuildPlace, Sfx.BuildDone, Sfx.Deposit,
            Sfx.GateMove, Sfx.Collapse, Sfx.Denied,
        };

        // Peak level every sound is normalised to. Short of full scale so that
        // several voices at once do not sum into hard clipping.
        const float Peak = 0.62f;

        // Every sound is ramped to silence over its last few milliseconds. Left
        // out, a buffer that stops mid-waveform ends on a step, and a step is a
        // click — the single most common way a synthesised effect sounds cheap.
        const float TailFade = 0.004f;

        public static short[] Render(Sfx kind)
        {
            float seconds = kind switch
            {
                Sfx.Select => 0.16f,
                Sfx.MoveOrder => 0.10f,
                Sfx.AttackOrder => 0.14f,
                Sfx.MeleeHit => 0.20f,
                Sfx.BowShot => 0.16f,
                Sfx.ArrowHit => 0.12f,
                Sfx.UnitDeath => 0.45f,
                Sfx.BuildPlace => 0.28f,
                Sfx.BuildDone => 0.34f,
                Sfx.Deposit => 0.09f,
                Sfx.GateMove => 0.55f,
                Sfx.Collapse => 0.80f,
                _ => 0.16f,
            };

            var buf = new float[(int)(seconds * SampleRate)];
            // One fixed seed per sound, so a given effect is byte-identical every
            // run and on every machine. Not a determinism requirement — audio can
            // never reach the simulation — but a sound that differed run to run
            // could not be tested, and re-tuning one would be guesswork.
            var rng = new Noise(0x51F70000u + (uint)kind);

            switch (kind)
            {
                case Sfx.Select: Build_Select(buf); break;
                case Sfx.MoveOrder: Build_MoveOrder(buf); break;
                case Sfx.AttackOrder: Build_AttackOrder(buf); break;
                case Sfx.MeleeHit: Build_MeleeHit(buf, rng); break;
                case Sfx.BowShot: Build_BowShot(buf, rng); break;
                case Sfx.ArrowHit: Build_ArrowHit(buf, rng); break;
                case Sfx.UnitDeath: Build_UnitDeath(buf, rng); break;
                case Sfx.BuildPlace: Build_BuildPlace(buf, rng); break;
                case Sfx.BuildDone: Build_BuildDone(buf); break;
                case Sfx.Deposit: Build_Deposit(buf); break;
                case Sfx.GateMove: Build_GateMove(buf, rng); break;
                case Sfx.Collapse: Build_Collapse(buf, rng); break;
                default: Build_Denied(buf); break;
            }

            Normalise(buf);
            FadeTail(buf);
            return ToPcm(buf);
        }

        // Little-endian 16-bit, which is what AudioStreamWAV wants and what a .wav
        // file stores — so the same bytes can be written straight to disk.
        public static byte[] RenderBytes(Sfx kind)
        {
            var s = Render(kind);
            var bytes = new byte[s.Length * 2];
            for (int i = 0; i < s.Length; i++)
            {
                bytes[i * 2] = (byte)(s[i] & 0xff);
                bytes[i * 2 + 1] = (byte)((s[i] >> 8) & 0xff);
            }
            return bytes;
        }

        // ---- the sounds ----------------------------------------------------
        // Each one reads as a recipe. The comment says what it is trying to be;
        // the numbers say how. Retuning is meant to be a one-line change.

        // Two quick bright blips, the second higher: reads as "acknowledged"
        // without being a musical note anyone will get tired of.
        static void Build_Select(float[] b)
        {
            Tone(b, 0f, 0.055f, 880f, 880f, 0.30f, Wave.Sine);
            Tone(b, 0.055f, 0.10f, 1320f, 1320f, 0.26f, Wave.Sine);
        }

        // Softer and single — issued constantly, so it must not nag.
        static void Build_MoveOrder(float[] b) =>
            Tone(b, 0f, 0.10f, 700f, 620f, 0.24f, Wave.Sine);

        // Lower and harder-edged than a move, so the two are never confused.
        static void Build_AttackOrder(float[] b)
        {
            Tone(b, 0f, 0.06f, 340f, 300f, 0.30f, Wave.Square);
            Tone(b, 0.05f, 0.14f, 230f, 200f, 0.24f, Wave.Square);
        }

        // Steel on shield: a bright noise crack over a low body thump.
        static void Build_MeleeHit(float[] b, Noise rng)
        {
            var band = new OnePole();
            var cut = new OnePole();
            for (int i = 0; i < b.Length; i++)
            {
                float t = i / (float)SampleRate;
                float n = band.HighPass(rng.Next(), 900f);
                n = cut.LowPass(n, 4200f);
                b[i] += n * Decay(t, 0.0008f, 0.030f) * 0.55f;
            }
            Tone(b, 0f, 0.20f, 150f, 70f, 0.45f, Wave.Sine);
        }

        // A bowstring: a burst of noise swept from bright to dull, which is what
        // a release actually sounds like as the string loses energy.
        static void Build_BowShot(float[] b, Noise rng)
        {
            var hp = new OnePole();
            var lp = new OnePole();
            for (int i = 0; i < b.Length; i++)
            {
                float t = i / (float)SampleRate;
                float k = t / (b.Length / (float)SampleRate);
                float n = hp.HighPass(rng.Next(), 1400f);
                n = lp.LowPass(n, Lerp(6000f, 1200f, k));      // the sweep
                b[i] += n * Decay(t, 0.004f, 0.045f) * 0.7f;
            }
        }

        // Arrival: a short hard tick with a little pitched body behind it.
        static void Build_ArrowHit(float[] b, Noise rng)
        {
            var hp = new OnePole();
            for (int i = 0; i < b.Length; i++)
            {
                float t = i / (float)SampleRate;
                b[i] += hp.HighPass(rng.Next(), 2000f) * Decay(t, 0.0005f, 0.012f) * 0.6f;
            }
            Tone(b, 0f, 0.09f, 420f, 300f, 0.30f, Wave.Triangle);
        }

        // A fall: pitch sagging away under a breath of noise.
        static void Build_UnitDeath(float[] b, Noise rng)
        {
            Tone(b, 0f, 0.45f, 300f, 110f, 0.40f, Wave.Triangle);
            var lp = new OnePole();
            for (int i = 0; i < b.Length; i++)
            {
                float t = i / (float)SampleRate;
                b[i] += lp.LowPass(rng.Next(), 1800f) * Decay(t, 0.01f, 0.13f) * 0.28f;
            }
        }

        // Timber set down: a woody thunk — low triangle, fast attack, no ring.
        // The transient is rolled off hard (1.2 kHz, and gone in ~18 ms): wood
        // has almost no high end, and leaving it bright made this too close to a
        // sword landing to tell apart in a busy moment.
        static void Build_BuildPlace(float[] b, Noise rng)
        {
            Tone(b, 0f, 0.28f, 165f, 120f, 0.50f, Wave.Triangle);
            var lp = new OnePole();
            var lp2 = new OnePole();
            for (int i = 0; i < b.Length; i++)
            {
                float t = i / (float)SampleRate;
                float n = lp.LowPass(rng.Next(), 1200f);
                n = lp2.LowPass(n, 1200f);
                b[i] += n * Decay(t, 0.001f, 0.018f) * 0.55f;
            }
        }

        // Two rising notes — the one unambiguously good-news sound in the set.
        static void Build_BuildDone(float[] b)
        {
            Tone(b, 0f, 0.16f, 523f, 523f, 0.30f, Wave.Sine);
            Tone(b, 0.13f, 0.34f, 784f, 784f, 0.28f, Wave.Sine);
        }

        // A small bright tick. Fires often, so it is the quietest thing here.
        static void Build_Deposit(float[] b) =>
            Tone(b, 0f, 0.09f, 1500f, 1700f, 0.20f, Wave.Sine);

        // A gate dragging: slow, rough, low. Noise pushed through a moving
        // low-pass, swelling rather than striking.
        static void Build_GateMove(float[] b, Noise rng)
        {
            var lp = new OnePole();
            var lp2 = new OnePole();
            int n = b.Length;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float k = i / (float)n;
                float v = lp.LowPass(rng.Next(), Lerp(400f, 900f, k));
                v = lp2.LowPass(v, 700f);
                // Swell in, hold, fall away — the shape of something heavy moving.
                float env = MathF.Sin(MathF.PI * MathF.Min(1f, k)) ;
                b[i] += v * env * 0.85f;
                // A slow grind riding on top, so it reads as stone not wind.
                b[i] += MathF.Sin(2f * MathF.PI * 42f * t) * env * 0.10f;
            }
        }

        // Masonry coming down: a long low rumble that keeps falling.
        static void Build_Collapse(float[] b, Noise rng)
        {
            var lp = new OnePole();
            var lp2 = new OnePole();
            for (int i = 0; i < b.Length; i++)
            {
                float t = i / (float)SampleRate;
                float v = lp.LowPass(rng.Next(), 520f);
                v = lp2.LowPass(v, 300f);
                b[i] += v * Decay(t, 0.02f, 0.26f) * 0.95f;
            }
            Tone(b, 0f, 0.80f, 90f, 45f, 0.35f, Wave.Sine);
        }

        // Refusal: a flat, unmusical buzz. Deliberately unpleasant — it only
        // plays when the game has declined to do what you asked.
        static void Build_Denied(float[] b)
        {
            Tone(b, 0f, 0.07f, 165f, 165f, 0.34f, Wave.Square);
            Tone(b, 0.085f, 0.16f, 138f, 130f, 0.30f, Wave.Square);
        }

        // ---- building blocks ------------------------------------------------

        enum Wave { Sine, Triangle, Square }

        // Add an oscillator sweeping from one frequency to another over a window
        // of the buffer, shaped by a percussive envelope.
        static void Tone(float[] b, float start, float end, float f0, float f1, float amp, Wave wave)
        {
            int i0 = (int)(start * SampleRate);
            int i1 = Math.Min(b.Length, (int)(end * SampleRate));
            if (i1 <= i0) return;

            float span = (i1 - i0) / (float)SampleRate;
            float phase = 0f;

            for (int i = i0; i < i1; i++)
            {
                float t = (i - i0) / (float)SampleRate;
                float k = t / span;
                float f = Lerp(f0, f1, k);
                phase += 2f * MathF.PI * f / SampleRate;

                float s = wave switch
                {
                    Wave.Sine => MathF.Sin(phase),
                    Wave.Triangle => 2f / MathF.PI * MathF.Asin(MathF.Sin(phase)),
                    _ => MathF.Sin(phase) >= 0f ? 1f : -1f,
                };
                b[i] += s * amp * Decay(t, 0.003f, span * 0.42f);
            }
        }

        // A percussive envelope: a short linear attack (so nothing starts on a
        // step) then exponential decay.
        static float Decay(float t, float attack, float tau)
        {
            if (t < 0f) return 0f;
            if (t < attack) return t / attack;
            return MathF.Exp(-(t - attack) / MathF.Max(1e-4f, tau));
        }

        static float Lerp(float a, float b, float k) => a + (b - a) * Math.Clamp(k, 0f, 1f);

        // Scale so the loudest sample sits at Peak. Also the safety net that
        // stops a retuned recipe from silently clipping.
        static void Normalise(float[] b)
        {
            float max = 0f;
            foreach (float v in b) max = MathF.Max(max, MathF.Abs(v));
            if (max < 1e-6f) return;
            float g = Peak / max;
            for (int i = 0; i < b.Length; i++) b[i] *= g;
        }

        static void FadeTail(float[] b)
        {
            int n = Math.Min(b.Length, (int)(TailFade * SampleRate));
            for (int i = 0; i < n; i++)
                b[b.Length - n + i] *= 1f - i / (float)n;
        }

        static short[] ToPcm(float[] b)
        {
            var outp = new short[b.Length];
            for (int i = 0; i < b.Length; i++)
                outp[i] = (short)Math.Clamp(MathF.Round(b[i] * 32767f), -32768f, 32767f);
            return outp;
        }

        // A private xorshift, deliberately NOT the simulation's Rng. The two have
        // nothing to do with each other, and keeping them separate means nobody
        // reading this later has to stop and work out whether generating a sound
        // could nudge the dice that decide a damage roll. It cannot.
        sealed class Noise
        {
            uint _s;
            public Noise(uint seed) { _s = seed == 0 ? 1u : seed; }

            public float Next()
            {
                _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
                return _s / 2147483648f - 1f;      // -1..1
            }
        }

        // One-pole filters. Crude by design-tool standards and exactly right
        // here: cheap, stable at any cutoff, and enough to turn white noise into
        // something that reads as wood, stone or a bowstring.
        sealed class OnePole
        {
            float _y, _xPrev, _yHp;

            public float LowPass(float x, float cutoff)
            {
                float a = 1f - MathF.Exp(-2f * MathF.PI * cutoff / SampleRate);
                _y += a * (x - _y);
                return _y;
            }

            public float HighPass(float x, float cutoff)
            {
                float a = MathF.Exp(-2f * MathF.PI * cutoff / SampleRate);
                _yHp = a * (_yHp + x - _xPrev);
                _xPrev = x;
                return _yHp;
            }
        }
    }
}
