# CI Workflow

Workflow file: `.github/workflows/odm.yml`

Triggers: push and pull request to the `development` branch, and `v*` tags.
Runner: `windows-2022`.

---

## One build command

CI and local builds use the same command:

```
msbuild build.slnx /restore /p:Configuration=Release /p:Platform=x64
```

This command builds:

| Output | What it is |
|--------|------------|
| `odm\odm.ui.app\bin\Release\x64\` | The application build output |
| `build\` | The files that ODM needs to run (the stage folder) |
| `out\odm-<version>-x64.msi` | The MSI installer (WiX v5, `installer\odm.installer.wixproj`) |
| `out\odm-<version>-x64-portable.exe` | The portable exe (`installer\portable\odm.portable.csproj`) |

`installer\odm.stage.targets` fills `build\`. It replaces `package.bat`. The MSI and the portable exe both use `build\`.

---

## Version

`version.json` is the only source of the version:

```json
{"version":"3.0.3"}
```

`Directory.Build.props` reads it. These MSBuild properties change the result:

| Property | Default | Effect |
|----------|---------|--------|
| `OdmRelease` | `false` | When `false`, the version gets `-dev` (`3.0.3-dev`) and the MSI name gets ` (dev)` |
| `OdmBuildNumber` | `0` | The 4th part of the file version (`3.0.3.<n>`). CI sets it to `github.run_number` |
| `OdmSkipStage` | `false` | When `true`, the MSI and the portable exe use `build\` as it is (used after code signing) |

The window title shows `v3.0.3-dev` or `v3.0.3`. The informational version also has the git hash (`3.0.3-dev+a91ee72`).

To make a release:

1. Change `version.json` and commit.
2. Push a tag with the same version, for example `v3.0.4`. CI stops if the tag and `version.json` are different.

---

## Build Order

| Step | What it does |
|------|-------------|
| Checkout | Full history (`fetch-depth: 0`) for the git hash in the version |
| Setup VS Dev Environment | Visual Studio 2022 build tools via `seanmiddleditch/gha-setup-vsdevenv@v4` |
| Install .NET Framework targeting packs | Copies .NET 4.0 and 4.5 reference assemblies via NuGet |
| Check tag against version.json | Tags only. The tag must be `v` + the version in `version.json` |
| Build | `msbuild build.slnx /restore ...` with `OdmBuildNumber`, and `OdmRelease=true` on tags |
| Restore / Build / Run tests | `odm.tests` with `vstest.console.exe`. Integration tests skip via `Assert.Inconclusive` |
| Verify required DLLs present | Checks `build\` and that `out\` has one MSI and one portable exe |
| Azure Login, Sign application exe files | Tags only. Signs the exe files in `build\` |
| Package signed files | Tags only. Makes the MSI and the portable exe again with `OdmSkipStage=true` |
| Sign packages | Tags only. Signs the MSI and the portable exe in `out\` |
| Upload installer / portable exe | Artifacts `odm-installer` and `odm-portable` |
| Create GitHub release | Tags only. Attaches the MSI and the portable exe |

---

## Artifacts

### `odm-installer` (`out/odm-<version>-x64.msi`)

- Per-machine install to `C:\Program Files\Synesis\ONVIF Device Manager`, with desktop and Start menu shortcuts.
- The UpgradeCode is the same as the old vdproj installer. A new MSI replaces an older per-machine install (MajorUpgrade).
- The MSI needs .NET Framework 4.8.

Old per-user installs: the old vdproj MSI installed per-user by default. A per-machine MSI cannot remove a per-user install. Remove such an install one time in "Installed apps" before you install the new MSI.

### `odm-portable` (`out/odm-<version>-x64-portable.exe`)

- One exe. It holds `build\` as an embedded zip.
- On start, it extracts the files to `%TEMP%\ONVIF Device Manager\portable\<version>` and starts `odm.exe` from there. The next start uses the same folder. A new build of the same version replaces the files.
- It starts `odm.exe` with `--data-dir <folder of the portable exe>`. Config and data (`config\`, credentials, trusted certificates) go next to the portable exe.
- If disk cleanup removes files from the cache, the next start extracts them again.

Required files in `build\` (CI stops if one is missing):
- `build/odm.exe`
- `build/odm.player.net.dll`
- `build/odm.player.host.exe`
- `build/odm.player.media.dll`
- `build/avcodec-61.dll`
- `build/avformat-61.dll`
- `build/avutil-59.dll`
- `build/swscale-8.dll`
- `build/swresample-5.dll`

---

## .NET Framework Reference Assemblies

`windows-2022` GitHub Actions runners include .NET 4.6+ reference assemblies but not 4.0 or 4.5. The workflow installs them via NuGet:

```
Microsoft.NETFramework.ReferenceAssemblies.net40
Microsoft.NETFramework.ReferenceAssemblies.net45
```

Copies are installed to the standard reference assembly path (`C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0` etc.) so MSBuild resolves them transparently.

---

## Closed Issues

| Issue | What was fixed |
|-------|---------------|
| #1 | Full solution build ensures native player projects are never skipped |
| #12 | Unified versioning — `version.json` is the only version source |
| #13 | WiX makes a new ProductCode for each build, so each MSI is a major upgrade |
| #15 | `build/` as single staging area for the MSI and the portable exe |
