"""Showroom "Glasvitrine": ein Oberteil dreimal (VORNE / SEITE / HINTEN) in Acrylglas-Vitrinen am Buegel,
Retro-Logo an der Rueckwand + am Sockel. Laeuft im Hintergrund (ohne Fenster), Cycles CPU.

blender -b --python vitrine.py -- <TEST|FINAL|GROSS> <grau|rot|rot_dunkel|weiss> <ydd|blend> <textur.png> <logo.png> <ausgabe.png> [titel] [m|f] [oben|hose]

<ydd>: Teil per Sollumz importieren. <blend>: fertig bearbeitetes Teil (z. B. mit neuer Schnalle) aus einer
.blend laden, die nur das Teil enthaelt (bpy.data.libraries.write aus der Arbeitsszene).
Typ oben = Schulterbuegel, Arme gesenkt; hose = Klammerbuegel am Bund, haengt hoeher (Standard: hose bei 'lowr' im Namen).

Stile (vom Nutzer abgesegnet, 07.11.2026):
  grau = dunkelgraues Studio, neutrales Licht, Spots auf die Teile, Logo farbig leuchtend
         (erstes fertiges YAZURAH-Bild).
  rot  = sehr dunkler Raum, Licht kommt aus den Vitrinen (Deckel nach unten, Sockel nach oben, warm-roetlich),
         rote Leuchtleisten oben/unten, schwarze Sockel, Logo roetlich eingefaerbt, roter Wandverlauf.
In der App heissen die Stile nach ihrem Aussehen (10.11.2026): weiss = "Schwarz (Standard)", grau = "Weiss",
rot = "Rot", rot_dunkel = "Rot, dunkel".
Kleidung (Nutzerwunsch 10.11.2026): Das Teil wird in jedem Stil so beleuchtet wie im Stil rot (TEST_FINAL.png),
damit Falten und Konturen immer gleich gut sichtbar sind. Der Stil aendert nur den Raum (Light Linking).
TEST = 32 Samples, 1200 px (~25 s); FINAL = 160 Samples, 2400 x 1350 (~6-9 min CPU).
"""
import bpy, sys, os, math
from mathutils import Vector, Matrix

A = sys.argv[sys.argv.index('--') + 1:]
SPOTS = '--spots' in A                 # Spotlights ueber den Vitrinen (Strahler auf das Teil)
KONTUR = '--kontur' in A               # Gegenlicht + leichter Stoffschimmer: macht schwarze Teile sichtbar
SPOT_W = next((float(a.split('=')[1]) for a in A if a.startswith('--spot_w=')), 90.0)
A = [a for a in A if not a.startswith('--')]
MODUS, STIL, YDD, TEX, LOGO, OUT = A[:6]
TITEL = A[6] if len(A) > 6 else 'RETRO'
GESCHLECHT = A[7] if len(A) > 7 else 'm'
TYP = A[8] if len(A) > 8 else ('hose' if 'lowr' in os.path.basename(YDD).lower() else 'oben')
TEST = MODUS.upper() == 'TEST'
STIL = STIL.lower()
assert STIL in ('grau', 'rot', 'rot_dunkel', 'weiss'), 'Stil muss grau, rot, rot_dunkel oder weiss sein'
ROT_STIL = STIL != 'grau'          # alle Stile ausser grau: Licht kommt aus den Vitrinen
GROSS = MODUS.upper() == 'GROSS'   # grosses Bild in hoher Qualitaet (3840 x 2160, 256 Samples)
UNTERZEILE = 'RETRO STUDIOS'       # Sockelbeschriftung (Nutzerwunsch 08.11.2026, vorher RETRO ROLEPLAY)
# Lichtstaerken je Stil (rot_dunkel = Nutzerwunsch 08.11.2026: Hintergrund dunkler, weniger ueberbelichtet)
LICHT = dict(rot=dict(deckel=60, sockel=45, front=70, rand=45, wand=45, leiste=2.5),
             rot_dunkel=dict(deckel=40, sockel=12, front=45, rand=25, wand=18, leiste=1.8, teil=20),
             weiss=dict(deckel=34, sockel=10, front=40, rand=22, wand=14, leiste=1.4, teil=18))
L = LICHT.get(STIL)

# ---------------- Stil-Parameter ----------------
ROT = (1.0, 0.32, 0.26)          # Akzentfarbe (Logo, Wand, Leuchtleisten) im Stil rot
WARM = (1.0, 0.62, 0.55)         # Vitrinenlicht im Stil rot: warm-roetlich, nicht zu satt
if STIL == 'weiss':               # Nutzerwunsch 08.11.2026: Licht weiss statt rot
    ROT = (1.0, 1.0, 1.0)
    WARM = (1.0, 0.98, 0.95)
