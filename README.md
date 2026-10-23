# CS2 Practice Mode (free Refrag/Blitz style practice)

Your own practice setup for Counter-Strike 2. Save grenade lineups, teleport to them,
place bots, run prefire drills with a timer, see damage you deal. Works on every map,
official ones and Workshop ones, no subscription.

There are two ways to use it.

| | Config only | Plugin (full) |
|---|---|---|
| Setup | 1 minute | ~20 minutes, once |
| Infinite ammo, nade trails, buy anywhere, noclip | yes | yes |
| Rethrow last nade, place bots | yes | yes |
| Save and load named lineups/spots per map | no | yes |
| Go back to your last throw spot (`!last`) | no | yes |
| Prefire drill with timer | no | yes |
| Damage readout in chat | no | yes |

---

## Option 1: config only (fastest)

1. Copy `cfg/practice.cfg` and `cfg/practice_off.cfg` into
   `Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg\`
2. In CS2: Play, Practice with bots (or Workshop map), pick a map, Go.
3. Console (`~`): `exec practice`

Binds it sets: **N** noclip, **Z** rethrow last nade, **X** place a bot where you look,
**C** kick bots, **V** give all nades. `exec practice_off` undoes everything.

---

## Option 2: the plugin (Refrag-like)

The plugin runs on a CS2 dedicated server. You can run one on your own PC for free,
CS2 already ships with it.

### 1. Install Metamod and CounterStrikeSharp

1. Download **Metamod:Source 2.x** (dev build for CS2): https://www.metamodsource.net/downloads.php?branch=dev
   Extract so you get `game/csgo/addons/metamod/`.
2. Open `game/csgo/gameinfo.gi`, find the line `Game_LowViolence csgo_lv` and add this
   line right under it:
   ```
   Game csgo/addons/metamod
   ```
3. Download **CounterStrikeSharp with runtime** (the `with-runtime-windows` zip):
   https://github.com/roflmuffin/CounterStrikeSharp/releases
   Extract into `game/csgo/` so you get `game/csgo/addons/counterstrikesharp/`.

Note: CS2 updates sometimes overwrite `gameinfo.gi`. If the plugin stops loading after
an update, add the line again.

### 2. Install the plugin

Get `PracticeMode.dll` in one of two ways:

- From GitHub: Actions tab, latest "Build plugin" run, download the `PracticeMode` artifact.
- Or build it yourself with the .NET 10 SDK:
  ```
  dotnet publish plugin/PracticeMode -c Release -o out
  ```

Put the files here:
```
game/csgo/addons/counterstrikesharp/plugins/PracticeMode/PracticeMode.dll
```

### 3. Start your local server

Create a shortcut or `.bat` file (change the path to your Steam library):

```bat
"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe" -dedicated -insecure +map de_mirage +game_type 0 +game_mode 1 +sv_lan 1
```

Then start CS2 normally and in the console type `connect localhost`.
In chat type `!prac` and you are in practice mode.

Want a Workshop map? Use `+host_workshop_map <workshop id>` instead of `+map de_mirage`.

Important: `-insecure` turns off VAC for that local server only. Never load
Metamod/plugins into the normal game client or try to use them on Valve servers.

### Commands (type in chat)

| Command | What it does |
|---|---|
| `!prac` | Turn on practice settings (ammo, trails, money, respawn, frozen bots) |
| `!help` | Show commands in chat |
| `!noclip` | Fly through walls |
| `!save <name>` / `!load <name>` | Save and teleport to a spot |
| `!spots` / `!delspot <name>` | List / delete spots |
| `!nade <name>` | Save the lineup you stand on (hold the grenade so its type is saved too) |
| `!nades` | List lineups for this map |
| `!goto <name or #>` | Teleport to a lineup, with the exact aim angle, and get the grenade |
| `!delnade <name or #>` | Delete a lineup |
| `!last` | Go back to where you threw your last grenade |
| `!rethrow` | Throw your last grenade again |
| `!bot` | Place an enemy bot where you stand, looking where you look |
| `!clearbots` | Kick all bots |
| `!shoot` | Toggle bots shooting back |
| `!pfadd` | Add a prefire spot (stand where the enemy holds, aim where you will peek from) |
| `!pflist` / `!pfdel <# or all>` | List / delete prefire spots |
| `!prefire` | Start the drill: bots on all prefire spots, timer prints when all are dead |
| `!pfstop` | Stop the drill |

Everything you save is stored per map in
`addons/counterstrikesharp/plugins/PracticeMode/data/<map>.json`.
You can share these files with friends or back them up.

### Typical workflow

- **Learning smokes:** `!prac`, find a lineup, hold the smoke, `!nade xbox smoke`.
  Throw. Missed? `!last` and try again. Next time just `!goto xbox`.
- **Prefire route:** walk the route, at each angle the enemy usually holds stand there
  and `!pfadd` while looking where you will come from. Then go back to spawn,
  `!prefire`, clear the route. Bots respawn on their spots so you can repeat.

---

## About making a real Workshop map

Blitz/Refrag style "maps" are mostly a server with a practice plugin, which is what this
repo gives you. An actual Workshop map (`.vpk`) has to be built in Hammer from the
CS2 Workshop Tools (Steam, CS2, right click, DLC, enable "Counter-Strike 2 Workshop
Tools"), which only runs on Windows. The plugin above works on any Workshop map you load,
so you can combine both.
