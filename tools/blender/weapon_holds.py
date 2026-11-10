"""Work out how the first-person arms hold each gun in Art/Models/Weapons, and write it into weapons.json.

Pipeline: export_weapons.py (models; rewrites weapons.json) -> this (holds) -> tools/ln.py scenes + build-web + serve
-> tools/ln.py bake-grasps (finger grips, from the game itself) -> tools/ln.py scenes (the weapons carry it all).

Usage (after tools/blender/export_weapons.py, which rewrites weapons.json):
    blender -b --disable-autoexec --python tools/blender/weapon_holds.py -- <Art/Models/Weapons dir> [--render <out dir>]

For each gun:
1. Shape: its surface is sampled every cm and sliced along the barrel. The body's underside is the usual bottom
   of the slices; what hangs below it is grip, trigger guard, magazine or stock toe. The grip is the rearmost hanging
   run clear of the butt; the left hand goes under the handguard ahead of the trigger guard.
2. Hold: the PSX arms (shoulders at +-0.2 m beside and 7 cm below the eye, upper arm 0.322 m + forearm 0.237 m) are
   fitted by a grid search over where the grip sits in front of the eye, how far ahead the left hand goes, how
   much the gun is shrunk for the view and how far the whole arms rig shifts while holding it (viewmodel tricks). A hold must keep both wrists in comfortable reach, the
   whole gun in front of the camera and off the crosshair, and the muzzle on screen; among those, the most natural
   (full size, a real hand span, right arm half bent, gun low and right) wins.
   Guns the shape analysis gets wrong (tools/blender/weapon_sockets.json: e.g. the heavy cannon, held by handles on
   top) give their hands' sockets by hand instead: a point and which way the palm and fingers face.
3. --render draws each gun so posed, with the arms as sticks, for checking (and a contact sheet, holds.png).

All points are in Unity's space for the model (+Z down the barrel, +Y up), which is what the scene builder and
WeaponView use: Unity (x, y, z) = Blender (-x, z, -y) for these FBXs. The hand rules (wrist offsets from the contact
points, palm and finger directions) mirror FirstPersonPresenter.HoldWeapon.
"""

import bpy, json, math, os, sys
import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:]
weapons_dir = argv[0]
render_dir = argv[argv.index("--render") + 1] if "--render" in argv else None
only = argv[argv.index("--only") + 1].split(",") if "--only" in argv else None   # just these guns (the rest kept)

# The arms (eye space, metres): ArmsView logs these as "shoulders (eye space)" and "reach".
SHOULDER = {"L": np.array([-0.20, -0.07, 0.0]), "R": np.array([0.20, -0.07, 0.0])}
UPPER, LOWER = 0.322, 0.237
REACH = UPPER + LOWER
FOV_V = math.radians(70.0)   # the eyes' vertical field of view
ASPECT = 16 / 9
AIM = np.array([0.0, 0.0, 25.0])   # barrels converge on the crosshair this far ahead


