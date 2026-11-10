"""Export a relic .blend to FBX for Art/Models/Relics: modifiers baked (subdivision off for the low-poly budget), centred,
Unity axes. Usage: blender -b <file.blend> --python tools/blender/export_relic.py -- <out.fbx>"""

import bpy, sys, mathutils
out = sys.argv[sys.argv.index("--") + 1]
# Low-poly (PS1 look, Web budget): the chain's subdivision off (61k -> ~4k triangles).
for o in bpy.data.objects:
    for m in getattr(o, 'modifiers', []):
        if m.type == 'SUBSURF':
            m.levels = 0
            m.render_levels = 0
deps = bpy.context.evaluated_depsgraph_get()
keep = []
for o in list(bpy.data.objects):
    if o.type != 'MESH':
        continue
    # Bake modifiers (the chain: array + curve + subsurf) into a plain mesh.
    ev = o.evaluated_get(deps)
    me = bpy.data.meshes.new_from_object(ev, preserve_all_data_layers=True, depsgraph=deps)
    no = bpy.data.objects.new(o.name + "_baked", me)
    no.matrix_world = o.matrix_world.copy()
    bpy.context.scene.collection.objects.link(no)
    keep.append(no)
for o in list(bpy.data.objects):
    if o not in keep:
        bpy.data.objects.remove(o, do_unlink=True)
# Centre the whole amulet at the origin.
corners = [o.matrix_world @ mathutils.Vector(c) for o in keep for c in o.bound_box]
lo = mathutils.Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
hi = mathutils.Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
centre = (lo + hi) / 2
for o in keep:
    o.location -= centre
print("SIZE", tuple(round(v, 3) for v in (hi - lo)), "parts", [o.name for o in keep], "mats", [[s.material.name for s in o.material_slots] for o in keep])
bpy.ops.export_scene.fbx(filepath=out, use_selection=False, object_types={'MESH'}, apply_unit_scale=True,
                         axis_forward='-Z', axis_up='Y', bake_space_transform=True, mesh_smooth_type='FACE',
                         use_mesh_modifiers=True, add_leaf_bones=False, path_mode='STRIP')
