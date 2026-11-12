"""Bake Vinrax's "PSX Skeleton" into Art/Characters/Skeleton: its skinned mesh + rig with clips retargeted from
Quaternius' Universal Animation Library (CC0), in place, as FBX for Unity, like the aberration's bake. The raw packs
stay local (art-src/, gitignored); only this bake is committed. Run by `tools/ln.py bake-monsters`.
Usage: blender -b --factory-startup --python tools/blender/export_skeleton.py -- <art-src dir> <out dir>
  reads <art-src>/Skeleton/skeleton.glb (+ skeleton_d.png) and <art-src>/UAL/{AnimationLibrary_Unity_Standard,UAL2_Standard}.fbx

Retarget (here, not Unity Humanoid): both rigs come from Rigify-style metarigs with the same joints in nearly the same
places (hip joints 0.94 vs 0.93 m), so a bone map is exact and no avatar is needed. Per frame, in armature space:
- each mapped skeleton bone takes the UAL bone's rotation away from its own rest (world delta), applied to the
  skeleton's rest turned onto the UAL rest's direction: the skeleton's A-pose arms follow UAL's T-pose rest, so an
  arm hanging in UAL hangs in the skeleton (no 10 deg offset into the ribs);
- its chest follows halfway between UAL's spine_02 and spine_03 (UAL has three spine bones, the skeleton two); the
  jaw (UAL has none) stays shut; fingers (the skeleton has none) are dropped;
- the hips move as UAL's, scaled by hip-joint height; then the body is grounded: its lowest vertex on the ground
  every frame (soles standing, the ribs and skull lying down), or for a clip with a flight phase (the jog) rising and
  falling as the UAL mannequin's does, its lowest moment on the ground. The bones' joints alone sank the toes 3 cm
  and the ribcage 4 cm, and UAL's own clips sit up to 3 cm off (its zombie idle in the ground, its walk above it).
Root motion is stripped (a loop's net hip travel cut, so it plays in place and CombatPresenter moves the body);
skeleton.json records each clip's stride speed (m/s): a loop's net travel, or for an in-place walk the speed of its
planted foot (the lower one) going back, so MonsterView plays the walk at the rate the body moves (no foot sliding).
The glTF's stray Icosphere is dropped; the texture is copied beside the FBX as skeleton_body.png. 30 fps, as UAL."""

import bpy, json, os, shutil, sys
import numpy as np
from mathutils import Matrix, Vector

art, out_dir = sys.argv[sys.argv.index("--") + 1:][:2]
os.makedirs(out_dir, exist_ok=True)
FPS = 30

# Clips: (out name, library, UAL action, locomotion: measure a stride, airborne: has moments off the ground).
UAL1, UAL2 = "AnimationLibrary_Unity_Standard.fbx", "UAL2_Standard.fbx"
CLIPS = [
    ("zombie_idle", UAL2, "Zombie_Idle_Loop", False, False),
    ("zombie_walk", UAL2, "Zombie_Walk_Fwd_Loop", True, False),
    ("jog", UAL1, "Jog_Fwd_Loop", True, True),
    ("zombie_scratch", UAL2, "Zombie_Scratch", False, False),
    ("hit_chest", UAL1, "Hit_Chest", False, False),
    ("death", UAL1, "Death01", False, False),
]

# Skeleton bone -> its UAL bone in each library (UAL 1: Rigify DEF- names; UAL 2: Unreal-style).
MAP = {
    "hips": ("DEF-hips", "pelvis"),
    "spine": ("DEF-spine.001", "spine_01"),
    "neck": ("DEF-neck", "neck_01"),
    "head": ("DEF-head", "Head"),
}
for s, (u1, u2) in {"L": ("L", "l"), "R": ("R", "r")}.items():
    MAP.update({
        f"shoulder.{s}": (f"DEF-shoulder.{u1}", f"clavicle_{u2}"),
        f"upper_arm.{s}": (f"DEF-upper_arm.{u1}", f"upperarm_{u2}"),
        f"forearm.{s}": (f"DEF-forearm.{u1}", f"lowerarm_{u2}"),
        f"hand.{s}": (f"DEF-hand.{u1}", f"hand_{u2}"),
        f"thigh.{s}": (f"DEF-thigh.{u1}", f"thigh_{u2}"),
        f"shin.{s}": (f"DEF-shin.{u1}", f"calf_{u2}"),
        f"foot.{s}": (f"DEF-foot.{u1}", f"foot_{u2}"),
        f"toe.{s}": (f"DEF-toe.{u1}", f"ball_{u2}"),
    })
