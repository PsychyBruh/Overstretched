# Step 9: every hand-made animation for The Stretched, authored as Blender actions on the game rig,
# plus one labelled preview timeline (with a real-size doorway and narrow-hall ceiling) to review.
#
# Poses are written as "point this bone this way" in armature space (-Y = forward, +X = the
# creature's left, +Z = up) and solved with our own forward kinematics straight into each bone's
# local transform, so no depsgraph updates are needed while authoring.
#
# Usage: blender -b 06_game.blend -P 09_animate.py -- <work_dir> <assets_dir> [render]
import bpy, math, os, random, sys
from mathutils import Matrix, Quaternion, Vector

args = sys.argv[sys.argv.index("--") + 1:]
work, assets = args[0], args[1]
do_render = len(args) > 2 and args[2] == "render"
scene = bpy.context.scene
scene.render.fps = 30
rig = bpy.data.objects["StretchedRig"]
rig.data.pose_position = "POSE"
FLOOR = -0.602           # rest-pose floor height of the game rig
CEIL = FLOOR + 3.4 / 1.42  # narrow-hallway ceiling (3.4 game units) in model units
DOOR_H, DOOR_W = 3.25 / 1.42, 1.78 / 1.42

bones = {b.name: b for b in rig.data.bones}
ORDER = []
def _walk(b):
    ORDER.append(b.name)
    for c in b.children:
        _walk(c)
for b in rig.data.bones:
    if b.parent is None:
        _walk(b)
REST = {n: b.matrix_local.copy() for n, b in bones.items()}
REST3 = {n: m.to_3x3().normalized() for n, m in REST.items()}
FINGERS = ("thumb", "index", "middle", "ring", "pinky")
for pb in rig.pose.bones:
    pb.rotation_mode = "QUATERNION"


def V(x, y, z):
    return Vector((x, y, z)).normalized()


# Hand frame for finger curl: knuckle line (index -> pinky) and the palm side (where the thumb sits).
HAND = {}
for s in ("L", "R"):
    h = REST[f"hand.{s}"]
    axis = REST3[f"hand.{s}"].col[1]
    lateral = (bones[f"pinky_1.{s}"].head_local - bones[f"index_1.{s}"].head_local).normalized()
    t = bones[f"thumb_3.{s}"].tail_local - h.translation
    palm = (t - axis * t.dot(axis) - lateral * t.dot(lateral)).normalized()
    curl_axis = lateral if lateral.cross(axis).dot(palm) > 0 else -lateral
    HAND[s] = (curl_axis, palm)

# ---------------------------------------------------------------- pose description
BASE = {
    "pelvis": V(0, 0.06, 1), "spine1": V(0, -0.12, 1), "spine2": V(0, -0.36, 0.93), "spine3": V(0, -0.5, 0.87),
    "neck1": V(0, -0.9, 0.45), "neck2": V(0.04, -0.75, 0.66), "neck3": V(0.12, -0.37, 0.92),
    "clavicle.R": V(-0.94, -0.25, 0.24), "clavicle.L": V(0.94, -0.25, 0.24),
    "upperarm.R": V(-0.14, -0.22, -0.96), "upperarm.L": V(0.14, -0.18, -0.97),
    "forearm_a.R": V(-0.07, -0.5, -0.86), "forearm_a.L": V(0.07, -0.45, -0.89),
    "forearm_b.R": V(-0.03, 0.55, -0.84), "forearm_b.L": V(0.03, 0.5, -0.87),
    "hand.R": V(-0.02, -0.2, -0.98), "hand.L": V(0.02, -0.15, -0.99),
    "thigh.R": V(-0.06, -0.1, -0.99), "thigh.L": V(0.06, -0.08, -0.99),
    "shin.R": V(-0.03, 0.15, -0.99), "shin.L": V(0.03, 0.13, -0.99),
    "foot.R": None, "foot.L": None,  # None = keep rest orientation (flat on the floor)
}


class Pose:
    def __init__(self):
        self.dirs = dict(BASE)
        self.twist = {}
        self.head_up, self.head_face = V(0.5, -0.3, 0.8), V(0.15, -1, -0.35)
        self.jaw = 8.0
        self.curl = {"L": 18.0, "R": 18.0}
        self.spread = {"L": 0.0, "R": 0.0}
        self.eye = (0.0, 0.0)
        self.offset = Vector((0, 0, 0))   # extra pelvis translation
        self.ground = "feet"              # "feet", "all" (feet+hands) or None
        self.pelvis_z = None              # explicit pelvis height when not grounded
        self.flip = False                 # rotate the whole pose 180 deg about the forward axis (ceiling)

    def copy(self):
        p = Pose()
        p.__dict__.update({k: (dict(v) if isinstance(v, dict) else (v.copy() if isinstance(v, Vector) else v))
                           for k, v in self.__dict__.items()})
        return p

    def set(self, **dirs):
        for k, v in dirs.items():
            self.dirs[k.replace("__", ".")] = V(*v) if v is not None else None
        return self


