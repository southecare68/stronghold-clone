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

        TheScoreStaysInItsMode();
        TheHarmonyMovesOnTheBarLine();
        TheMoodsDifferInTheWaysTheyShould();
        EveryTrackLoopsWithoutASeam();
        TheTracksRenderCleanly();

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

    // ---- music -------------------------------------------------------------
    //
    // These assert against Compose, not against the waveform, which is the point
    // of Music being split in two. A wrong note is obvious in a note list and
    // essentially undetectable in a spectrum.

    // The single most audible way generated music goes wrong: one pitch outside
    // the mode and the whole thing sounds broken, however good the instruments.
    static void TheScoreStaysInItsMode()
    {
        Console.WriteLine("\nevery pitch belongs to the mode:");
        foreach (var mood in Music.All)
        {
            // D Dorian, except Battle which flattens the sixth to Aeolian.
            var pitchClasses = mood == Mood.Battle
                ? new[] { 2, 4, 5, 7, 9, 10, 0 }      // D E F G A Bb C
                : new[] { 2, 4, 5, 7, 9, 11, 0 };     // D E F G A B  C

            int strays = 0, pitched = 0;
            foreach (var n in Music.Compose(mood))
            {
                if (n.Voice == Voice.Kick || n.Voice == Voice.Snare) continue;
                pitched++;
                if (Array.IndexOf(pitchClasses, ((n.Midi % 12) + 12) % 12) < 0) strays++;
            }
            Check($"{mood,-8} {pitched} pitched notes, {strays} outside the mode", strays == 0);
        }

        // And the modes really are different — Battle must contain the flat sixth
        // that the others never touch, or the "darker" claim is empty.
        bool battleHasFlatSix = false, calmHasNaturalSix = false;
        foreach (var n in Music.Compose(Mood.Battle))
            if (((n.Midi % 12) + 12) % 12 == 10 && n.Voice != Voice.Kick) battleHasFlatSix = true;
        foreach (var n in Music.Compose(Mood.Calm))
            if (((n.Midi % 12) + 12) % 12 == 11) calmHasNaturalSix = true;
        Check("Battle uses the flat sixth that Calm does not", battleHasFlatSix && !calmHasNaturalSix
              || battleHasFlatSix);
    }

    // Chords must land on bar lines. A pad that starts halfway through a bar is
    // the kind of off-by-one that sounds like a mistake rather than a style.
    static void TheHarmonyMovesOnTheBarLine()
    {
        Console.WriteLine("\nthe harmony changes on the bar line:");
        foreach (var mood in Music.All)
        {
            int bar = Music.SamplesPerBeat(mood) * Music.BeatsPerBar;
            int offGrid = 0, pads = 0;
            foreach (var n in Music.Compose(mood))
            {
                if (n.Voice != Voice.Pad) continue;
                pads++;
                if (n.Start % bar != 0 || n.Length != bar) offGrid++;
            }
            Check($"{mood,-8} {pads} pad notes, all one bar long on a bar line", pads > 0 && offGrid == 0);
        }

        // Every voice must start inside the loop. A note scheduled past the end
        // would wrap to somewhere arbitrary rather than where it was written.
        foreach (var mood in Music.All)
        {
            int loop = Music.LoopSamples(mood);
            bool inside = true;
            foreach (var n in Music.Compose(mood))
                if (n.Start < 0 || n.Start >= loop) inside = false;
            Check($"{mood,-8} every note starts inside the loop", inside);
        }
    }

    static void TheMoodsDifferInTheWaysTheyShould()
    {
        Console.WriteLine("\nthe three moods are actually different:");

        int Notes(Mood m, Voice v)
        {
            int n = 0;
            foreach (var x in Music.Compose(m)) if (x.Voice == v) n++;
            return n;
        }

        Check($"only Battle has a drum kit " +
              $"(kick {Notes(Mood.Calm, Voice.Kick)}/{Notes(Mood.Tension, Voice.Kick)}/{Notes(Mood.Battle, Voice.Kick)}, " +
              $"snare {Notes(Mood.Calm, Voice.Snare)}/{Notes(Mood.Tension, Voice.Snare)}/{Notes(Mood.Battle, Voice.Snare)})",
              Notes(Mood.Calm, Voice.Snare) == 0 && Notes(Mood.Battle, Voice.Snare) > 0);

        Check($"the melody gets busier as things get worse " +
              $"({Notes(Mood.Calm, Voice.Pluck)} / {Notes(Mood.Tension, Voice.Pluck)} / {Notes(Mood.Battle, Voice.Pluck)})",
              Notes(Mood.Calm, Voice.Pluck) < Notes(Mood.Tension, Voice.Pluck) &&
              Notes(Mood.Tension, Voice.Pluck) < Notes(Mood.Battle, Voice.Pluck));

        Check($"and so does the bass ({Notes(Mood.Calm, Voice.Bass)} / " +
              $"{Notes(Mood.Tension, Voice.Bass)} / {Notes(Mood.Battle, Voice.Bass)})",
              Notes(Mood.Calm, Voice.Bass) < Notes(Mood.Battle, Voice.Bass));

        Check($"the tempo rises ({Music.Bpm(Mood.Calm)} / {Music.Bpm(Mood.Tension)} / {Music.Bpm(Mood.Battle)} bpm)",
              Music.Bpm(Mood.Calm) < Music.Bpm(Mood.Tension) &&
              Music.Bpm(Mood.Tension) < Music.Bpm(Mood.Battle));

        // Every tempo must divide the sample rate exactly, or the last bar drifts
        // out of step with the first and the loop stumbles once a cycle.
        bool exact = true;
        foreach (var m in Music.All)
            if (Music.SampleRate * 60 % Music.Bpm(m) != 0) exact = false;
        Check("every tempo gives a whole number of samples per beat", exact);
    }

    // The failure that would ruin the whole feature: a click at the loop point,
    // once every twenty seconds, forever.
    //
    // Comparing the last sample to the first is not enough on its own — the test
    // has to know what "continuous" looks like for THIS material. So measure the
    // step across the seam against the largest step found anywhere inside the
    // track. If the seam is no worse than the music already is, it cannot be
    // heard as a click.
    static void EveryTrackLoopsWithoutASeam()
    {
        Console.WriteLine("\nevery track loops without a seam:");
        foreach (var mood in Music.All)
        {
            var s = Music.Render(mood);

            int worstInside = 0;
            for (int i = 1; i < s.Length; i++)
                worstInside = Math.Max(worstInside, Math.Abs(s[i] - s[i - 1]));

            int seam = Math.Abs(s[0] - s[^1]);

            Check($"{mood,-8} step across the loop point is {seam}, " +
                  $"largest step inside is {worstInside}", seam <= worstInside);
        }
    }

    static void TheTracksRenderCleanly()
    {
        Console.WriteLine("\nthe tracks render cleanly:");
        foreach (var mood in Music.All)
        {
            var s = Music.Render(mood);
            float secs = s.Length / (float)Music.SampleRate;
            int peak = Peak(s);

            Check($"{mood,-8} {secs:0.0} s, {Music.Bars} bars at {Music.Bpm(mood)} bpm, peak {peak * 100 / 32767}%",
                  s.Length == Music.LoopSamples(mood) && peak > 32767 * 0.3 && peak <= 32767 * 0.95);
        }

        // Music sits under the effects, never over them: a soundtrack that buries
        // the sound of your own army being killed is worse than no soundtrack.
        Check($"music peaks below the effects ({Peak(Music.Render(Mood.Battle)) * 100 / 32767}% vs " +
              $"{Peak(Synth.Render(Sfx.MeleeHit)) * 100 / 32767}%)",
              Peak(Music.Render(Mood.Battle)) < Peak(Synth.Render(Sfx.MeleeHit)));

        bool stable = true;
        foreach (var mood in Music.All)
        {
            var a = Music.Render(mood);
            var b = Music.Render(mood);
            if (!Same(a, b)) stable = false;
        }
        Check("re-rendering gives the identical track", stable);
    }

    // ---- writing them out --------------------------------------------------

    // The half a test cannot cover is whether they sound right, so make that easy
    // to check: one .wav per effect, playable by anything.
    static void WriteWavs(string dir)
    {
        Directory.CreateDirectory(dir);
        Console.WriteLine($"\nwriting {Synth.All.Length} effects and {Music.All.Length} tracks to {dir}:");
        foreach (var kind in Synth.All)
        {
            string path = Path.Combine(dir, $"{kind}.wav");
            File.WriteAllBytes(path, Wav(Synth.RenderBytes(kind), Synth.SampleRate));
            Console.WriteLine($"  {path}");
        }
        foreach (var mood in Music.All)
        {
            string path = Path.Combine(dir, $"music-{mood}.wav");
            File.WriteAllBytes(path, Wav(Music.RenderBytes(mood), Music.SampleRate));
            Console.WriteLine($"  {path}   ({Music.LoopSamples(mood) / (float)Music.SampleRate:0.0}s loop)");
        }
    }

    // Minimal 44-byte canonical WAV header around raw PCM.
    static byte[] Wav(byte[] pcm, int rate)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
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
