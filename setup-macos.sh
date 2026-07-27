#!/usr/bin/env bash
#
# setup-macos.sh — get the game runnable on a fresh Mac in one command.
#
# It does the machine-specific steps that a `git clone` cannot: check the
# toolchain is present, confirm the (git-ignored) art pack has been copied in,
# generate this machine's Godot import cache, and build the C#. Safe to re-run —
# each step is a no-op if already done, so it doubles as "pull latest and rebuild".
#
#   ./setup-macos.sh          # set up, then print how to play
#   ./setup-macos.sh --play   # set up, then launch the game
#   ./setup-macos.sh --play --ai=hard --no-fog   # launch with game flags
#
# What it does NOT do: install the toolchain or copy the assets, because those
# need choices only you can make (where to get Godot, how to move 251 MB between
# your machines). It prints exactly what to do when either is missing.

set -euo pipefail

# --- resolve paths, independent of where this is run from --------------------
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME="$REPO/game"
CSPROJ="$GAME/StrongholdClone.csproj"
ASSETS="$GAME/Assets"

bold() { printf '\033[1m%s\033[0m\n' "$1"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
info() { printf '  \033[36m•\033[0m %s\n' "$1"; }
die()  { printf '\n\033[31m✗ %s\033[0m\n' "$1" >&2; exit 1; }

[ "$(uname)" = "Darwin" ] || die "This is the macOS setup — on Linux see the README."
[ -f "$CSPROJ" ] || die "Can't find $CSPROJ — run this from inside the cloned repo."

# --- separate our flags from game flags to forward after `--play` ------------
PLAY=0
GAME_ARGS=()
for arg in "$@"; do
  case "$arg" in
    --play) PLAY=1 ;;
    *) GAME_ARGS+=("$arg") ;;   # everything else is a game flag (--ai=hard, --no-fog, …)
  esac
done

bold "Stronghold — macOS setup"

# --- 1. .NET SDK -------------------------------------------------------------
# Godot needs dotnet on PATH not just to build but to LAUNCH (it loads .NET
# through hostfxr); without it the editor/runtime hard-crashes with a signal 11
# that reads like a game bug and is not one. So we put it on PATH here.
if command -v dotnet >/dev/null 2>&1; then
  DOTNET_DIR="$(dirname "$(command -v dotnet)")"
elif [ -x "$HOME/.dotnet/dotnet" ]; then
  DOTNET_DIR="$HOME/.dotnet"
else
  die "No .NET SDK found. Install it (no sudo needed):
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
  then re-run this script (add it to your shell too:
    echo 'export PATH=\"\$HOME/.dotnet:\$PATH\"' >> ~/.zshrc )."
fi
export PATH="$DOTNET_DIR:$PATH"
ok ".NET SDK $(dotnet --version) — $DOTNET_DIR"

# --- 2. Godot (the .NET / mono build) ----------------------------------------
# Honour a GODOT override, else try the usual spots. Must be the mono build:
# the plain export cannot run C#.
GODOT="${GODOT:-}"
if [ -z "$GODOT" ]; then
  for cand in \
    "/Applications/Godot_mono.app/Contents/MacOS/Godot" \
    "/Applications/Godot.app/Contents/MacOS/Godot" \
    "$(command -v godot 2>/dev/null || true)"; do
    if [ -n "$cand" ] && [ -x "$cand" ]; then GODOT="$cand"; break; fi
  done
fi
[ -n "$GODOT" ] && [ -x "$GODOT" ] || die "No Godot found. Install the Godot 4.7.x .NET (mono) build for
  macOS in /Applications (AirDrop /Applications/Godot_mono.app from your other
  Mac, or download it from godotengine.org). Or point me at it:
    GODOT=/path/to/Godot ./setup-macos.sh"
# Clear the Gatekeeper quarantine so it launches without a right-click dance.
APP="${GODOT%/Contents/MacOS/*}"   # the .app bundle, or the binary itself if unbundled
xattr -dr com.apple.quarantine "$APP" >/dev/null 2>&1 || true
ok "Godot $("$GODOT" --version 2>/dev/null | head -1) — $GODOT"

# --- 3. Assets (git-ignored, ~251 MB, copied in by hand) ---------------------
if [ ! -d "$ASSETS" ] || [ -z "$(ls -A "$ASSETS" 2>/dev/null)" ]; then
  die "The art pack is missing: $ASSETS is empty.
  It is not in Git (too big), so copy it from a machine that has it — e.g.:
    • AirDrop that machine's  stronghold-clone/game/Assets  folder here, or
    • rsync -a user@<that-mac-ip>:~/stronghold-clone/game/Assets/ \"$ASSETS/\"
  then re-run this script."
fi
ok "Art pack present ($(du -sh "$ASSETS" 2>/dev/null | cut -f1))"

# --- 4. Build the C# ---------------------------------------------------------
info "Building…"
dotnet build "$CSPROJ" -v q --nologo >/dev/null
ok "Built StrongholdClone.dll"

# --- 5. Import cache (per-machine — the thing a clone/rsync can't carry) ------
# Textures etc. are imported into game/.godot/imported/, which is machine-local
# and git-ignored. Without it, models render untextured or missing (the Ubuntu
# "no turrets/walls" symptom). --import builds it headlessly.
info "Importing assets (first time can take a minute)…"
"$GODOT" --headless --path "$GAME" --import >/dev/null 2>&1 || true
ok "Import cache ready ($(du -sh "$GAME/.godot/imported" 2>/dev/null | cut -f1 || echo '—'))"

# --- done --------------------------------------------------------------------
printf '\n'
if [ "$PLAY" = "1" ]; then
  # Reference the array only inside the non-empty branch — macOS bash 3.2 errors
  # on "${arr[@]}"/"${arr[*]}" for an empty array under `set -u`.
  if [ ${#GAME_ARGS[@]} -gt 0 ]; then
    bold "Launching (${GAME_ARGS[*]})…"
    exec "$GODOT" --path "$GAME" -- "${GAME_ARGS[@]}"
  else
    bold "Launching…"
    exec "$GODOT" --path "$GAME"
  fi
else
  bold "Ready. To play:"
  echo "  GODOT=\"$GODOT\""
  echo "  \"\$GODOT\" --path \"$GAME\"                 # solo"
  echo "  \"\$GODOT\" --path \"$GAME\" -- --ai=hard    # vs computer"
  echo "  \"\$GODOT\" --path \"$GAME\" -- --no-fog     # reveal the map"
  echo
  echo "  Or just:  ./setup-macos.sh --play           (add game flags after it)"
fi