def solve(p):
    """Forward kinematics: armature-space matrix for every bone."""
    G = Matrix.Rotation(math.pi, 3, "Y") if p.flip else Matrix.Identity(3)
    M = {}
    head_rot = None
    for n in ORDER:
        b = bones[n]
        if b.parent:
            pm = M[b.parent.name]
            follow = pm @ REST[b.parent.name].inverted() @ REST[n]
            head = follow.translation
        else:
            follow, head = REST[n].copy(), REST[n].translation.copy()
        if n == "head":
            up, face = G @ p.head_up, G @ p.head_face
            face = (face - up * face.dot(up)).normalized()
            rest_up, rest_face = REST3[n].col[1], Vector((0, -1, 0))
            src = Matrix((rest_up, rest_face, rest_up.cross(rest_face))).transposed()
            dst = Matrix((up, face, up.cross(face))).transposed()
            rot = dst @ src.inverted() @ REST3[n]
            head_rot = dst @ src.inverted()
        elif n in p.dirs:
            d = p.dirs[n]
            if d is None:
                rot = G @ REST3[n]
            else:
                d = G @ d
                rot = REST3[n].col[1].rotation_difference(d).to_matrix() @ REST3[n]
            tw = p.twist.get(n, 0.0)
            if tw:
                rot = Matrix.Rotation(math.radians(tw), 3, rot.col[1].normalized()) @ rot
        elif n == "jaw":
            # Closed = rest relation to the head; open by rotating about the head's own X axis.
            rot = Matrix.Rotation(math.radians(p.jaw), 3, head_rot @ Vector((1, 0, 0))) @ follow.to_3x3().normalized()
        elif n.startswith("eye."):
            rot = follow.to_3x3().normalized() @ Matrix.Rotation(math.radians(p.eye[0]), 3, "X") @ \
                Matrix.Rotation(math.radians(p.eye[1]), 3, "Z")
        elif n.startswith(FINGERS):
            s = n[-1]
            hand_delta = M[f"hand.{s}"].to_3x3().normalized() @ REST3[f"hand.{s}"].inverted()
            curl_axis, palm = (hand_delta @ v for v in HAND[s])
            seg = int(n.split("_")[1][0])
            curl = p.curl[s] * (0.6 if n.startswith("thumb") else 1.0)
            fi = FINGERS.index(n.split("_")[0])
            spread = p.spread[s] * (fi - 2) * 0.5 if seg == 1 else 0.0
            base = hand_delta @ REST3[n]
            rot = Matrix.Rotation(math.radians(curl * seg), 3, curl_axis) @ \
                Matrix.Rotation(math.radians(spread), 3, palm) @ base
        else:
            rot = follow.to_3x3().normalized()
        m = rot.to_4x4()
        m.translation = head
        M[n] = m
    # Root placement: grounded on the feet (and hands), or an explicit height.
    shift = p.offset.copy()
    if p.ground:
        pts = []
        for s in ("L", "R"):
            f = M[f"foot.{s}"]
            pts += [f.translation.z - 0.115, (f @ Vector((0, bones[f"foot.{s}"].length, 0, 1))).z - 0.011]
            if p.ground == "all":
                for fn in ("index_3", "middle_3", "ring_3"):
                    m3 = M[f"{fn}.{s}"]
                    pts.append((m3 @ Vector((0, bones[f"{fn}.{s}"].length, 0, 1))).z - 0.008)
        shift.z += FLOOR - min(pts)
    elif p.pelvis_z is not None:
        shift.z += p.pelvis_z - M["pelvis"].translation.z
    for m in M.values():
        m.translation += shift
    return M


def to_basis(M):
    B = {}
    for n in ORDER:
        b = bones[n]
        if b.parent:
            B[n] = REST[n].inverted() @ REST[b.parent.name] @ M[b.parent.name].inverted() @ M[n]
        else:
            B[n] = REST[n].inverted() @ M[n]
    return B


# ---------------------------------------------------------------- keying into clip + preview actions
rig.animation_data_create()
PREVIEW = bpy.data.actions.new("Preview")
PREVIEW.use_fake_user = True
clips = {}            # name -> (action, length)
preview_ranges = []   # (name, start, end, note)
_last_q = {}
_cursor = 1
GAP = 12


def key_action(action, frame, B, extra_obj_keys=None):
    rig.animation_data.action = action
    for n, b in B.items():
        pb = rig.pose.bones[n]
        q = b.to_quaternion()
        prev = _last_q.get((action.name, n))
        if prev is not None and prev.dot(q) < 0:
            q.negate()
        _last_q[(action.name, n)] = q
        pb.rotation_quaternion = q
        pb.keyframe_insert("rotation_quaternion", frame=frame, group=n)
        if bones[n].parent is None:
            pb.location = b.translation
            pb.keyframe_insert("location", frame=frame, group=n)


class Clip:
    """Collects (frame, pose) keys; jitter/hold helpers; writes the clip and its preview copy."""

    def __init__(self, name, length, note="", loop=False):
        self.name, self.length, self.note, self.loop = name, length, note, loop
        self.keys = []
        self.obj_keys = []  # preview-only rig object transforms: (frame, location, rotation_euler)

    def at(self, frame, pose):
        self.keys.append((frame, pose))
        return self

    def hold(self, frame_from, frame_to, pose):
        return self.at(frame_from, pose).at(frame_to, pose)

    def shake(self, frame_from, frame_to, pose, amp, bone_names, step=2, seed=1):
        rng = random.Random(seed)
        for f in range(frame_from, frame_to + 1, step):
            q = pose.copy()
            for n in bone_names:
                d = q.dirs.get(n)
                if d is None:
                    continue
                jit = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))) * math.radians(amp)
                q.dirs[n] = (d + jit).normalized()
            q.jaw += rng.uniform(-amp, amp) * 0.4
            self.at(f, q)
        return self

    def write(self):
        global _cursor
        action = bpy.data.actions.new(self.name)
        action.use_fake_user = True
        # Metadata read by 10_export_anims.py.
        action["lp_length"] = self.length
        action["lp_loop"] = int(self.loop)
        action["lp_ceiling"] = int(any(abs(rot[1]) > 1 for _, _, rot in self.obj_keys))
        keys = sorted(self.keys, key=lambda k: k[0])
        if self.loop:
            keys.append((self.length, keys[0][1]))
        start = _cursor
        for frame, pose in keys:
            B = to_basis(solve(pose))
            key_action(action, frame, B)
            key_action(PREVIEW, start + frame, B)
        rig.animation_data.action = PREVIEW
        if len(self.obj_keys) == 1:  # a single placement holds for the whole clip
            self.obj_keys.append((self.length, *self.obj_keys[0][1:]))
        for frame, loc, rot in self.obj_keys:
            rig.location, rig.rotation_euler = loc, rot
            rig.keyframe_insert("location", frame=start + frame)
            rig.keyframe_insert("rotation_euler", frame=start + frame)
        if not self.obj_keys:  # keep the rig parked at the origin for clips without preview motion
            for f in (start, start + self.length):
                rig.location, rig.rotation_euler = (0, 0, 0), (0, 0, 0)
                rig.keyframe_insert("location", frame=f)
                rig.keyframe_insert("rotation_euler", frame=f)
        clips[self.name] = (action, self.length)
        preview_ranges.append((self.name, start, start + self.length, self.note))
        _cursor = start + self.length + GAP


