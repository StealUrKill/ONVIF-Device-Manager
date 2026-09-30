# ODM — Local Test Deploy

Work folder: `C:\akhil\git\ONVIF-Device-Manager`

## One-time setup (run once from an elevated shell)

Register two scheduled tasks — one to kill ODM, one to launch it elevated. Both require the creating shell to be elevated (run as Administrator):

```
schtasks /Create /TN "ODM-kill" /TR "taskkill /F /IM odm.exe /T" /SC ONSTART /RL HIGHEST /F
schtasks /Create /TN "ODM-dev" /TR "C:\akhil\git\ONVIF-Device-Manager\build\ODM.exe" /SC ONSTART /RL HIGHEST /F
```

## Deploy steps — run ALL steps every time, no exceptions

### Step 1 — Check the version

`version.json` is the only version source. Local builds show `-dev` and the git hash (for example `3.0.3-dev+a91ee72` in the file properties).
Commit your changes before building, so the git hash identifies the build.

### Step 2 — Kill the running process

ODM.exe holds locks on its DLLs while running — kill it before building or the updated files will not copy.

```
powershell -Command "schtasks /Run /TN 'ODM-kill'; Start-Sleep 2"
```

### Step 3 — Build

```
powershell -Command "& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' 'C:\akhil\git\ONVIF-Device-Manager\build.slnx' /restore /p:Configuration=Release /p:Platform=x64 /v:minimal"
```

Build must complete with 0 errors before continuing.

### Step 4 — Check build\

The build of `build.slnx` fills `build\` (target `OdmStage` in `installer\odm.stage.targets`). It also makes the MSI and the portable exe in `out\`.
Make sure that the timestamp of `build\odm.exe` is new.

### Step 5 — Launch

```
powershell -Command "schtasks /Run /TN 'ODM-dev'"
```

### Step 6 — Verify

Check the window title shows the version with `-dev` (e.g. `v3.0.3-dev`). The file properties of `build\odm.exe` show the git hash.

---

## One-liner (steps 2–5 combined)

```powershell
powershell -Command "schtasks /Run /TN 'ODM-kill'; Start-Sleep 2; & 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' 'C:\akhil\git\ONVIF-Device-Manager\build.slnx' /restore /p:Configuration=Release /p:Platform=x64 /v:minimal; schtasks /Run /TN 'ODM-dev'"
```

The one-liner builds too, so it replaces steps 2 to 5.

---

## Common mistakes to avoid

- **Building only one project** — only `build.slnx` fills `build\`. The scheduled task runs from `build\`.
- **Building uncommitted changes** — the git hash in the version then does not identify the build.
- **Killing after building** — kill BEFORE building, not after, or the copy will fail on locked DLLs.
