# Testszene mit denselben Aufrufparametern wie vitrine.py. Stellt wie vitrine.py fest auf CPU,
# damit man sieht, ob das GPU-Skript der App trotzdem auf die Grafikkarte umschaltet.
import bpy, sys, math

A = sys.argv[sys.argv.index('--') + 1:]
A = [a for a in A if not a.startswith('--')]
MODUS, STIL, YDD, TEX, LOGO, OUT = A[:6]
sc = bpy.context.scene
for o in list(bpy.data.objects):
    bpy.data.objects.remove(o, do_unlink=True)

glas = bpy.data.materials.new('Glas'); glas.use_nodes = True
g = next(n for n in glas.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
g.inputs['Transmission Weight'].default_value = 1.0
g.inputs['Roughness'].default_value = 0.05

stoff = bpy.data.materials.new('Stoff'); stoff.use_nodes = True
nt = stoff.node_tree
b = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
t = nt.nodes.new('ShaderNodeTexImage'); t.image = bpy.data.images.load(TEX)
nt.links.new(t.outputs[0], b.inputs['Base Color'])

for i in range(5):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.5, location=(i * 1.1 - 2.2, 0, 0.5), segments=64, ring_count=32)
    o = bpy.context.active_object
    o.data.materials.append(glas if i % 2 else stoff)
bpy.ops.mesh.primitive_plane_add(size=20)
bpy.ops.object.light_add(type='AREA', location=(0, -2, 4))
bpy.context.active_object.data.energy = 500
cam = bpy.data.objects.new('Kamera', bpy.data.cameras.new('Kamera'))
sc.collection.objects.link(cam)
cam.location = (0, -7, 2)
cam.rotation_euler = (math.radians(78), 0, 0)
sc.camera = cam

sc.render.engine = 'CYCLES'
sc.cycles.device = 'CPU'                      # wie vitrine.py
sc.cycles.samples = 256
sc.cycles.use_adaptive_sampling = False
sc.cycles.use_denoising = True
if hasattr(sc.cycles, 'denoising_use_gpu'):
    sc.cycles.denoising_use_gpu = False       # wie vitrine.py
sc.render.resolution_x, sc.render.resolution_y = 1200, 675
sc.render.resolution_percentage = 100
sc.render.filepath = OUT
print('Teil VORNE hoehe 0.700 meshes 1 armature False')
bpy.ops.render.render(write_still=True)
print('GERAET NACH RENDER', sc.cycles.device)
print('VITRINE FERTIG', STIL, OUT)
