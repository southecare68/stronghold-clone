// Anim3D.cs — procedural skeleton posing for the 3D units.
//
// The POLYGON models ship no locomotion clips (only a bind pose), so — exactly
// as the sprite bake did — the walk, idle and swing are posed by hand from bone
// rotations. This is that same posing, ported to C# and driven live each frame
// off the unit's state instead of baked to frames. A missing bone is skipped, so
// it survives a differently-named rig.

using Godot;

public static class Anim3D
{
    const float ArmLowerDeg = -72f;   // T-pose arms down to the sides (about Z)
    const float LegSwingDeg = 30f;    // hip pitch amplitude
    const float ArmSwingDeg = 22f;    // shoulder pitch, counter to the legs
    const float KneeBendDeg = 26f;    // lower-leg bend on the through-swing leg
    public const int AttackFrames = 4;

    static readonly Vector3 X = new(1, 0, 0);
    static readonly Vector3 Z = new(0, 0, 1);

    // Standing: arms brought down out of the T-pose, a touch forward.
    public static void Idle(Skeleton3D s)
    {
        Reset(s);
        var lower = Axis(Z, ArmLowerDeg) * Axis(X, 6f);
        Pose(s, "Shoulder_L", lower);
        Pose(s, "Shoulder_R", lower);
    }

    // One instant of the walk cycle; phase runs 0..TAU. Left/right limbs half a
    // cycle apart, arms countering the legs.
    public static void Walk(Skeleton3D s, float phase)
    {
        Reset(s);
        float leg = Mathf.Sin(phase), legOpp = Mathf.Sin(phase + Mathf.Pi);

        Pose(s, "UpperLeg_L", Axis(X, LegSwingDeg * leg));
        Pose(s, "UpperLeg_R", Axis(X, LegSwingDeg * legOpp));
        Pose(s, "LowerLeg_L", Axis(X, -KneeBendDeg * Mathf.Max(0f, Mathf.Cos(phase))));
        Pose(s, "LowerLeg_R", Axis(X, -KneeBendDeg * Mathf.Max(0f, Mathf.Cos(phase + Mathf.Pi))));

        var arm = Axis(Z, ArmLowerDeg);
        Pose(s, "Shoulder_L", arm * Axis(X, ArmSwingDeg * legOpp));
        Pose(s, "Shoulder_R", arm * Axis(X, ArmSwingDeg * leg));
    }

    // A heavy overhead swing with the right arm: ready -> wind up -> strike ->
    // follow through. The renderer times the four frames against the cooldown so
    // the strike lands roughly when the sim's blow does.
    public static void Attack(Skeleton3D s, int f)
    {
        Reset(s);
        var arm = Axis(Z, ArmLowerDeg);
        switch (f)
        {
            case 0:  // ready
                Pose(s, "Shoulder_R", arm * Axis(X, 18f));
                Pose(s, "Shoulder_L", arm * Axis(X, 10f));
                Pose(s, "Spine_02", Axis(X, 6f));
                Pose(s, "Hips", Axis(X, -6f));
                break;
            case 1:  // wind up — right arm hauled overhead, torso coiled
                Pose(s, "Shoulder_R", Axis(Z, -38f) * Axis(X, -82f));
                Pose(s, "Elbow_R", Axis(X, -62f));
                Pose(s, "Shoulder_L", arm * Axis(X, -24f));
                Pose(s, "Spine_02", Axis(X, -18f));
                Pose(s, "Hips", Axis(X, -12f));
                break;
            case 2:  // strike — slammed down and forward, the whole body lunging in
                Pose(s, "Shoulder_R", arm * Axis(X, 112f));
                Pose(s, "Elbow_R", Axis(X, -8f));
                Pose(s, "Shoulder_L", arm * Axis(X, 28f));
                Pose(s, "Spine_02", Axis(X, 30f));
                Pose(s, "Hips", Axis(X, 16f));
                break;
            default: // follow through
                Pose(s, "Shoulder_R", arm * Axis(X, 66f));
                Pose(s, "Shoulder_L", arm * Axis(X, 12f));
                Pose(s, "Spine_02", Axis(X, 18f));
                Pose(s, "Hips", Axis(X, 6f));
                break;
        }
    }

    // ---- bone helpers ------------------------------------------------------

    static void Reset(Skeleton3D s)
    {
        for (int i = 0; i < s.GetBoneCount(); i++)
            s.SetBonePoseRotation(i, s.GetBoneRest(i).Basis.GetRotationQuaternion());
    }

    // Set a bone to its rest rotation composed with a delta in the bone's rest
    // frame. Missing bones are ignored.
    static void Pose(Skeleton3D s, string bone, Quaternion delta)
    {
        int b = s.FindBone(bone);
        if (b < 0) return;
        var rest = s.GetBoneRest(b).Basis.GetRotationQuaternion();
        s.SetBonePoseRotation(b, rest * delta);
    }

    static Quaternion Axis(Vector3 a, float deg) => new(a.Normalized(), Mathf.DegToRad(deg));

    // The first Skeleton3D under a node, or null.
    public static Skeleton3D Find(Node n)
    {
        if (n is Skeleton3D sk) return sk;
        foreach (var c in n.GetChildren())
        {
            var found = Find(c);
            if (found != null) return found;
        }
        return null;
    }
}
