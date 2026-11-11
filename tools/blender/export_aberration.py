"""Bake Rafael Martins' "Aberration" PSX creature into Art/Characters/Aberration: one skinned mesh + its armature and
every clip, in place, as FBX for Unity. The raw pack stays local (art-src/, gitignored: its files may not be
redistributed); only this bake is committed.
Usage: blender -b --factory-startup --python tools/blender/export_aberration.py -- <aberration.glb|.blend> <out dir>

- Every action in the source is exported as a clip (add your own actions to a .blend and bake from it: they come too).
- Root motion is stripped: the "root" bone's travel along the walk (Blender -Y, the creature's front) is cut to zero,
  so clips play in place and CombatPresenter moves the body. aberration.json records each clip's travel speed (m/s),
  so MonsterView can play walks at the rate the body actually moves (no foot sliding).
- Axes applied (bake_space_transform): the armature and mesh come in unrotated, so the takes' keys on the armature
  object don't tip the creature over (with a +90 deg X node they'd lay it on its back).
- The pack's stray Icosphere is dropped; the texture is copied beside the FBX (Unity imports no materials).
Scene at 30 fps (the clips' rate), so keys land on whole frames."""

import bpy, json, os, shutil, sys

src, out_dir = sys.argv[sys.argv.index("--") + 1:][:2]
os.makedirs(out_dir, exist_ok=True)
FPS = 30
ROOT = "root"

if src.endswith(".blend"):
    bpy.ops.wm.open_mainfile(filepath=src)
else:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.render.fps = FPS
    bpy.ops.import_scene.gltf(filepath=src)
scene = bpy.context.scene
scene.render.fps = FPS
scene.render.fps_base = 1.0

arm = next(o for o in scene.objects if o.type == "ARMATURE")
body = next(o for o in scene.objects if o.type == "MESH" and o.parent == arm)
for o in list(scene.objects):
    if o not in (arm, body):
        bpy.data.objects.remove(o, do_unlink=True)
arm.name, body.name = "Aberration_Armature", "Aberration"


def curves(action):
    """F-curves of an action (Blender 4.4+ slotted actions keep them in layers/strips/channelbags)."""
    if hasattr(action, "layers") and action.layers:
        return [c for l in action.layers for s in l.strips for cb in s.channelbags for c in cb.fcurves]
    return list(action.fcurves)


root_loc = f'pose.bones["{ROOT}"].location'
clips = []
for action in bpy.data.actions:
    f0, f1 = action.frame_range
    seconds = max(1e-3, (f1 - f0) / FPS)
    travel = 0.0
    for c in curves(action):
        if c.data_path != root_loc:
            continue
        start = c.evaluate(f0)
        delta = c.evaluate(f1) - start
        if c.array_index == 1:
            travel = -delta   # the root bone's local Y is the world's: forward is -Y
        if abs(delta) > 1e-4:   # it travels on this axis: hold it where it starts
            for k in c.keyframe_points:
                k.co.y = start
                k.handle_left.y = start
                k.handle_right.y = start
            c.update()
    clips.append({"name": action.name, "seconds": round(seconds, 4), "speed": round(travel / seconds, 4) + 0.0})
    action.use_fake_user = True
    print("CLIP", action.name, f"{seconds:.2f} s", f"travel {travel:.3f} m", f"{travel / seconds:.3f} m/s")

# Rest pose for the export's bind; every action goes out as its own take.
if arm.animation_data:
    arm.animation_data.action = None
for o in scene.objects:
    o.select_set(o in (arm, body))
bpy.context.view_layer.objects.active = arm
bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "aberration.fbx"), use_selection=True,
                         object_types={"ARMATURE", "MESH"}, apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
                         axis_forward="-Z", axis_up="Y", bake_space_transform=True, mesh_smooth_type="FACE",
                         add_leaf_bones=False, primary_bone_axis="Y", secondary_bone_axis="X",
                         bake_anim=True, bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False,
                         bake_anim_force_startend_keying=True, bake_anim_simplify_factor=1.0,
                         path_mode="STRIP", embed_textures=False)

texture = os.path.join(os.path.dirname(os.path.abspath(src)), "aberration_body.png")
if os.path.exists(texture):
    shutil.copyfile(texture, os.path.join(out_dir, "aberration_body.png"))
with open(os.path.join(out_dir, "aberration.json"), "w") as f:
    json.dump({"clips": clips}, f, indent=2)
    f.write("\n")
tris = sum(len(p.vertices) - 2 for p in body.data.polygons)
print("BAKED", len(clips), "clips", tris, "tris ->", out_dir)
