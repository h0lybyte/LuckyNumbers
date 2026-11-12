"""Bake imaginais' "Plague Arcanist" (retro PS1 low-poly magic character) into Art/Characters/Arcanist: its skinned
mesh + Mixamo rig and the clips its pack ships, in place, as FBX for Unity, like the skeleton's bake. The raw pack
stays local (art-src/, gitignored); only this bake is committed. Run by `tools/ln.py bake-monsters`.
Usage: blender -b --factory-startup --python tools/blender/export_arcanist.py -- <art-src dir> <out dir>
  reads <art-src>/PlagueArcanist/Plague Arcanist.rar (extracted with bsdtar to a temp dir), or the extracted
  <art-src>/PlagueArcanist/Plague Arcanist/{Plague Arcanist.fbx, Texture/*.png} if they're there

The pack's FBX holds ten unnamed Mixamo takes ("Armature|Armature|Armature.00N|mixamo.com|Layer0"): nine clips and
one 736-frame take with all of them on one timeline, which is also the only one with an idle (its frames 1-56). They
were told apart from contact sheets (see CLIPS). There's no death or flinch, so two are cut from the pack's own keys:
the death is the ground slam up to the moment it lands on its knees (held there, then CombatPresenter sinks it); the
flinch is the leap's crouch played down and back up.

- The rig comes in at 1/100 scale, rotated 90 deg (Mixamo cm, Y up): every pose is sampled in world space, the rig and
  skin applied to unit transforms and scaled so it stands HEIGHT m in its bind pose, and each clip keyed again on it.
  Bones lose their "mixamorig:" prefix (vertex groups follow); the 26 nothing is skinned to are dropped.
- Grounded per frame (its lowest vertex on the ground; the run: its lowest moment, so it still bounds); the takes
  are already in place (Mixamo "in place"), so arcanist.json's stride speeds are the planted foot's speed going back.
- Five materials, one texture each (head, robe, hands, coat, boots; ~850 px): packed into one 512 atlas (the
  monster skin cap, ArtImport) as arcanist_body.png, one material, so the VAT draws it in one instanced call.
  The pack's file names are dropped.
30 fps, as the pack."""

import atexit, bpy, json, os, shutil, subprocess, sys, tempfile
import numpy as np
from mathutils import Matrix, Vector

art, out_dir = sys.argv[sys.argv.index("--") + 1:][:2]
os.makedirs(out_dir, exist_ok=True)
FPS = 30
HEIGHT = 1.9    # bind pose (T-pose) top of the hood, m; its crouched stance stands ~1.75
ATLAS = 512
TIMELINE = "Armature"   # the all-clips take: "Armature|Armature|Armature|mixamo.com|Layer0"

# Clips: (out name, take (its 3rd "|" field), first frame, last frame (None: the take's), how: "loop" / "once" /
# "pingpong" (down and back: a flinch), locomotion (measure a stride), airborne (bounds: grounded per clip, not frame)).
CLIPS = [
    ("idle", TIMELINE, 1, 56, "loop", False, False),                 # crouched spellcaster's stance, breathing
    ("creep", "Armature.009", 1, None, "loop", True, False),          # hunched scuttle, arms out
    ("run", "Armature.008", 1, None, "loop", True, True),             # a sprint, robe flapping
    ("palm_strike", "Armature.006", 1, None, "once", False, False),   # lunge, one palm shoved out: its melee
    ("channel", "Armature.005", 1, None, "once", False, False),       # both hands drawn back, thrust out, held: its cast
    ("flinch", "Armature.001", 1, 11, "pingpong", False, False),      # the leap's crouch, down and back up
    ("collapse", "Armature.004", 1, 43, "once", False, False),        # spins, arms up, drops to its knees: its death
]
# Not used (in the pack): Armature.002 / .003 (side steps), .007 (a rising two-hand cast), .001's leap itself.

PACK = "Plague Arcanist"
src_dir = os.path.join(art, "PlagueArcanist", PACK)
if not os.path.isfile(os.path.join(src_dir, f"{PACK}.fbx")):
    tmp = tempfile.mkdtemp(prefix="arcanist-")
    atexit.register(shutil.rmtree, tmp, True)
    subprocess.run(["bsdtar", "-xf", os.path.join(art, "PlagueArcanist", f"{PACK}.rar"), "-C", tmp], check=True)
    src_dir = os.path.join(tmp, PACK)
fbx = os.path.join(src_dir, f"{PACK}.fbx")

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.fps, scene.render.fps_base = FPS, 1.0
bpy.ops.import_scene.fbx(filepath=fbx)
arm = next(o for o in scene.objects if o.type == "ARMATURE")
body = next(o for o in scene.objects if o.type == "MESH" and o.parent == arm)
for o in list(scene.objects):
    if o not in (arm, body):
        bpy.data.objects.remove(o, do_unlink=True)
