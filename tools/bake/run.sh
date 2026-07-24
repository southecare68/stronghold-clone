#!/usr/bin/env zsh
# run.sh — bake the 3D asset packs into 2D sprites for the game.
#
# The bake tool has to run INSIDE the asset project, because that is where the
# prefabs' mesh and material references resolve (see the header of bake.gd). So
# this copies the two tool files into the asset project, runs Godot with bake.tscn
# as the scene, and cleans up afterwards. The asset project is gitignored and
# disposable, so borrowing it for a moment costs nothing.
#
# Output lands in game/Art/ (an absolute path baked into bake.gd). Re-run any time
# the entity list or camera changes; it overwrites.
#
# Usage:  tools/bake/run.sh
# Needs:  Godot on PATH as $GODOT, or the default below.

set -e

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
ASSET="$REPO/polygon-fantasy-kingdom"
GODOT="${GODOT:-/Users/jamesparker/Downloads/Godot_mono.app/Contents/MacOS/Godot}"

if [ ! -d "$ASSET" ]; then
	echo "asset project not found at $ASSET" >&2
	echo "(it is gitignored — you need the POLYGON Fantasy Kingdom pack unpacked there)" >&2
	exit 1
fi

echo "staging bake tool into the asset project..."
cp "$REPO/tools/bake/bake.gd" "$ASSET/bake.gd"

cleanup() { rm -f "$ASSET/bake.gd" "$ASSET/bake.gd.uid"; }
trap cleanup EXIT

# dotnet must be on PATH or the mono editor build cannot start (hostfxr).
export PATH="$HOME/.dotnet:$PATH"

echo "baking (a window opens, renders every entity, and closes itself)..."
"$GODOT" --path "$ASSET" --position 80,80 --resolution 320x320 --script res://bake.gd

echo "done. sprites are in $REPO/game/Art/"
