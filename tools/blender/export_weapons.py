"""Split Niko's "futuristic weapons assets" .blend into one FBX per weapon for Art/Models/Weapons: modifiers baked,
each weapon joined into one mesh and centred on its bounds, scaled to metres (rifles ~1.1 m), barrel along Blender -Y,
Unity axes.
Usage: blender -b --disable-autoexec <file.blend> --python tools/blender/export_weapons.py -- <out dir>

The source has 27 loose, generically named parts laid out in a row along X; WEAPONS lists which make each item.
Unity imports no materials (Art/Models rules): weapons.json beside the FBXs lists each one's kind (gun, melee, mod)
and its submeshes' materials in order (unused slots dropped), for the scene builder to colour."""

import bpy, json, os, sys, mathutils

out_dir = sys.argv[sys.argv.index("--") + 1]
SCALE = 0.05   # source units -> metres
SLOTS = ["metal", "darker metal", "lights"]   # submesh order

# name: the source parts that make it (the source has 9 guns, 2 melee weapons and loose mods that fit any gun).
# Guns keep the mods they're shown with (scopes, shrouds, sights); the loose ones export on their own (mod_*).
WEAPONS = {
    # guns
    "rifle_block":    ["Cube.009"],
    "rifle_vent":     ["Cube.012", "Cube.013", "Cube.016"],
    "sniper_barrel":  ["Cube.019", "Cube.020", "Cylinder.007", "Cylinder.008"],   # scope + barrel shroud
    "sniper":         ["Cube.001", "Cylinder.001"],                               # scope
    "sniper_long":    ["Cube.011", "Cube.018", "Cylinder.005", "Cylinder.006"],   # scope + long barrel shroud
    "rifle_grip":     ["Cube.014", "Cube.015", "Cube.017"],
    "carbine":        ["Cube", "Cube.010"],                                       # sight
    "heavy_cannon":   ["Cube.003"],                                               # carry handle on top
    "rifle_sleek":    ["Cube.005"],
    # melee
    "hammer":         ["Cylinder.004"],
    "knife":          ["Cube.008"],
    # loose mods
    "mod_scope":      ["Cylinder"],
    "mod_sight":      ["Cube.002"],
    "mod_box":        ["Cube.004"],
    "mod_grip":       ["Cylinder.002"],                                           # vertical foregrip
}

deps = bpy.context.evaluated_depsgraph_get()
groups = {name: [] for name in WEAPONS}
for o in bpy.data.objects:
    if o.type != 'MESH':
        continue
    name = next((n for n, parts in WEAPONS.items() if o.name in parts), None)
    if name is None:
        raise SystemExit(f"part {o.name} is in no weapon")
    # Bake modifiers (two small parts carry mirror + subsurf) into a plain mesh with the world transform applied,
    # so mirrored (negative) scales come out with the right winding.
    me = bpy.data.meshes.new_from_object(o.evaluated_get(deps), preserve_all_data_layers=True, depsgraph=deps)
    me.transform(o.matrix_world)
    if o.matrix_world.determinant() < 0:
        me.flip_normals()
    groups[name].append((o.name, me))

for o in list(bpy.data.objects):
    bpy.data.objects.remove(o, do_unlink=True)

os.makedirs(out_dir, exist_ok=True)
MELEE = {"hammer", "knife"}
index = []   # (a list, for Unity's JsonUtility)
for name, parts in groups.items():
    objs = []
    for part, me in parts:
        no = bpy.data.objects.new(f"{name}_{part}", me)
        bpy.context.scene.collection.objects.link(no)
        objs.append(no)
    corners = [mathutils.Vector(v.co) for o in objs for v in o.data.vertices]
    lo = mathutils.Vector([min(c[i] for c in corners) for i in range(3)])
    hi = mathutils.Vector([max(c[i] for c in corners) for i in range(3)])
    centre = (lo + hi) / 2
    # Centre, scale to metres, and turn the barrel (source +Y) to Blender front (-Y).
    m = mathutils.Matrix.Rotation(3.14159265, 4, 'Z') @ mathutils.Matrix.Scale(SCALE, 4) @ mathutils.Matrix.Translation(-centre)
    for o in objs:
        o.data.transform(m)
    # One mesh, slots in SLOTS order: join into an empty object that already has them (joining keeps the active
    # object's slots first and maps the parts' materials onto them by name).
    base = bpy.data.objects.new(name, bpy.data.meshes.new(name))
    bpy.context.scene.collection.objects.link(base)
    for slot in SLOTS:
        base.data.materials.append(bpy.data.materials[slot])
    for o in bpy.data.objects:
        o.select_set(o in objs or o == base)
    bpy.context.view_layer.objects.active = base
    bpy.ops.object.join()
    objs = [base]
    used = {p.material_index for p in base.data.polygons}
    for i in reversed(range(len(base.data.materials))):
        if i not in used:
            base.data.materials.pop(index=i)
    index.append({"name": name, "kind": "mod" if name.startswith("mod_") else "melee" if name in MELEE else "gun",
                  "submeshes": [m.name for m in base.data.materials]})
    tris = sum(len(p.vertices) - 2 for o in objs for p in o.data.polygons)
    size = (hi - lo) * SCALE
    print("WEAPON", name, "size m", tuple(round(v, 3) for v in size), "tris", tris, "parts", [p for p, _ in parts])
    for o in bpy.data.objects:
        o.select_set(o in objs)
    bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, name + ".fbx"), use_selection=True, object_types={'MESH'},
                             apply_unit_scale=True, axis_forward='-Z', axis_up='Y', bake_space_transform=True,
                             mesh_smooth_type='FACE', use_mesh_modifiers=False, add_leaf_bones=False, path_mode='STRIP')
    for o in objs:
        bpy.data.objects.remove(o, do_unlink=True)

with open(os.path.join(out_dir, "weapons.json"), "w") as f:
    json.dump({"items": index}, f, indent=2)
    f.write("\n")
print("INDEX", json.dumps(index))
