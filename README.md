# Nemesis Protocol v1.1.4 Source

This is the complete source project for Nemesis Protocol on SPT 4.0.13.

## What changed

- Added the fully documented `config.jsonc` file to the source package.
- Changed `NemesisRuntime.cs` to load `config.jsonc` instead of `config.json`.
- Preserved the readable `//` descriptions without strict-JSON editor warnings.
- Added a one-click build script that creates a normal drag-and-drop release ZIP.

## Build

Double-click:

```text
BUILD-DRAG-DROP.cmd
```

The script restores SPT 4.0.13 packages, compiles the DLL, and creates:

```text
release/NemesisProtocol-v1.1.4-SPT-4.0.13-DRAG-DROP.zip
```

The resulting ZIP contains:

```text
SPT/user/mods/ForgeHQ-NemesisProtocol/
```

Extract that ZIP into the folder that contains your `SPT` directory.

## Manual build

From `src/ForgeHQ.NemesisProtocol`:

```powershell
dotnet restore
dotnet build ForgeHQ.NemesisProtocol.csproj -c Release
```

The DLL is produced at:

```text
src/ForgeHQ.NemesisProtocol/bin/Release/ForgeHQ.NemesisProtocol.dll
```
