# FrizzQOL Server Map

One shared map for a server. Explored ground and shared markers update live. Buildings, hoe paths, clear-cuts, and planted groves can be drawn on that map.

Players do not need a cartography table for this. The table still shares explored ground the vanilla way, and it does not copy pins or pin names.

A player without the mod can join. They see a normal map.

Pin names longer than 32 characters stay on the local map only.

## Config

`BepInEx/config/com.frizzqol.servermap.cfg`

World files are stored in `BepInEx/config/ServerMap/`.

| Setting | Default | What it does |
| --- | --- | --- |
| Enabled | true | When false, this install does not send or apply the shared map. |
| DrawFootprints | false | When false, buildings and paths are not scanned or drawn. Shared exploration and markers stay on. |
| ShowBuildings | true | Draw build footprints while DrawFootprints is on. |
| OnlyPlayerBuilt | false | Hide pieces that have no creator. |
| ShowPaths | true | Draw dirt, paving, and cultivated soil. |
| ShowClearedGround | false | Also draw levelled ground with no paint. |
| ShowClearedForest | false | Remove the vanilla forest pattern where the trees are gone. |
| ShowPlantedForest | false | Add forest where enough trees stand. |
| RespectFog | true | Draw the overlay only on explored pixels. |

Color settings are in the same file.

## Multiplayer

Install the dll on the dedicated server and on every client. In a hosted game, the hosting player is the server.

## Install

Install with r2modman or the Thunderstore Mod Manager.

To install by hand, copy `FrizzQOL.ServerMap.dll` into `BepInEx/plugins`.

## Requirements

- Valheim
- [BepInExPack for Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)

## License

[MIT](LICENSE). You can use, copy, change, and share this mod. Keep the copyright notice with any copy.

## Building

1. Copy `Environment.props.example` to `Environment.props`.
2. Set your Valheim and BepInEx folders in that file.
3. From this folder, run:

```
dotnet build ServerMap.sln -c Release
```

The plugin file is `FrizzQOL.ServerMap.dll`, under the project `bin\Release\net48` folder.

`Environment.props` stays on your machine. It is listed in `.gitignore`.

## Source

https://github.com/frizzlebeard/FrizzQOL.ServerMap


## Thunderstore package

A valid upload is a zip whose root contains `icon.png`, `README.md`, and `manifest.json`. `CHANGELOG.md` is optional and is included here. Copy `FrizzQOL.ServerMap.dll` from the Release build into `Package`, then zip the files themselves. Do not zip the `Package` folder. If the files sit inside a folder in the zip, Thunderstore rejects the package.

- `manifest.json`
- `README.md`
- `CHANGELOG.md`
- `icon.png`
- `LICENSE`
- `FrizzQOL.ServerMap.dll`

The dll belongs at the zip root. The mod manager installs those files under `BepInEx/plugins/<Team>-<PackageName>/`.

Before you upload, check the package readme in the [markdown preview](https://thunderstore.io/tools/markdown-preview/) and the manifest in the [manifest validator](https://thunderstore.io/tools/manifest-v1-validator/). The package rules are in [Creating a Package](https://wiki.thunderstore.io/mods/creating-a-package).
