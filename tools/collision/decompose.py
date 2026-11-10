"""Solid collision for a static model: convex decomposition (CoACD) of a watertight remesh.

    python tools/collision/decompose.py in.gltf out.obj [--threshold 0.05] [--max-hulls 48]

1. Load every mesh in the file (with node transforms) as one shape.
2. CoACD's preprocess remeshes it watertight (voxel-based), which seals hollow interiors: nothing to fall into.
3. Decompose into convex pieces that follow the silhouette (concavity <= threshold, share of the size).
4. Thin each piece to its most spread-out points and re-hull (<= 255 triangles, Unity's convex limit).
5. Write them as OBJ objects hull_000, hull_001... (Unity: one convex MeshCollider each, not rendered).

Needs `pip install coacd trimesh scipy` (offline tool only; the game doesn't depend on it).
"""
import argparse
import time

import coacd
import numpy as np
import trimesh

ap = argparse.ArgumentParser()
ap.add_argument("src")
ap.add_argument("dst")
ap.add_argument("--threshold", type=float, default=0.05, help="max concavity per piece (share of the size); lower = tighter, more pieces")
ap.add_argument("--max-hulls", type=int, default=48, help="cap on pieces (-1 = no cap)")
ap.add_argument("--resolution", type=int, default=60, help="watertight remesh resolution (voxels across)")
ap.add_argument("--max-points", type=int, default=120, help="per piece: keep the most spread-out points, then re-hull (Unity convex colliders take <= 255 triangles)")
args = ap.parse_args()

scene = trimesh.load(args.src, force="scene")
mesh = scene.to_geometry() if hasattr(scene, "to_geometry") else scene.dump(concatenate=True)
print(f"input: {len(mesh.faces)} triangles, extents {np.round(mesh.extents, 2)}, watertight {mesh.is_watertight}")

coacd.set_log_level("error")
started = time.time()
parts = coacd.run_coacd(coacd.Mesh(mesh.vertices, mesh.faces), threshold=args.threshold, max_convex_hull=args.max_hulls,
                        preprocess_mode="on", preprocess_resolution=args.resolution, resolution=2000, mcts_iterations=150,
                        merge=True, seed=1)
print(f"decomposed into {len(parts)} convex pieces in {time.time() - started:.0f} s")


def spread(points, k):
    """Farthest-point sampling: k points covering the piece's extremes (the hull of these hugs the original)."""
    if len(points) <= k:
        return points
    chosen = [int(np.argmax(np.linalg.norm(points - points.mean(0), axis=1)))]
    dist = np.linalg.norm(points - points[chosen[0]], axis=1)
    for _ in range(k - 1):
        chosen.append(int(np.argmax(dist)))
        dist = np.minimum(dist, np.linalg.norm(points - points[chosen[-1]], axis=1))
    return points[chosen]


parts = [(h.vertices, h.faces) for h in (trimesh.convex.convex_hull(spread(np.asarray(v), args.max_points)) for v, _ in parts)]

with open(args.dst, "w") as f:
    f.write("# Convex collision pieces (tools/collision/decompose.py). Not rendered.\n")
    base = 1
    tris = 0
    for i, (v, t) in enumerate(parts):
        f.write(f"o hull_{i:03d}\ng hull_{i:03d}\n")   # Unity splits OBJ objects on g
        for p in v:
            f.write(f"v {p[0]:.4f} {p[1]:.4f} {p[2]:.4f}\n")
        for a, b, c in t:
            f.write(f"f {a + base} {b + base} {c + base}\n")
        base += len(v)
        tris += len(t)
hull = trimesh.util.concatenate([trimesh.Trimesh(v, t) for v, t in parts])
print(f"wrote {args.dst}: {len(parts)} pieces, {tris} triangles, extents {np.round(hull.extents, 2)} "
      f"(max {max(len(t) for _, t in parts)} triangles per piece; convex MeshColliders allow 255)")