if STIL in ('rot_dunkel', 'weiss'):
    P = dict(wand=(0.004, 0.004, 0.0045), boden=(0.007, 0.007, 0.008), sockel=(0.006, 0.006, 0.007), sockel_rough=0.3,
             welt=(0.0015, 0.0015, 0.0018), logo_tint=0.0 if STIL == 'weiss' else 0.75, logo_emit=0.16, belichtung=-0.15)
elif ROT_STIL:
    P = dict(wand=(0.008, 0.008, 0.009), boden=(0.012, 0.012, 0.013), sockel=(0.008, 0.008, 0.009), sockel_rough=0.3,
             welt=(0.003, 0.003, 0.0035), logo_tint=0.75, logo_emit=0.22, belichtung=0.0)
else:
    P = dict(wand=(0.018, 0.018, 0.02), boden=(0.025, 0.025, 0.027), sockel=(0.05, 0.05, 0.053), sockel_rough=0.45,
             welt=(0.012, 0.012, 0.014), logo_tint=0.0, logo_emit=0.55, belichtung=0.1)

# ---------------- Szene leeren ----------------
sc = bpy.context.scene
for o in list(bpy.data.objects):
    bpy.data.objects.remove(o, do_unlink=True)

def mat_principled(name, color, rough=0.5, spec=0.5, emit=None, emit_str=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bs = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    bs.inputs['Base Color'].default_value = (*color, 1)
    bs.inputs['Roughness'].default_value = rough
    if 'Specular IOR Level' in bs.inputs:
        bs.inputs['Specular IOR Level'].default_value = spec
    if emit is not None:
        bs.inputs['Emission Color'].default_value = (*emit, 1)
        bs.inputs['Emission Strength'].default_value = emit_str
    return m

def box(name, size, loc, mat):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.active_object
    o.name = name
    o.scale = size
    bpy.ops.object.transform_apply(scale=True)
    o.data.materials.append(mat)
    return o

def text(body, size, loc, mat, align='LEFT', rot_z=0.0):
    cu = bpy.data.curves.new(body, 'FONT')
    cu.body = body
    cu.size = size
    cu.align_x = align
    cu.space_character = 1.15
    o = bpy.data.objects.new('TXT_' + body, cu)
    sc.collection.objects.link(o)
    o.location = loc
    o.rotation_euler = (math.radians(90), 0, rot_z)
    o.data.materials.append(mat)
    return o

# ---------------- Materialien ----------------
M_WAND = mat_principled('Wand', P['wand'], rough=0.9, spec=0.2)
M_BODEN = mat_principled('Boden', P['boden'], rough=0.45 if ROT_STIL else 0.5, spec=0.35 if ROT_STIL else 0.3)
M_LEUCHT = mat_principled('Leuchtleiste', (0.0, 0.0, 0.0), rough=0.5, emit=ROT, emit_str=L['leiste'] if L else 2.5)
M_SOCKEL = mat_principled('Sockel', P['sockel'], rough=P['sockel_rough'], spec=0.5 if ROT_STIL else 0.4)
M_SCHRIFT = mat_principled('Schrift', (0.9, 0.9, 0.9), rough=0.6, emit=(1, 1, 1), emit_str=1.2)
M_SEIL = mat_principled('Seil', (0.75, 0.75, 0.75), rough=0.6)
M_BUEGEL = mat_principled('Buegel', (0.02, 0.02, 0.02), rough=0.35, spec=0.6)
M_HAKEN = mat_principled('Haken', (0.8, 0.8, 0.82), rough=0.2, spec=0.8)
next(n for n in M_HAKEN.node_tree.nodes if n.type == 'BSDF_PRINCIPLED').inputs['Metallic'].default_value = 1.0

M_GLAS = bpy.data.materials.new('Glas')
M_GLAS.use_nodes = True
g = next(n for n in M_GLAS.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
g.inputs['Base Color'].default_value = (1, 1, 1, 1)
g.inputs['Roughness'].default_value = 0.03
g.inputs['IOR'].default_value = 1.45
g.inputs['Transmission Weight'].default_value = 1.0

# ---------------- Studio ----------------
bpy.ops.mesh.primitive_plane_add(size=20, location=(0, 0, 0))
boden = bpy.context.active_object; boden.name = 'Boden'; boden.data.materials.append(M_BODEN)
bpy.ops.mesh.primitive_plane_add(size=20, location=(0, 2.4, 5), rotation=(math.radians(90), 0, 0))
wand = bpy.context.active_object; wand.name = 'Wand'; wand.data.materials.append(M_WAND)

# Logo (Alpha, leichtes Eigenleuchten; im Stil rot roetlich eingefaerbt)
M_LOGO = bpy.data.materials.new('Logo')
M_LOGO.use_nodes = True
nt = M_LOGO.node_tree
nt.nodes.clear()
out = nt.nodes.new('ShaderNodeOutputMaterial')
mix = nt.nodes.new('ShaderNodeMixShader')
tr = nt.nodes.new('ShaderNodeBsdfTransparent')
bs = nt.nodes.new('ShaderNodeBsdfPrincipled')
ti = nt.nodes.new('ShaderNodeTexImage')
ti.image = bpy.data.images.load(LOGO)
tint = nt.nodes.new('ShaderNodeMix'); tint.data_type = 'RGBA'; tint.blend_type = 'MULTIPLY'
tint.inputs['Factor'].default_value = P['logo_tint']
nt.links.new(ti.outputs['Color'], tint.inputs['A'])
tint.inputs['B'].default_value = (*ROT, 1)
nt.links.new(tint.outputs['Result'], bs.inputs['Base Color'])
nt.links.new(tint.outputs['Result'], bs.inputs['Emission Color'])
bs.inputs['Emission Strength'].default_value = P['logo_emit']
bs.inputs['Roughness'].default_value = 0.6
nt.links.new(ti.outputs['Alpha'], mix.inputs['Fac'])
nt.links.new(tr.outputs[0], mix.inputs[1])
nt.links.new(bs.outputs[0], mix.inputs[2])
nt.links.new(mix.outputs[0], out.inputs[0])
LOGO_GROESSE = 0.95
LANG = '--lang' in sys.argv          # lange Oberteile (Mantel): hoehere Vitrine wie bei Hosen
LOGO_Z = 2.12 if not ('--spots' in sys.argv and (TYP == 'hose' or LANG)) else 2.3   # mit Strahlern ueber hohen Vitrinen hoeher
bpy.ops.mesh.primitive_plane_add(size=LOGO_GROESSE, location=(0, 2.39, LOGO_Z), rotation=(math.radians(90), 0, 0))
logo_wand = bpy.context.active_object; logo_wand.name = 'Logo_Wand'; logo_wand.data.materials.append(M_LOGO)

# ---------------- Vitrinen ----------------
W, D, H, SOCKEL = 0.80, 0.56, 1.18, 0.17
if TYP == 'hose' or LANG:
    H = 1.34          # Hosen sind ~1 m lang -> hoehere Vitrine
XS = [-1.08, 0.0, 1.08]
ANSICHTEN = [('VORNE', 0.0), ('SEITE', -90.0), ('HINTEN', 180.0)]

def vitrine(x, label):
    box('Sockel', (W + 0.06, D + 0.06, SOCKEL), (x, 0, SOCKEL / 2), M_SOCKEL)
    glas = box('Glas', (W, D, H), (x, 0, SOCKEL + H / 2), M_GLAS)
    so = glas.modifiers.new('dicke', 'SOLIDIFY'); so.thickness = 0.008
    bv = glas.modifiers.new('kante', 'BEVEL'); bv.width = 0.002; bv.segments = 2
    glas.visible_shadow = False          # sonst dunkler Kasten-Schatten
    yv = -D / 2 - 0.0015
    box('Linie', (W - 0.06, 0.001, 0.0025), (x, yv, SOCKEL + H - 0.13), M_SCHRIFT)
    for dx in (-W / 2 + 0.06, W / 2 - 0.06):
        box('Punkt', (0.012, 0.001, 0.012), (x + dx, yv, SOCKEL + H - 0.19), M_SCHRIFT)
    text(label, 0.05, (x - W / 2 + 0.06, yv, SOCKEL + H - 0.095), M_SCHRIFT)
    ys = -(D + 0.06) / 2 - 0.0015
    text(TITEL, 0.045, (x - (W + 0.06) / 2 + 0.06, ys, SOCKEL * 0.52), M_SCHRIFT)
    text(UNTERZEILE, 0.03, (x - (W + 0.06) / 2 + 0.06, ys, SOCKEL * 0.22), M_SCHRIFT)
    bpy.ops.mesh.primitive_plane_add(size=0.11, location=(x + (W + 0.06) / 2 - 0.1, ys, SOCKEL * 0.5),
                                     rotation=(math.radians(90), 0, 0))
    bpy.context.active_object.data.materials.append(M_LOGO)

for x, (label, _) in zip(XS, ANSICHTEN):
    vitrine(x, label)

# ---------------- Kleidungsstueck (3x importieren) ----------------
prefs = bpy.context.preferences.addons['bl_ext.user_default.sollumz'].preferences
SK = next(p.name for p in prefs.external_skeleton_paths if 'mp_%s_freemode_01.yft' % GESCHLECHT in p.name)
d, f = os.path.split(YDD)

M_STOFF = None
def stoff_material(alt):
    m = bpy.data.materials.new('Stoff')
    m.use_nodes = True
    nt = m.node_tree; nt.nodes.clear()
    o = nt.nodes.new('ShaderNodeOutputMaterial')
    b = nt.nodes.new('ShaderNodeBsdfPrincipled'); nt.links.new(b.outputs[0], o.inputs[0])
    b.inputs['Roughness'].default_value = 1.0
    if 'Specular IOR Level' in b.inputs: b.inputs['Specular IOR Level'].default_value = 0.0
    if 'Sheen Weight' in b.inputs: b.inputs['Sheen Weight'].default_value = 0.12 if KONTUR else 0.0   # 0.5 machte Schwarz grau-weiss
    if KONTUR and 'Sheen Roughness' in b.inputs: b.inputs['Sheen Roughness'].default_value = 0.4
    uv = nt.nodes.new('ShaderNodeUVMap')
    t = nt.nodes.new('ShaderNodeTexImage'); t.image = bpy.data.images.load(TEX, check_existing=True); t.interpolation = 'Cubic'
    nt.links.new(uv.outputs[0], t.inputs[0]); nt.links.new(t.outputs[0], b.inputs['Base Color'])
    bump = next((n.image for n in alt.node_tree.nodes if n.type == 'TEX_IMAGE' and n.name == 'BumpSampler'), None) if alt else None
    if bump is not None and bump.size[0] > 4:
        bump.colorspace_settings.name = 'Non-Color'
        tb = nt.nodes.new('ShaderNodeTexImage'); tb.image = bump; nt.links.new(uv.outputs[0], tb.inputs[0])
        sep = nt.nodes.new('ShaderNodeSeparateColor'); com = nt.nodes.new('ShaderNodeCombineColor'); inv = nt.nodes.new('ShaderNodeMath')
        inv.operation = 'SUBTRACT'; inv.inputs[0].default_value = 1.0
        nt.links.new(tb.outputs[0], sep.inputs[0]); nt.links.new(sep.outputs[0], com.inputs[0])
        nt.links.new(sep.outputs[1], inv.inputs[1]); nt.links.new(inv.outputs[0], com.inputs[1]); nt.links.new(sep.outputs[2], com.inputs[2])
        nm = nt.nodes.new('ShaderNodeNormalMap'); nm.inputs['Strength'].default_value = 0.8
        nt.links.new(com.outputs[0], nm.inputs['Color']); nt.links.new(nm.outputs[0], b.inputs['Normal'])
    return m

def welt_bbox(objs):
    dg = bpy.context.evaluated_depsgraph_get()
    pts = []
    for o in objs:
        oe = o.evaluated_get(dg)
        me = oe.to_mesh()
        pts += [o.matrix_world @ v.co for v in me.vertices]
        oe.to_mesh_clear()
    mn = Vector([min(p[i] for p in pts) for i in range(3)])
    mx = Vector([max(p[i] for p in pts) for i in range(3)])
    return mn, mx

def arme_senken(arm, winkel):
    """Oberarme aus der A-Pose Richtung Koerper drehen (haengt dann natuerlicher am Buegel)."""
    for seite, hand in (('SKEL_L_UpperArm', 'SKEL_L_Hand'), ('SKEL_R_UpperArm', 'SKEL_R_Hand')):
        pb = arm.pose.bones.get(seite); ph = arm.pose.bones.get(hand)
        if pb is None or ph is None:
            print('Knochen fehlt', seite); continue
        z0 = ph.head.z
        M0 = pb.matrix.copy(); kopf = pb.head.copy()
        for s in (1, -1):
            R = Matrix.Translation(kopf) @ Matrix.Rotation(s * winkel, 4, 'Y') @ Matrix.Translation(-kopf)
            pb.matrix = R @ M0
            bpy.context.view_layer.update()
            if ph.head.z < z0:
                break
            pb.matrix = M0
            bpy.context.view_layer.update()

def buegel(parent, z_top):
    """Schwarzer Buegel + Chromhaken, Koordinaten relativ zum Teil-Pivot."""
    cu = bpy.data.curves.new('buegel', 'CURVE'); cu.dimensions = '3D'
    cu.bevel_depth = 0.006; cu.bevel_resolution = 3
    sp = cu.splines.new('BEZIER'); sp.bezier_points.add(2)
    for bp, co in zip(sp.bezier_points, [(-0.19, 0, -0.045), (0, 0, 0), (0.19, 0, -0.045)]):
        bp.co = co; bp.handle_left_type = bp.handle_right_type = 'AUTO'
    b = bpy.data.objects.new('Buegel', cu); sc.collection.objects.link(b); b.data.materials.append(M_BUEGEL)
    hk = bpy.data.curves.new('haken', 'CURVE'); hk.dimensions = '3D'; hk.bevel_depth = 0.0028; hk.bevel_resolution = 3
    sp = hk.splines.new('BEZIER'); sp.bezier_points.add(3)
    for bp, co in zip(sp.bezier_points, [(0, 0, 0), (0, 0, 0.045), (0.022, 0, 0.075), (0.036, 0, 0.055)]):
        bp.co = co; bp.handle_left_type = bp.handle_right_type = 'AUTO'
    h = bpy.data.objects.new('Haken', hk); sc.collection.objects.link(h); h.data.materials.append(M_HAKEN)
    for o in (b, h):
        o.parent = parent
        o.location = (0, 0, z_top)
    return z_top + 0.075

def klammerbuegel(parent, z_top):
    """Hosenbuegel: waagerechte Stange ueber dem Bund, zwei Klammern, Chromhaken."""
    b = bpy.data.curves.new('stange', 'CURVE'); b.dimensions = '3D'; b.bevel_depth = 0.005; b.bevel_resolution = 3
    sp = b.splines.new('POLY'); sp.points.add(1)
    sp.points[0].co = (-0.21, 0, 0.03, 1); sp.points[1].co = (0.21, 0, 0.03, 1)
    st = bpy.data.objects.new('Stange', b); sc.collection.objects.link(st); st.data.materials.append(M_BUEGEL)
    teile = [st]
    for sx in (-0.15, 0.15):
        bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, 0))
        k = bpy.context.active_object; k.name = 'Klammer'
        k.scale = (0.03, 0.03, 0.05); bpy.ops.object.transform_apply(scale=True)
        k.data.materials.append(M_HAKEN)
        k.location = (sx, 0, 0.005); teile.append(k)
    hk = bpy.data.curves.new('haken', 'CURVE'); hk.dimensions = '3D'; hk.bevel_depth = 0.0028; hk.bevel_resolution = 3
    sp = hk.splines.new('BEZIER'); sp.bezier_points.add(3)
    for bp, co in zip(sp.bezier_points, [(0, 0, 0.03), (0, 0, 0.075), (0.022, 0, 0.105), (0.036, 0, 0.085)]):
        bp.co = co; bp.handle_left_type = bp.handle_right_type = 'AUTO'
    h = bpy.data.objects.new('Haken', hk); sc.collection.objects.link(h); h.data.materials.append(M_HAKEN)
    teile.append(h)
    for o in teile:
        loc = o.location.copy()
        o.parent = parent
        o.location = (loc.x, loc.y, loc.z + z_top)
    return z_top + 0.105

