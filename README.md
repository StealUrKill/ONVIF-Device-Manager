[![ODM](https://github.com/StealUrKill/ONVIF-Device-Manager/actions/workflows/odm.yml/badge.svg?branch=development)](https://github.com/StealUrKill/ONVIF-Device-Manager/actions/workflows/odm.yml)
[![Latest release](https://img.shields.io/github/v/release/StealUrKill/ONVIF-Device-Manager?display_name=tag&label=release)](https://github.com/StealUrKill/ONVIF-Device-Manager/releases/latest)

# ONVIF Device Manager

A Windows program to find, configure and watch ONVIF cameras, encoders and decoders.
It is a fork of the ONVIF Device Manager of synesis.ru ([mirror of the SourceForge project](http://sourceforge.net/projects/onvifdm/)).

# Download

**[Get the latest release](https://github.com/StealUrKill/ONVIF-Device-Manager/releases/latest)**.

Each release has two files:

| File | Use |
|---|---|
| `odm-<version>-x64.msi` | Installer. It replaces an earlier install, also a 2.2.x install, and can start ODM when it finishes. |
| `odm-<version>-x64-portable.exe` | One file. It needs no install, and it keeps its settings in the folder of the exe. |

Requirements: Windows x64 and .NET Framework 4.8 (Windows 11 and current Windows 10 have it).

# What ODM can do

- Find devices with WS-Discovery, and **scan other subnets** (for example a camera VLAN) with a subnet mask list.
- Add a device with the right-click menu of the device list, and **choose one saved account for each device**, so that ODM does not try many passwords and lock the device.
- Keep many logins in **Manage Credentials**, with a notes column. The logins are encrypted for the Windows user.
- Live video with H.264 and H.265, Media1 and Media2 streaming, and HTTPS cameras with certificate trust on first use.
- Camera settings: identification, time, network, users, certificates, relays, imaging, PTZ, profiles, analytics and rules, metadata, events.
- Newer ONVIF features: on-screen display, privacy masks, video source modes and rotation, IP address filter, and **recordings** with clip search, playback and MP4 download.
- **Dark theme.** It follows the Windows setting by default. Change it in Settings.
- Languages: English, Russian, Traditional Chinese and Spanish (thanks [wolfbelmi88](https://sourceforge.net/u/wolfbelmi88/profile)).

# Build from source

You need Visual Studio 2022 (Community is enough) with these parts:

- .NET desktop development
- F# desktop language support
- Desktop development with C++ (the video player uses C++/CLI)

Then run one command:

```
msbuild build.slnx /restore /p:Configuration=Release /p:Platform=x64
```

The command builds everything and fills two folders:

- `build\` is the folder to run ODM from. ODM keeps its settings and logs there, and the build does not delete them. Close ODM before you build again, or the copy fails on files that are in use.
- `out\` has the MSI and the portable exe, made from a clean copy of the same files.

`version.json` is the only place for the version number. A local build shows `-dev` in the title.

More in [docs/ci.md](docs/ci.md), [docs/features/installer.md](docs/features/installer.md), [docs/credentials.md](docs/credentials.md) and [docs/features/https-tls.md](docs/features/https-tls.md).

# Make a release

1. Change the number in `version.json`.
2. Commit and push to `development`, and wait for the workflow to pass.
3. Push a tag that matches the number, for example `v3.2.1`.

The workflow builds the tag and publishes the release with the MSI and the portable exe. The **Get the latest release** link then shows the new version.

# License

GNU General Public License version 2.0 (GPLv2)

# Disclaimer

The credit for this open source project remains with the original authors (synesis.ru).
This fork descends from the fork of [Apra Labs](https://github.com/Apra-Labs/ONVIF-Device-Manager).
It attempts to keep the project alive and to make sure that new builds can be built and debugged easily.
