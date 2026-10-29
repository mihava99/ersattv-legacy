#!/usr/bin/env bash
# ErsatzTV Legacy installer for Linux (systemd).
#
# Download the Linux package from GitHub Actions, unzip it, then in that folder run:
#
#   sudo bash install.sh
#
# Run it again from a newer package to upgrade. Options:
#   --run-as USER    run the service as an existing user instead of a new "ersatztv" user
#                    (handy when your media lives in your home folder or on a mount only you can read;
#                    your existing ErsatzTV settings in ~/.local/share/ersatztv are picked up as they are)
#   --port N         web UI / streaming port (default 8409)
#   --uninstall      remove ErsatzTV (your channels and settings are kept)
#   --purge          with --uninstall: also delete your channels and settings
#   -h, --help       show this help
set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="/opt/ersatztv"
DATA_DIR="/var/lib/ersatztv"
ENV_DIR="/etc/ersatztv"
ENV_FILE="${ENV_DIR}/ersatztv.env"
UNIT_FILE="/etc/systemd/system/ersatztv.service"
DEFAULT_USER="ersatztv"

RUN_AS=""
PORT=""
UNINSTALL=0
PURGE=0

say() { printf '\n==> %s\n' "$*"; }
warn() { printf 'Warning: %s\n' "$*" >&2; }
die() { printf '\nError: %s\n' "$*" >&2; exit 1; }

usage() {
  cat << EOF
ErsatzTV Legacy installer for Linux (systemd)

  sudo bash install.sh [options]

Options:
  --run-as USER    run the service as an existing user instead of a new "ersatztv" user
  --port N         web UI / streaming port (default 8409)
  --uninstall      remove ErsatzTV (your channels and settings are kept)
  --purge          with --uninstall: also delete your channels and settings
  -h, --help       show this help
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --run-as) [ $# -ge 2 ] || die "--run-as needs a user name"; RUN_AS="$2"; shift 2 ;;
    --port) [ $# -ge 2 ] || die "--port needs a number"; PORT="$2"; shift 2 ;;
    --uninstall) UNINSTALL=1; shift ;;
    --purge) PURGE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "Unknown option: $1 (try --help)" ;;
  esac
done

if [ -n "$PORT" ]; then
  [[ "$PORT" =~ ^[0-9]+$ ]] && [ "$PORT" -ge 1 ] && [ "$PORT" -le 65535 ] || die "--port must be a number from 1 to 65535"
fi
if [ "$PURGE" -eq 1 ] && [ "$UNINSTALL" -eq 0 ]; then
  die "--purge only makes sense together with --uninstall"
fi

[ "$(id -u)" -eq 0 ] || die "This needs to run as root. Use:  sudo bash install.sh"

# ---------------------------------------------------------------- uninstall
if [ "$UNINSTALL" -eq 1 ]; then
  say "Removing ErsatzTV"
  if command -v systemctl > /dev/null 2>&1; then
    systemctl disable --now ersatztv > /dev/null 2>&1 || true
  fi
  rm -f "$UNIT_FILE"
  if command -v systemctl > /dev/null 2>&1; then
    systemctl daemon-reload || true
  fi
  rm -rf "$INSTALL_DIR" "${INSTALL_DIR}.old"

  if [ "$PURGE" -eq 1 ]; then
    rm -rf "$DATA_DIR" "$ENV_DIR"
    if id "$DEFAULT_USER" > /dev/null 2>&1; then
      userdel "$DEFAULT_USER" > /dev/null 2>&1 || true
    fi
    echo "ErsatzTV and its data were removed."
    echo "If you ran it with --run-as, your settings are still in that user's ~/.local/share/ersatztv."
  else
    echo "ErsatzTV was removed. Your channels and settings were kept in ${DATA_DIR}"
    echo "(add --purge to delete them too)."
  fi
  exit 0
fi

# ------------------------------------------------------------ preflight
[ -x "${SELF_DIR}/ErsatzTV" ] || [ -f "${SELF_DIR}/ErsatzTV" ] \
  || die "Run this from the unzipped ErsatzTV package (ErsatzTV was not found next to install.sh)."

