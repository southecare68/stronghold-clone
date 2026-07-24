// Audio — the synthesizer, checked numerically.
//
// You cannot assert that something "sounds good", and this does not try. What it
// asserts is everything underneath that judgement: no sound is silent, none
// clips, none begins or ends on a step (which is heard as a click), every one is
// reproducible, and the sounds that must be told apart in play — a bowshot from
// a collapsing wall, a move order from an attack order — measurably differ in
// the ways your ear uses to tell them apart.
//
// Pass --write <dir> to also dump every effect as a .wav, which is how a human
// checks the half a test cannot.

using System;
using System.IO;
using Audio;

static class Program
{
    static int _failures;

    static void Main(string[] args)
    {
        Console.WriteLine("Audio — procedurally generated sound effects\n");

        EverySoundIsUsable();
        NothingStartsOrEndsOnAStep();
        SoundsAreReproducible();
        ThePcmEncodingIsCorrect();
        SoundsAreTellableApart();
        TheQuietOnesAreQuietAndTheHeavyOnesAreLong();

        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--write") WriteWavs(args[i + 1]);

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    static void EverySoundIsUsable()
    {
        Console.WriteLine("every sound renders to something audible:");
        foreach (var kind in Synth.All)
        {
            var s = Synth.Render(kind);
            float secs = s.Length / (float)Synth.SampleRate;
            int peak = Peak(s);

            // Long enough to hear, short enough not to overlap the next one.
            bool sane = secs >= 0.05f && secs <= 1.5f;
            // Not silence — the most likely outcome of a recipe with a typo.
            bool audible = peak > 32767 * 0.25;
            // Not clipping. Synth normalises to 0.62 of full scale, so anything
            // at the rail means the normaliser was bypassed.
            bool clean = peak <= 32767 * 0.95;

            Check($"{kind,-12} {secs * 1000f,5:0} ms, peak {peak * 100 / 32767,3}%", sane && audible && clean);
        }
    }

    // A buffer that stops mid-waveform ends on a discontinuity, and a
    // discontinuity is a click. Both ends have to arrive at silence.
    static void NothingStartsOrEndsOnAStep()
    {
        Console.WriteLine("\nno clicks at the edges:");
        foreach (var kind in Synth.All)
        {
            var s = Synth.Render(kind);
            int head = Math.Abs(s[0]);
            int tail = Math.Abs(s[^1]);
            // Within 1% of full scale of silence at both ends.
            Check($"{kind,-12} starts at {head} and ends at {tail}",
                  head < 328 && tail < 328);
        }
    }

    // Not a determinism requirement — audio can never reach the simulation — but
    // a sound that differed between runs could not be tested or retuned.
    static void SoundsAreReproducible()
    {
        Console.WriteLine("\nrendering twice gives the identical buffer:");
        bool all = true;
        foreach (var kind in Synth.All)
        {
            var a = Synth.Render(kind);
            var b = Synth.Render(kind);
            if (a.Length != b.Length) { all = false; continue; }
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { all = false; break; }
        }
        Check("all 13 effects are bit-identical on a re-render", all);
    }

    static void ThePcmEncodingIsCorrect()
    {
        Console.WriteLine("\n16-bit little-endian encoding:");
        var samples = Synth.Render(Sfx.MeleeHit);
        var bytes = Synth.RenderBytes(Sfx.MeleeHit);

        Check("two bytes per sample", bytes.Length == samples.Length * 2);

        bool roundTrips = true;
        for (int i = 0; i < samples.Length; i++)
        {
            short back = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
            if (back != samples[i]) { roundTrips = false; break; }
        }
        Check("every sample decodes back to itself, low byte first", roundTrips);
    }

    // The real question a listener asks: can I tell these apart? Zero-crossing
    // rate is a cheap stand-in for brightness — a hiss crosses zero constantly, a
    // rumble hardly ever — and it is enough to pin the character of each sound so
    // a retune cannot quietly turn the bowshot into a thud.
    static void SoundsAreTellableApart()
    {
        Console.WriteLine("\nthe sounds are distinguishable:");

        float bow = Brightness(Sfx.BowShot);
        float collapse = Brightness(Sfx.Collapse);
        float melee = Brightness(Sfx.MeleeHit);
        float place = Brightness(Sfx.BuildPlace);
        float deposit = Brightness(Sfx.Deposit);
        float gate = Brightness(Sfx.GateMove);

        Check($"a bowshot is brighter than a collapsing wall ({bow:0.000} vs {collapse:0.000})",
              bow > collapse * 3f);
        Check($"the deposit tick is bright ({deposit:0.000})", deposit > 0.05f);

        // A gate is mid-dark: duller than anything ticky, but nowhere near the
        // sub-bass of a wall coming down.
        Check($"a gate grinding sits between the ticks and the rumble ({gate:0.000})",
              gate < deposit && gate < bow && gate > collapse * 2f);

        // Impacts are a bright crack over a long low body, so their whole-buffer
        // average says almost nothing — it is dominated by the tail. The claim
        // worth making about a sword blow is about its ONSET, which is the part
        // an ear actually uses to identify it.
        float meleeHit = Onset(Sfx.MeleeHit);
        float timber = Onset(Sfx.BuildPlace);
        Check($"a sword blow lands with a brighter crack than timber " +
              $"({meleeHit:0.000} vs {timber:0.000})", meleeHit > timber * 1.5f);
        Check($"and both have far more body than onset ({melee:0.000} vs {meleeHit:0.000})",
              melee < meleeHit && place < timber);

        // The two order sounds fire constantly and must never be confused, so
        // they are held apart explicitly.
        Check($"move and attack orders differ in pitch " +
              $"({Brightness(Sfx.MoveOrder):0.000} vs {Brightness(Sfx.AttackOrder):0.000})",
              Brightness(Sfx.MoveOrder) > Brightness(Sfx.AttackOrder) * 1.3f);

        // And no two effects are literally the same buffer.
        int clashes = 0;
        for (int i = 0; i < Synth.All.Length; i++)
            for (int j = i + 1; j < Synth.All.Length; j++)
                if (Same(Synth.Render(Synth.All[i]), Synth.Render(Synth.All[j]))) clashes++;
        Check("no two effects are identical", clashes == 0);
    }

    // Sounds that fire often must be short and quiet; sounds that mark something
    // important may be long. Getting this backwards is what makes a game tiring
    // to listen to, and it is a property, not a taste.
    static void TheQuietOnesAreQuietAndTheHeavyOnesAreLong()
    {
        Console.WriteLine("\nfrequent sounds are short, rare ones may be long:");
        float deposit = Seconds(Sfx.Deposit);
        float move = Seconds(Sfx.MoveOrder);
        float collapse = Seconds(Sfx.Collapse);
        float death = Seconds(Sfx.UnitDeath);

        Check($"the deposit tick is under 120 ms ({deposit * 1000:0} ms)", deposit < 0.12f);
        Check($"a move order is under 150 ms ({move * 1000:0} ms)", move < 0.15f);
        Check($"a building collapsing is the longest sound ({collapse * 1000:0} ms)",
              collapse > death && collapse > 0.5f);

        // Energy, not peak: a sound normalised to the same peak can still be far
        // more tiring if it sustains. The tick must be the least of them.
        Check($"and the tick carries the least energy of the three",
              Energy(Sfx.Deposit) < Energy(Sfx.UnitDeath) &&
              Energy(Sfx.Deposit) < Energy(Sfx.Collapse));
    }

    // ---- writing them out --------------------------------------------------

    // The half a test cannot cover is whether they sound right, so make that easy
    // to check: one .wav per effect, playable by anything.
    static void WriteWavs(string dir)
    {
        Directory.CreateDirectory(dir);
        Console.WriteLine($"\nwriting {Synth.All.Length} wav files to {dir}:");
        foreach (var kind in Synth.All)
        {
            string path = Path.Combine(dir, $"{kind}.wav");
            File.WriteAllBytes(path, Wav(Synth.RenderBytes(kind)));
            Console.WriteLine($"  {path}");
        }
    }

    // Minimal 44-byte canonical WAV header around raw PCM.
    static byte[] Wav(byte[] pcm)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int rate = Synth.SampleRate;
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + pcm.Length);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);                 // PCM header size
        w.Write((short)1);           // format: PCM
        w.Write((short)1);           // channels: mono
        w.Write(rate);
        w.Write(rate * 2);           // byte rate
        w.Write((short)2);           // block align
        w.Write((short)16);          // bits per sample
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    // ---- measurements ------------------------------------------------------

    static int Peak(short[] s)
    {
        int p = 0;
        foreach (short v in s) p = Math.Max(p, Math.Abs((int)v));
        return p;
    }

    static float Seconds(Sfx k) => Synth.Render(k).Length / (float)Synth.SampleRate;

    // Fraction of adjacent sample pairs that cross zero. High for hiss, low for
    // rumble — the cheapest useful proxy for "brightness".
    static float Brightness(Sfx k)
    {
        var s = Synth.Render(k);
        int crossings = 0;
        for (int i = 1; i < s.Length; i++)
            if ((s[i - 1] < 0) != (s[i] < 0)) crossings++;
        return crossings / (float)s.Length;
    }

    // Brightness of the first 15 ms only — the strike, before the body takes
    // over. For a percussive sound this is what identifies it.
    static float Onset(Sfx k)
    {
        var s = Synth.Render(k);
        int n = Math.Min(s.Length, Synth.SampleRate * 15 / 1000);
        int crossings = 0;
        for (int i = 1; i < n; i++)
            if ((s[i - 1] < 0) != (s[i] < 0)) crossings++;
        return crossings / (float)n;
    }

    // Mean square amplitude over the whole buffer — how much sound there is in
    // total, rather than how loud its loudest instant was.
    static double Energy(Sfx k)
    {
        var s = Synth.Render(k);
        double sum = 0;
        foreach (short v in s) sum += (double)v * v;
        return sum / 32767.0 / 32767.0;
    }

    static bool Same(short[] a, short[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
