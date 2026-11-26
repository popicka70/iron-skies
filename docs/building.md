# Building and packaging

## Development build

```bash
dotnet test
dotnet build game/IronSkies.Game.csproj
godot --path game
```

Requirements: .NET SDK 10, Godot 4.7.2 .NET build. The simulation library builds without
Godot.

## Release exports

Install the **mono export templates** for 4.7.2 once:

```bash
curl -L -o /tmp/godot_templates.tpz \
  https://github.com/godotengine/godot/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_export_templates.tpz
unzip -q /tmp/godot_templates.tpz -d /tmp/godot_templates
mkdir -p ~/.local/share/godot/export_templates/4.7.2.stable.mono
cp -r /tmp/godot_templates/templates/* ~/.local/share/godot/export_templates/4.7.2.stable.mono/
```

Then:

```bash
./tools/export_builds.sh
```

Outputs:

```
build/linux/iron-skies.x86_64
build/linux/data_IronSkies.Game_linuxbsd_x86_64/
build/windows/iron-skies.exe
build/windows/iron-skies.pck
build/windows/data_IronSkies.Game_windows_x86_64/
build/server/iron-skies-server.x86_64
build/server/data_IronSkies.Game_linuxbsd_x86_64/
```

The dedicated server package and container are described in
[dedicated-server.md](dedicated-server.md).

Linux and the dedicated server embed the PCK. Windows uses a sidecar
`iron-skies.pck` because Godot refuses to embed a PCK of 4 GiB or more; keep the `.exe`
and `.pck` together. The .NET assemblies live in the `data_*` directory next to the
executable, so ship that too. With all generated terrain layers installed the packages
are ~5 GB each; a clean clone without generated bundles is much smaller.

`export_presets.cfg` lists the plain terrain and aircraft files explicitly, because
`.r16`, `.json`, `.bin` and `.csv` are not imported resources. The orthophoto and
landcover are stored as raw `.jpg.bin` / `.png.bin` buffers because Godot does not ship
imported image originals in the PCK; the runtime decodes them with `LoadJpgFromBuffer` /
`LoadPngFromBuffer`.

## CI

`.github/workflows/export.yml` is a manual workflow that installs the templates (cached),
runs both exports and uploads the artifacts. Normal CI does not download the 1.2 GB
template pack; it builds and smoke-tests the project instead.

## Notes

- Windows export from Linux works with the standard templates; resource editing is
  disabled so no Wine/rcedit is required.
- The Linux package was verified locally: it starts headless, loads terrain/aircraft/OSM,
  and renders at ~159 FPS. The Windows package builds but has not been runtime-tested on
  Windows from this Linux environment.
- The exports are unsigned. Code signing is a later distribution concern.
