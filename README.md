# ErsatzTV

ErsatzTV lets you transform your media library into a personalized, live TV experience - complete with EPG, channel scheduling, and seamless streaming to all your devices. Rediscover your content, your way.

[![contact](https://img.shields.io/badge/contact_us-510b80?style=for-the-badge)](https://ersatztv.org/contact)
[![features](https://img.shields.io/badge/vote_on_features-darkgreen?style=for-the-badge)](https://features.ersatztv.org/)
[![community](https://img.shields.io/badge/join_the_community-blue?style=for-the-badge)](https://discuss.ersatztv.org)

![epg-example](https://ersatztv.org/images/home/epg-example.png)

## How It Works

1. **Install ErsatzTV**: Download and set up the server on your system.
2. **Add Your Media**: Connect your media libraries and collections.
3. **Create Channels**: Design and schedule your own live channels.
4. **Stream Anywhere**: Watch on any device with IPTV and EPG support.

## Key Features

- **Custom channels**: Create and schedule your own live TV channels.
- **IPTV & EPG**: Stream with IPTV and Electronic Program Guide support.
- **Hardware Transcoding**: High-performance streaming with hardware acceleration (NVENC, QSV, VAAPI, AMF, VideoToolbox)
- **Media Server Integration**: Connect Plex, Jellyfin, Emby and more.
- **Music & Subtitles**: Mix music videos and enjoy subtitle support.
- **Open Source**: Free, open, and community-driven project.

## This fork: VISUALXP patch

This is a fork of [ErsatzTV Legacy](https://github.com/ErsatzTV/legacy) with the **VISUALXP patch** on top of upstream
v26.11.3, built for broadcast use. The current version is **v0.2.4**.

### Install

Download the latest build from the [Releases](../../releases) page: a Windows installer (or portable zip) and Linux
packages with ffmpeg included. See [INSTALL.md](INSTALL.md) for step-by-step instructions.

### What it adds

- **50p output**: mixed frame rates are normalized to 50/1, odd scaled sizes are rounded down to even values, and
  `setsar=1` is applied after anamorphic scaling
- **Seven-day schedules**: day-name `wait_until` (for example `"Monday 00:00"`), `skip_if_passed`, and weekday
  fast-forward on reset and after an alternate schedule ends
- **Priority schedules** for special days (birthdays, holidays): `schedules`, `name`, `start_date`, `end_date`, `priority`
- **Text file graphics**: `text_file` shows text from a file that an external app updates (for example a clock written by
  Snaz), and the text re-centers whenever it changes
- **Tag-driven graphics**: show an image only on clips with a given tag, with `opacity_expression: "has_tag(\"TAG\")"`;
  also `fade_in`, `fade_out`, `persistent`, `element_seconds`, `block_remaining_seconds`, the `HoldIndefinitely` motion end
  behavior, and `graphics_off` with `fade_out_seconds`

### Fixes

- Opacity expressions (including `has_tag`) no longer use more memory on every frame, which slowed down long items
- `text_file` keeps the previous text if the new image cannot be built, and an empty file hides the text
- The weekday fast-forward on reset no longer skips the first instruction in a playout without a weekday `wait_until`

### Versions

| Version | Changes |
|---|---|
| v0.2.4 | Install packages bundle the stable ffmpeg and Next from the official release, fixing ffmpeg memory errors on Windows |
| v0.2.3 | Packaging only: Windows installer and portable zip, Linux installer, GitHub Releases. No app changes. |
| v0.2.2 | Rebased on upstream v26.11.3 and the three fixes above |
| v0.2.1 | The features above, ported to the v26.11.3 base |

Full details are in [CHANGELOG.md](CHANGELOG.md).

## Documentation

Documentation is available at [ersatztv.org](https://ersatztv.org/docs/).

## License

This project is inspired by [pseudotv-plex](https://github.com/DEFENDORe/pseudotv) and
the [dizquetv](https://github.com/vexorian/dizquetv) fork and is released under the [zlib license](LICENSE).