# ---------------------------------------------------------------- pose library
def idle():
    return Pose()


def tall():
    """Upright to full height: used for the doorway stare and frozen poses."""
    p = Pose().set(spine1=(0, -0.04, 1), spine2=(0, -0.1, 1), spine3=(0, -0.18, 1),
                   neck1=(0, -0.3, 1), neck2=(0, -0.2, 1), neck3=(0, -0.1, 1))
    p.head_up, p.head_face = V(0.55, -0.1, 0.83), V(0.05, -1, -0.1)
    return p


def hunched(depth):
    """depth 0 = idle, 1 = duck walk (game height ~3.1), 2 = deep duck (~2.6, knuckles near the floor)."""
    p = Pose()
    k = min(depth, 1.0)
    k2 = max(0.0, depth - 1.0)
    p.set(spine1=(0, -0.2 - 0.5 * k - 0.4 * k2, 1), spine2=(0, -0.55 - 0.6 * k - 0.6 * k2, 1),
          spine3=(0, -0.8 - 0.6 * k - 0.8 * k2, 1),
          neck1=(0, -1, 0.25 - 0.2 * k), neck2=(0.03, -1, 0.35 - 0.1 * k2), neck3=(0.08, -0.8, 0.6),
          thigh__R=(-0.07, -0.45 * k - 0.4 * k2, -1), thigh__L=(0.07, -0.42 * k - 0.4 * k2, -1),
          shin__R=(-0.03, 0.45 * k + 0.5 * k2, -1), shin__L=(0.03, 0.42 * k + 0.5 * k2, -1),
          upperarm__R=(-0.12, -0.5 * k - 0.6 * k2, -1), upperarm__L=(0.12, -0.45 * k - 0.6 * k2, -1),
          forearm_a__R=(-0.05, -0.6 - 0.3 * k2, -1), forearm_a__L=(0.05, -0.55 - 0.3 * k2, -1))
    p.head_up = V(0.4, -0.7, 0.6)
    p.head_face = V(0.1, -0.6, -0.8)
    if k2 > 0:
        p.ground = "all"
    return p


def crawl(phase=0.0):
    """Belly-down quadruped crawl on long arms (on the ceiling the whole thing is flipped)."""
    p = Pose()
    s = math.sin(phase * 2 * math.pi)
    c = math.cos(phase * 2 * math.pi)
    p.set(pelvis=(0, -1, 0.15), spine1=(0, -1, 0.05), spine2=(0, -1, 0.0), spine3=(0, -1, -0.05),
          neck1=(0, -1, 0.35), neck2=(0, -0.6, 0.8), neck3=(0, 0.2, 1),
          upperarm__R=(-0.35, -0.55 - 0.35 * s, -1), upperarm__L=(0.35, -0.55 + 0.35 * s, -1),
          forearm_a__R=(-0.2, -0.4 - 0.3 * s, -1), forearm_a__L=(0.2, -0.4 + 0.3 * s, -1),
          forearm_b__R=(-0.1, 0.25, -1), forearm_b__L=(0.1, 0.25, -1),
          hand__R=(-0.1, -0.9, -0.4), hand__L=(0.1, -0.9, -0.4),
          thigh__R=(-0.3, 0.3 + 0.35 * s, -1), thigh__L=(0.3, 0.3 - 0.35 * s, -1),
          shin__R=(-0.1, 1, -0.4 + 0.3 * max(0, c)), shin__L=(0.1, 1, -0.4 + 0.3 * max(0, -c)),
          foot__R=(-0.1, 0.4, -1), foot__L=(0.1, 0.4, -1))
    # head twisted all the way round so the face points up (down, once flipped onto the ceiling)
    p.head_up, p.head_face = V(0, 0.75, 0.66), V(0.1, -0.3, 1)
    p.curl = {"L": 35.0, "R": 35.0}
    p.jaw = 14
    p.ground = "all"
    return p


def walk_pose(phase, stoop=0.0, stride=1.0, arm_swing=1.0):
    """phase 0..1, two steps per cycle; stoop blends toward the duck walk."""
    p = hunched(stoop)
    a = phase * 2 * math.pi
    s, c = math.sin(a), math.cos(a)
    lift_r, lift_l = max(0.0, -c), max(0.0, c)   # swing-leg knee lift
    bend = 0.42 * min(stoop, 1.0) + 0.4 * max(0.0, stoop - 1.0)
    p.set(thigh__R=(-0.07, -0.4 * s * stride - bend, -1), thigh__L=(0.07, 0.4 * s * stride - bend, -1),
          shin__R=(-0.03, 0.12 + bend + 0.8 * lift_r * stride, -1), shin__L=(0.03, 0.12 + bend + 0.8 * lift_l * stride, -1))
    sway = 0.28 * arm_swing
    for side, sign in (("R", -1), ("L", 1)):
        base_ua = p.dirs[f"upperarm.{side}"]
        p.dirs[f"upperarm.{side}"] = (base_ua + Vector((0, sway * s * (1 if side == "R" else -1), 0))).normalized()
    p.twist["spine2"] = 8 * s
    p.head_up = (p.head_up + Vector((0.05 * s, 0, 0))).normalized()
    return p


