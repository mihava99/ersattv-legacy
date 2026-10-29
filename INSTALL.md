# Installing ErsatzTV Legacy

Builds come from the **Build** workflow on the Actions tab. Nothing is published as a release.

1. Open the **Actions** tab, choose **Build**, and open the newest green run.
   (No run yet? Use **Run workflow** on that page.)
2. Under **Artifacts** at the bottom, download the one for your system:

| System | Artifact |
|---|---|
| Windows 10 / 11, 64-bit | `ErsatzTV-Legacy-win-x64` |
| Linux, 64-bit Intel/AMD | `ErsatzTV-Legacy-linux-x64` |
| Linux, 64-bit ARM (Raspberry Pi 4/5 with 64-bit OS, etc.) | `ErsatzTV-Legacy-linux-arm64` |

Everything ErsatzTV needs (ffmpeg included) is in the package. There is nothing else to install.

## Windows

1. Unzip the download and double-click **ErsatzTV-Legacy-Setup-win-x64.exe**.
2. Windows may say *"Windows protected your PC"* because the installer is not code-signed. Click **More info**, then **Run anyway**.
3. Keep the pre-ticked options unless you have a reason not to, then click **Install** and **Finish**.

ErsatzTV starts with an icon in the system tray. Right-click it and choose **Launch Web UI**.
Your channels, settings and logs live in `%LOCALAPPDATA%\ersatztv`, so upgrading (run a newer installer) and uninstalling keep them. The uninstaller asks whether to delete them too.

## Linux (systemd distributions: Ubuntu, Debian, Fedora, Arch, openSUSE, ...)

```bash
unzip ErsatzTV-Legacy-linux-x64.zip -d ersatztv
cd ersatztv
sudo bash install.sh
```

The script installs ErsatzTV to `/opt/ersatztv`, creates an `ersatztv` service user, and starts it as a service at boot.
At the end it prints the address to open, usually `http://<your-ip>:8409`.

| What | Command |
|---|---|
| Upgrade | unzip the newer package, run `sudo bash install.sh` again |
| Logs | `journalctl -u ersatztv -f` |
| Run as your own user (media in your home folder) | `sudo bash install.sh --run-as YOUR_USER_NAME` |
| Different port | `sudo bash install.sh --port 8500` |
| Uninstall, keep your data | `sudo bash /opt/ersatztv/install.sh --uninstall` |
| Uninstall, delete everything | `sudo bash /opt/ersatztv/install.sh --uninstall --purge` |

Notes:
- The service user must be able to **read your media folders**. If a library looks empty, fix the folder permissions or use `--run-as`.
- The script never opens firewall ports. If `ufw` or `firewalld` is on and other devices cannot connect, it prints the one command to allow the port.
- Alpine Linux and other non-systemd systems are not supported by `install.sh`; use the Docker image there.