CHEST = (("DEF-spine.002", "DEF-spine.003"), ("spine_02", "spine_03"))   # blended half and half

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.fps, scene.render.fps_base = FPS, 1.0

# The skeleton.
bpy.ops.import_scene.gltf(filepath=os.path.join(art, "Skeleton", "skeleton.glb"))
arm = next(o for o in scene.objects if o.type == "ARMATURE")
body = next(o for o in scene.objects if o.type == "MESH" and o.parent == arm)
for o in list(scene.objects):
    if o not in (arm, body):
        bpy.data.objects.remove(o, do_unlink=True)
for a in list(bpy.data.actions):
    bpy.data.actions.remove(a)
arm.name, body.name = "Skeleton_Armature", "Skeleton"
if arm.animation_data:
    arm.animation_data.action = None
tbones = arm.data.bones
order = []   # parents first
def walk(b):
    order.append(b.name)
    for c in b.children:
        walk(c)
for b in tbones:
    if b.parent is None:
        walk(b)
rest = {n: tbones[n].matrix_local.copy() for n in order}

# The skin, for its lowest point in a pose (linear blend skinning, as Unity draws it): rest positions (armature
# space) and each vertex's normalised weights per bone in `order`.
verts = np.array([(body.matrix_world @ v.co).to_4d() for v in body.data.vertices])
weights = np.zeros((len(verts), len(order)))
groups = {g.index: g.name for g in body.vertex_groups}
for v in body.data.vertices:
    for g in v.groups:
        name = groups.get(g.group)
        if name in rest:
            weights[v.index, order.index(name)] += g.weight
weights /= np.maximum(weights.sum(axis=1, keepdims=True), 1e-6)
rest_inv = {n: rest[n].inverted() for n in order}


def lowest_skin(mats):
    """The skin's lowest height (armature space) posed by armature-space bone matrices `mats`."""
    rows = np.array([list((mats[n] @ rest_inv[n])[2]) for n in order])   # each bone's skinning matrix's z row
    return float(((verts @ rows.T) * weights).sum(axis=1).min())


def load_library(name):
    before = set(bpy.data.objects)
    acts_before = set(bpy.data.actions)
    bpy.ops.import_scene.fbx(filepath=os.path.join(art, "UAL", name))
    new = [o for o in bpy.data.objects if o not in before]
    src = next(o for o in new if o.type == "ARMATURE")
    mannequin = next(o for o in new if o.type == "MESH")
    for o in new:
        if o not in (src, mannequin):
            bpy.data.objects.remove(o, do_unlink=True)
    return src, mannequin, [a for a in bpy.data.actions if a not in acts_before]


def lowest_world(obj, evaluated=True):
    """A mesh's lowest vertex height (world), as posed now (or its rest)."""
    o = obj.evaluated_get(bpy.context.evaluated_depsgraph_get()) if evaluated else obj
    co = np.empty(len(o.data.vertices) * 3)
    o.data.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    m = np.array(o.matrix_world)
    return float((co @ m[2, :3] + m[2, 3]).min())


libraries = {}
for lib in (UAL1, UAL2):
    libraries[lib] = load_library(lib)


def use_action(obj, action):
    ad = obj.animation_data or obj.animation_data_create()
    ad.action = action
    if getattr(ad, "action_slot", None) is None and getattr(action, "slots", None):
        ad.action_slot = action.slots[0]