def teil_importieren():
    vorher = set(bpy.data.objects)
    if YDD.lower().endswith('.blend'):
        with bpy.data.libraries.load(YDD, link=False) as (src, dst):
            dst.objects = list(src.objects)
        for o in dst.objects:
            if o is not None:
                sc.collection.objects.link(o)
    else:
        bpy.ops.sollumz.import_assets(directory=d + os.sep, files=[{'name': f}], use_custom_settings=True,
                                      dwd_import_external_skeleton='SAVED', dwd_import_external_skeleton_saved_path=SK)
    return [o for o in bpy.data.objects if o not in vorher]

stuecke = []
STOFF_OBJEKTE = []                     # alle Meshes des Teils (fuer das Light Linking)
for x, (label, drehung) in zip(XS, ANSICHTEN):
    neu = teil_importieren()
    root = [o for o in neu if o.parent is None][0]
    meshes = [o for o in neu if o.type == 'MESH']
    STOFF_OBJEKTE += meshes
    arm = next((o for o in neu if o.type == 'ARMATURE'), None)
    if M_STOFF is None:
        alt = meshes[0].material_slots[0].material if meshes[0].material_slots else None
        M_STOFF = stoff_material(alt)
    for o in meshes:
        for s in o.material_slots:
            s.material = M_STOFF
    if arm is not None and TYP == 'oben':
        arme_senken(arm, math.radians(24))
    piv = bpy.data.objects.new('Pivot_' + label, None); sc.collection.objects.link(piv)
    bpy.context.view_layer.update()
    mn, mx = welt_bbox(meshes)
    mitte = (mn + mx) / 2
    root.location -= Vector((mitte.x, mitte.y, mx.z))      # Oberkante des Teils auf Pivot-Hoehe
    bpy.context.view_layer.update()
    root.parent = piv
    stuecke.append((piv, x, drehung))
    print('Teil', label, 'hoehe %.3f' % (mx.z - mn.z), 'meshes', len(meshes), 'armature', arm is not None)