# Skinned as Unity skins it (4 bones a vertex, weights summing to 1), so the grounding below measures the skin the
# game draws (158 of the pack's vertices, round the robe, have more than 4).
over = sum(1 for v in body.data.vertices if sum(1 for g in v.groups if g.weight > 0) > 4)
bpy.ops.object.select_all(action="DESELECT")
body.select_set(True)
bpy.context.view_layer.objects.active = body
bpy.ops.object.vertex_group_limit_total(group_select_mode="ALL", limit=4)
bpy.ops.object.vertex_group_normalize_all(group_select_mode="ALL", lock_active=False)
print("WEIGHTS", over, "vertices had more than 4 bones; cut to 4, normalised")
PREFIX = "mixamorig:"
names = [b.name for b in arm.data.bones]
short = {n: n[len(PREFIX):] if n.startswith(PREFIX) else n for n in names}


def use_action(obj, action):
    ad = obj.animation_data or obj.animation_data_create()
    ad.action = action
    if action is not None and getattr(action, "slots", None):
        ad.action_slot = action.slots[0]


def take(name):
    for a in bpy.data.actions:
        if a.name.split("|")[2] == name:
            return a
    raise SystemExit(f"no take {name}")


def lowest_world():
    o = body.evaluated_get(bpy.context.evaluated_depsgraph_get())
    co = np.empty(len(o.data.vertices) * 3)
    o.data.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    m = np.array(o.matrix_world)
    return float((co @ m[2, :3] + m[2, 3]).min())


# 1. Sample every clip in the source's world space: each bone's world matrix, and the skin's lowest point.
samples = {}
for out_name, take_name, f0, f1, how, _, _ in CLIPS:
    action = take(take_name)
    use_action(arm, action)
    f1 = f1 or int(round(action.frame_range[1]))
    frames = list(range(f0, f1 + 1))
    if how == "pingpong":
        frames += frames[-2::-1]
    rows = []
    for f in frames:
        scene.frame_set(f)
        rows.append(({short[pb.name]: arm.matrix_world @ pb.matrix for pb in arm.pose.bones}, lowest_world()))
    samples[out_name] = rows

# 2. The rig and skin on unit transforms, metres, HEIGHT tall in the bind pose; bones renamed.
use_action(arm, None)
for pb in arm.pose.bones:
    pb.location, pb.rotation_quaternion, pb.rotation_euler, pb.scale = (0, 0, 0), (1, 0, 0, 0), (0, 0, 0), (1, 1, 1)
for a in list(bpy.data.actions):
    bpy.data.actions.remove(a)
bpy.context.view_layer.update()
rest_co = [body.matrix_world @ v.co for v in body.data.vertices]
rest_low = min(v.z for v in rest_co)
rest_high = max(v.z for v in rest_co)
s = HEIGHT / (rest_high - rest_low)
arm.scale = arm.scale * s
bpy.context.view_layer.update()
for o in scene.objects:
    o.select_set(o in (arm, body))
bpy.context.view_layer.objects.active = arm
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
# The bind pose stands on the ground too (the pack's sits 1.6 m under it; only the clips' poses stood up), so the
# rest the FBX opens in is upright where the model is placed. Poses are sampled in world space: unaffected.
arm.location.z = -min((body.matrix_world @ v.co).z for v in body.data.vertices)
bpy.context.view_layer.update()
bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
for b in arm.data.bones:
    b.name = short[b.name]
arm.name, body.name = "Arcanist_Armature", "Arcanist"
arm.data.name, body.data.name = "Arcanist_Armature", "Arcanist"
# Bones nothing is skinned to (Mixamo's tip and "_end" bones: 26 of 78) go, unless a skinned one hangs off them: each
# would be 3 more texels per VAT frame and 10 more curves per clip, for no vertex.
groups = {g.index: g.name for g in body.vertex_groups}
skinned = {groups[g.group] for v in body.data.vertices for g in v.groups if g.weight > 0 and g.group in groups}
def needed(b):
    return b.name in skinned or any(needed(c) for c in b.children)
drop = [b.name for b in arm.data.bones if not needed(b)]
bpy.ops.object.select_all(action="DESELECT")
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode="EDIT")
for n in drop:
    arm.data.edit_bones.remove(arm.data.edit_bones[n])
bpy.ops.object.mode_set(mode="OBJECT")
for n in drop:
    if n in body.vertex_groups:
        body.vertex_groups.remove(body.vertex_groups[n])
print("DROPPED", len(drop), "unskinned bones:", ", ".join(drop))
tbones = arm.data.bones
order = []
def walk(b):
    order.append(b.name)
    for c in b.children:
        walk(c)
for b in tbones:
    if b.parent is None:
        walk(b)
rest = {n: tbones[n].matrix_local.copy() for n in order}   # armature space == world now


def scaled(m):
    loc, rot, _ = m.decompose()
    out = rot.to_matrix().to_4x4()
    out.translation = loc * s
    return out


