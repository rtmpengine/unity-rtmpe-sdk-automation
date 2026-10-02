#!/usr/bin/env bash
#
# Reads the Roslyn (csc) compiler version bundled in the Unity editor and checks
# it against the analyzer's pinned Microsoft.CodeAnalysis.CSharp version. The
# verdict is printed and appended to
# clients/unity-sdk/Tooling/COMPILER_COMPATIBILITY.md.
#
# Run on a machine with the FLOOR editor (Unity 2022.3 LTS) installed — that is
# the only host whose Roslyn can establish the pin. A newer editor passes for any
# pin at or below its own Roslyn and so proves nothing about the floor; the
# recorded verdict names the csc.dll it read so which editor answered is visible.
# macOS / Linux / Git-Bash; for native Windows PowerShell use check-roslyn-version.ps1.
#
#   bash scripts/check-roslyn-version.sh
#   RTMPE_UNITY_EDITOR_PATH=/path/to/Editor bash scripts/check-roslyn-version.sh
#   bash scripts/check-roslyn-version.sh /explicit/path/to/csc.dll
set -euo pipefail

PINNED="${RTMPE_ANALYZER_PIN:-4.3.0}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
md="$repo_root/clients/unity-sdk/Tooling/COMPILER_COMPATIBILITY.md"

# 1. Locate the bundled Roslyn csc.dll — explicit arg, env hint, or OS defaults.
csc="${1:-}"
if [ -z "$csc" ]; then
  roots=()
  [ -n "${RTMPE_UNITY_EDITOR_PATH:-}" ] && roots+=("$RTMPE_UNITY_EDITOR_PATH")
  # Floor editor first. An unmatched glob expands to its own literal, which the
  # -d test below drops, so an absent LTS simply falls through to the next root.
  case "$(uname -s)" in
    Darwin)
      roots+=(/Applications/Unity/Hub/Editor/2022.3.*/Unity.app/Contents)
      roots+=("/Applications/Unity/Hub/Editor/6000.3.13f1/Unity.app/Contents" "/Applications/Unity/Hub/Editor")
      ;;
    Linux)
      roots+=("$HOME"/Unity/Hub/Editor/2022.3.*/Editor)
      roots+=("$HOME/Unity/Hub/Editor/6000.3.13f1/Editor" "$HOME/Unity/Hub/Editor")
      ;;
    *)
      roots+=("/c/Program Files/Unity/Hub/Editor"/2022.3.*/Editor)
      roots+=("/c/Program Files/Unity/Hub/Editor/6000.3.13f1/Editor" "/c/Program Files/Unity/Hub/Editor")
      ;;
  esac
  for r in "${roots[@]}"; do
    [ -d "$r" ] || continue
    found="$(find "$r" -type f -name csc.dll -path '*Roslyn*' 2>/dev/null | head -1 || true)"
    [ -n "$found" ] && { csc="$found"; break; }
  done
fi
if [ -z "$csc" ] || [ ! -f "$csc" ]; then
  echo "ERROR: could not locate a Roslyn csc.dll. Pass the path explicitly:" >&2
  echo "  bash scripts/check-roslyn-version.sh /path/to/Editor/Data/Tools/Roslyn/csc.dll" >&2
  exit 3
fi

# 2. Read the compiler version — run csc (authoritative), else a Mono disassembler.
raw=""
if command -v dotnet >/dev/null 2>&1; then
  raw="$(dotnet "$csc" -version 2>/dev/null | head -1 || true)"
fi
if [ -z "$raw" ] && command -v monodis >/dev/null 2>&1; then
  raw="$(monodis --assembly "$csc" 2>/dev/null | awk -F': ' '/Version:/{print $2; exit}')"
fi
if [ -z "$raw" ]; then
  echo "ERROR: found csc.dll but no tool to read its version (need dotnet or monodis)." >&2
  echo "  csc.dll: $csc" >&2
  exit 4
fi

# 3. Reduce to a comparable major.minor.patch.
ver="$(printf '%s' "$raw" | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1 || true)"
[ -n "$ver" ] || { echo "ERROR: unrecognized version string: $raw" >&2; exit 5; }

# 4. PINNED <= host ? — version-aware compare (4.11 > 4.8, not lexical).
lowest="$(printf '%s\n%s\n' "$PINNED" "$ver" | sort -V | head -1)"
[ "$lowest" = "$PINNED" ] && ok="YES" || ok="NO"

# 5. Report.
ts="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo ""
echo "  Unity csc.dll : $csc"
echo "  Host Roslyn   : $raw  (parsed $ver)"
echo "  Analyzer pin  : $PINNED"
if [ "$ok" = "YES" ]; then
  echo "  Verdict       : YES — $PINNED <= host ($ver). The pin is safe; record and proceed."
else
  echo "  Verdict       : NO — host ($ver) is BELOW $PINNED. Lower the analyzer pin to <= $ver and rebuild."
fi
echo ""

# 6. Append the verdict to the canonical record.
{
  printf '\n<!-- auto-recorded by scripts/check-roslyn-version.sh -->\n'
  printf -- '- **%s** — host Roslyn `%s` (`%s`); pinned `%s`; `%s <= host` → **%s**.\n' \
    "$ts" "$ver" "$csc" "$PINNED" "$PINNED" "$ok"
} >> "$md"
echo "Recorded in: $md"