for piv, x, drehung in stuecke:
    z_ober = SOCKEL + H - (0.30 if (TYP == 'oben' and not LANG) else 0.17)    # Hose ~1 m lang -> hoeher aufhaengen
    piv.location = (x, 0, z_ober)
    piv.rotation_euler = (0, 0, math.radians(drehung))
    haken_oben = buegel(piv, -0.06) if TYP == 'oben' else klammerbuegel(piv, 0.0)
    z0 = z_ober + haken_oben; z1 = SOCKEL + H
    bpy.ops.mesh.primitive_cylinder_add(radius=0.0012, depth=z1 - z0, location=(x, 0, (z0 + z1) / 2))
    bpy.context.active_object.data.materials.append(M_SEIL)

# ---------------- Licht ----------------
def flaeche(name, loc, rot, size, energy, color=(1, 1, 1)):
    l = bpy.data.lights.new(name, 'AREA'); l.shape = 'RECTANGLE'
    l.size, l.size_y = size; l.energy = energy; l.color = color
    o = bpy.data.objects.new(name, l); sc.collection.objects.link(o)
    o.location = loc; o.rotation_euler = rot
    o.visible_camera = False
    return o

def zielen(o, ziel):
    o.rotation_euler = (Vector(ziel) - o.location).to_track_quat('-Z', 'Y').to_euler()