# 3. Each clip: scaled, grounded, stride measured, keyed on the new rig.
clips = []
for out_name, take_name, f0, f1, how, locomotion, airborne in CLIPS:
    rows = samples[out_name]
    frames = [{n: scaled(m) for n, m in mats.items()} for mats, _ in rows]
    lows = [low * s for _, low in rows]
    floor = min(lows)
    for mats, low in zip(frames, lows):
        lift = -(floor if airborne else low)
        for m in mats.values():
            m.translation.z += lift
    n = len(frames)
    seconds = max(1e-3, (n - 1) / FPS)
    h0, h1 = frames[0]["Hips"].translation, frames[-1]["Hips"].translation
    travel = Vector((h1.x - h0.x, h1.y - h0.y, 0.0))
    if how == "loop" and travel.length > 1e-3:   # any net drift over a loop: cut
        for i, mats in enumerate(frames):
            for m in mats.values():
                m.translation -= travel * (i / (n - 1))
    speed, spread = 0.0, ""
    if locomotion:
        # In place: the planted (lower) foot slides back (+Y: the front is -Y) at the body's speed.
        toes = ("LeftToeBase", "RightToeBase")
        ground = min(m[t].translation.z for m in frames for t in toes)
        v = []
        for i in range(n - 1):
            a, b = frames[i], frames[i + 1]
            low = min(toes, key=lambda t: a[t].translation.z + b[t].translation.z)
            if max(a[low].translation.z, b[low].translation.z) < ground + 0.03:
                v.append((b[low].translation.y - a[low].translation.y) * FPS)
        v.sort()
        speed = v[len(v) // 2]
        spread = f" (planted foot {v[len(v) // 4]:.2f}..{v[3 * len(v) // 4]:.2f} m/s, middle half, {len(v)} frames)"
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
    tops = [m["Head"].translation.z for m in frames]
    print("CLIP", out_name, f"<- {take_name} {f0}..{f1 or 'end'} {how}", f"{seconds:.2f} s", f"travel {travel.length:.3f} m",
          f"stride {speed:.3f} m/s{spread}", f"head {min(tops):.2f}..{max(tops):.2f} m")
    clips.append({"name": out_name, "seconds": round(seconds, 4), "speed": round(speed, 4) + 0.0})

# 4. One atlas, one material. Tiles (x, y, w, h in px, y up as UVs are): what each slot's texture shows on screen.
me = body.data
TILES = {   # by the source material's name prefix
    "cuerpo": (0, 256, 256, 256),     # robe / torso
    "cota": (256, 256, 256, 256),     # coat skirt (its texture's file says trousers)
    "cabeza": (0, 0, 256, 256),       # head: hood and beaked mask
    "manos": (256, 128, 256, 128),    # hands (half height: small on screen)
    "piernas": (256, 0, 256, 128),    # boots and shins
}
atlas = np.zeros((ATLAS, ATLAS, 4), dtype=np.float32)
atlas[..., 3] = 1.0
slot_tile = {}
for i, mat in enumerate(me.materials):
    key = next(k for k in TILES if mat.name.startswith(k))
    x, y, w, h = TILES[key]
    slot_tile[i] = (x, y, w, h)
    img = next(n.image for n in mat.node_tree.nodes if n.type == "TEX_IMAGE" and n.image)
    small = img.copy()
    small.scale(w, h)
    px = np.array(small.pixels[:], dtype=np.float32).reshape(h, w, 4)
    atlas[y:y + h, x:x + w] = px
    bpy.data.images.remove(small)
uv = me.uv_layers.active.data
for p in me.polygons:
    x, y, w, h = slot_tile[p.material_index]
    for li in p.loop_indices:
        u, v = uv[li].uv
        uv[li].uv = ((x + u * w) / ATLAS, (y + v * h) / ATLAS)
    p.material_index = 0
skin = bpy.data.materials.new("Arcanist")
me.materials.clear()
me.materials.append(skin)
out_img = bpy.data.images.new("arcanist_body", ATLAS, ATLAS, alpha=False)
out_img.pixels = atlas.ravel()
out_img.filepath_raw = os.path.join(out_dir, "arcanist_body.png")
out_img.file_format = "PNG"
out_img.save()

# 5. Out: the rest pose for the bind, every clip a take.
arm.animation_data.action = None
for pb in arm.pose.bones:
    pb.location, pb.rotation_quaternion, pb.scale = (0, 0, 0), (1, 0, 0, 0), (1, 1, 1)
scene.frame_set(0)
bpy.context.view_layer.update()
for o in scene.objects:
    o.select_set(o in (arm, body))
bpy.context.view_layer.objects.active = arm
bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "arcanist.fbx"), use_selection=True,
                         object_types={"ARMATURE", "MESH"}, apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
                         axis_forward="-Z", axis_up="Y", bake_space_transform=True, mesh_smooth_type="FACE",
                         add_leaf_bones=False, primary_bone_axis="Y", secondary_bone_axis="X",
                         bake_anim=True, bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False,
                         bake_anim_force_startend_keying=True, bake_anim_simplify_factor=1.0,
                         path_mode="STRIP", embed_textures=False)
with open(os.path.join(out_dir, "arcanist.json"), "w") as f:
    json.dump({"clips": sorted(clips, key=lambda c: c["name"])}, f, indent=2)
    f.write("\n")
tris = sum(len(p.vertices) - 2 for p in me.polygons)
print("BAKED", len(clips), "clips", len(me.vertices), "vertices", len(tbones), "bones", tris, "tris", f"scale {s:.4f} ->", out_dir)
