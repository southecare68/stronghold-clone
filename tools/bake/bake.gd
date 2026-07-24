# bake.gd — render the 3D asset packs down to 2D sprites, offline and once.
#
# The game is 2D (Node2D, hand-rolled camera, DrawTexture). The Synty packs are
# 3D meshes. This is the bridge the classic isometric RTS always used: render
# each model once from a fixed 3/4 view into a PNG with alpha, then the game draws
# flat sprites and never touches a mesh at runtime. It is exactly how Stronghold
# and Age of Empires II made their art.
#
# WHY it runs against the ASSET project rather than the game project: the prefabs
# carry a web of uid:// references to their meshes and shared material atlas, all
# resolved WITHIN polygon-fantasy-kingdom/. Running the bake inside the asset
# project lets each prefab load correctly the way its author wired it; the output
# PNGs are written into the game repo by absolute path (see OUT_DIR).
#
# WHY it is a SceneTree MainLoop, not a scene with a script: launched with
#   Godot --path <asset-project> --script res://bake.gd
# so there is no .tscn whose script attachment could silently fail to resolve in
# the mono build (which is exactly what happened first time round — a grey window
# and no output). A MainLoop is the canonical way to run a headless render tool:
# it owns the frame loop directly and builds its own render tree.
#
# The output is committed to game/Art/. A baked sprite is derived, small, and —
# unlike the multi-gigabyte source packs — shippable as part of a game under
# Synty's licence. The source packs stay gitignored; their sprites do not.
#
# This tool cannot touch the simulation: a separate program that only reads meshes
# and writes PNGs. Determinism and 0xB1A7A676 are not in scope here.

extends SceneTree

# Absolute path into the game repo. Godot writes to an OS-absolute path, which is
# what lets a tool running in one project deposit output in another.
const OUT_DIR := "/Users/jamesparker/Desktop/stronghold-clone/game/Art"
const PREFABS := "res://Assets/PolygonFantasyKingdom/Prefabs/"

const ELEVATION_DEG := 52.0     # steep 3/4 view; flatter hides building tops
const SPRITE_PX := 256
const UNIT_FACINGS := 8

# The packs ship no animation clips, but the characters are rigged (one shared
# humanoid Skeleton3D). So the "animation" is authored here: we pose the bones
# directly and bake a frame per pose. Two products per facing —
#   idle : a fixed standing pose. Its whole reason to exist is that the models'
#          rest pose is a T-pose (arms straight out), which looks broken standing
#          still. Lowering the arms to the sides fixes that for free.
#   walk : WALK_FRAMES of a cycle — hips and arms swinging in opposition, a knee
#          bend on the swinging leg. Cheap and stylised, which is exactly right
#          at RTS scale where a unit is a few dozen pixels.
#
# Calibrated against the actual rig (tools/bake/calibrate.gd): the limb PITCH axis
# (forward/back swing) is local X; the arm RAISE axis is local Z. Both shoulders
# and both legs share the same local frame, so the same sign moves them the same
# way — mirroring is done by hand with per-side phase.
const WALK_FRAMES := 6
const ARM_LOWER_DEG := -72.0    # T-pose arms down to the sides (about Z)
const LEG_SWING_DEG := 30.0     # hip pitch amplitude
const ARM_SWING_DEG := 22.0     # shoulder pitch amplitude, counter to the legs
const KNEE_BEND_DEG := 26.0     # lower-leg bend on the leg that is swinging through

const ENTITIES := [
	{ "out": "buildings/keep",      "prefab": "Castle/SM_Bld_Castle_Wall_Tower_L_01",                       "turns": false, "fit": 1.15 },
	{ "out": "buildings/barracks",  "prefab": "Buildings/Preset_Houses/SM_Bld_Preset_House_01_A_Optimized", "turns": false, "fit": 1.15 },
	{ "out": "buildings/wall",      "prefab": "Castle/SM_Bld_Castle_Battlements_01",                        "turns": false, "fit": 1.05 },
	{ "out": "buildings/gatehouse", "prefab": "Castle/SM_Bld_Castle_Wall_Gate_01",                          "turns": false, "fit": 1.10 },
	{ "out": "units/soldier", "prefab": "Characters/SM_Chr_Soldier_Male_01",   "turns": true, "fit": 1.25 },
	{ "out": "units/runner",  "prefab": "Characters/SM_Chr_Soldier_Female_01", "turns": true, "fit": 1.25 },
	{ "out": "units/brute",   "prefab": "Characters/SM_Chr_Rider_01",          "turns": true, "fit": 1.25 },
	{ "out": "units/archer",  "prefab": "Characters/SM_Chr_King_01",           "turns": true, "fit": 1.25 },
]