case "$(uname -m)" in
  x86_64|amd64) ARCH="x64" ;;
  aarch64|arm64) ARCH="arm64" ;;
  *) die "Unsupported CPU type: $(uname -m). Builds exist for 64-bit Intel/AMD and 64-bit ARM." ;;
esac
if [ -f "${SELF_DIR}/PLATFORM" ]; then
  PKG_PLATFORM="$(cat "${SELF_DIR}/PLATFORM")"
  [ "$PKG_PLATFORM" = "linux-${ARCH}" ] \
    || die "This package is for ${PKG_PLATFORM}, but this machine is linux-${ARCH}. Download the matching package."
fi

libc_info="$(ldd --version 2>&1 || true)"
if [ -f /etc/alpine-release ] || [[ "$libc_info" == *musl* ]]; then
  die "This installer does not support Alpine / musl systems. Use the Docker image there."
fi

command -v systemctl > /dev/null 2>&1 && [ -d /run/systemd/system ] \
  || die "This installer sets up a systemd service, but systemd is not running here."

# an existing install keeps its service user unless --run-as says otherwise
if [ -z "$RUN_AS" ] && [ -f "$UNIT_FILE" ]; then
  RUN_AS="$(sed -n '/^User=/{s/^User=//;p;q}' "$UNIT_FILE")"
fi
RUN_AS="${RUN_AS:-$DEFAULT_USER}"
MANAGED_USER=0
if [ "$RUN_AS" = "$DEFAULT_USER" ]; then MANAGED_USER=1; fi
if [ "$MANAGED_USER" -eq 0 ]; then
  id "$RUN_AS" > /dev/null 2>&1 || die "The user '$RUN_AS' does not exist."
fi

# ---------------------------------------------------------------- install
say "Installing to ${INSTALL_DIR}"
if systemctl is-active --quiet ersatztv 2> /dev/null; then
  systemctl stop ersatztv
fi

mkdir -p /opt
STAGE="$(mktemp -d /opt/.ersatztv-install.XXXXXX)"
trap 'rm -rf "$STAGE"' EXIT
cp -a "${SELF_DIR}/." "${STAGE}/"

rm -rf "${INSTALL_DIR}.old"
if [ -d "$INSTALL_DIR" ]; then mv "$INSTALL_DIR" "${INSTALL_DIR}.old"; fi
mv "$STAGE" "$INSTALL_DIR"
chown -R root:root "$INSTALL_DIR"
chmod -R go-w "$INSTALL_DIR"
# zip files from GitHub lose their executable bit
for f in ErsatzTV ErsatzTV.Scanner ersatztv-channel ffmpeg ffprobe; do
  if [ -e "${INSTALL_DIR}/${f}" ]; then chmod 755 "${INSTALL_DIR}/${f}"; fi
done

if [ "$MANAGED_USER" -eq 1 ]; then
  if ! id "$DEFAULT_USER" > /dev/null 2>&1; then
    nologin="$(command -v nologin || true)"
    useradd --system --home-dir "$DATA_DIR" --shell "${nologin:-/bin/false}" "$DEFAULT_USER"
  fi
  # hardware transcoding needs the video / render groups where they exist
  for g in video render; do
    if getent group "$g" > /dev/null 2>&1; then
      usermod -aG "$g" "$DEFAULT_USER"
    fi
  done
  install -d -o "$DEFAULT_USER" -g "$(id -gn "$DEFAULT_USER")" -m 750 "$DATA_DIR" "${DATA_DIR}/config" "${DATA_DIR}/transcode"
fi
RUN_GROUP="$(id -gn "$RUN_AS")"

# settings live in an env file so upgrades never overwrite them
install -d -m 755 "$ENV_DIR"
if [ ! -f "$ENV_FILE" ]; then
  {
    echo "# ErsatzTV service settings. After editing: sudo systemctl restart ersatztv"
    echo "# Other options: https://ersatztv.org/docs/"
    if [ "$MANAGED_USER" -eq 1 ]; then
      echo "ETV_CONFIG_FOLDER=${DATA_DIR}/config"
      echo "ETV_TRANSCODE_FOLDER=${DATA_DIR}/transcode"
    fi
  } > "$ENV_FILE"