def load_points(path):
    """Dense surface points (Unity model space) of the FBX's meshes."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=path)
    pts = []
    for o in bpy.data.objects:
        if o.type != 'MESH':
            continue
        me = o.data
        me.calc_loop_triangles()
        W = o.matrix_world
        V = np.array([tuple(W @ v.co) for v in me.vertices])
        for t in me.loop_triangles:
            a, b, c = (V[i] for i in t.vertices)
            n = int(min(12, max(1, max(np.linalg.norm(a - b), np.linalg.norm(b - c), np.linalg.norm(c - a)) / 0.01)))
            for i in range(n + 1):
                for j in range(n + 1 - i):
                    pts.append(a + (b - a) * (i / n) + (c - a) * (j / n))
    p = np.array(pts)
    return np.stack([-p[:, 0], p[:, 2], -p[:, 1]], axis=1)   # Blender -> Unity


def analyse(p):
    """Grip, foregrip range and muzzle from the shape (model space)."""
    zmin, zmax = p[:, 2].min(), p[:, 2].max()
    L = zmax - zmin
    dz = 0.01
    edges = np.arange(zmin, zmax + dz, dz)
    idx = np.clip(((p[:, 2] - zmin) / dz).astype(int), 0, len(edges) - 2)
    n = len(edges) - 1
    ymin = np.full(n, np.nan)
    for i in range(n):
        s = p[idx == i]
        if len(s):
            ymin[i] = s[:, 1].min()
    zc = zmin + (np.arange(n) + 0.5) * dz
    body_bottom = np.nanpercentile(ymin, 70)    # the usual underside: hanging parts are the low exceptions
    hang = body_bottom - ymin
    hanging = np.nan_to_num(hang, nan=0.0) > 0.025
    runs, i = [], 0
    while i < n:
        if hanging[i]:
            j = i
            while j + 1 < n and hanging[j + 1]:
                j += 1
            runs.append((i, j))
            i = j + 1
        else:
            i += 1
    butt = int(0.06 * n)   # slices at the very back: the stock's toe, not a grip
    mid = (zmin + zmax) / 2
    grip_runs = [r for r in runs if r[0] > butt and zc[r[0]] < mid + 0.1 * L]
    y_palm = body_bottom - 0.035
    if grip_runs:
        g0, g1 = grip_runs[0]   # rearmost
        depth = np.nanmax(hang[g0:g1 + 1])
        band = p[(p[:, 2] >= zc[g0] - dz) & (p[:, 2] <= zc[g1] + dz) &
                 (p[:, 1] > body_bottom - 0.05) & (p[:, 1] < body_bottom - 0.02)]
        grip = np.array([band[:, 0].mean(), y_palm, band[:, 2].mean()]) if len(band) else \
            np.array([0.0, y_palm, zc[(g0 + g1) // 2]])
        # The trigger guard (one short run just ahead) is in front of the grip: the left hand goes past it. Only the
        # first: longer low parts further on (a magazine, a barrel shroud) are where the left hand can go.
        front = zc[g1]
        guard = next((r for r in runs if r[0] > g1), None)
        if guard is not None and zc[guard[0]] - front < 0.06 and zc[guard[1]] - zc[guard[0]] < 0.1:
            front = zc[guard[1]]
        kind = f"pistol grip ({depth * 100:.0f} cm deep)"
    else:
        # No grip hanging below (a blocky gun): the hand goes under the body behind the middle.
        z = mid - 0.15 * L
        k = int((z - zmin) / dz)
        grip = np.array([0.0, (ymin[k] if not np.isnan(ymin[k]) else body_bottom) + 0.02, z])
        front = grip[2] + 0.06
        kind = "no pistol grip: under the body"
    fore_from = front + 0.04
    fore_to = zmax - 0.06

    def fore_at(z):
        k = int(np.clip((z - zmin) / dz, 0, n - 1))
        s = p[np.abs(p[:, 2] - z) < 0.015]
        y = s[:, 1].min() if len(s) else body_bottom
        return np.array([s[:, 0].mean() if len(s) else 0.0, y, z])

    tip = p[p[:, 2] > zmax - 0.02 * L]
    muzzle = np.array([tip[:, 0].mean(), tip[:, 1].mean(), zmax + 0.01])
    return dict(grip=grip, fore_from=fore_from, fore_to=fore_to, fore_at=fore_at, muzzle=muzzle, kind=kind,
                L=L, body_bottom=body_bottom)


# Default hand directions (model space): a pistol grip (palm against its left side, knuckles forward and down its
# rake) and a handguard (palm up under it, fingers wrapping over to the right). FirstPersonPresenter's defaults too.
GRIP_PALM, GRIP_ALONG = np.array([-1.0, 0.0, 0.0]), np.array([0.0, -0.5, 1.0]) / math.hypot(0.5, 1.0)
FORE_PALM, FORE_ALONG = np.array([0.0, 1.0, 0.0]), np.array([0.5, 0.0, 1.0]) / math.hypot(0.5, 1.0)

SOCKETS = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "weapon_sockets.json")))


def apply_sockets(name, shape):
    """Hand-placed sockets override the analysis: the grip point, one foregrip point, and the hands' directions."""
    sk = SOCKETS.get(name)
    if not sk:
        return
    g, f = sk["grip"], sk["foregrip"]
    shape["grip"] = np.array(g["point"], dtype=float)
    fore = np.array(f["point"], dtype=float)
    shape["fore_at"] = lambda z: fore
    shape["fore_from"] = shape["fore_to"] = fore[2]
    shape["dirs"] = tuple(norm(np.array(v, dtype=float)) for v in (g["palm"], g["along"], f["palm"], f["along"]))
    shape["kind"] = "hand-placed sockets"


