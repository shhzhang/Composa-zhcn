#!/usr/bin/env bash
# Builds the Windows downloads for one architecture from a single published tree: a portable zip, an
# MSI, and an Inno Setup installer.
#
#   usage: windows.sh [rid] [output-dir] [--no-installer] [--no-msi]     default: win-x64, dist/
#
# Runs in Git Bash on Windows, and on Linux as well, where .NET cross-publishes the same build. Both
# installers need a compiler that may not be there — Inno Setup's only runs on Windows, and WiX is a
# separate `dotnet tool install --global wix` — so a run that cannot build one passes --no-installer
# or --no-msi. Without those flags a missing compiler is an error rather than a quietly smaller
# release, which is the whole point of saying so out loud.
#
# Neither download is single-file, unlike the Linux tarball. With the native libraries bundled, a
# single-file build unpacks some 40 MB into %TEMP% on its first launch, which is slow and exactly the
# behaviour that antivirus software distrusts; a folder in a zip is what Windows users expect anyway.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

RID="${1:-win-x64}"
OUT="$(ensure_dir "${2:-$BUILD}")"
shift 2 2>/dev/null || true   # anything left is an option, not a path

INSTALLER=true
MSI=true
for option in "$@"; do
  case "$option" in
    --no-installer) INSTALLER=false ;;
    --no-msi) MSI=false ;;
    *) echo "windows.sh: unknown option: $option" >&2; exit 2 ;;
  esac
done

# Both compilers are Windows programs that need Windows paths. Git Bash would otherwise rewrite
# anything that looks like a path, including the /D switches themselves, so its conversion is
# switched off at every call site and the paths are converted here instead.
winpath() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else echo "$1"; fi; }

VERSION="$(app_version)"
ARCH="$(inno_arch "$RID")"
NAME="$APP-$VERSION-$RID"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
TREE="$WORK/$NAME"

echo "Building Composa $VERSION for $RID"

# The subject detection models travel with the build; the one not in git is fetched and verified first.
"$ROOT/scripts/models/fetch.sh"

# Nothing manages a Windows install, so this build announces new releases itself.
dotnet publish "$ROOT/src/Composa.App" -c Release -r "$RID" --self-contained true \
  -p:DebugType=none -p:UpdateChannel=github -o "$TREE"
cp "$ROOT/LICENSE" "$ROOT/README.md" "$ROOT/CHANGELOG.md" "$TREE/"

# Bundling ImageMagick redistributes LGPL libraries, and the notices are what makes that allowed.
# The project file adds them; this makes sure no change there can ship a build without them.
for notice in THIRD-PARTY-NOTICES.txt ImageMagick-NOTICE.txt models/u2netp.onnx models/modnet.onnx models/realesr-general-x4v3.onnx; do
  [ -f "$TREE/$notice" ] || { echo "windows.sh: $notice is missing from the published build." >&2; exit 1; }
done

echo "==> zip"
rm -f "$OUT/$NAME.zip"
if command -v 7z >/dev/null; then
  (cd "$WORK" && 7z a -tzip -mx=9 "$OUT/$NAME.zip" "$NAME" >/dev/null)
elif command -v zip >/dev/null; then
  (cd "$WORK" && zip -qr9 "$OUT/$NAME.zip" "$NAME")
else
  echo "windows.sh: neither 7z nor zip is available to build the portable zip." >&2; exit 1
fi
echo "built $OUT/$NAME.zip"

if $MSI; then
  echo "==> msi"
  WIX="$(command -v wix || true)"
  # A global dotnet tool lands here, and dotnet only puts it on PATH in a shell it started itself.
  [ -z "$WIX" ] && [ -x "$HOME/.dotnet/tools/wix" ] && WIX="$HOME/.dotnet/tools/wix"
  [ -n "$WIX" ] || {
    echo "windows.sh: the WiX toolset (wix) was not found. Install it with 'dotnet tool install --global wix' and 'wix extension add -g WixToolset.UI.wixext', or pass --no-msi." >&2
    exit 1
  }

  rm -f "$OUT/$NAME.msi"
  # Anything WiX writes from here on is this build's; the marker is what tells it apart from a package
  # an earlier run left in the same directory.
  MSI_MARKER="$WORK/.msi-marker"
  touch "$MSI_MARKER"

  # WinUI_FeatureTree's language, the licence page and the desktop shortcut all live in the .wxs; the
  # version and the product family are the only things this build decides.
  MSYS2_ARG_CONV_EXCL='*' "$WIX" build \
    -arch "$(wix_arch "$RID")" \
    -culture zh-CN \
    -ext WixToolset.UI.wixext \
    -d "PublishDir=$(winpath "$TREE")" \
    -d "DisplayVersion=$VERSION" \
    -d "ProductVersion=$(msi_product_version "$VERSION")" \
    -d "UpgradeCode=$(msi_upgrade_code "$RID")" \
    -out "$(winpath "$OUT/$NAME.msi")" \
    "$(winpath "$ROOT/packaging/windows/composa-msi.wxs")"

  # WiX creates the package under its 8.3 short name on some machines — COMPOS~1.MSI for the long name
  # asked for above — while the release job and the checksums file look for the long one. Renaming it
  # here covers both, and does nothing at all where WiX already wrote the name it was given.
  if [ ! -f "$OUT/$NAME.msi" ]; then
    short="$(find "$OUT" -maxdepth 1 \( -name '*.msi' -o -name '*.MSI' \) -newer "$MSI_MARKER" -print -quit)"
    [ -n "$short" ] && mv "$short" "$OUT/$NAME.msi"
  fi
  [ -f "$OUT/$NAME.msi" ] || { echo "windows.sh: wix reported success but $OUT/$NAME.msi is not there." >&2; exit 1; }

  # WiX writes a .wixpdb beside every package. It is a debugging aid for a build, and the release job
  # uploads everything it finds in the output directory, so it must not be left there.
  rm -f "$OUT/$NAME.wixpdb"
  echo "built $OUT/$NAME.msi"
fi

$INSTALLER || exit 0

echo "==> installer"
ISCC="$(command -v iscc || command -v ISCC.exe || true)"
for candidate in "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" "/c/Program Files/Inno Setup 6/ISCC.exe"; do
  [ -z "$ISCC" ] && [ -f "$candidate" ] && ISCC="$candidate"
done
[ -n "$ISCC" ] || { echo "windows.sh: Inno Setup 6 (ISCC.exe) was not found. Install it, or pass --no-installer." >&2; exit 1; }

MSYS2_ARG_CONV_EXCL='*' "$ISCC" /Q \
  "/DAppVersion=$VERSION" \
  "/DFileVersion=$(win_file_version "$VERSION")" \
  "/DArchitecture=$ARCH" \
  "/DSourceDir=$(winpath "$TREE")" \
  "/DOutputDir=$(winpath "$OUT")" \
  "/DOutputName=$NAME-setup" \
  "$(winpath "$ROOT/packaging/windows/composa.iss")"
echo "built $OUT/$NAME-setup.exe"
