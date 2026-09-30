# Installer Pipeline

---

## Overview

`msbuild build.slnx /restore /p:Configuration=Release /p:Platform=x64` makes two packages from the same `out/package/` folder:

| Package | Project | Contents |
|---|---|---|
| `out/odm-<version>-x64.msi` | `installer/odm.installer.wixproj` (WiX v5) | All files in `out/package/` |
| `out/odm-<version>-x64-portable.exe` | `installer/portable/odm.portable.csproj` | `out/package/` as an embedded zip |

CI uploads them as the `odm-installer` and `odm-portable` artifacts. See [ci.md](../ci.md).

---

## Versioning Scheme

`version.json` is the only version source. `Directory.Build.props` reads it, and `Directory.Build.targets` writes the assembly attributes at build time.

| Field | Dev build | Release build (`OdmRelease=true`) |
|-------|-----------|-----------------------------------|
| `AssemblyVersion` / `AssemblyFileVersion` | `3.3.0.<OdmBuildNumber>` | `3.3.0.<OdmBuildNumber>` |
| `AssemblyInformationalVersion` | `3.3.0-dev+<git hash>` | `3.3.0+<git hash>` |
| Window title | `v3.3.0-dev` | `v3.3.0` |
| MSI `ProductVersion` | `3.3.0` | `3.3.0` |
| MSI `ProductName` | `ONVIF Device Manager (dev)` | `ONVIF Device Manager` |
| MSI `ProductCode` | New GUID for each build (WiX) | New GUID for each build (WiX) |

The MSI keeps the UpgradeCode of the old vdproj installer. `MajorUpgrade` with `AllowSameVersionUpgrades` replaces an install of the same or an older version.

---

## `build/` and `out/package/`

`installer/odm.stage.targets` (target `OdmStage`) fills two folders with the same files. It replaces `package.bat`.

- `build/` is the folder to run ODM from. ODM writes its config, logins and logs there, so the build copies only changed files and deletes nothing. If ODM runs, the build stops with "file in use".
- `out/package/` is deleted and made again on each build. The packages use it, so they never get local data.

The files are:

- the top-level files of `odm/odm.ui.app/bin/Release/x64/` without `*.pdb`
- `odm.player.net.dll` from the native player output
- the FFmpeg DLLs from `libs/ffmpeg-n7.1-lgpl-shared/x64/bin/`
- `images/wheel_zoom.cur`, `locales/`, `meta/` and `logs/`

`OdmSkipStage=true` keeps `out/package/` as it is. CI uses it to make the packages again after it signs the exe files in `out/package/`.

**Required files verified by CI** (the build fails if one is missing):

```
build/odm.exe
build/odm.player.net.dll
build/odm.player.host.exe
build/odm.player.media.dll
build/avcodec-61.dll
build/avformat-61.dll
build/avutil-59.dll
build/swscale-8.dll
build/swresample-5.dll
```

---

## MSI

- Per-machine install to `C:\Program Files\Synesis\ONVIF Device Manager`.
- Desktop and Start menu shortcuts.
- Dialogs: welcome, install folder, ready and finish. The finish dialog has a "Launch ONVIF Device Manager" check box (set by default).
- It removes 2.2.x installs (UpgradeCode `{69653681-56F7-48B6-8BB6-69B7278106C6}`) and 3.x installs (the UpgradeCode of the package).
- 2.2.x installed to `Program Files (x86)\Synesis\ONVIF Device Manager`. Its `config\` folder stays there after the upgrade.
- It needs .NET Framework 4.8 (registry `Release` >= 528040).
- The old vdproj MSI installed per-user by default. The new MSI cannot remove a per-user install. Remove it one time in "Installed apps".

---

## Portable exe

- It extracts `out/package/` to `%TEMP%\ONVIF Device Manager\portable\<version>` and starts `odm.exe` from there.
- It gives `odm.exe` the option `--data-dir <folder of the portable exe>`. Config and data go next to the portable exe.
- A new build of the same version replaces the files in the cache. If an older build still runs from the cache, the new build uses a separate folder.
- If files are missing from the cache (for example after disk cleanup), it extracts them again.
