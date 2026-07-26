// SpriteBank.cs — load the baked sprites, and fall back gracefully when they are
// not there.
//
// The sprites in game/Art/ are produced offline by tools/bake/ from the 3D asset
// packs (see that folder). This loads them at startup and hands the renderer a
// texture per building type and per unit-design-and-facing.
//
// Two decisions worth stating:
//
//   Loaded at RUNTIME with Image.Load, not through Godot's import pipeline. The
//   art is generated, exactly like the audio the game already synthesises at
//   startup, so it belongs in the same category: no .import files to keep in sync,
//   nothing to reimport when a sprite is rebaked. Drop a new PNG in Art/ and it is
//   picked up on the next launch.
//
//   Missing art is NOT an error. Every lookup can return null, and the renderer
//   keeps its original shape-drawing for anything that comes back null. So the
//   game runs identically whether or not the packs have been baked — which is what
//   keeps the headless test projects, and anyone who has not unpacked the gigabyte
//   of source art, entirely unaffected. The art is an overlay on a game that was
//   already complete, not a dependency of it.
//
// Nothing here touches the simulation.

using Godot;
using System.Collections.Generic;
using Sim;

public sealed class SpriteBank
{
    // Sprite index -> the sprite basename under Art/units/. Indices 0..3 are the
    // point-buy designs (0 Soldier, 1 Runner, 2 Brute, 3 Archer, matching
    // Skirmish.Setup). Index 4 is the Peasant — NOT a design; it is a render-only
    // sprite the game picks for a hut's woodcutter, so the worker felling trees
    // looks like a peasant rather than a soldier, without changing the sim.
    static readonly string[] UnitArt = { "soldier", "runner", "brute", "archer", "peasant" };
    public const int PeasantSprite = 4;

    static readonly Dictionary<BuildingType, string> BuildingArt = new()
    {
        [BuildingType.Keep] = "keep",
        [BuildingType.Barracks] = "barracks",
        [BuildingType.Wall] = "wall",
        [BuildingType.Gatehouse] = "gatehouse",
        [BuildingType.WoodcutterHut] = "woodcutter",
        [BuildingType.Storehouse] = "storehouse",
        [BuildingType.Quarry] = "quarry",
        [BuildingType.Farm] = "farm",
        [BuildingType.Mill] = "mill",
        [BuildingType.Bakery] = "bakery",
        [BuildingType.House] = "house",
    };

    // The animation states a unit sprite can be in. The clip name under Art/units
    // is the lowercased state ("walk", "atk", "death"); Idle is a single frame.
    public enum Anim { Idle, Walk, Attack, Death }
    static string Clip(Anim a) => a switch
    {
        Anim.Walk => "walk",
        Anim.Attack => "atk",
        Anim.Death => "death",
        _ => "idle",
    };

    // How many facings the bake produced per unit. Must match UNIT_FACINGS in
    // tools/bake/bake.gd.
    public const int Facings = 8;
    // Upper bound on frames per clip we probe for at load. The real count is
    // discovered per (design, clip), so a re-bake with different counts needs no
    // code change here.
    const int MaxFrames = 32;

    readonly Dictionary<string, Texture2D> _cache = new();
    // [design][state] -> how many frames actually loaded, so the renderer knows a
    // clip's length without probing the dictionary every draw.
    readonly int[,] _frames;
    public bool AnyLoaded { get; private set; }

    public SpriteBank()
    {
        _frames = new int[UnitArt.Length, 4];

        for (int d = 0; d < UnitArt.Length; d++)
        {
            var name = UnitArt[d];
            for (int f = 0; f < Facings; f++)
            {
                // Idle, plus the un-suffixed name the first bake produced, so an
                // old Art/ folder still works.
                TryLoad($"units/{name}_{f}_idle");
                TryLoad($"units/{name}_{f}");
                foreach (Anim a in new[] { Anim.Walk, Anim.Attack, Anim.Death })
                    for (int k = 0; k < MaxFrames; k++)
                        if (!TryLoad($"units/{name}_{f}_{Clip(a)}{k}")) break;
            }
            // Clip length = however many frames facing 0 has.
            foreach (Anim a in new[] { Anim.Walk, Anim.Attack, Anim.Death })
            {
                int n = 0;
                while (_cache.ContainsKey($"units/{name}_0_{Clip(a)}{n}")) n++;
                _frames[d, (int)a] = n;
            }
        }

        foreach (var name in BuildingArt.Values)
            TryLoad($"buildings/{name}");
        foreach (var t in new[] { "ground", "rock", "marsh", "water" })
            TryLoad($"terrain/{t}");

        GD.Print(AnyLoaded
            ? $"[art] {_cache.Count} sprites loaded from res://Art"
            : "[art] no baked sprites found — drawing shapes (run tools/bake/run.sh to add art)");
    }

    // Frames in a clip for a design (0 if that clip was not baked).
    public int FrameCount(int designId, Anim a) =>
        designId >= 0 && designId < UnitArt.Length ? _frames[designId, (int)a] : 0;

    bool TryLoad(string key)
    {
        if (_cache.ContainsKey(key)) return true;
        var tex = LoadPng($"res://Art/{key}.png");
        if (tex == null) return false;
        _cache[key] = tex;
        AnyLoaded = true;
        return true;
    }

    // Runtime PNG load. Image.Load reads the raw file when running from source,
    // so a freshly-baked sprite needs no import step. Returns null for anything
    // missing or unreadable — the caller treats null as "draw the old shape".
    static Texture2D LoadPng(string resPath)
    {
        if (!Godot.FileAccess.FileExists(resPath)) return null;
        var img = new Image();
        if (img.Load(resPath) != Error.Ok) return null;
        img.GenerateMipmaps();      // so a sprite scaled down at low zoom stays smooth
        return ImageTexture.CreateFromImage(img);
    }

    public Texture2D Building(BuildingType type) =>
        BuildingArt.TryGetValue(type, out var name) && _cache.TryGetValue($"buildings/{name}", out var t) ? t : null;

    public Texture2D Terrain(string name) => _cache.TryGetValue($"terrain/{name}", out var t) ? t : null;

    // The sprite for a unit design, facing a screen direction, in a state at a
    // frame. The lookup degrades gracefully: a missing clip frame falls back to
    // idle, and idle to the old un-suffixed sprite — so a partially-baked or
    // pre-animation Art/ folder still draws something.
    public Texture2D Unit(int designId, int facing, Anim state, int frame)
    {
        if (designId < 0 || designId >= UnitArt.Length) designId = 0;
        facing = ((facing % Facings) + Facings) % Facings;
        string b = $"units/{UnitArt[designId]}_{facing}";

        if (state != Anim.Idle && _cache.TryGetValue($"{b}_{Clip(state)}{frame}", out var c)) return c;
        if (_cache.TryGetValue($"{b}_idle", out var idle)) return idle;
        return _cache.TryGetValue(b, out var t) ? t : null;
    }
}
