using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Crashes (content/dogfight.txtpb collisions): ships against each other and against the station's hull. What
    /// hurts is the closing speed along the contact (a head-on hit, not a scrape), and both bounce: `restitution` of
    /// that speed comes back, and the scrape keeps `scrape` of the speed along the face.
    /// </summary>
    public static class Impacts
    {
        /// <summary>Damage for a crash at `closing` m/s against something `size` times as big as you (0 below `minSpeed`).</summary>
        public static int Damage(float closing, float minSpeed, float perSpeed, float size) =>
            closing <= minSpeed ? 0 : (int)math.ceil((closing - minSpeed) * math.max(0f, perSpeed) * math.clamp(size, 0.5f, 2f));

        /// <summary>
        /// Two ships touching along `normal` (from b to a): if they're closing, each bounces by its share of the
        /// impulse (heavier moves less: `massA`, `massB`), the scrape loses a little speed. Returns the closing speed
        /// (0 if they were already parting).
        /// </summary>
        public static float Bounce(ref Vector3 va, ref Vector3 vb, Vector3 normal, float massA, float massB, float restitution,
                                   float scrape)
        {
            var closing = -Vector3.Dot(va - vb, normal);
            if (closing <= 0f) return 0f;
            var ma = math.max(1e-3f, massA);
            var mb = math.max(1e-3f, massB);
            var j = (1f + math.saturate(restitution)) * closing / (1f / ma + 1f / mb);
            va += normal * (j / ma);
            vb -= normal * (j / mb);
            // The scrape: the relative speed along the face, eased toward the pair's shared drift.
            var keep = math.saturate(scrape);
            var tanA = Vector3.ProjectOnPlane(va, normal);
            var tanB = Vector3.ProjectOnPlane(vb, normal);
            var shared = (tanA * ma + tanB * mb) / (ma + mb);
            va += (shared - tanA) * (1f - keep);
            vb += (shared - tanB) * (1f - keep);
            return closing;
        }

        /// <summary>
        /// A sphere (`p`, `radius`) pushed out of every box it's in (along its shallowest side), bouncing off the face
        /// (`v`): returns the hardest closing speed it hit at (0: no contact, or sliding along), and that face's normal.
        /// </summary>
        public static float OutOfBoxes(IReadOnlyList<Bounds> boxes, ref Vector3 p, ref Vector3 v, float radius, float restitution,
                                       float scrape, out Vector3 normal)
        {
            normal = Vector3.zero;
            var hardest = 0f;
            if (boxes == null) return 0f;
            for (var i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                var e = b.extents + Vector3.one * radius;
                var d = p - b.center;
                var pen = new Vector3(e.x - math.abs(d.x), e.y - math.abs(d.y), e.z - math.abs(d.z));
                if (pen.x <= 0f || pen.y <= 0f || pen.z <= 0f) continue;
                Vector3 n;
                if (pen.x <= pen.y && pen.x <= pen.z) { n = new Vector3(math.sign(d.x), 0f, 0f); p.x += n.x * pen.x; }
                else if (pen.y <= pen.z) { n = new Vector3(0f, math.sign(d.y), 0f); p.y += n.y * pen.y; }
                else { n = new Vector3(0f, 0f, math.sign(d.z)); p.z += n.z * pen.z; }
                if (n.sqrMagnitude < 0.5f) n = Vector3.up;
                var into = -Vector3.Dot(v, n);
                if (into <= 0f) continue;
                var along = Vector3.ProjectOnPlane(v, n) * math.saturate(scrape);
                v = along + n * (into * math.saturate(restitution));
                if (into > hardest) { hardest = into; normal = n; }
            }
            return hardest;
        }
    }
}