# ---------------------------------------------------------------- the clips
def flipped(p, pelvis_z):
    q = p.copy()
    q.flip, q.ground, q.pelvis_z = True, None, pelvis_z
    return q


def crawl_height():
    return solve(crawl())["pelvis"].translation.z - FLOOR


# 1 idle
c = Clip("idle", 90, "hunched, breathing, loops", loop=True)
p0 = idle()
p1 = idle().set(spine2=(0, -0.3, 0.95), spine3=(0, -0.42, 0.9), clavicle__R=(-0.94, -0.25, 0.3), clavicle__L=(0.94, -0.25, 0.3))
p1.jaw, p1.curl = 13, {"L": 26, "R": 14}
p1.head_up = V(0.52, -0.26, 0.81)
c.at(0, p0).at(45, p1).write()

# 2 walk
c = Clip("walk", 48, "slow, stiff, loops", loop=True)
for f in range(0, 48, 6):
    c.at(f, walk_pose(f / 48))
c.write()

# 3 chase lope
c = Clip("chase_lope", 24, "fast lope, arms reaching, loops", loop=True)
for f in range(0, 24, 3):
    ph = f / 24
    p = walk_pose(ph, stoop=0.9, stride=1.6, arm_swing=0.0)
    s = math.sin(ph * 2 * math.pi)
    p.set(upperarm__R=(-0.15, -0.9 + 0.5 * s, -0.45), upperarm__L=(0.15, -0.9 - 0.5 * s, -0.45),
          forearm_a__R=(-0.1, -1, -0.2), forearm_a__L=(0.1, -1, -0.2),
          forearm_b__R=(-0.05, -0.6, -0.8), forearm_b__L=(0.05, -0.6, -0.8),
          hand__R=(-0.05, -1, -0.3), hand__L=(0.05, -1, -0.3))
    p.jaw, p.spread = 34, {"L": 25, "R": 25}
    p.curl = {"L": 10, "R": 10}
    c.at(f, p)
c.write()

# 4 turn in place: the body follows the head in jerks (the game rotates the root after it plays)
c = Clip("turn_in_place", 40, "jerky 90 deg turn, head leads")
for f, yaw_head, yaw_body in ((0, 0, 0), (5, 70, 0), (10, 85, 25), (16, 90, 25), (21, 90, 60), (28, 90, 60), (33, 90, 90), (40, 90, 90)):
    p = idle()
    p.twist.update({"pelvis": yaw_body, "spine3": (yaw_head - yaw_body) * 0.3})
    rh = Matrix.Rotation(math.radians(yaw_head), 3, "Z")
    p.head_up, p.head_face = rh @ p.head_up, rh @ p.head_face
    c.at(f, p)
c.write()

# 5 duck walk (narrow halls / doorways) and 6 deep duck (mineshaft caves)
c = Clip("duck_walk", 56, "stooped walk for ~3.1 ceilings, loops", loop=True)
for f in range(0, 56, 7):
    c.at(f, walk_pose(f / 56, stoop=1.0, stride=0.8))
c.write()
c = Clip("deep_duck_walk", 64, "knuckle-walk for ~2.6 caves, loops", loop=True)
for f in range(0, 64, 8):
    ph = f / 64
    p = walk_pose(ph, stoop=2.0, stride=0.6, arm_swing=0.0)
    s = math.sin(ph * 2 * math.pi)
    p.set(upperarm__R=(-0.2, -0.55 - 0.3 * s, -1), upperarm__L=(0.2, -0.55 + 0.3 * s, -1),
          forearm_b__R=(-0.05, 0.1, -1), forearm_b__L=(0.05, 0.1, -1), hand__R=(-0.05, -0.4, -1), hand__L=(0.05, -0.4, -1))
    p.curl = {"L": 75, "R": 75}
    c.at(f, p)
c.write()

# 7 squeeze through a doorway (preview walks it through a real-size door frame)
c = Clip("doorway_squeeze", 70, "duck + turn shoulders through a 3.25 door")
squeeze = hunched(1.25)
squeeze.twist.update({"pelvis": 55, "spine2": 15})
squeeze.set(upperarm__L=(0.2, 0.3, -1), upperarm__R=(-0.3, -0.8, -0.6))
c.at(0, idle()).at(14, squeeze).at(26, walk_pose(0.25, 1.25, 0.6)).at(38, walk_pose(0.75, 1.25, 0.6))
c.at(48, squeeze).at(70, idle())
c.obj_keys = [(0, Vector((0, 1.1, 0)), Vector((0, 0, 0))), (70, Vector((0, -1.1, 0)), Vector((0, 0, 0)))]
c.write()

