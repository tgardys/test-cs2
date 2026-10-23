using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracticeMode;

// A single saved place on the map: where you stood and where you looked.
public class Spot
{
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public string Grenade { get; set; } = "";
}

// Everything we remember for one map. Stored as data/<map>.json.
public class MapData
{
    public List<Spot> Spots { get; set; } = new();
    public List<Spot> Nades { get; set; } = new();
    public List<Spot> Prefire { get; set; } = new();
}

public class PracticeModePlugin : BasePlugin
{
    public override string ModuleName => "PracticeMode";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "TG";
    public override string ModuleDescription => "Free practice server: nade lineups, saved spots, bots and prefire drills.";

    private static readonly string Tag = $" {ChatColors.Green}[PRAC]{ChatColors.Default}";

    private static readonly string[] PracticeCvars =
    {
        "sv_cheats 1",
        "mp_warmup_end",
        "mp_limitteams 0",
        "mp_autoteambalance 0",
        "mp_freezetime 0",
        "mp_roundtime 60",
        "mp_roundtime_defuse 60",
        "mp_roundtime_hostage 60",
        "mp_maxmoney 60000",
        "mp_startmoney 60000",
        "mp_buytime 9999",
        "mp_buy_anywhere 1",
        "mp_respawn_on_death_ct 1",
        "mp_respawn_on_death_t 1",
        "mp_ignore_round_win_conditions 1",
        "mp_death_drop_gun 0",
        "sv_infinite_ammo 1",
        "ammo_grenade_limit_total 5",
        "sv_grenade_trajectory_prac_pipreview 1",
        "sv_grenade_trajectory_prac_trailtime 6",
        "sv_showimpacts 1",
        "sv_showimpacts_time 5",
        "bot_quota_mode normal",
        "bot_quota 0",
        "bot_stop 1",
        "bot_dont_shoot 1",
        "mp_restartgame 1",
    };

    private MapData _data = new();
    private string _map = "";

    // Bot slot -> the spot it should stand on. Used to put bots back after they respawn.
    private readonly Dictionary<int, Spot> _botSpots = new();
    private readonly Queue<Spot> _pendingBots = new();

    // Player slot -> where they last threw a grenade from.
    private readonly Dictionary<int, Spot> _lastThrow = new();

    // Prefire drill state.
    private bool _prefireRunning;
    private readonly HashSet<int> _prefireKilled = new();
    private readonly Stopwatch _prefireTimer = new();

    private string DataDir => Path.Combine(ModuleDirectory, "data");

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(map =>
        {
            _map = map;
            _botSpots.Clear();
            _pendingBots.Clear();
            _lastThrow.Clear();
            _prefireRunning = false;
            LoadData();
        });