def look_rotation(fwd, up=np.array([0.0, 1.0, 0.0])):
    """Columns: right, up, forward (Unity's LookRotation as a matrix)."""
    f = fwd / np.linalg.norm(fwd)
    r = np.cross(up, f)   # Unity is left-handed: right = up x forward
    r /= np.linalg.norm(r)
    u = np.cross(f, r)
    return np.stack([r, u, f], axis=1)


def norm(v):
    return v / np.linalg.norm(v)


def wrists(R, grip_eye, fore_eye, dirs):
    """The wrist targets FirstPersonPresenter.HoldWeapon asks the IK for (right on the grip, left on the foregrip):
    from each contact, back along the fingers and off the palm."""
    gp, ga, fp, fa = dirs

    def wrist(contact, palm, along):
        along = norm(R @ along)
        palm = R @ palm
        palm = norm(palm - along * np.dot(palm, along))
        return contact - along * 0.075 - palm * 0.035
    return wrist(grip_eye, gp, ga), wrist(fore_eye, fp, fa)


def elbow(shoulder, wrist, side):
    """Two-bone elbow, bent down and out (like the rig's poles)."""
    d = wrist - shoulder
    dist = min(np.linalg.norm(d), REACH * 0.999)
    n = norm(d)
    cos_a = np.clip((UPPER ** 2 + dist ** 2 - LOWER ** 2) / (2 * UPPER * dist), -1, 1)
    pole = np.array([side * 0.6, -1.0, -0.2])
    perp = norm(pole - n * np.dot(pole, n))
    return shoulder + n * UPPER * cos_a + perp * UPPER * math.sqrt(1 - cos_a ** 2)


# Shifts of the whole arms rig (ArmsView.Pose's offset) allowed while a gun is held: right brings the left shoulder
# toward a gun held low and right, so its side shows and the left hand still reaches a real foregrip.
SHIFTS = [np.array([x, y, 0.0]) for x in (0.0, 0.04, 0.08, 0.12) for y in (0.0, -0.05)]


