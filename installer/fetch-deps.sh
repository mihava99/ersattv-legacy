#!/usr/bin/env bash
# Downloads the pieces bundled next to ErsatzTV: ffmpeg, ersatztv-channel (and the tray launcher on Windows).
# Plain public downloads, no login or token. Versions come from installer/pins.env.
#
#   fetch-deps.sh <linux-x64|linux-arm64|win-x64> <output folder>
set -euo pipefail

platform="${1:?usage: fetch-deps.sh <linux-x64|linux-arm64|win-x64> <output folder>}"
out="${2:?usage: fetch-deps.sh <linux-x64|linux-arm64|win-x64> <output folder>}"
root="$(cd "$(dirname "$0")/.." && pwd)"
launcher_tag="v1.0.0"

case "$platform" in
  linux-x64) ffmpeg_target=linux64; next_target=linux-x64 ;;
  linux-arm64) ffmpeg_target=linuxarm64; next_target=linux-arm64 ;;
  win-x64) ffmpeg_target=win64; next_target=windows-x64 ;;
  *) echo "unknown platform: $platform" >&2; exit 1 ;;
esac

tmp="$(mktemp -d fetch-deps.XXXXXX)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$out"

download() { curl -fsSL --retry 3 --retry-delay 2 -o "$2" "$1"; }

extract() { # extract <archive> <folder>
  case "$1" in
    *.zip) 7z x -bso0 -o"$2" "$1" ;;
    *.tar.xz) tar xJf "$1" -C "$2" ;;
    *) tar xzf "$1" -C "$2" ;;
  esac
}

# ---- ffmpeg (checksum list is pinned in installer/pins.env)
# shellcheck source=pins.env
source "$root/installer/pins.env"
tag="$FFMPEG_TAG" repo="$FFMPEG_REPO" pinned_sums="$FFMPEG_SHA256SUMS"
version="${tag%%-*}"
if [[ "$tag" =~ -([0-9a-f]{8})-develop$ ]]; then extra="etv.dev.${BASH_REMATCH[1]}"; else extra="etv.${tag#*-}"; fi
base="ffmpeg-n${version}-${extra}-${ffmpeg_target}-gpl-${version%.*}"
case "$ffmpeg_target" in win*) asset="${base}.zip" ;; *) asset="${base}.tar.xz" ;; esac

url="https://github.com/${repo}/releases/download/${tag}"
download "${url}/SHA256SUMS" "$tmp/SHA256SUMS"
download "${url}/${asset}" "$tmp/${asset}"
[[ "$(sha256sum "$tmp/SHA256SUMS" | cut -d' ' -f1)" == "$pinned_sums" ]] || { echo "ffmpeg SHA256SUMS does not match the pin" >&2; exit 1; }
(cd "$tmp" && grep -F -- " ${asset}" SHA256SUMS | sha256sum -c -)
mkdir "$tmp/ffmpeg"
extract "$tmp/${asset}" "$tmp/ffmpeg"
ffmpeg_dir="$tmp/ffmpeg/${base}"
if [[ "$platform" == win-* ]]; then
  cp "$ffmpeg_dir"/bin/*.exe "$out/"
else
  cp "$ffmpeg_dir/bin/ffmpeg" "$ffmpeg_dir/bin/ffprobe" "$out/"
fi
cp "$ffmpeg_dir/LICENSE.txt" "$out/ffmpeg-LICENSE.txt"

# ---- ersatztv-channel
next_tag="$NEXT_TAG" next_repo="$NEXT_REPO"
next_base="ersatztv-next-${next_tag}-${next_target}"
case "$next_target" in windows-*) next_asset="${next_base}.zip" ;; *) next_asset="${next_base}.tar.gz" ;; esac
download "https://github.com/${next_repo}/releases/download/${next_tag}/${next_asset}" "$tmp/${next_asset}"
mkdir "$tmp/next"
extract "$tmp/${next_asset}" "$tmp/next"
if [[ "$platform" == win-* ]]; then
  cp "$tmp/next/${next_base}/ersatztv-channel.exe" "$tmp/next/${next_base}/libvpl.dll" "$tmp/next/${next_base}/libvpl-LICENSE.txt" "$out/"
  download "https://github.com/ErsatzTV/ErsatzTV-Windows/releases/download/${launcher_tag}/ErsatzTV-Windows.exe" "$out/ErsatzTV-Windows.exe"
else
  cp "$tmp/next/${next_base}/ersatztv-channel" "$out/"
fi

echo "fetched ffmpeg ${tag} and ersatztv-channel ${next_tag} for ${platform} into ${out}"