def rot_licht(Lx, warm, rot, faktor=1.0, leiste=None):
    """Lichtaufbau der Rot-Stile. Gibt die Lichter zurueck (ohne Leuchtleisten)."""
    lichter = []
    for x in XS:
        # Licht kommt aus den Vitrinen: Deckel nach unten, Sockel nach oben
        for l in (flaeche('Deckel', (x, 0, SOCKEL + H - 0.02), (0, 0, 0), (W - 0.08, D - 0.08), Lx['deckel'] * faktor, warm),
                  flaeche('Sockel_hoch', (x, 0, SOCKEL + 0.015), (math.radians(180), 0, 0), (W - 0.08, D - 0.08), Lx['sockel'] * faktor, warm)):
            l.visible_glossy = False          # sonst spiegelt sich die Lichtflaeche als weisse Platte im Glasboden
            l.visible_transmission = False
            lichter.append(l)
        if Lx.get('teil'):
            # schwaches neutrales Licht gezielt aufs Teil, damit Drucke lesbar bleiben (ohne den Raum aufzuhellen)
            mitte_z = SOCKEL + H - (0.30 + 0.35 if TYP == 'oben' else 0.17 + 0.5)
            lt = flaeche('Teil', (x, -2.0, mitte_z + 0.5), (0, 0, 0), (0.6, 0.6), Lx['teil'] * faktor, (1.0, 0.92, 0.88))
            lt.data.spread = math.radians(30)
            lt.visible_glossy = False; lt.visible_transmission = False   # sonst weisser Fleck als Spiegelung im Glas
            zielen(lt, (x, 0, mitte_z))
            lichter.append(lt)
        if leiste is not None:
            for z in (SOCKEL + H - 0.012, SOCKEL + 0.01):
                box('Leiste', (W - 0.04, 0.012, 0.006), (x, -D / 2 + 0.02, z), leiste)
                box('Leiste', (W - 0.04, 0.012, 0.006), (x, D / 2 - 0.02, z), leiste)
    lichter.append(flaeche('Front', (0, -3.6, 2.2), (math.radians(62), 0, 0), (5.0, 1.6), Lx['front'] * faktor))
    lichter.append(flaeche('Rand_L', (-3.2, 1.2, 1.6), (math.radians(80), 0, math.radians(-115)), (0.6, 2.5), Lx['rand'] * faktor, rot))
    lichter.append(flaeche('Rand_R', (3.2, 1.2, 1.6), (math.radians(80), 0, math.radians(115)), (0.6, 2.5), Lx['rand'] * faktor, rot))
    wl = flaeche('Wandlicht', (0, 0.6, 0.15), (0, 0, 0), (4.5, 0.4), Lx['wand'] * faktor, rot)
    zielen(wl, (0, 2.4, 1.4))   # weicher Verlauf an der Rueckwand von unten
    lichter.append(wl)
    return lichter