var _viewport: SubViewport
var _camera: Camera3D
var _pivot: Node3D

func _initialize() -> void:
	print("[bake] starting")
	_build_rig()
	_run()          # a coroutine; it quits the tree when done

func _build_rig() -> void:
	_viewport = SubViewport.new()
	_viewport.size = Vector2i(SPRITE_PX, SPRITE_PX)
	_viewport.transparent_bg = true
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	_viewport.msaa_3d = Viewport.MSAA_4X
	# A MainLoop owns the root Window; the SubViewport hangs under it.
	root.add_child(_viewport)

	_pivot = Node3D.new()
	_viewport.add_child(_pivot)

	_camera = Camera3D.new()
	_camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	_viewport.add_child(_camera)

	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-50, -40, 0)
	sun.light_energy = 1.3
	_viewport.add_child(sun)

	var fill := DirectionalLight3D.new()
	fill.rotation_degrees = Vector3(-20, 140, 0)
	fill.light_energy = 0.5
	_viewport.add_child(fill)

	var we := WorldEnvironment.new()
	var e := Environment.new()
	e.background_mode = Environment.BG_CLEAR_COLOR
	e.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	e.ambient_light_color = Color(0.5, 0.5, 0.55)
	e.ambient_light_energy = 1.0
	we.environment = e
	_viewport.add_child(we)

func _run() -> void:
	# Let the render tree finish entering before the first frame is framed —
	# without this the first entity's camera look_at fires before its nodes are
	# in-tree, and it bakes blank (which is exactly what happened to the keep).
	await process_frame
	await process_frame
	for entity in ENTITIES:
		await _bake(entity)
	print("[bake] done — wrote sprites to ", OUT_DIR)
	quit()

func _bake(entity: Dictionary) -> void:
	for c in _pivot.get_children():
		c.free()

	var path: String = PREFABS + entity["prefab"] + ".tscn"
	var packed := load(path) as PackedScene
	if packed == null:
		push_error("[bake] could not load " + path)
		return
	var model := packed.instantiate()
	_pivot.add_child(model)

	var aabb := _aabb_of(model)
	_pivot.position = -aabb.get_center()
	var radius := aabb.size.length() * 0.5 * float(entity["fit"])
	_camera.size = radius * 2.0

	var el := deg_to_rad(ELEVATION_DEG)
	var dir := Vector3(cos(el) * 0.707, sin(el), cos(el) * 0.707)
	_camera.position = dir * (radius * 4.0 + 5.0)
	_camera.look_at(Vector3.ZERO, Vector3.UP)
	_camera.near = 0.01
	_camera.far = radius * 8.0 + 20.0

	if not entity["turns"]:
		# A building: one 3/4 view, no rotation, no animation.
		await _grab_named(entity["out"] + ".png")
		return

	# A unit: eight facings, and for each an idle pose plus a walk cycle.
	var skel := _find_skeleton(model)
	for i in range(UNIT_FACINGS):
		_pivot.rotation_degrees.y = -360.0 / float(UNIT_FACINGS) * float(i)

		if skel == null:
			# No rig (should not happen for these characters) — bake the bind pose.
			await _grab_named("%s_%d.png" % [entity["out"], i])
			continue

		_pose_idle(skel)
		await _grab_named("%s_%d_idle.png" % [entity["out"], i])

		for f in range(WALK_FRAMES):
			_pose_walk(skel, TAU * float(f) / float(WALK_FRAMES))
			await _grab_named("%s_%d_walk%d.png" % [entity["out"], i, f])

