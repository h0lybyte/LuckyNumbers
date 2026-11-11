"""Export the Roman Vase (RenderX, Sketchfab, CC BY 4.0) to FBX for Art/Models/Relics: one part per material (Body,
Trim, PanelA, PanelB: SceneBuilder dresses each by name), decimated from 86k triangles to a low-poly Web budget,
centred, Unity axes. Usage: blender -b --python tools/blender/export_vase.py -- <ROMAN VASE.fbx> <out.fbx> [ratio]"""

import bpy, sys, mathutils

args = sys.argv[sys.argv.index("--") + 1:]
src, out = args[0], args[1]
ratio = float(args[2]) if len(args) > 2 else 0.05

bpy.ops.wm.read_factory_settings(use_empty=True)
# Blender 5's FBX importer sets a Cycles light property that no longer exists; the file's light is dropped anyway.
light = bpy.data.lights.new("tmp", 'POINT')
type(light.cycles).cast_shadow = bpy.props.BoolProperty(default=True)
bpy.data.lights.remove(light)
bpy.ops.import_scene.fbx(filepath=src)

for o in list(bpy.data.objects):
    if o.type != 'MESH':
        bpy.data.objects.remove(o, do_unlink=True)
vase = bpy.data.objects[0]
bpy.context.view_layer.objects.active = vase
vase.select_set(True)
bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

# One object per material, so each part is one mesh with one known name in Unity.
names = {"Grey Marble": "Body", "Gold Damaged": "Trim", "Material.002": "PanelA", "Material.003": "PanelB"}
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.separate(type='MATERIAL')
bpy.ops.object.mode_set(mode='OBJECT')

parts = []
before = after = 0
for o in list(bpy.data.objects):
    mat = o.material_slots[0].material.name if o.material_slots else ""
    key = next((k for k in names if mat.startswith(k)), None)
    if key is None:
        print("SKIP", o.name, mat)
        bpy.data.objects.remove(o, do_unlink=True)
        continue
    o.name = o.data.name = names[key]
    before += sum(len(p.vertices) - 2 for p in o.data.polygons)
    # Collapse decimation, per part so the material borders (gold trim against marble) stay where they were.
    d = o.modifiers.new("Decimate", 'DECIMATE')
    d.decimate_type = 'COLLAPSE'
    d.ratio = ratio
    d.use_collapse_triangulate = True
    bpy.context.view_layer.objects.active = o
    bpy.ops.object.modifier_apply(modifier=d.name)
    o.data.materials.clear()
    after += sum(len(p.vertices) - 2 for p in o.data.polygons)
    parts.append(o)

# Centre the whole vase at the origin (its parts keep their places relative to each other).
corners = [o.matrix_world @ mathutils.Vector(c) for o in parts for c in o.bound_box]
lo = mathutils.Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
hi = mathutils.Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
centre = (lo + hi) / 2
for o in parts:
    o.location -= centre
print("SIZE", tuple(round(v, 3) for v in (hi - lo)), "TRIS", before, "->", after,
      {o.name: sum(len(p.vertices) - 2 for p in o.data.polygons) for o in parts})
bpy.ops.export_scene.fbx(filepath=out, use_selection=False, object_types={'MESH'}, apply_unit_scale=True,
                         axis_forward='-Z', axis_up='Y', bake_space_transform=True, mesh_smooth_type='FACE',
                         use_mesh_modifiers=True, add_leaf_bones=False, path_mode='STRIP')