# 8 frozen flicker poses: six poses it snaps between when the lights flicker
c = Clip("flicker_poses", 96, "6 snap poses, one per light flicker")
f1 = tall().set(upperarm__R=(-0.4, -0.3, -0.8), upperarm__L=(0.4, -0.3, -0.8))
f1.head_up, f1.head_face = V(1, -0.1, 0.15), V(0, -1, 0)                          # head lolled sideways
f2 = hunched(0.7).set(upperarm__L=(0.2, -1, -0.2), forearm_a__L=(0.1, -1, 0), forearm_b__L=(0.05, -1, -0.1), hand__L=(0.05, -1, 0))
f2.jaw, f2.spread = 40, {"L": 30, "R": 0}                                          # reaching out, mouth open
f3 = hunched(0.6).set(neck1=(0, -1, -0.3), neck2=(0, -0.9, -0.4), neck3=(0, -0.7, -0.6))
f3.head_up, f3.head_face = V(0, -0.8, -0.6), V(0, -0.6, 0.8)                       # head craned to the floor
f4 = tall()
f4.twist.update({"spine2": 50, "spine3": 40})
f4.head_up, f4.head_face = V(-0.2, 0.3, 0.93), V(-0.3, 1, 0)                       # looking back over its shoulder
f5 = hunched(0.9).set(upperarm__R=(-0.2, -0.3, 0.9), forearm_a__R=(-0.1, 0, 1), forearm_b__R=(0, 0.5, 0.8), hand__R=(0, -0.3, 1))
f5.curl = {"L": 20, "R": 60}                                                        # one arm up, clawing at the ceiling
f6 = tall().set(upperarm__R=(-0.1, 0.4, -1), upperarm__L=(0.1, 0.4, -1), forearm_a__R=(0.1, 0.5, -1), forearm_a__L=(-0.1, 0.5, -1))
f6.head_up, f6.head_face = V(0.1, -0.8, -0.55), V(0, -0.5, 0.86)                   # arms behind, head hanging limp
for i, p in enumerate((f1, f2, f3, f4, f5, f6)):
    c.hold(i * 16, i * 16 + 15, p)
c.write()

# 9 doorway stance: standing dead still at the end of a hall
c = Clip("doorway_stance", 90, "dead still, watching; tiny twitches")
d0 = tall()
d0.head_up = V(0.6, -0.05, 0.8)
d1 = d0.copy()
d1.curl = {"L": 30, "R": 18}
d2 = d0.copy()
d2.head_up = V(0.66, -0.05, 0.75)
c.hold(0, 58, d0).at(60, d1).at(64, d0).hold(74, 76, d2).at(90, d2).write()

# 10 listen: head snaps toward a sound
c = Clip("listen", 45, "head snaps to a sound, holds, listens")
l1 = idle()
l1.head_up, l1.head_face = V(-0.3, -0.4, 0.86), V(-0.9, -0.4, 0.0)
l1.jaw = 18
l1.twist["spine3"] = -20
l2 = l1.copy()
l2.head_up = V(-0.45, -0.4, 0.8)
l2.curl = {"L": 40, "R": 40}
c.at(0, idle()).at(4, l1).at(18, l2).hold(19, 30, l2).at(45, idle()).write()

# 11 watched reaction: the head cranes round, far too far
c = Clip("watched_reaction", 60, "head slowly cranes toward the viewer, too far")
w = idle()
for f, yaw, roll in ((0, 0, 0), (20, 40, 20), (40, 85, 55), (50, 110, 75)):
    q = w.copy()
    rz = Matrix.Rotation(math.radians(yaw), 3, "Z") @ Matrix.Rotation(math.radians(roll), 3, "Y")
    q.head_up, q.head_face = rz @ w.head_up, rz @ w.head_face
    q.twist["neck3"] = yaw * 0.3
    c.at(f, q)
c.at(60, q).write()

# 12 lean over: reappears behind you, face inches from yours
c = Clip("lean_over", 60, "bent down over a player, breathing")
lo = hunched(0.6).set(neck1=(0, -1, -0.2), neck2=(0, -0.8, -0.5), neck3=(0, -0.4, -0.9),
                      upperarm__R=(-0.7, -0.5, -0.5), upperarm__L=(0.7, -0.5, -0.5),
                      forearm_a__R=(-0.3, -0.9, -0.3), forearm_a__L=(0.3, -0.9, -0.3),
                      forearm_b__R=(0.2, -0.8, -0.5), forearm_b__L=(-0.2, -0.8, -0.5))
lo.head_up, lo.head_face = V(0, -0.5, -0.86), V(0, -0.86, 0.5)
lo.spread = {"L": 30, "R": 30}
lo2 = lo.copy()
lo2.jaw = 30
lo2.offset = Vector((0, -0.03, 0))
c.at(0, lo).at(30, lo2).at(60, lo).write()

# 13 flashlight recoil, 14 retreat
c = Clip("flashlight_recoil", 32, "arms up over its face, staggers back")
r = idle().set(spine2=(0, 0.1, 1), spine3=(0, 0.2, 1),
               upperarm__R=(-0.3, -0.7, 0.6), upperarm__L=(0.3, -0.7, 0.6),
               forearm_a__R=(0.6, -0.4, 0.6), forearm_a__L=(-0.6, -0.4, 0.6),
               forearm_b__R=(0.6, 0.2, 0.7), forearm_b__L=(-0.6, 0.2, 0.7),
               thigh__L=(0.08, 0.35, -1), shin__L=(0.03, 0.45, -1))
r.head_up, r.head_face = V(-0.5, 0.3, 0.8), V(-0.6, -0.2, -0.2)
r.curl = {"L": 60, "R": 60}
c.at(0, idle()).at(4, r).shake(6, 32, r, 6, ["spine3", "neck2", "upperarm.R", "upperarm.L"], step=2, seed=3).write()
c = Clip("retreat", 36, "crouched backward scuttle, shielding face, loops", loop=True)
for f in range(0, 36, 6):
    p = walk_pose(1 - f / 36, stoop=0.8, stride=0.7, arm_swing=0)
    p.set(upperarm__R=(-0.3, -0.7, 0.4), upperarm__L=(0.3, -0.7, 0.4),
          forearm_a__R=(0.6, -0.4, 0.5), forearm_a__L=(-0.6, -0.4, 0.5))
    p.head_up, p.head_face = V(-0.3, 0.2, 0.9), V(-0.5, -0.3, -0.6)
    p.curl = {"L": 55, "R": 55}
    c.at(f, p)
c.write()