func _grab_named(rel: String) -> void:
	# Let the pose and transform apply and the GPU actually draw before reading
	# pixels — without this a frame bakes the previous pose, or blank.
	await process_frame
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw

	var img := _viewport.get_texture().get_image()
	var file := OUT_DIR + "/" + rel
	DirAccess.make_dir_recursive_absolute(file.get_base_dir())
	img.save_png(file)
	print("[bake] ", file)

# ---- posing ------------------------------------------------------------------
# All poses are expressed as a delta from the bone's REST rotation, so they layer
# cleanly and a bone left untouched simply stays at rest.

func _find_skeleton(n: Node) -> Skeleton3D:
	if n is Skeleton3D:
		return n
	for c in n.get_children():
		var s := _find_skeleton(c)
		if s != null:
			return s
	return null

func _reset(skel: Skeleton3D) -> void:
	for i in range(skel.get_bone_count()):
		skel.set_bone_pose_rotation(i, skel.get_bone_rest(i).basis.get_rotation_quaternion())

# Set a bone to its rest rotation composed with a delta (given in the bone's rest
# frame). A missing bone is ignored, so the tool survives a differently-named rig.
func _pose(skel: Skeleton3D, bone_name: String, delta: Quaternion) -> void:
	var b := skel.find_bone(bone_name)
	if b < 0:
		return
	var rest_q := skel.get_bone_rest(b).basis.get_rotation_quaternion()
	skel.set_bone_pose_rotation(b, rest_q * delta)

func _axis(a: Vector3, deg: float) -> Quaternion:
	return Quaternion(a.normalized(), deg_to_rad(deg))

const X := Vector3(1, 0, 0)
const Z := Vector3(0, 0, 1)

# Standing: just bring the arms down out of the T-pose, with a touch of forward
# so they read as relaxed rather than pinned to the sides.
func _pose_idle(skel: Skeleton3D) -> void:
	_reset(skel)
	var lower := _axis(Z, ARM_LOWER_DEG) * _axis(X, 6.0)
	_pose(skel, "Shoulder_L", lower)
	_pose(skel, "Shoulder_R", lower)

# One instant of the walk cycle. phase runs 0..TAU across WALK_FRAMES. Left and
# right limbs are half a cycle apart; arms counter the legs.
func _pose_walk(skel: Skeleton3D, phase: float) -> void:
	_reset(skel)

	var leg := sin(phase)
	var leg_opp := sin(phase + PI)

	# Hips pitch forward/back (about X).
	_pose(skel, "UpperLeg_L", _axis(X, LEG_SWING_DEG * leg))
	_pose(skel, "UpperLeg_R", _axis(X, LEG_SWING_DEG * leg_opp))

	# Knee bends as the leg swings THROUGH (moving forward = positive velocity,
	# cos(phase) > 0), which is what makes the two mid-stride frames distinct
	# instead of identical passing poses.
	_pose(skel, "LowerLeg_L", _axis(X, -KNEE_BEND_DEG * max(0.0, cos(phase))))
	_pose(skel, "LowerLeg_R", _axis(X, -KNEE_BEND_DEG * max(0.0, cos(phase + PI))))

	# Arms: lowered (the idle base) plus a pitch swing opposite the same-side leg.
	var base := _axis(Z, ARM_LOWER_DEG)
	_pose(skel, "Shoulder_L", base * _axis(X, ARM_SWING_DEG * leg_opp))
	_pose(skel, "Shoulder_R", base * _axis(X, ARM_SWING_DEG * leg))

func _aabb_of(root_node: Node) -> AABB:
	var acc := AABB()
	var started := false
	for mi in _all_mesh_instances(root_node):
		var box: AABB = mi.get_aabb()
		var xform: Transform3D = _pivot.global_transform.affine_inverse() * mi.global_transform
		box = xform * box
		if not started:
			acc = box
			started = true
		else:
			acc = acc.merge(box)
	if not started:
		return AABB(Vector3(-1, -1, -1), Vector3(2, 2, 2))
	return acc

func _all_mesh_instances(node: Node) -> Array:
	var out := []
	if node is MeshInstance3D:
		out.append(node)
	for c in node.get_children():
		out.append_array(_all_mesh_instances(c))
	return out