fi

set_env() { # set_env KEY VALUE  (VALUE must not contain | or newlines)
  if grep -q "^${1}=" "$ENV_FILE"; then
    sed -i "s|^${1}=.*|${1}=${2}|" "$ENV_FILE"
  else
    printf '%s=%s\n' "$1" "$2" >> "$ENV_FILE"
  fi
}

if [ -n "$PORT" ]; then
  set_env ETV_UI_PORT "$PORT"
  set_env ETV_STREAMING_PORT "$PORT"
fi

# .NET refuses to start without ICU unless told to ignore it
# (output is captured first: grep -q closing the pipe early would trip pipefail)
ldconfig_out="$(ldconfig -p 2> /dev/null || true)"
if [[ "$ldconfig_out" != *libicuuc.so* ]]; then
  if ! grep -q '^DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=' "$ENV_FILE"; then
    warn "ICU libraries were not found; ErsatzTV will run in invariant-globalization mode."
    warn "Install your distribution's libicu package and remove that line from ${ENV_FILE} for full locale support."
    set_env DOTNET_SYSTEM_GLOBALIZATION_INVARIANT 1
  fi
fi

cat > "$UNIT_FILE" << EOF
[Unit]
Description=ErsatzTV Legacy
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${RUN_AS}
Group=${RUN_GROUP}
WorkingDirectory=${INSTALL_DIR}
EnvironmentFile=-${ENV_FILE}
ExecStart=${INSTALL_DIR}/ErsatzTV
Restart=on-failure
RestartSec=5
TimeoutStopSec=30
SyslogIdentifier=ersatztv
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
EOF

# ------------------------------------------------------------------ start
say "Starting ErsatzTV"
systemctl daemon-reload
systemctl enable ersatztv > /dev/null 2>&1 || warn "Could not enable ErsatzTV at boot."
systemctl restart ersatztv

sleep 3
if ! systemctl is-active --quiet ersatztv; then
  journalctl -u ersatztv -n 30 --no-pager 2> /dev/null || true
  die "ErsatzTV did not start. The log lines above should say why."
fi
rm -rf "${INSTALL_DIR}.old"

UI_PORT="$(sed -n '/^ETV_UI_PORT=/{s/^ETV_UI_PORT=//;p;q}' "$ENV_FILE")"
UI_PORT="${UI_PORT:-8409}"

if command -v curl > /dev/null 2>&1; then
  for _ in $(seq 1 45); do
    curl -fsS -o /dev/null "http://127.0.0.1:${UI_PORT}/" 2> /dev/null && break
    sleep 1
  done
fi

HOST_IP="$(hostname -I 2> /dev/null | awk '{print $1}' || true)"
HOST_IP="${HOST_IP:-localhost}"

cat << EOF

ErsatzTV is installed and running.

  Open it:      http://${HOST_IP}:${UI_PORT}
  See the log:  journalctl -u ersatztv -f
  Upgrade:      unzip a newer package and run  sudo bash install.sh  again
  Remove it:    sudo bash ${INSTALL_DIR}/install.sh --uninstall

EOF

if [ "$MANAGED_USER" -eq 1 ]; then
  cat << EOF
ErsatzTV runs as the '${DEFAULT_USER}' user, so that user must be able to read your media folders.
If it cannot, grant it access to the folder, or reinstall to run as yourself:
  sudo bash install.sh --run-as YOUR_USER_NAME

EOF
fi

ufw_out="$(command -v ufw > /dev/null 2>&1 && ufw status 2> /dev/null || true)"
if [[ "$ufw_out" == "Status: active"* ]]; then
  echo "Your firewall (ufw) is on. To reach ErsatzTV from other devices on your network, run:"
  echo "  sudo ufw allow ${UI_PORT}/tcp"
elif command -v firewall-cmd > /dev/null 2>&1 && firewall-cmd --state > /dev/null 2>&1; then
  echo "Your firewall (firewalld) is on. To reach ErsatzTV from other devices on your network, run:"
  echo "  sudo firewall-cmd --permanent --add-port=${UI_PORT}/tcp && sudo firewall-cmd --reload"
fi