def grau_licht():
    lichter = []
    jacke_z = SOCKEL + H - 0.30 - 0.40
    for x in XS:
        lichter.append(flaeche('Oben', (x, 0, SOCKEL + H + 0.9), (0, 0, 0), (0.6, 0.4), 110))
        l = flaeche('Jacke', (x, -2.3, 1.9), (0, 0, 0), (0.7, 0.7), 38)
        l.data.spread = math.radians(25)
        zielen(l, (x, 0, jacke_z))
        lichter.append(l)
    lichter.append(flaeche('Front', (0, -3.6, 2.6), (math.radians(58), 0, 0), (5.0, 1.6), 90))
    lichter.append(flaeche('Rand_L', (-3.2, 1.2, 1.6), (math.radians(80), 0, math.radians(-115)), (0.6, 2.5), 120, (0.85, 0.85, 1.0)))
    lichter.append(flaeche('Rand_R', (3.2, 1.2, 1.6), (math.radians(80), 0, math.radians(115)), (0.6, 2.5), 120, (1.0, 0.9, 0.85)))
    wl = flaeche('Wandlicht', (0, 0.6, 0.15), (0, 0, 0), (4.5, 0.4), 140)
    zielen(wl, (0, 2.4, 1.4))   # weicher Verlauf an der Rueckwand von unten
    lichter.append(wl)
    return lichter