def solve(shape, p):
    rng = np.random.default_rng(0)
    hull = p[rng.choice(len(p), size=min(1200, len(p)), replace=False)]
    grip = shape["grip"]
    tan_v = math.tan(FOV_V / 2)
    best = None
    zs = np.arange(shape["fore_from"], max(shape["fore_from"] + 0.001, shape["fore_to"]), 0.02)
    fores = np.array([shape["fore_at"](z) for z in zs])   # the handguard's underside, ahead of the trigger guard
    for s in np.arange(1.0, 0.55, -0.05):
        for hx in np.arange(0.08, 0.23, 0.02):   # off centre: the gun's side shows, the crosshair stays clear
            for hy in np.arange(-0.30, -0.03, 0.02):   # (high: a gun held by handles on top hangs below the hands)
                for hz in np.arange(0.14, 0.50, 0.02):
                    hold = np.array([hx, hy, hz])
                    R = look_rotation(AIM - hold)
                    world = hold + ((hull - grip) * s) @ R.T
                    if world[:, 2].min() < 0.03:
                        continue   # pokes behind the camera
                    sx = world[:, 0] / (world[:, 2] * tan_v * ASPECT)
                    sy = world[:, 1] / (world[:, 2] * tan_v)
                    if np.any((np.abs(sx) < 0.1) & (np.abs(sy) < 0.12)):
                        continue   # covers the crosshair
                    on = (np.abs(sx) < 1) & (np.abs(sy) < 1)
                    cover = 0.0
                    if on.any():
                        cover = (min(1, sx[on].max()) - max(-1, sx[on].min())) * (min(1, sy[on].max()) - max(-1, sy[on].min())) / 4
                    if cover > 0.4 or cover < 0.08:
                        continue   # hides too much of the view, or barely in it
                    m = hold + ((shape["muzzle"] - grip) * s) @ R.T
                    msx, msy = m[0] / (m[2] * tan_v * ASPECT), m[1] / (m[2] * tan_v)
                    if not (-0.7 < msx < 0.95 and -0.95 < msy < -0.02):
                        continue   # muzzle off screen (or above the crosshair)
                    fe = hold + ((fores - grip) * s) @ R.T
                    for k, fore in enumerate(fores):
                        wr, wl = wrists(R, hold, fe[k], shape.get("dirs", (GRIP_PALM, GRIP_ALONG, FORE_PALM, FORE_ALONG)))
                        span = (fore[2] - grip[2]) * s
                        for shift in SHIFTS:
                            dr = np.linalg.norm(wr - SHOULDER["R"] - shift)
                            dl = np.linalg.norm(wl - SHOULDER["L"] - shift)
                            if not (0.22 < dr < 0.5 and 0.22 < dl < 0.5):
                                continue   # out of comfortable reach (or folded up against the shoulder)
                            score = (abs(dr - 0.38) * 2 + max(0, dl - 0.44) * 4 + (1 - s) * 1.5 +
                                     abs(span - 0.24) * 1.5 + abs(hx - 0.15) + abs(hy + 0.21) + cover * 0.5 +
                                     np.linalg.norm(shift) * 0.8)
                            if best is None or score < best[0]:
                                best = (score, dict(scale=s, hold=hold, fore=fore, wr=wr, wl=wl, dr=dr, dl=dl, cover=cover,
                                                    R=R, span=span, shift=shift))
    return best[1] if best else None