# 15 stunned, 16 hit flinch
c = Clip("stunned", 48, "convulsing, knees buckling")
st = hunched(0.8)
st.jaw = 30
c.at(0, idle()).shake(2, 46, st, 14, ["spine1", "spine2", "spine3", "neck1", "neck2", "upperarm.R", "upperarm.L",
                                       "forearm_a.R", "forearm_a.L", "thigh.R", "thigh.L"], step=2, seed=5)
c.at(48, idle()).write()
c = Clip("hit_flinch", 16, "violent jerk, recovers")
fl = idle()
fl.twist.update({"spine2": -30, "spine3": -25})
fl.head_up, fl.head_face = V(-0.6, 0.0, 0.8), V(-0.3, -1, 0)
fl.jaw, fl.spread = 25, {"L": 25, "R": 25}
c.at(0, idle()).at(2, fl).at(16, idle()).write()

# 17 climb up, 18 ceiling crawl, 19 ceiling hang, 20 drop down
H_CRAWL = crawl_height()
ceil_pose_z = CEIL - H_CRAWL
reach = tall().set(upperarm__R=(-0.15, -0.1, 1), upperarm__L=(0.15, -0.1, 1), forearm_a__R=(-0.05, 0, 1),
                   forearm_a__L=(0.05, 0, 1), forearm_b__R=(0, 0.1, 1), forearm_b__L=(0, 0.1, 1),
                   hand__R=(0, 0, 1), hand__L=(0, 0, 1))
reach.head_up, reach.head_face = V(0, 0.4, 0.9), V(0, -0.3, 1)
reach.curl = {"L": 45, "R": 45}
pull = reach.copy()
pull.ground, pull.pelvis_z = None, solve(reach)["pelvis"].translation.z + 0.6
pull.set(thigh__R=(-0.1, -0.8, -0.6), thigh__L=(0.1, -0.8, -0.6), shin__R=(-0.05, 0.9, -0.4), shin__L=(0.05, 0.9, -0.4))
c = Clip("climb_up", 60, "reaches up, hauls itself onto the ceiling")
c.at(0, idle()).at(14, reach).at(30, pull).at(48, flipped(crawl(0.0), ceil_pose_z)).at(60, flipped(crawl(0.0), ceil_pose_z))
c.write()
c = Clip("ceiling_crawl", 32, "upside-down crawl, head twisted round, loops", loop=True)
for f in range(0, 32, 4):
    c.at(f, crawl(f / 32))
# Preview shows it where it really lives: flipped about the forward axis onto the ceiling.
c.obj_keys = [(0, Vector((0, 0, CEIL + FLOOR)), Vector((0, math.pi, 0)))]
c.write()
c = Clip("ceiling_hang", 60, "clinging to the ceiling, face hanging down", loop=True)
hang = crawl(0.25)
hang.head_up, hang.head_face = V(0, 0.4, 0.92), V(0.2, 0.1, 1)
hang2 = hang.copy()
hang2.jaw = 30
c.at(0, hang).at(30, hang2).at(60, hang)
c.obj_keys = [(0, Vector((0, 0, CEIL + FLOOR)), Vector((0, math.pi, 0)))]
c.write()
c = Clip("drop_down", 40, "lets go of the ceiling, lands, unfolds")
land = hunched(1.4)
land.jaw = 25
c.at(0, flipped(crawl(0.0), ceil_pose_z)).at(6, flipped(crawl(0.1), ceil_pose_z - 0.05))
fall = hunched(0.5)
fall.ground, fall.pelvis_z = None, (ceil_pose_z + solve(idle())["pelvis"].translation.z) / 2
c.at(16, fall).at(24, land).at(40, idle()).write()

# 21 scream
c = Clip("scream", 60, "head thrown back, jaw unhinged, arms out")
sc = tall().set(spine2=(0, 0.15, 1), spine3=(0, 0.3, 1), upperarm__R=(-0.9, 0.3, -0.3), upperarm__L=(0.9, 0.3, -0.3),
                forearm_a__R=(-1, 0.2, 0), forearm_a__L=(1, 0.2, 0), forearm_b__R=(-1, 0.4, 0.1), forearm_b__L=(1, 0.4, 0.1),
                hand__R=(-1, 0, 0), hand__L=(1, 0, 0))
sc.head_up, sc.head_face = V(0.15, 0.6, 0.78), V(0, -0.6, 0.8)
sc.jaw, sc.spread, sc.curl = 62, {"L": 35, "R": 35}, {"L": -10, "R": -10}
c.at(0, idle()).at(10, sc).shake(12, 44, sc, 3, ["neck2", "neck3", "spine3", "hand.R", "hand.L"], step=2, seed=9).at(60, idle()).write()

# 22 knock, 23 pry door
c = Clip("knock", 44, "three slow knocks at head height")
# Knuckles end up just above shoulder height, about an arm's length in front of the face.
kn_up = tall().set(upperarm__R=(-0.15, -0.9, -0.4), forearm_a__R=(0.0, -0.7, 0.7), forearm_b__R=(0.0, -0.2, 0.98),
                   hand__R=(0.0, -0.95, 0.3))
kn_up.curl = {"L": 20, "R": 85}
kn_hit = kn_up.copy().set(forearm_b__R=(0.0, -0.55, 0.83), hand__R=(0.0, -0.95, -0.3))
c.at(0, idle()).at(10, kn_up)
for f in (14, 22, 30):
    c.at(f, kn_hit).at(f + 4, kn_up)
c.at(44, idle()).write()
c = Clip("pry_door", 60, "fingers in the gap, forcing a door open, loops", loop=True)
pr = tall().set(spine2=(0, 0.05, 1), upperarm__R=(-0.35, -0.9, -0.1), upperarm__L=(0.35, -0.9, -0.1),
                forearm_a__R=(-0.1, -1, 0.1), forearm_a__L=(0.1, -1, 0.1), forearm_b__R=(0.1, -1, 0), forearm_b__L=(-0.1, -1, 0),
                hand__R=(0.1, -1, 0), hand__L=(-0.1, -1, 0))