        if (hotReload)
        {
            _map = Server.MapName;
            LoadData();
        }
    }

    // ---------- storage ----------

    private string DataFile => Path.Combine(DataDir, $"{_map}.json");

    private void LoadData()
    {
        try
        {
            _data = File.Exists(DataFile)
                ? JsonSerializer.Deserialize<MapData>(File.ReadAllText(DataFile)) ?? new MapData()
                : new MapData();
        }
        catch (Exception e)
        {
            Logger.LogWarning("Could not read {File}: {Error}", DataFile, e.Message);
            _data = new MapData();
        }
    }

    private void SaveData()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(DataFile, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------- helpers ----------

    private static CCSPlayerPawn? AlivePawn(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return null;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return null;
        return pawn;
    }

    private static Spot? SpotFrom(CCSPlayerController player, string name)
    {
        var pawn = AlivePawn(player);
        if (pawn?.AbsOrigin == null) return null;
        var o = pawn.AbsOrigin;
        var a = pawn.EyeAngles;
        return new Spot { Name = name, X = o.X, Y = o.Y, Z = o.Z, Pitch = a.X, Yaw = a.Y };
    }

    private static void TeleportTo(CCSPlayerPawn pawn, Spot s)
    {
        pawn.Teleport(new Vector(s.X, s.Y, s.Z), new QAngle(s.Pitch, s.Yaw, 0), new Vector(0, 0, 0));
    }

    private static Spot? Find(List<Spot> list, string key)
    {
        if (int.TryParse(key, out var n) && n >= 1 && n <= list.Count) return list[n - 1];
        return list.FirstOrDefault(s => s.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(s => s.Name.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    private static void Reply(CCSPlayerController? player, string msg)
    {
        if (player == null) Console.WriteLine($"[PRAC] {msg}");
        else player.PrintToChat($"{Tag} {msg}");
    }

    private static void PrintList(CCSPlayerController? player, string title, List<Spot> list)
    {
        if (list.Count == 0) { Reply(player, $"No {title} saved on this map yet."); return; }
        Reply(player, $"{title} ({list.Count}):");
        for (var i = 0; i < list.Count; i++)
        {
            var extra = string.IsNullOrEmpty(list[i].Grenade) ? "" : $" {ChatColors.Grey}({list[i].Grenade}){ChatColors.Default}";
            Reply(player, $"{ChatColors.Yellow}{i + 1}.{ChatColors.Default} {list[i].Name}{extra}");
        }
    }

    private static string GrenadeShortName(string designerName) => designerName switch
    {
        "weapon_smokegrenade" => "smoke",
        "weapon_flashbang" => "flash",
        "weapon_hegrenade" => "he",
        "weapon_molotov" => "molotov",
        "weapon_incgrenade" => "molotov",
        "weapon_decoy" => "decoy",
        _ => ""
    };

    private static string GrenadeItem(string shortName, CsTeam team) => shortName switch
    {
        "smoke" => "weapon_smokegrenade",
        "flash" => "weapon_flashbang",
        "he" => "weapon_hegrenade",
        "molotov" => team == CsTeam.CounterTerrorist ? "weapon_incgrenade" : "weapon_molotov",
        "decoy" => "weapon_decoy",
        _ => ""
    };

    // ---------- general ----------

    [ConsoleCommand("css_prac", "Turn on practice settings")]
    public void OnPrac(CCSPlayerController? player, CommandInfo cmd)
    {
        foreach (var c in PracticeCvars) Server.ExecuteCommand(c);
        _botSpots.Clear();
        Reply(player, "Practice mode ON. Type !help for commands.");
    }

    [ConsoleCommand("css_help", "List practice commands")]
    public void OnHelp(CCSPlayerController? player, CommandInfo cmd)
    {
        string[] lines =
        {
            "!prac  turn on practice settings",
            "!noclip  fly through walls",
            "!save <name> / !load <name> / !spots / !delspot <name>",
            "!nade <name>  save the lineup you are standing on",
            "!nades / !goto <name|#> / !delnade <name|#>",
            "!last  go back to your last throw   !rethrow  throw it again",
            "!bot  place a bot here   !clearbots  kick all bots",
            "!pfadd / !pflist / !pfdel <#> / !prefire / !pfstop",
            "!shoot  toggle bots shooting back",
        };
        foreach (var l in lines) Reply(player, l);
    }

    [ConsoleCommand("css_noclip", "Toggle noclip")]
    public void OnNoclip(CCSPlayerController? player, CommandInfo cmd)
    {
        var pawn = AlivePawn(player);
        if (pawn == null) return;

        var on = pawn.MoveType != MoveType_t.MOVETYPE_NOCLIP;
        var type = on ? MoveType_t.MOVETYPE_NOCLIP : MoveType_t.MOVETYPE_WALK;
        pawn.MoveType = type;
        Schema.SetSchemaValue(pawn.Handle, "CBaseEntity", "m_nActualMoveType", (int)type);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
        Reply(player, on ? "Noclip ON" : "Noclip OFF");
    }

    // ---------- spots ----------

    [ConsoleCommand("css_save", "Save your current position")]
    [CommandHelper(minArgs: 1, usage: "<name>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnSave(CCSPlayerController? player, CommandInfo cmd)
    {
        var name = cmd.ArgString.Trim();
        var spot = SpotFrom(player!, name);
        if (spot == null) return;
        _data.Spots.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _data.Spots.Add(spot);
        SaveData();
        Reply(player, $"Saved spot {ChatColors.Yellow}{name}");
    }

    [ConsoleCommand("css_load", "Teleport to a saved position")]
    [CommandHelper(minArgs: 1, usage: "<name|#>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnLoad(CCSPlayerController? player, CommandInfo cmd)
    {
        var pawn = AlivePawn(player);
        var spot = Find(_data.Spots, cmd.ArgString.Trim());
        if (pawn == null) return;
        if (spot == null) { Reply(player, "Spot not found. Type !spots"); return; }
        TeleportTo(pawn, spot);
    }

    [ConsoleCommand("css_spots", "List saved positions")]
    public void OnSpots(CCSPlayerController? player, CommandInfo cmd) => PrintList(player, "Spots", _data.Spots);

    [ConsoleCommand("css_delspot", "Delete a saved position")]
    [CommandHelper(minArgs: 1, usage: "<name|#>")]
    public void OnDelSpot(CCSPlayerController? player, CommandInfo cmd)
    {
        var spot = Find(_data.Spots, cmd.ArgString.Trim());
        if (spot == null) { Reply(player, "Spot not found."); return; }
        _data.Spots.Remove(spot);
        SaveData();
        Reply(player, $"Deleted {spot.Name}");
    }

    // ---------- grenade lineups ----------

    [ConsoleCommand("css_nade", "Save the grenade lineup you are standing on")]
    [CommandHelper(minArgs: 1, usage: "<name>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnNade(CCSPlayerController? player, CommandInfo cmd)
    {
        var name = cmd.ArgString.Trim();
        var spot = SpotFrom(player!, name);
        if (spot == null) return;

        var active = player!.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        spot.Grenade = active != null ? GrenadeShortName(active.DesignerName) : "";

        _data.Nades.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _data.Nades.Add(spot);
        SaveData();
        var kind = spot.Grenade == "" ? "" : $" ({spot.Grenade})";
        Reply(player, $"Saved lineup {ChatColors.Yellow}{name}{ChatColors.Default}{kind}");
    }

    [ConsoleCommand("css_nades", "List saved grenade lineups")]
    public void OnNades(CCSPlayerController? player, CommandInfo cmd) => PrintList(player, "Lineups", _data.Nades);

    [ConsoleCommand("css_goto", "Go to a saved grenade lineup")]
    [CommandHelper(minArgs: 1, usage: "<name|#>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnGoto(CCSPlayerController? player, CommandInfo cmd)
    {
        var pawn = AlivePawn(player);
        var spot = Find(_data.Nades, cmd.ArgString.Trim());
        if (pawn == null) return;
        if (spot == null) { Reply(player, "Lineup not found. Type !nades"); return; }

        TeleportTo(pawn, spot);
        var item = GrenadeItem(spot.Grenade, player!.Team);
        if (item != "")
        {
            player.GiveNamedItem(item);
            player.ExecuteClientCommand($"use {item}");
        }
        Reply(player, $"{ChatColors.Yellow}{spot.Name}{ChatColors.Default} {spot.Grenade}");
    }

    [ConsoleCommand("css_delnade", "Delete a grenade lineup")]
    [CommandHelper(minArgs: 1, usage: "<name|#>")]
    public void OnDelNade(CCSPlayerController? player, CommandInfo cmd)
    {
        var spot = Find(_data.Nades, cmd.ArgString.Trim());
        if (spot == null) { Reply(player, "Lineup not found."); return; }
        _data.Nades.Remove(spot);
        SaveData();
        Reply(player, $"Deleted {spot.Name}");
    }

    [ConsoleCommand("css_last", "Go back to where you threw your last grenade")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnLast(CCSPlayerController? player, CommandInfo cmd)
    {
        var pawn = AlivePawn(player);
        if (pawn == null) return;
        if (!_lastThrow.TryGetValue(player!.Slot, out var spot)) { Reply(player, "You have not thrown anything yet."); return; }
        TeleportTo(pawn, spot);
        var item = GrenadeItem(spot.Grenade, player.Team);
        if (item != "")
        {
            player.GiveNamedItem(item);
            player.ExecuteClientCommand($"use {item}");
        }
    }

    [ConsoleCommand("css_rethrow", "Throw your last grenade again")]
    public void OnRethrow(CCSPlayerController? player, CommandInfo cmd)
    {
        Server.ExecuteCommand("sv_rethrow_last_grenade");
    }

    [GameEventHandler]
    public HookResult OnGrenadeThrown(EventGrenadeThrown ev, GameEventInfo info)
    {
        var player = ev.Userid;
        if (player == null || player.IsBot) return HookResult.Continue;
        var spot = SpotFrom(player, "last");
        if (spot == null) return HookResult.Continue;
        spot.Grenade = GrenadeShortName("weapon_" + ev.Weapon);
        _lastThrow[player.Slot] = spot;
        return HookResult.Continue;
    }

    // ---------- bots ----------

    [ConsoleCommand("css_bot", "Place a bot where you are standing, looking where you look")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnBot(CCSPlayerController? player, CommandInfo cmd)
    {
        var spot = SpotFrom(player!, "bot");
        if (spot == null) return;
        SpawnBot(spot, player!.Team);
        Reply(player, "Bot placed. Move away so you are not stuck in it.");
    }

    [ConsoleCommand("css_clearbots", "Kick all bots")]
    public void OnClearBots(CCSPlayerController? player, CommandInfo cmd)
    {
        Server.ExecuteCommand("bot_kick");
        _botSpots.Clear();
        _pendingBots.Clear();
        _prefireRunning = false;
        Reply(player, "Bots kicked.");
    }

    [ConsoleCommand("css_shoot", "Toggle bots shooting back")]
    public void OnShoot(CCSPlayerController? player, CommandInfo cmd)
    {
        var cvar = CounterStrikeSharp.API.Modules.Cvars.ConVar.Find("bot_dont_shoot");
        var shootNow = cvar != null && cvar.GetPrimitiveValue<bool>();
        Server.ExecuteCommand($"bot_dont_shoot {(shootNow ? 0 : 1)}");
        Server.ExecuteCommand($"bot_stop {(shootNow ? 0 : 1)}");
        Reply(player, shootNow ? "Bots shoot back now (and can move a bit)." : "Bots are frozen and harmless.");
    }

    private void SpawnBot(Spot spot, CsTeam playerTeam)
    {
        _pendingBots.Enqueue(spot);
        // Put the bot on the other team so you can shoot it.
        Server.ExecuteCommand(playerTeam == CsTeam.Terrorist ? "bot_add_ct" : "bot_add_t");
        AddTimer(0.3f, AssignPendingBots);
    }

    private void AssignPendingBots()
    {
        foreach (var bot in Utilities.GetPlayers().Where(p => p.IsBot && !_botSpots.ContainsKey(p.Slot)))
        {
            if (_pendingBots.Count == 0) break;
            var spot = _pendingBots.Dequeue();
            _botSpots[bot.Slot] = spot;
            var pawn = AlivePawn(bot);
            if (pawn != null) TeleportTo(pawn, spot);
        }
        // Bot may not be in the game yet, try again a bit later.
        if (_pendingBots.Count > 0) AddTimer(0.5f, AssignPendingBots);
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        var bot = ev.Userid;
        if (bot == null || !bot.IsBot || !_botSpots.TryGetValue(bot.Slot, out var spot)) return HookResult.Continue;
        // Wait a tick, the game moves the player to a spawn point right after this event.
        Server.NextFrame(() =>
        {
            var pawn = AlivePawn(bot);
            if (pawn != null) TeleportTo(pawn, spot);
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        if (ev.Userid != null)
        {
            _botSpots.Remove(ev.Userid.Slot);
            _lastThrow.Remove(ev.Userid.Slot);
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerHurt(EventPlayerHurt ev, GameEventInfo info)
    {
        var attacker = ev.Attacker;
        var victim = ev.Userid;
        if (attacker == null || attacker.IsBot || victim == null || attacker == victim) return HookResult.Continue;
        var where = ev.Hitgroup switch
        {
            1 => "head", 2 => "chest", 3 => "stomach", 4 or 5 => "arm", 6 or 7 => "leg", _ => "body"
        };
        attacker.PrintToChat($"{Tag} {ChatColors.Red}{ev.DmgHealth}{ChatColors.Default} dmg to {victim.PlayerName} ({where})");
        return HookResult.Continue;
    }

    // ---------- prefire drill ----------

    [ConsoleCommand("css_pfadd", "Add a prefire bot spot where you stand")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnPfAdd(CCSPlayerController? player, CommandInfo cmd)
    {
        var spot = SpotFrom(player!, $"pf{_data.Prefire.Count + 1}");
        if (spot == null) return;
        _data.Prefire.Add(spot);
        SaveData();
        Reply(player, $"Prefire spot #{_data.Prefire.Count} added. Stand where the enemy holds, look at where you will peek from.");
    }

    [ConsoleCommand("css_pflist", "List prefire spots")]
    public void OnPfList(CCSPlayerController? player, CommandInfo cmd) => PrintList(player, "Prefire spots", _data.Prefire);

    [ConsoleCommand("css_pfdel", "Delete a prefire spot")]
    [CommandHelper(minArgs: 1, usage: "<#|all>")]
    public void OnPfDel(CCSPlayerController? player, CommandInfo cmd)
    {
        var key = cmd.ArgString.Trim();
        if (key.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            _data.Prefire.Clear();
        }
        else
        {
            var spot = Find(_data.Prefire, key);
            if (spot == null) { Reply(player, "Spot not found."); return; }
            _data.Prefire.Remove(spot);
        }
        SaveData();
        Reply(player, "Deleted.");
    }

    [ConsoleCommand("css_prefire", "Start a prefire drill with all saved prefire spots")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnPrefire(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_data.Prefire.Count == 0) { Reply(player, "No prefire spots yet. Use !pfadd first."); return; }

        Server.ExecuteCommand("bot_kick");
        _botSpots.Clear();
        _pendingBots.Clear();
        _prefireKilled.Clear();
        foreach (var s in _data.Prefire) SpawnBot(s, player!.Team);

        _prefireRunning = true;
        _prefireTimer.Restart();
        Reply(player, $"Prefire drill: {_data.Prefire.Count} bots. Kill them all, the clock is running.");
    }

    [ConsoleCommand("css_pfstop", "Stop the prefire drill")]
    public void OnPfStop(CCSPlayerController? player, CommandInfo cmd)
    {
        _prefireRunning = false;
        Server.ExecuteCommand("bot_kick");
        _botSpots.Clear();
        Reply(player, "Prefire drill stopped.");
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (!_prefireRunning) return HookResult.Continue;
        var victim = ev.Userid;
        if (victim == null || !victim.IsBot || !_botSpots.ContainsKey(victim.Slot)) return HookResult.Continue;

        _prefireKilled.Add(victim.Slot);
        var total = _botSpots.Count;
        if (_prefireKilled.Count < total) return HookResult.Continue;

        var time = _prefireTimer.Elapsed.TotalSeconds;
        Server.PrintToChatAll($"{Tag} All {total} bots down in {ChatColors.Yellow}{time:F2}s{ChatColors.Default}. Next run starts now.");
        _prefireKilled.Clear();
        _prefireTimer.Restart();
        return HookResult.Continue;
    }
}
