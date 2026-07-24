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
    // Design id -> the sprite basename under Art/units/. The order matches the
    // roster registered in Skirmish.Setup: 0 Soldier, 1 Runner, 2 Brute, 3 Archer.
    static readonly string[] UnitArt = { "soldier", "runner", "brute", "archer" };

    static readonly Dictionary<BuildingType, string> BuildingArt = new()
    {
        [BuildingType.Keep] = "keep",
        [BuildingType.Barracks] = "barracks",
        [BuildingType.Wall] = "wall",
        [BuildingType.Gatehouse] = "gatehouse",
    };

    // How many facings the bake produced per unit. Must match UNIT_FACINGS in
    // tools/bake/bake.gd.
    public const int Facings = 8;
    // Walk-cycle frames per facing. Must match WALK_FRAMES in bake.gd. Discovered
    // at load time (below) so a re-bake with a different count needs no code
    // change; this is just the ceiling we probe up to.
    const int MaxWalkFrames = 16;

    readonly Dictionary<string, Texture2D> _cache = new();
    // Per design, how many walk frames actually loaded — so the renderer knows
    // the cycle length without probing the dictionary every draw.
    readonly int[] _walkFrames;
    public bool AnyLoaded { get; private set; }

    public SpriteBank()
    {
        _walkFrames = new int[UnitArt.Length];

        for (int d = 0; d < UnitArt.Length; d++)
        {
            var name = UnitArt[d];
            for (int f = 0; f < Facings; f++)
            {
                // Idle, and the un-suffixed name the pre-animation bake produced,
                // so an old Art/ folder still works.
                TryLoad($"units/{name}_{f}_idle");
                TryLoad($"units/{name}_{f}");
                for (int w = 0; w < MaxWalkFrames; w++)
                    if (!TryLoad($"units/{name}_{f}_walk{w}")) break;
            }
            // How many walk frames facing 0 has is the cycle length for the design.
            int wf = 0;
            while (_cache.ContainsKey($"units/{name}_0_walk{wf}")) wf++;
            _walkFrames[d] = wf;
        }

        foreach (var name in BuildingArt.Values)
            TryLoad($"buildings/{name}");
        foreach (var t in new[] { "ground", "rock", "marsh", "water" })
            TryLoad($"terrain/{t}");

        GD.Print(AnyLoaded
            ? $"[art] {_cache.Count} sprites loaded from res://Art " +
              $"(walk frames: {string.Join("/", _walkFrames)})"
            : "[art] no baked sprites found — drawing shapes (run tools/bake/run.sh to add art)");
    }

    public int WalkFrames(int designId) =>
        designId >= 0 && designId < _walkFrames.Length ? _walkFrames[designId] : 0;

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

    // The sprite for a unit design, facing a screen direction, at an animation
    // frame. walkFrame < 0 means standing (idle). The lookup degrades gracefully:
    // a missing walk frame falls back to idle, and idle falls back to the old
    // un-suffixed sprite, so a partially-baked or pre-animation Art/ still draws.
    public Texture2D Unit(int designId, int facing, int walkFrame)
    {
        if (designId < 0 || designId >= UnitArt.Length) designId = 0;
        facing = ((facing % Facings) + Facings) % Facings;
        string b = $"units/{UnitArt[designId]}_{facing}";

        if (walkFrame >= 0 && _cache.TryGetValue($"{b}_walk{walkFrame}", out var w)) return w;
        if (_cache.TryGetValue($"{b}_idle", out var idle)) return idle;
        return _cache.TryGetValue(b, out var t) ? t : null;
    }
}