pr.curl = {"L": 65, "R": 65}
pr2 = pr.copy().set(forearm_b__R=(-0.5, -0.85, 0), forearm_b__L=(0.5, -0.85, 0), hand__R=(-0.6, -0.8, 0), hand__L=(0.6, -0.8, 0),
                    spine2=(0, 0.2, 1))
c.at(0, pr).shake(4, 26, pr2, 4, ["forearm_b.R", "forearm_b.L", "spine2"], step=3, seed=11).at(30, pr2).write()

# 24 lunge + grab, 25 lift, 26 retract (the kill)
grab = hunched(0.7).set(spine1=(0, -0.5, 1), upperarm__R=(-0.15, -1, -0.1), upperarm__L=(0.15, -1, -0.1),
                        forearm_a__R=(-0.05, -1, -0.05), forearm_a__L=(0.05, -1, -0.05), forearm_b__R=(0.05, -1, -0.1),
                        forearm_b__L=(-0.05, -1, -0.1), hand__R=(0.1, -1, 0), hand__L=(-0.1, -1, 0))
grab.offset = Vector((0, -0.35, 0))
grab.jaw, grab.spread, grab.curl = 45, {"L": 35, "R": 35}, {"L": 0, "R": 0}
held = grab.copy()
held.curl, held.spread = {"L": 70, "R": 70}, {"L": 0, "R": 0}
c = Clip("lunge_grab", 22, "explodes forward, long fingers close")
c.at(0, idle()).at(6, grab).at(12, held).at(22, held).write()
lift = tall().set(upperarm__R=(-0.12, -0.3, 1), upperarm__L=(0.12, -0.3, 1), forearm_a__R=(0.05, -0.4, 1),
                  forearm_a__L=(-0.05, -0.4, 1), forearm_b__R=(0.1, -0.5, 1), forearm_b__L=(-0.1, -0.5, 1),
                  hand__R=(0.1, -0.4, 1), hand__L=(-0.1, -0.4, 1))
lift.head_up, lift.head_face = V(0, 0.5, 0.86), V(0, -0.86, 0.5)
lift.curl, lift.jaw = {"L": 70, "R": 70}, 30
c = Clip("lift", 40, "raises the victim toward the ceiling")
c.at(0, held).at(30, lift).at(40, lift).write()
c = Clip("retract", 45, "rises into the dark, victim and all")
up = lift.copy()
up.ground, up.pelvis_z = None, solve(lift)["pelvis"].translation.z + 1.1
up.set(thigh__R=(-0.05, 0.2, -1), thigh__L=(0.05, 0.25, -1), shin__R=(-0.02, 0.4, -1), shin__L=(0.02, 0.45, -1))
c.at(0, lift).at(45, up).write()

# 27 vent emerge, 28 despawn
folded = hunched(2.0).set(spine1=(0, -1, 0.3), spine2=(0, -1, -0.2), spine3=(0, -0.6, -0.8),
                          neck1=(0, 0.2, -1), neck2=(0, 0.6, -0.8), upperarm__R=(0.3, -0.3, -1), upperarm__L=(-0.3, -0.3, -1),
                          thigh__R=(-0.2, -1, -0.1), thigh__L=(0.2, -1, -0.1), shin__R=(-0.1, 1, -0.6), shin__L=(0.1, 1, -0.6))
folded.ground = "feet"
c = Clip("vent_emerge", 64, "unfolds out of a vent, limb by limb")
e1 = folded.copy().set(upperarm__R=(-0.4, -1, -0.3), forearm_a__R=(-0.2, -1, -0.2), hand__R=(0, -1, -0.5))
e2 = e1.copy().set(upperarm__L=(0.4, -1, -0.3), forearm_a__L=(0.2, -1, -0.2), hand__L=(0, -1, -0.5))
e3 = hunched(1.5)
e3.jaw = 30
c.at(0, folded).at(14, e1).at(26, e2).at(36, e2).at(48, e3).at(64, idle()).write()
c = Clip("despawn", 45, "folds down and sinks into the dark")
c.at(0, idle()).at(20, hunched(1.6)).at(38, folded).at(45, folded).write()

print("CLIPS", len(clips), "preview frames", _cursor)
for name, (action, length) in clips.items():
    print("CLIP %-18s %3d frames" % (name, length))

# ---------------------------------------------------------------- preview scene
rig.animation_data.action = PREVIEW
scene.frame_start, scene.frame_end = 1, _cursor


def material(name, base, normal=None, mask=None, color=None):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    if color:
        bsdf.inputs["Base Color"].default_value = color
        bsdf.inputs["Roughness"].default_value = 0.4
        return m
    t = nt.nodes.new("ShaderNodeTexImage")
    t.image = bpy.data.images.load(os.path.join(assets, base))
    nt.links.new(t.outputs["Color"], bsdf.inputs["Base Color"])
    if normal:
        tn = nt.nodes.new("ShaderNodeTexImage")
        tn.image = bpy.data.images.load(os.path.join(assets, normal))
        tn.image.colorspace_settings.name = "Non-Color"
        nm = nt.nodes.new("ShaderNodeNormalMap")
        nt.links.new(tn.outputs["Color"], nm.inputs["Color"])
        nt.links.new(nm.outputs["Normal"], bsdf.inputs["Normal"])
    if mask:
        tm = nt.nodes.new("ShaderNodeTexImage")
        tm.image = bpy.data.images.load(os.path.join(assets, mask))
        tm.image.colorspace_settings.name = "Non-Color"
        inv = nt.nodes.new("ShaderNodeMath")
        inv.operation = "SUBTRACT"
        inv.inputs[0].default_value = 1.0
        nt.links.new(tm.outputs["Alpha"], inv.inputs[1])
        nt.links.new(inv.outputs["Value"], bsdf.inputs["Roughness"])
    return m