def find_action(actions, short):
    for a in actions:
        if a.name.split("|")[-1] == short:
            return a
    raise SystemExit(f"no action {short}")


def bake_clip(out_name, lib, action_name, locomotion, airborne):
    src, mannequin, actions = libraries[lib]
    k = 0 if lib == UAL1 else 1
    sb = src.data.bones
    action = find_action(actions, action_name)
    use_action(src, action)
    f0, f1 = (int(round(x)) for x in action.frame_range)
    srest = {b.name: b.matrix_local.copy() for b in sb}

    def sname(t):
        return MAP[t][k]

    # Each mapped bone's rest turned onto UAL's rest direction (world), so the skeleton copies UAL's shape.
    aligned = {}
    for t in order:
        if t in MAP or t == "chest":
            s0 = srest[CHEST[k][1]] if t == "chest" else srest[sname(t)]
            r_t = rest[t].to_quaternion()
            if t == "hips":
                aligned[t] = r_t
                continue
            d_t = (r_t @ Vector((0, 1, 0))).normalized()
            d_s = (s0.to_quaternion() @ Vector((0, 1, 0))).normalized()
            aligned[t] = d_t.rotation_difference(d_s) @ r_t
    s_hip0 = srest[MAP["thigh.L"][k]].translation.z
    t_hip0 = rest["thigh.L"].translation.z
    scale = t_hip0 / s_hip0

    lows = []   # per frame: the mannequin's lowest vertex, the skeleton's

    frames = []
    for f in range(f0, f1 + 1):
        scene.frame_set(f)
        pose = {pb.name: pb.matrix.copy() for pb in src.pose.bones}
        def delta(name):
            return pose[name].to_quaternion() @ srest[name].to_quaternion().inverted()
        mats = {}
        for t in order:
            b = tbones[t]
            if b.parent is None:
                head = rest[t].translation.copy()
                if t == "hips":
                    sh = sname(t)
                    head = rest[t].translation + (pose[sh].translation - srest[sh].translation) * scale
            else:
                p = b.parent.name
                head = (mats[p] @ (rest[p].inverted() @ rest[t])).translation
            if t == "chest":
                a, c = CHEST[k]
                q = delta(a).slerp(delta(c), 0.5) @ aligned[t]
            elif t in MAP:
                q = delta(sname(t)) @ aligned[t]
            else:   # the jaw: rest, relative to its parent
                q = (mats[b.parent.name] @ (rest[b.parent.name].inverted() @ rest[t])).to_quaternion()
            m = q.to_matrix().to_4x4()
            m.translation = head
            mats[t] = m
        lows.append((lowest_world(mannequin), lowest_skin(mats)))
        frames.append(mats)
    ground = min(a for a, _ in lows)
    for mats, (a, b) in zip(frames, lows):
        lift = ((a - ground) if airborne else 0.0) - b
        for m in mats.values():
            m.translation.z += lift

    n = len(frames)
    seconds = max(1e-3, (n - 1) / FPS)
    # Net hip travel over a loop: cut (linearly), and kept as the stride.
    h0, h1 = frames[0]["hips"].translation, frames[-1]["hips"].translation
    travel = Vector((h1.x - h0.x, h1.y - h0.y, 0.0))
    loop = locomotion or action_name.endswith("_Loop")
    if loop and travel.length > 1e-3:
        for i, mats in enumerate(frames):
            shift = travel * (i / (n - 1))
            for m in mats.values():
                m.translation -= shift
    speed, spread = 0.0, ""
    if locomotion:
        forward = -travel.y   # front is -Y
        if abs(forward) > 0.05:
            speed = forward / seconds
        else:
            # In place: the planted (lower) foot slides back at the walk's speed.
            # Only frames it's down (its ball within 3 cm of the clip's lowest): a jog's flight phase has none.
            floor = min(m[s].translation.z for m in frames for s in ("toe.L", "toe.R"))
            v = []
            for i in range(n - 1):
                a, b = frames[i], frames[i + 1]
                low = min(("toe.L", "toe.R"), key=lambda s: a[s].translation.z + b[s].translation.z)
                if max(a[low].translation.z, b[low].translation.z) < floor + 0.03:
                    v.append((b[low].translation.y - a[low].translation.y) * FPS)
            v.sort()
            speed = v[len(v) // 2]
            spread = f" (planted foot {v[len(v) // 4]:.2f}..{v[3 * len(v) // 4]:.2f} m/s, middle half)"
    # Keys: each bone's local pose (matrix_basis) per frame.
    act = bpy.data.actions.new(out_name)
    act.use_fake_user = True
    use_action(arm, act)
    for pb in arm.pose.bones:
        pb.rotation_mode = "QUATERNION"
    for i, mats in enumerate(frames):
        for t in order:
            pb = arm.pose.bones[t]
            b = tbones[t]
            if b.parent is None:
                local_rest, parent = rest[t], Matrix.Identity(4)
            else:
                local_rest, parent = rest[b.parent.name].inverted() @ rest[t], mats[b.parent.name]
            basis = local_rest.inverted() @ parent.inverted() @ mats[t]
            loc, rot, _ = basis.decompose()
            pb.location, pb.rotation_quaternion, pb.scale = loc, rot, (1, 1, 1)
            pb.keyframe_insert("location", frame=i)
            pb.keyframe_insert("rotation_quaternion", frame=i)
            pb.keyframe_insert("scale", frame=i)
    low = min(lowest_skin(m) for m in frames)
    hips = sum(m["hips"].translation.z for m in frames) / n
    print("CLIP", out_name, f"<- {action_name}", f"{seconds:.2f} s", f"travel {travel.length:.3f} m",
          f"stride {speed:.3f} m/s{spread}", f"hips avg {hips:.3f} m", f"lowest vertex {low:.3f} m")
    return {"name": out_name, "seconds": round(seconds, 4), "speed": round(speed, 4) + 0.0}


clips = [bake_clip(*c) for c in CLIPS]

for src, mannequin, _ in libraries.values():
    bpy.data.objects.remove(mannequin, do_unlink=True)
    bpy.data.objects.remove(src, do_unlink=True)
for a in list(bpy.data.actions):
    if a.name not in {c[0] for c in CLIPS}:
        bpy.data.actions.remove(a)
for a in bpy.data.actions:   # every pose bone keyed: one slot per action
    a.use_fake_user = True
arm.animation_data.action = None
for pb in arm.pose.bones:
    pb.location, pb.rotation_quaternion, pb.scale = (0, 0, 0), (1, 0, 0, 0), (1, 1, 1)
scene.frame_set(0)
bpy.context.view_layer.update()   # the FBX's nodes are the evaluated pose: the rest, not the last clip's last frame
for o in scene.objects:
    o.select_set(o in (arm, body))
bpy.context.view_layer.objects.active = arm
bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "skeleton.fbx"), use_selection=True,
                         object_types={"ARMATURE", "MESH"}, apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
                         axis_forward="-Z", axis_up="Y", bake_space_transform=True, mesh_smooth_type="FACE",
                         add_leaf_bones=False, primary_bone_axis="Y", secondary_bone_axis="X",
                         bake_anim=True, bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False,
                         bake_anim_force_startend_keying=True, bake_anim_simplify_factor=1.0,
                         path_mode="STRIP", embed_textures=False)
shutil.copyfile(os.path.join(art, "Skeleton", "skeleton_d.png"), os.path.join(out_dir, "skeleton_body.png"))
with open(os.path.join(out_dir, "skeleton.json"), "w") as f:
    json.dump({"clips": sorted(clips, key=lambda c: c["name"])}, f, indent=2)
    f.write("\n")
tris = sum(len(p.vertices) - 2 for p in body.data.polygons)
print("BAKED", len(clips), "clips", len(body.data.vertices), "vertices", tris, "tris ->", out_dir)
