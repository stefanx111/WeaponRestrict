﻿using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Config;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using System.Data;

namespace WeaponRestrict
{
    // Possible results for CSPlayer::CanAcquire
    public enum AcquireResult : int
    {
        Allowed = 0,
        InvalidItem,
        AlreadyOwned,
        AlreadyPurchased,
        ReachedGrenadeTypeLimit,
        ReachedGrenadeTotalLimit,
        NotAllowedByTeam,
        NotAllowedByMap,
        NotAllowedByMode,
        NotAllowedForPurchase,
        NotAllowedByProhibition,
    };

    // Possible results for CSPlayer::CanAcquire
    public enum AcquireMethod : int
    {
        PickUp = 0,
        Buy,
    };

    public class WeaponRestrictConfig : BasePluginConfig
    {
        [JsonPropertyName("MessagePrefix")] public string MessagePrefix { get; set; } = "\u1010\u0010[WeaponRestrict] ";
        [JsonPropertyName("RestrictMessage")] public string RestrictMessage { get; set; } = "\u0003{0}\u0001 is currently restricted to \u000F{1}\u0001 per team.";
        [JsonPropertyName("DisabledMessage")] public string DisabledMessage { get; set; } = "\u0003{0}\u0001 is currently \u000Fdisabled\u0001.";

        [JsonPropertyName("WeaponQuotas")]
        public Dictionary<string, float> WeaponQuotas { get; set; } = new Dictionary<string, float>()
        {
            ["weapon_awp"] = 0.2f
        };

        [JsonPropertyName("WeaponLimits")]
        public Dictionary<string, int> WeaponLimits { get; set; } = new Dictionary<string, int>()
        {
            ["weapon_awp"] = 1
        };

        [JsonPropertyName("DoTeamCheck")] public bool DoTeamCheck { get; set; } = true;

        [JsonPropertyName("VIPFlag")] public string VIPFlag { get; set; } = "@css/vip";

        [JsonPropertyName("ConfigVersion")] public new int Version { get; set; } = 1;
    }

    public class WeaponRestrictPlugin : BasePlugin, IPluginConfig<WeaponRestrictConfig>
    {
        public override string ModuleName => "WeaponRestrict";

        public override string ModuleVersion => "2.1.1";

        public override string ModuleAuthor => "jon, sapphyrus & FireBird";

        public override string ModuleDescription => "Restricts player weapons based on total player or teammate count.";

        public required WeaponRestrictConfig Config { get; set; }

        public required MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, AcquireMethod, NativeObject, AcquireResult>? CCSPlayer_CanAcquireFunc;

        public required MemoryFunctionWithReturn<int, string, CCSWeaponBaseVData> GetCSWeaponDataFromKeyFunc;

        public override void Load(bool hotReload)
        {
            GetCSWeaponDataFromKeyFunc = new(GameData.GetSignature("GetCSWeaponDataFromKey"));
            CCSPlayer_CanAcquireFunc = new(GameData.GetSignature("CCSPlayer_CanAcquire"));
            CCSPlayer_CanAcquireFunc.Hook(OnWeaponCanAcquire, HookMode.Pre);
        }

        public override void Unload(bool hotReload)
        {
            CCSPlayer_CanAcquireFunc!.Unhook(OnWeaponCanAcquire, HookMode.Pre);

            base.Unload(hotReload);
        }

        public void OnConfigParsed(WeaponRestrictConfig newConfig)
        {
            newConfig = ConfigManager.Load<WeaponRestrictConfig>("WeaponRestrict");
            Config = newConfig;
        }

        private int CountWeaponsOnTeam(string name, List<CCSPlayerController> players)
        {
            int count = 0;

            foreach (CCSPlayerController player in players)
            {
                // Skip counting VIP players
                if (Config.VIPFlag != "" && AdminManager.PlayerHasPermissions(player, Config.VIPFlag)) continue;

                // Get all weapons (ignore null dereference because we already null checked these)
                foreach (CHandle<CBasePlayerWeapon> weapon in player.PlayerPawn.Value!.WeaponServices!.MyWeapons)
                {
                    //Get the item DesignerName and compare it to the counted name
                    if (weapon.Value is null || weapon.Value.DesignerName != name) continue;
                    // Increment count if weapon is found
                    count++;
                }
            }

            return count;
        }

        private HookResult OnWeaponCanAcquire(DynamicHook hook)
        {
            var vdata = GetCSWeaponDataFromKeyFunc.Invoke(-1, hook.GetParam<CEconItemView>(1).ItemDefinitionIndex.ToString());

            // Weapon is not restricted
            if (!Config.WeaponQuotas.ContainsKey(vdata.Name) && !Config.WeaponLimits.ContainsKey(vdata.Name))
                return HookResult.Continue;

            var client = hook.GetParam<CCSPlayer_ItemServices>(0).Pawn.Value!.Controller.Value!.As<CCSPlayerController>();

            if (client is null || !client.IsValid || !client.PawnIsAlive)
                return HookResult.Continue;

            // Player is VIP proof
            if (Config.VIPFlag != "" && AdminManager.PlayerHasPermissions(client, Config.VIPFlag))
                return HookResult.Continue;

            // Get every valid player, that is currently connected, and is alive, also check for teamcheck (and LOTS of null checks... because Valve)
            List<CCSPlayerController> players = Utilities.GetPlayers().Where(player => 
                player.IsValid 
                && player.Connected == PlayerConnectedState.PlayerConnected 
                && player.PlayerPawn != null
                && player.PawnIsAlive
                && player.PlayerPawn.Value != null 
                && player.PlayerPawn.Value.IsValid
                && player.PlayerPawn.Value.WeaponServices != null 
                && player.PlayerPawn.Value.WeaponServices.MyWeapons != null
                && (!Config.DoTeamCheck || player.Team == client.Team)
                ).ToList();

            int limit = int.MaxValue;
            bool disabled = false;
            if (Config.WeaponQuotas.ContainsKey(vdata.Name))
            {
                limit = Math.Min(limit, Config.WeaponQuotas[vdata.Name] > 0f ? (int)(players.Count * Config.WeaponQuotas[vdata.Name]) : 0);
                disabled |= Config.WeaponQuotas[vdata.Name] == 0f;
            }
            if (Config.WeaponLimits.ContainsKey(vdata.Name))
            {
                limit = Math.Min(limit, Config.WeaponLimits[vdata.Name]);
                disabled |= Config.WeaponLimits[vdata.Name] == 0;
            }

            int count = CountWeaponsOnTeam(vdata.Name, players);
            if (count < limit)
                return HookResult.Continue;

            // Print chat message if we attempted to buy this weapon
            if (hook.GetParam<AcquireMethod>(2) != AcquireMethod.PickUp)
            {
                hook.SetReturn(AcquireResult.AlreadyOwned);

                string msg = "";
                if (disabled && Config.DisabledMessage != "")
                    msg = FormatChatMessage(Config.DisabledMessage, vdata.Name);
                else if (Config.RestrictMessage != "")
                    msg = FormatChatMessage(Config.RestrictMessage, vdata.Name, limit.ToString());

                if (msg != "")
                    Server.NextFrame(() => client.PrintToChat(msg));
            }
            else
            {
                hook.SetReturn(AcquireResult.InvalidItem);
            }

            return HookResult.Stop;
        }

        private string FormatChatMessage(string message, params string[] varArgs)
        {
            return string.Format(Config.MessagePrefix + message, varArgs);
        }
    }
}