mats = {"Skin": material("PrevSkin", "StretchedSkin_albedo.png", "StretchedSkin_normal.png", "StretchedSkin_mask.png"),
        "Suit": material("PrevSuit", "StretchedSuit_albedo.png", None, "StretchedSuit_mask.png"),
        "Teeth": material("PrevTeeth", None, color=(0.74, 0.64, 0.42, 1))}
for o in scene.objects:
    if o.name.startswith("Bake"):
        o.hide_render = o.hide_viewport = True
    elif o.type == "MESH" and o.data.materials:
        key = o.data.materials[0].name
        if key in mats:
            o.data.materials[0] = mats[key]

bpy.ops.mesh.primitive_plane_add(size=12, location=(0, 0, FLOOR))
floor = bpy.context.active_object
floor.data.materials.append(material("PrevFloor", None, color=(0.025, 0.025, 0.03, 1)))
prop_mat = material("PrevProp", None, color=(0.25, 0.27, 0.3, 1))


def box(name, loc, size):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.active_object
    o.name, o.scale = name, size
    o.data.materials.append(prop_mat)
    return o


door = [box("DoorPostL", (DOOR_W / 2 + 0.06, 0, FLOOR + DOOR_H / 2), (0.12, 0.25, DOOR_H)),
        box("DoorPostR", (-DOOR_W / 2 - 0.06, 0, FLOOR + DOOR_H / 2), (0.12, 0.25, DOOR_H)),
        box("DoorLintel", (0, 0, FLOOR + DOOR_H + 0.15), (DOOR_W + 0.24, 0.25, 0.3)),
        box("DoorWallL", (DOOR_W / 2 + 1.2, 0, FLOOR + 1.6), (2.2, 0.2, 3.2)),
        box("DoorWallR", (-DOOR_W / 2 - 1.2, 0, FLOOR + 1.6), (2.2, 0.2, 3.2))]
ceiling = box("Ceiling", (0, 0, CEIL + 0.05), (6, 6, 0.1))

cam = bpy.data.objects.new("PreviewCam", bpy.data.cameras.new("PreviewCam"))
scene.collection.objects.link(cam)
scene.camera = cam
cam.location = (2.1, -2.9, 0.75)
cam.rotation_euler = (Vector((0, 0, 0.42)) - cam.location).to_track_quat("-Z", "Y").to_euler()
cam.data.lens = 30
for name, loc, energy, size in (("Key", (-1.8, -2.6, 3.2), 420, 2), ("Rim", (2.2, 2.6, 2.8), 380, 1.5), ("Fill", (3, -1, 0.3), 60, 3)):
    l = bpy.data.objects.new(name, bpy.data.lights.new(name, "AREA"))
    l.data.energy, l.data.size = energy, size
    l.location = loc
    l.rotation_euler = (Vector((0, 0, 0.6)) - l.location).to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(l)
world = bpy.data.worlds.new("PrevWorld")
world.color = (0.03, 0.03, 0.035)
scene.world = world

font_mat = bpy.data.materials.new("PrevText")  # emissive so labels stay readable whatever the lighting
font_mat.use_nodes = True
fnt = font_mat.node_tree
fnt.nodes.clear()
emit = fnt.nodes.new("ShaderNodeEmission")
emit.inputs["Color"].default_value = (1.0, 0.85, 0.35, 1)
emit.inputs["Strength"].default_value = 3.0
fnt.links.new(emit.outputs["Emission"], fnt.nodes.new("ShaderNodeOutputMaterial").inputs["Surface"])
labels = []
for i, (name, start, end, note) in enumerate(preview_ranges):
    curve = bpy.data.curves.new(f"Label_{name}", "FONT")
    curve.body = f"{i + 1:02d}  {name.replace('_', ' ').upper()}\n{note}"
    curve.size = 0.075
    t = bpy.data.objects.new(f"Label_{name}", curve)
    t.data.materials.append(font_mat)
    scene.collection.objects.link(t)
    t.parent = cam
    t.location = (-0.68, -0.52, -2.0)  # bottom-left, clear of the figure
    for f, hidden in ((1, True), (start, False), (end + GAP - 1, True)):
        t.hide_render = hidden
        t.keyframe_insert("hide_render", frame=f)
    labels.append(t)


def show_only_during(objs, ranges):
    for o in objs:
        o.hide_render = True
        o.keyframe_insert("hide_render", frame=1)
        for start, end in ranges:
            o.hide_render = False
            o.keyframe_insert("hide_render", frame=start)
            o.hide_render = True
            o.keyframe_insert("hide_render", frame=end + GAP - 1)


rng = {n: (s, e) for n, s, e, _ in preview_ranges}
show_only_during(door, [rng["doorway_squeeze"], rng["duck_walk"]])
show_only_during([ceiling], [rng[n] for n in ("climb_up", "ceiling_crawl", "ceiling_hang", "drop_down", "duck_walk", "deep_duck_walk")])

scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = scene.render.resolution_y = 640
scene.render.image_settings.media_type = "VIDEO" if hasattr(scene.render.image_settings, "media_type") else scene.render.image_settings.file_format
try:
    scene.render.image_settings.file_format = "FFMPEG"
except TypeError:
    pass
scene.render.ffmpeg.format = "MPEG4"
scene.render.ffmpeg.codec = "H264"
scene.render.ffmpeg.constant_rate_factor = "MEDIUM"
scene.render.filepath = os.path.join(work, "stretched_animations_preview.mp4")
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work, "09_animations.blend"))
print("ANIM_OK")
if do_render:
    bpy.ops.render.render(animation=True)
    print("PREVIEW_RENDERED", scene.render.filepath)
