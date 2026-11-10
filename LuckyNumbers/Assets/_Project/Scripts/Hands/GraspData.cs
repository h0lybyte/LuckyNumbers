using System;

namespace LuckyNumbers.Hands
{
    /// <summary>
    /// A hand's settled grip on something, as stored in Art/Models/Weapons/weapons.json ("grasp": left / right) by
    /// tools/ln.py bake-grasps, and replayed by ArmsView instead of solving it: every finger joint's local rotation
    /// (x, y, z, w each, fingers in the rig's order, joints root to tip), how far the palm backs off the surface, and
    /// how many fingers ended touching. Only valid for the rig it was baked on (`rig`: its joint count).
    /// </summary>
    [Serializable]
    public sealed class GraspData
    {
        public float push;          // metres the wrist sits off along the palm
        public int touching, fingers;
        public int joints;          // finger joints in the rig when baked: a mismatch means it's stale
        public float[] rotations;   // joints * 4

        public bool Valid(int rigJoints) => rotations != null && joints == rigJoints && rotations.Length == rigJoints * 4;
    }
}