# Kleidung immer wie im Stil rot beleuchten (TEST_FINAL.png, Nutzerwunsch 10.11.2026): Das Raumlicht des
# Stils erreicht das Teil nicht, stattdessen bekommt das Teil unsichtbar den Lichtaufbau des Stils rot.
# Im Stil rot selbst bleibt alles wie bisher.
TEIL_STIL = 'rot'
TEIL_WARM, TEIL_ROT = (1.0, 0.62, 0.55), (1.0, 0.32, 0.26)   # Lichtfarben im Stil rot
TEIL_BELICHTUNG = 0.0                                         # Belichtung im Stil rot
TEIL_EIGEN = STIL == TEIL_STIL
# Die Belichtung wirkt aufs ganze Bild, also Licht fuers Teil so anpassen, dass es am Ende gleich hell ist.
# Dazu ein kleiner Ausgleich fuers Streulicht, das der Raum je nach Stil aufs Teil wirft (gemessen 10.11.2026).
KOMP = 2 ** (TEIL_BELICHTUNG - P['belichtung']) * dict(rot_dunkel=1.07, weiss=1.07, grau=0.97).get(STIL, 1.0)

raum = rot_licht(L, WARM, ROT, leiste=M_LEUCHT) if ROT_STIL else grau_licht()
if not TEIL_EIGEN:
    ohne_teil = bpy.data.collections.new('Licht_ohne_Teil')
    nur_teil = bpy.data.collections.new('Licht_nur_Teil')
    for o in STOFF_OBJEKTE:
        ohne_teil.objects.link(o)
        nur_teil.objects.link(o)
    for co in ohne_teil.collection_objects:
        co.light_linking.link_state = 'EXCLUDE'
    for co in nur_teil.collection_objects:
        co.light_linking.link_state = 'INCLUDE'
    for l in raum:
        l.light_linking.receiver_collection = ohne_teil
    for l in rot_licht(LICHT[TEIL_STIL], TEIL_WARM, TEIL_ROT, faktor=KOMP):
        l.light_linking.receiver_collection = nur_teil   # trifft nur das Teil, spiegelt sich also auch nicht im Glas

