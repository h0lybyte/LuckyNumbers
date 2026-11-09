# Usage: blender -b --factory-startup --python tools/blender/gltf_to_fbx_by_material.py -- in.gltf out.fbx
# Blender: glTF -> FBX with one mesh object per material, each named after its material (mat_0005 -> "mat_0005"),
# so Unity (materials not imported) can still pair each part with its texture. Args after "--": in.gltf out.fbx
import bpy, sys, re
src, dst = sys.argv[sys.argv.index("--") + 1:][:2]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
for o in [o for o in bpy.context.scene.objects if o.type == "MESH"]:
    bpy.ops.object.select_all(action="DESELECT"); o.select_set(True); bpy.context.view_layer.objects.active = o
    bpy.ops.object.mode_set(mode="EDIT"); bpy.ops.mesh.separate(type="MATERIAL"); bpy.ops.object.mode_set(mode="OBJECT")
parts = {}
for o in [o for o in bpy.context.scene.objects if o.type == "MESH"]:
    mat = o.active_material.name if o.active_material else "none"
    parts[mat] = parts.get(mat, 0) + 1
    o.name = mat if parts[mat] == 1 else f"{mat}.{parts[mat]:03d}"
    o.data.name = o.name
print("PARTS", len([o for o in bpy.context.scene.objects if o.type == "MESH"]), sorted(parts.items()))
bpy.ops.export_scene.fbx(filepath=dst, apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
                         path_mode="STRIP", embed_textures=False, mesh_smooth_type="FACE", add_leaf_bones=False)