def render(name, path, shape, h, out):
    """The gun posed in eye space with the arms as sticks (Blender coordinates: x = -ux, y = uz, z = uy... with the
    camera at the eye looking down Unity +Z)."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=path)
    sc = bpy.context.scene

    def to_b(u):   # Unity eye/model space -> Blender
        return (-u[0], -u[2], u[1])

    from mathutils import Matrix, Vector
    R, s, grip, hold = h["R"], h["scale"], shape["grip"], h["hold"]
    # Model (Unity) -> eye (Unity): e = hold + R s (m - grip). Blender model coords b: m = C b, C = Unity<-Blender.
    C = np.array([[-1, 0, 0], [0, 0, 1], [0, -1, 0]], dtype=float)
    A = C.T @ (R * s) @ C
    t = C.T @ (hold - (R * s) @ grip)
    M = Matrix(((*A[0], t[0]), (*A[1], t[1]), (*A[2], t[2]), (0, 0, 0, 1)))
    for o in [o for o in sc.objects if o.type == 'MESH']:
        o.matrix_world = M @ o.matrix_world
    for m in bpy.data.materials:
        m.diffuse_color = (0.1, 0.7, 1, 1) if 'light' in m.name else ((0.3, 0.3, 0.32, 1) if 'dark' in m.name else (0.75, 0.75, 0.72, 1))

    def mat(name, rgba):
        m = bpy.data.materials.new(name)
        m.diffuse_color = rgba
        return m
    skin, mark = mat("skin", (0.85, 0.6, 0.45, 1)), {}

    def stick(a, b, r, m):
        a, b = Vector(to_b(a)), Vector(to_b(b))
        bpy.ops.mesh.primitive_cylinder_add(radius=r, depth=(b - a).length, location=(a + b) / 2)
        o = bpy.context.active_object
        o.rotation_mode = 'QUATERNION'
        o.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(b - a)
        o.data.materials.append(m)

    def ball(c, r, m):
        bpy.ops.mesh.primitive_uv_sphere_add(radius=r, location=to_b(c))
        bpy.context.active_object.data.materials.append(m)
    for side, w in (("R", h["wr"]), ("L", h["wl"])):
        sh = SHOULDER[side] + h["shift"]
        e = elbow(sh, w, 1 if side == "R" else -1)
        stick(sh, e, 0.035, skin)
        stick(e, w, 0.03, skin)
        ball(w, 0.04, skin)
    e = lambda u: hold + ((u - grip) * s) @ R.T
    ball(e(grip), 0.012, mat("g", (0, 1, 0, 1)))
    ball(e(h["fore"]), 0.012, mat("f", (1, 1, 0, 1)))
    ball(e(shape["muzzle"]), 0.01, mat("m", (1, 0, 1, 1)))

    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.color_type = 'MATERIAL'
    sc.render.resolution_x, sc.render.resolution_y = 640, 360
    cam = bpy.data.objects.new("eye", bpy.data.cameras.new("eye"))
    sc.collection.objects.link(cam)
    cam.data.sensor_fit = 'VERTICAL'
    cam.data.angle = FOV_V
    cam.data.clip_start = 0.01
    cam.location = (0, 0, 0)
    cam.rotation_euler = (math.pi / 2, 0, math.pi)   # looking down Blender -Y (= Unity +Z), Blender +Z up
    sc.camera = cam
    sc.render.filepath = os.path.join(out, f"hold_{name}.png")
    bpy.ops.render.render(write_still=True)


index_path = os.path.join(weapons_dir, "weapons.json")
with open(index_path) as f:
    index = json.load(f)
rendered = []
for item in index["items"]:
    if item["kind"] != "gun":
        continue
    name = item["name"]
    if only and name not in only:
        continue
    path = os.path.join(weapons_dir, name + ".fbx")
    p = load_points(path)
    shape = analyse(p)
    apply_sockets(name, shape)
    print(f"SHAPE {name}: {shape['kind']}, grip {np.round(shape['grip'], 3)}, left hand from z {shape['fore_from']:.2f} "
          f"to {shape['fore_to']:.2f}, muzzle {np.round(shape['muzzle'], 3)}, length {shape['L']:.2f} m")
    h = solve(shape, p)
    if h is None:
        print(f"HOLD {name}: no hold fits ({shape['kind']})")
        item.pop("hold", None)
        continue
    r3 = lambda v: [round(float(x), 4) for x in v]
    item["hold"] = {"grip": r3(shape["grip"]), "foregrip": r3(h["fore"]), "muzzle": r3(shape["muzzle"]),
                    "offset": r3(h["hold"]), "scale": round(float(h["scale"]), 3), "arms_shift": r3(h["shift"])}
    gp, ga, fp, fa = shape.get("dirs", (GRIP_PALM, GRIP_ALONG, FORE_PALM, FORE_ALONG))
    item["hold"].update(grip_palm=r3(gp), grip_along=r3(ga), fore_palm=r3(fp), fore_along=r3(fa))
    print(f"HOLD {name}: {shape['kind']}; scale {h['scale']:.2f}, grip at {r3(h['hold'])}, hand span {h['span']:.2f} m, "
          f"reach R {h['dr']:.2f} L {h['dl']:.2f} m (of {REACH:.2f}), arms shifted {r3(h['shift'])}, covers {h['cover'] * 100:.0f}% of the view")
    if render_dir:
        os.makedirs(render_dir, exist_ok=True)
        render(name, path, shape, h, render_dir)
        rendered.append(name)

import re
with open(index_path, "w") as f:
    # Like tools/ln.py bake-grasps: indented, but arrays of plain numbers on one line.
    text = re.sub(r"\[\s*(-?[\d.e+-]+(?:,\s*-?[\d.e+-]+)*)\s*\]",
                  lambda m: "[" + ", ".join(x.strip() for x in m.group(1).split(",")) + "]", json.dumps(index, indent=2))
    f.write(text + "\n")
print("WROTE", index_path, "renders:", rendered)
