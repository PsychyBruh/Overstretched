# Step 4: materials. Skin = CC0 skin scan drained to drowned gray-blue + procedural veins, lividity,
# stretch marks; anatomical masks (sockets, mouth gore, necrotic fingertips/toes) baked into a colour
# attribute; Poly Haven fabric for the suit scraps; yellowed bone teeth.
# Usage: blender -b 03_posed.blend -P 04_materials.py -- <work_dir> <downloads_dir>
import bpy, json, math, os, sys
from mathutils import Vector

work, dl = sys.argv[sys.argv.index("--") + 1:][:2]
SKIN = os.path.join(dl, "human-materials")
PH = os.path.join(dl, "polyhaven")
scene = bpy.context.scene
body = bpy.data.objects["Stretched"]
scraps = bpy.data.objects["SuitScraps"]
rig = bpy.data.objects["StretchedRig"]
cut = json.load(open(os.path.join(work, "cut.json")))


def smoothstep(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


# ---------------------------------------------------------------- anatomical masks (rest pose coords)
eyes = [Vector(c) for c in cut["eyes"]]
cutp = [Vector((c[0], c[2], c[1])) for c in cut["cut"]]  # stored as (x, z, y)
bones = {b.name: b for b in rig.data.bones}
gname = {g.index: g.name for g in body.vertex_groups}


def seg_dist(p, a, b):
    ab = b - a
    t = max(0.0, min(1.0, (p - a).dot(ab) / max(ab.length_squared, 1e-12)))
    return (p - (a + ab * t)).length, t


attr = body.data.color_attributes.new("Masks", "FLOAT_COLOR", "POINT")
for v in body.data.vertices:
    p, n = v.co, v.normal
    r = g = b = 0.0
    inside = 0.0
    for c in eyes:  # bruised, sunken sockets
        r = max(r, smoothstep(0.036, 0.008, (p - c).length) * (1 if p.y < c.y + 0.02 else 0))
    if 1.43 < p.z < 1.56 and abs(p.x) < 0.09 and p.y < 0.0:
        d = min(seg_dist(p, a, bb)[0] for a, bb in zip(cutp, cutp[1:]))
        g = smoothstep(0.011, 0.0015, d)  # raw lips and cheek tears
        if abs(p.x) < 0.04 and 1.462 < p.z < 1.545 and -0.145 < p.y < -0.045 and (n.y > -0.35 or p.y > -0.125):
            g, inside = 1.0, 1.0  # inside the mouth
    for ge in v.groups:  # necrotic fingertips and toes, darkening toward the tips
        name = gname[ge.group]
        if name.startswith("hand.") or name.startswith("foot."):
            bone = bones[name]
            _, t = seg_dist(p, bone.head_local, bone.tail_local)
            b = max(b, ge.weight * smoothstep(0.35, 1.0, t))
    attr.data[v.index].color = (r, g, b, inside)


# ---------------------------------------------------------------- node helpers
def new_mat(name):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return m, nt, bsdf


def tex(nt, path, vec, non_color=False):
    t = nt.nodes.new("ShaderNodeTexImage")
    t.image = bpy.data.images.load(path, check_existing=True)
    if non_color:
        t.image.colorspace_settings.name = "Non-Color"
    nt.links.new(vec, t.inputs["Vector"])
    return t


def mapping(nt, vec, scale):
    m = nt.nodes.new("ShaderNodeMapping")
    m.inputs["Scale"].default_value = scale
    nt.links.new(vec, m.inputs["Vector"])
    return m.outputs["Vector"]


def mix(nt, a, b, fac, blend="MIX"):
    m = nt.nodes.new("ShaderNodeMix")
    m.data_type = "RGBA"
    m.blend_type = blend
    for sock, val in (("A", a), ("B", b), ("Factor", fac)):
        if isinstance(val, (tuple, float, int)):
            m.inputs[sock].default_value = val
        else:
            nt.links.new(val, m.inputs[sock])
    return m.outputs["Result"]


def ramp(nt, src, stops):
    r = nt.nodes.new("ShaderNodeValToRGB")
    els = r.color_ramp.elements
    els[0].position, els[0].color = stops[0]
    els[1].position, els[1].color = stops[-1]
    for pos, col in stops[1:-1]:
        e = els.new(pos)
        e.color = col
    nt.links.new(src, r.inputs["Fac"])
    return r.outputs["Color"]


def math_node(nt, op, a, b=None):
    m = nt.nodes.new("ShaderNodeMath")
    m.operation = op
    for i, val in enumerate((a, b)):
        if val is None:
            continue
        if isinstance(val, (float, int)):
            m.inputs[i].default_value = val
        else:
            nt.links.new(val, m.inputs[i])
    return m.outputs["Value"]


BW = lambda c: (c, c, c, 1)

# ---------------------------------------------------------------- skin
skin, nt, bsdf = new_mat("StretchedSkin")
uv = nt.nodes.new("ShaderNodeTexCoord")
skin_uv = mapping(nt, uv.outputs["UV"], (7, 7, 7))
col = tex(nt, os.path.join(SKIN, "human_skin_pores_1_2k_col.png"), skin_uv)
nor = tex(nt, os.path.join(SKIN, "human_skin_pores_1_2k_norm.png"), skin_uv, True)
ruf = tex(nt, os.path.join(SKIN, "human_skin_pores_1_2k_ruff@channels=G.png"), skin_uv, True)
masks = nt.nodes.new("ShaderNodeAttribute")
masks.attribute_name = "Masks"
sep = nt.nodes.new("ShaderNodeSeparateColor")
nt.links.new(masks.outputs["Color"], sep.inputs["Color"])
m_socket, m_gore, m_necro = sep.outputs["Red"], sep.outputs["Green"], sep.outputs["Blue"]

hsv = nt.nodes.new("ShaderNodeHueSaturation")
hsv.inputs["Saturation"].default_value = 0.08
hsv.inputs["Value"].default_value = 0.78
nt.links.new(col.outputs["Color"], hsv.inputs["Color"])
c = mix(nt, hsv.outputs["Color"], (0.33, 0.41, 0.48, 1), 1.0, "MULTIPLY")  # drowned gray-blue

blot = nt.nodes.new("ShaderNodeTexNoise")  # lividity: pooled, bruise-coloured blotches
blot.inputs["Scale"].default_value = 3.5
blot.inputs["Detail"].default_value = 6
nt.links.new(uv.outputs["Object"], blot.inputs["Vector"])
c = mix(nt, c, (0.1, 0.1, 0.17, 1), ramp(nt, blot.outputs["Fac"], [(0.45, BW(0)), (0.7, BW(0.75))]))
mottle = nt.nodes.new("ShaderNodeTexNoise")  # fine blotching so it never reads as clean plaster
mottle.inputs["Scale"].default_value = 40
mottle.inputs["Detail"].default_value = 4
nt.links.new(uv.outputs["Object"], mottle.inputs["Vector"])
c = mix(nt, c, (0.18, 0.2, 0.25, 1), ramp(nt, mottle.outputs["Fac"], [(0.4, BW(0)), (0.65, BW(0.45))]))

# Veins: the zero-crossings of a distorted noise field give long, meandering, branching lines
# (a voronoi edge network reads as cracked plaster instead). Two scales: trunks and fine branches.
def vein_lines(scale, width, distortion):
    vn = nt.nodes.new("ShaderNodeTexNoise")
    vn.inputs["Scale"].default_value = scale
    vn.inputs["Detail"].default_value = 3
    vn.inputs["Distortion"].default_value = distortion
    nt.links.new(uv.outputs["UV"], vn.inputs["Vector"])
    dist = math_node(nt, "ABSOLUTE", math_node(nt, "SUBTRACT", vn.outputs["Fac"], 0.5))
    return ramp(nt, dist, [(0.0, BW(1)), (width, BW(0))])


veins = math_node(nt, "MAXIMUM", vein_lines(7, 0.014, 0.6), math_node(nt, "MULTIPLY", vein_lines(19, 0.01, 0.9), 0.6))
vein_mask = nt.nodes.new("ShaderNodeTexNoise")  # veins only show through thin skin, in patches
vein_mask.inputs["Scale"].default_value = 4
nt.links.new(uv.outputs["Object"], vein_mask.inputs["Vector"])
vein_amt = math_node(nt, "MULTIPLY", veins, ramp(nt, vein_mask.outputs["Fac"], [(0.42, BW(0)), (0.62, BW(0.5))]))
c = mix(nt, c, (0.12, 0.13, 0.28, 1), vein_amt)  # blue-purple under the skin, not black grooves

stretch = nt.nodes.new("ShaderNodeTexNoise")  # stretch marks: long streaks along the limbs
stretch.inputs["Detail"].default_value = 2
nt.links.new(mapping(nt, uv.outputs["UV"], (90, 5, 1)), stretch.inputs["Vector"])
marks = ramp(nt, stretch.outputs["Fac"], [(0.0, BW(0)), (0.64, BW(0)), (0.68, BW(1)), (1.0, BW(1))])
raw = ramp(nt, stretch.outputs["Fac"], [(0.0, BW(0)), (0.735, BW(0)), (0.75, BW(1)), (1.0, BW(1))])
c = mix(nt, c, (0.6, 0.58, 0.62, 1), math_node(nt, "MULTIPLY", marks, 0.35))
c = mix(nt, c, (0.24, 0.03, 0.04, 1), raw)

m_inside = masks.outputs["Alpha"]
c = mix(nt, c, (0.035, 0.025, 0.05, 1), ramp(nt, m_socket, [(0.0, BW(0)), (0.55, BW(0.95))]))  # sockets
c = mix(nt, c, (0.13, 0.02, 0.025, 1), m_gore)  # raw lips / tears
c = mix(nt, c, (0.03, 0.004, 0.008, 1), m_inside)  # inside the mouth: near black, wet
c = mix(nt, c, (0.03, 0.025, 0.035, 1), math_node(nt, "MULTIPLY", m_necro, 0.95))  # fingertips, toes
ao = nt.nodes.new("ShaderNodeAmbientOcclusion")
ao.inputs["Distance"].default_value = 0.05
nt.links.new(c, ao.inputs["Color"])
c = mix(nt, c, (0.03, 0.01, 0.015, 1), ramp(nt, ao.outputs["AO"], [(0.25, BW(1)), (0.85, BW(0))]))
nt.links.new(c, bsdf.inputs["Base Color"])

rough = math_node(nt, "MULTIPLY_ADD", ruf.outputs["Color"], 0.45)
rough.node.inputs[2].default_value = 0.42  # dry, slightly dusty skin: no plastic sheen on stretched areas
wet = math_node(nt, "MAXIMUM", m_gore, math_node(nt, "MULTIPLY", m_socket, 0.6))
wet = math_node(nt, "MAXIMUM", wet, raw)
wet_rough = mix(nt, rough, BW(0.2), wet)
nt.links.new(mix(nt, wet_rough, BW(0.5), m_inside), bsdf.inputs["Roughness"])  # inner mouth: no mirror sheen
bsdf.inputs["Subsurface Weight"].default_value = 0.14
bsdf.inputs["Subsurface Radius"].default_value = (0.5, 0.65, 1.0)
bsdf.inputs["Subsurface Scale"].default_value = 0.008
nt.links.new(math_node(nt, "MULTIPLY_ADD", m_inside, -0.47), bsdf.inputs["Specular IOR Level"])
bsdf.inputs["Specular IOR Level"].links[0].from_node.inputs[2].default_value = 0.55  # 0.55 skin, ~0.08 in the mouth

nmap = nt.nodes.new("ShaderNodeNormalMap")
nmap.inputs["Strength"].default_value = 1.0
nt.links.new(nor.outputs["Color"], nmap.inputs["Color"])
bump = nt.nodes.new("ShaderNodeBump")
bump.inputs["Strength"].default_value = 0.18
bump.inputs["Distance"].default_value = 0.0015
nt.links.new(math_node(nt, "SUBTRACT", vein_amt, raw), bump.inputs["Height"])  # veins slightly raised
nt.links.new(nmap.outputs["Normal"], bump.inputs["Normal"])
nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
body.data.materials.clear()
body.data.materials.append(skin)

# ---------------------------------------------------------------- suit scraps
suit, nt, bsdf = new_mat("SuitScraps")
uv = nt.nodes.new("ShaderNodeTexCoord")
fuv = mapping(nt, uv.outputs["UV"], (14, 14, 14))
fcol = tex(nt, os.path.join(PH, "cotton_jersey_diff_2k.png"), fuv)
fnor = tex(nt, os.path.join(PH, "cotton_jersey_nor_gl_2k.png"), fuv, True)
bw = nt.nodes.new("ShaderNodeRGBToBW")
nt.links.new(fcol.outputs["Color"], bw.inputs["Color"])
c = mix(nt, (0.36, 0.1, 0.02, 1), bw.outputs["Val"], 1.0, "MULTIPLY")  # dirty burnt company orange
grime = nt.nodes.new("ShaderNodeTexNoise")
grime.inputs["Scale"].default_value = 6
grime.inputs["Detail"].default_value = 8
nt.links.new(uv.outputs["Object"], grime.inputs["Vector"])
c = mix(nt, c, (0.07, 0.05, 0.035, 1), ramp(nt, grime.outputs["Fac"], [(0.32, BW(0)), (0.64, BW(0.92))]))
blood = nt.nodes.new("ShaderNodeTexNoise")
blood.inputs["Scale"].default_value = 4
blood.inputs["Detail"].default_value = 5
nt.links.new(uv.outputs["Object"], blood.inputs["Vector"])
stain = ramp(nt, blood.outputs["Fac"], [(0.6, BW(0)), (0.64, BW(1))])
c = mix(nt, c, (0.13, 0.015, 0.015, 1), stain)
nt.links.new(c, bsdf.inputs["Base Color"])
nt.links.new(mix(nt, BW(0.88), BW(0.32), stain), bsdf.inputs["Roughness"])
nm = nt.nodes.new("ShaderNodeNormalMap")
nt.links.new(fnor.outputs["Color"], nm.inputs["Color"])
nt.links.new(nm.outputs["Normal"], bsdf.inputs["Normal"])
scraps.data.materials.clear()
scraps.data.materials.append(suit)

# ---------------------------------------------------------------- teeth
teeth, nt, bsdf = new_mat("Teeth")
co = nt.nodes.new("ShaderNodeTexCoord")
n = nt.nodes.new("ShaderNodeTexNoise")
n.inputs["Scale"].default_value = 300
nt.links.new(co.outputs["Object"], n.inputs["Vector"])
nt.links.new(mix(nt, (0.74, 0.64, 0.42, 1), (0.36, 0.27, 0.15, 1), ramp(nt, n.outputs["Fac"], [(0.45, BW(0)), (0.7, BW(1))])),
             bsdf.inputs["Base Color"])
bsdf.inputs["Roughness"].default_value = 0.28
bsdf.inputs["Subsurface Weight"].default_value = 0.1
for name in ("TeethUpper", "TeethLower"):
    o = bpy.data.objects[name]
    o.data.materials.clear()
    o.data.materials.append(teeth)

bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work, "04_materials.blend"))
print("MATERIALS_OK")
