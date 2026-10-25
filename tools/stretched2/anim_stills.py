# Render stills at chosen moments of the preview timeline (quick sanity check before the full video).
# Usage: blender -b 09_animations.blend -P anim_stills.py -- <out_dir> name:frame_offset ...
import bpy, os, sys

args = sys.argv[sys.argv.index("--") + 1:]
out = args[0]
scene = bpy.context.scene
scene.render.resolution_x = scene.render.resolution_y = 400
if hasattr(scene.render.image_settings, "media_type"):
    scene.render.image_settings.media_type = "IMAGE"  # Blender 5.x: the preview file is set up for video
scene.render.image_settings.file_format = "PNG"
starts = {}
for o in scene.objects:
    if o.name.startswith("Label_"):
        name = o.name[6:]
        fc = None
        ad = o.animation_data
        # first frame the label becomes visible = clip start
        for f in range(1, scene.frame_end + 1):
            scene.frame_set(f)
            if not o.hide_render:
                starts[name] = f
                break
for spec in args[1:]:
    name, off = spec.rsplit(":", 1)
    f = starts[name] + int(off)
    scene.frame_set(f)
    scene.render.filepath = os.path.join(out, f"still_{name}_{off}.png")
    bpy.ops.render.render(write_still=True)
    print("STILL", name, f)
