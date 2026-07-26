# Nemesis Protocol Configuration Guide

The configuration file is:

```text
SPT/user/mods/ForgeHQ-NemesisProtocol/config.jsonc
```

SPT 4.0.13 supports `//` comments in JSON configuration files, so the descriptions inside `config.jsonc` are ignored safely when the file is loaded.

Restart `SPT.Server.exe` after changing any setting.

> **Important for this compiled release:** setting names are case-sensitive. Keep the PascalCase names exactly as provided, such as `SpawnChancePercent` and `UseApbsProgression`. Change only the values unless you know the source model.

## Core spawning and progression

| Setting | Type | Suggested range | Effect |
|---|---:|---:|---|
| `Enabled` | Boolean | `true` / `false` | Enables or disables rivalry creation and spawning. |
| `SpawnChancePercent` | Number | `0`–`100` | Chance an eligible active nemesis is scheduled for a PMC raid. |
| `MinimumRaidsBetweenAppearances` | Integer | `0`–`10` | Completed PMC raids required before the nemesis can appear again. |
| `MaximumBonusLevels` | Integer | `0`–`40` | Maximum progression above the rival's original level. |
| `LevelGainPerPlayerKill` | Integer | `0`–`10` | Bonus levels gained when the nemesis kills the player. |
| `LevelGainPerEscape` | Integer | `0`–`5` | Bonus levels gained when the nemesis survives an appearance. |
| `HealthBonusPerRank` | Decimal | `0`–`0.25` | Health increase per rank. `0.08` equals 8% per rank. |
| `RequireMatchingFaction` | Boolean | Usually `true` | Restricts USEC rivals to USEC replacement candidates and BEAR rivals to BEAR candidates. |

## Messages and naming

| Setting | Effect |
|---|---|
| `SendMessages` | Enables all Nemesis Network messages. |
| `SendEscapeMessages` | Enables messages sent when a spawned rival survives. |
| `NameSuffix` | Text appended to the rival's original name. An empty string keeps the original name. |
| `CreationMessages` | Pool used when a new rival is created. |
| `KillMessages` | Pool used after the rival kills the player. |
| `EscapeMessages` | Pool used when the rival survives its appearance. |
| `DeathMessages` | Pool used when the player defeats the rival. |

Message pools can contain any number of entries. Keep every message inside double quotation marks and place a comma after every entry except the last.

## APBS and ORBIT compatibility

| Setting | Effect |
|---|---|
| `Compatibility.UseApbsProgression` | Lets Nemesis provide its requested progression level before APBS selects the final bot level, tier, and inventory. APBS remains the final loadout authority. |
| `Compatibility.PreserveOrbitPmcRole` | Keeps the rival as a normal USEC/BEAR PMC so ORBIT, SAIN, BigBrain, Waypoints, and similar AI systems continue handling it normally. Keep this enabled. |
| `Compatibility.ExactLevelFallbackWithoutApbs` | Uses Nemesis progression through the standard SPT generation path when APBS is not detected. |
| `Compatibility.LogDetection` | Prints detected compatibility and authority information in the server console. |

## Disabled maps

`DisabledMaps` contains internal SPT location IDs where rivalry logic should not run. The defaults are:

```json
[
  "hideout",
  "develop"
]
```

Use internal map IDs, not the display names shown in the game menu.

## Example presets

### Frequent rival

```json
"SpawnChancePercent": 70.0,
"MinimumRaidsBetweenAppearances": 0,
"LevelGainPerPlayerKill": 3,
"LevelGainPerEscape": 1
```

### Rare but dangerous rival

```json
"SpawnChancePercent": 20.0,
"MinimumRaidsBetweenAppearances": 3,
"MaximumBonusLevels": 25,
"LevelGainPerPlayerKill": 5,
"HealthBonusPerRank": 0.12
```

### Identity and persistence only

```json
"MaximumBonusLevels": 0,
"LevelGainPerPlayerKill": 0,
"LevelGainPerEscape": 0,
"HealthBonusPerRank": 0.0
```

## Recovering from an invalid edit

Keep a copy of the original `config.jsonc` before editing. If the server reports a parsing error, restore that copy or extract the default file from the release ZIP again.
