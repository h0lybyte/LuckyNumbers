"""Export a downloaded relic model (FBX, e.g. from Sketchfab) for Art/Models/Relics: one part per material, each named
for SceneBuilder (which paints it by `<model>/<part>`, see SceneBuilder.RelicPaints), optionally decimated, only the
first UV layer kept, centred, Unity axes, no materials.

Usage: blender -b --python tools/blender/export_relic_parts.py -- <in.fbx> <out.fbx> [--ratio R] --part MAT=NAME ...
  --part MAT=NAME  faces whose material name starts with MAT become part NAME (other materials are dropped)
  --ratio R        collapse-decimate each part to R of its triangles (default 1: as is)
  --upright        clear the objects' rotation first (a model tilted for its showcase, like the Aztec Block)

The relics so far:
  Roman Vase (RenderX):  "ROMAN VASE.fbx" roman_vase.fbx --ratio 0.05
                         --part "Grey Marble=Body" --part Material.002=PanelA --part Material.003=PanelB
  Aztec Block (RenderX): "aztec block a.fbx" aztec_block.fbx --upright --part Material.004=Stone"""

import bpy, sys, mathutils

args = sys.argv[sys.argv.index("--") + 1:]
src, out = args[0], args[1]
ratio, names, upright = 1.0, {}, False
i = 2
while i < len(args):
    if args[i] == "--upright":
        upright = True
        i += 1
        continue
    if args[i] == "--ratio":
        ratio = float(args[i + 1])
    elif args[i] == "--part":
        mat, name = args[i + 1].split("=", 1)
        names[mat] = name
    else:
        raise SystemExit(f"unknown argument {args[i]}")
    i += 2
if not names:
    raise SystemExit("no --part MAT=NAME given")

bpy.ops.wm.read_factory_settings(use_empty=True)
# Blender 5's FBX importer sets a Cycles light property that no longer exists; the file's light is dropped anyway.
light = bpy.data.lights.new("tmp", 'POINT')
type(light.cycles).cast_shadow = bpy.props.BoolProperty(default=True)
bpy.data.lights.remove(light)
bpy.ops.import_scene.fbx(filepath=src)

for o in list(bpy.data.objects):
    if o.type != 'MESH':
        bpy.data.objects.remove(o, do_unlink=True)
for o in bpy.data.objects:
    if upright:
        o.rotation_euler = (0.0, 0.0, 0.0)
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

# One object per material, so each part is one mesh with one known name in Unity.
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.separate(type='MATERIAL')
bpy.ops.object.mode_set(mode='OBJECT')

parts = []
before = after = 0
for o in list(bpy.data.objects):
    mat = o.material_slots[0].material.name if o.material_slots and o.material_slots[0].material else ""
    key = next((k for k in names if mat.startswith(k)), None)
    if key is None or not o.data.polygons:
        print("SKIP", o.name, mat)
        bpy.data.objects.remove(o, do_unlink=True)
        continue
    o.name = o.data.name = names[key]
    before += sum(len(p.vertices) - 2 for p in o.data.polygons)
    # Unity reads the first UV layer: keep only the active (rendered) one.
    keep = o.data.uv_layers.active.name if o.data.uv_layers.active else None
    for uv in [u.name for u in o.data.uv_layers if u.name != keep]:
        o.data.uv_layers.remove(o.data.uv_layers[uv])
    if ratio < 1:
        # Collapse decimation, per part so the material borders stay where they were.
        d = o.modifiers.new("Decimate", 'DECIMATE')
        d.decimate_type = 'COLLAPSE'
        d.ratio = ratio
        d.use_collapse_triangulate = True
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.modifier_apply(modifier=d.name)
    o.data.materials.clear()
    after += sum(len(p.vertices) - 2 for p in o.data.polygons)
    parts.append(o)

# Centre the whole model at the origin (its parts keep their places relative to each other).
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