if SPOTS:
    # Strahler ueber jeder Vitrine: kleines schwarzes Gehaeuse mit leuchtender Linse, leicht nach vorne geneigt
    M_GEH = mat_principled('Strahler', (0.015, 0.015, 0.015), rough=0.35, spec=0.6)
    M_LINSE = mat_principled('Linse', (1, 1, 1), rough=0.2, emit=(1, 0.97, 0.92), emit_str=25)
    teil_mitte = SOCKEL + H - (0.30 + 0.35 if TYP == 'oben' else 0.17 + 0.5)
    for x in XS:
        pos = Vector((x, -0.22, SOCKEL + H + 0.17))
        ziel_p = Vector((x, -0.05, teil_mitte))
        rot = (ziel_p - pos).to_track_quat('-Z', 'Y').to_euler()
        richtung = (ziel_p - pos).normalized()
        # Mitte ohne sichtbaren Strahler: Gehaeuse und Stange hingen sonst vor dem Logo (Nutzerwunsch 10.11.2026).
        # Das Licht bleibt (unsichtbar), damit die mittlere Ansicht genauso hell ist wie die beiden anderen.
        sichtbar = x != 0.0
        if sichtbar:
            bpy.ops.mesh.primitive_cylinder_add(radius=0.035, depth=0.11, vertices=24, location=pos, rotation=rot)
            geh = bpy.context.active_object; geh.data.materials.append(M_GEH)
            bpy.ops.mesh.primitive_circle_add(radius=0.03, vertices=24, fill_type='NGON', location=pos + richtung * 0.056, rotation=rot)
            linse = bpy.context.active_object; linse.data.materials.append(M_LINSE); linse.visible_shadow = False
            # Halterung zur Decke
            bpy.ops.mesh.primitive_cylinder_add(radius=0.004, depth=1.2, location=(x, -0.22, pos.z + 0.6))
            bpy.context.active_object.data.materials.append(M_GEH)
        sp = bpy.data.lights.new('Spot', 'SPOT')
        sp.energy = SPOT_W * KOMP; sp.spot_size = math.radians(34); sp.spot_blend = 0.35
        sp.color = (1.0, 0.97, 0.92); sp.shadow_soft_size = 0.03
        so = bpy.data.objects.new('Spot', sp); sc.collection.objects.link(so)
        so.location = pos + richtung * 0.06; so.rotation_euler = rot
        if not sichtbar:
            so.visible_camera = False; so.visible_glossy = False; so.visible_transmission = False
    # Logo an der Rueckwand: unsichtbarer Strahler von vorne oben (Nutzerwunsch 10.11.2026)
    # Feste Leistung, unabhaengig von --spot_w: mehr Licht wusch die Logofarben aus (300 W schon pastellig)
    ls = bpy.data.lights.new('Spot_Logo', 'SPOT')
    ls.energy = 150; ls.spot_size = math.radians(40); ls.spot_blend = 0.6
    ls.color = (1.0, 0.97, 0.92); ls.shadow_soft_size = 0.05
    lo = bpy.data.objects.new('Spot_Logo', ls); sc.collection.objects.link(lo)
    lo.location = (0, 2.39 - 1.3, LOGO_Z + 1.1)
    zielen(lo, (0, 2.39, LOGO_Z))
    lo.visible_camera = False; lo.visible_glossy = False; lo.visible_transmission = False

if KONTUR:
    # Gegenlicht hinter jedem Teil (schraeg von oben hinten), unsichtbar fuer Kamera/Spiegelungen
    k_mitte = SOCKEL + H - (0.30 + 0.35 if TYP == 'oben' else 0.17 + 0.5)
    for x in XS:
        for sx in (-0.25, 0.25):
            gl = flaeche('Gegenlicht', (x + sx, 0.45, k_mitte + 0.55), (0, 0, 0), (0.25, 0.9), 30 * KOMP, (1.0, 0.98, 0.95))
            zielen(gl, (x, 0, k_mitte))
            gl.visible_glossy = False; gl.visible_transmission = False

w = bpy.data.worlds.new('Welt'); sc.world = w
w.use_nodes = True
next(n for n in w.node_tree.nodes if n.type == 'BACKGROUND').inputs['Color'].default_value = (*P['welt'], 1)

# ---------------- Kamera + Render ----------------
cam = bpy.data.objects.new('Kamera', bpy.data.cameras.new('Kamera')); sc.collection.objects.link(cam)
cam.data.lens = 40
cam.location = (0, -5.1, 1.15) if (TYP == 'oben' and not LANG) else (0, -5.4, 1.2)
ziel = Vector((0, 0, 1.0)) if (TYP == 'oben' and not LANG) else Vector((0, 0, 1.08))
cam.rotation_euler = (ziel - cam.location).to_track_quat('-Z', 'Y').to_euler()
sc.camera = cam

sc.render.engine = 'CYCLES'
sc.cycles.device = 'CPU'
sc.cycles.samples = 32 if TEST else (256 if GROSS else 160)
sc.cycles.use_denoising = True
if hasattr(sc.cycles, 'denoising_use_gpu'):
    sc.cycles.denoising_use_gpu = False   # OIDN sonst auf der GPU -> "out of memory", wenn andere Blender-Fenster VRAM belegen
sc.cycles.caustics_reflective = False
sc.cycles.caustics_refractive = False
sc.cycles.max_bounces = 10; sc.cycles.transmission_bounces = 10; sc.cycles.transparent_max_bounces = 12
sc.render.resolution_x, sc.render.resolution_y = ((1200, 675) if TEST else ((3840, 2160) if GROSS else (2400, 1350)))
sc.render.resolution_percentage = 100
_AUF = next((a.split('=')[1] for a in sys.argv if a.startswith('--aufloesung=')), None)   # z. B. --aufloesung=1920x1080
if _AUF and not TEST:
    sc.render.resolution_x, sc.render.resolution_y = (int(v) for v in _AUF.lower().split('x'))
try:
    sc.view_settings.view_transform = 'Khronos PBR Neutral'
except TypeError:
    sc.view_settings.view_transform = 'Standard'
sc.view_settings.look = 'None'
sc.view_settings.exposure = P['belichtung']
sc.render.filepath = OUT
bpy.ops.render.render(write_still=True)
print('VITRINE FERTIG', STIL, OUT)
