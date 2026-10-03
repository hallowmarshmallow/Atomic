using System;
using Atomic.Core;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace Atomic
{
    [BepInPlugin(Guid, "Atomic", Version)]
    public class AtomicPlugin : BasePlugin
    {
        // identifies this plugin and its version.
        public const string Guid = "atomic";
        public const string Version = "1.1.0";

        public static ManualLogSource Log;

        // turn this on to kick players who do not have the mod.
        public static ConfigEntry<bool> EnforceCompatibility { get; private set; }

        // lists patches in order. avoid coroutine patches because they crash on linux.
        private static readonly Type[] PatchTypes = new[]
        {
            typeof(PlayerControl_HandleRpc_Patch),
            typeof(AmongUsClient_OnPlayerJoined_Patch),
            typeof(AmongUsClient_OnPlayerLeft_Patch),
            typeof(AmongUsClient_OnGameJoined_Patch),
            typeof(GameStartManager_Start_Patch),
            typeof(GameStartManager_Update_Patch),
            typeof(HudManager_FixedUpdate_Il2CppTypeRegistrar_Patch),
            typeof(VersionShower_Start_VersionShowerPatch),
            typeof(MeetingHud_Start_Patch),
            typeof(PlayerControl_MurderPlayer_Patch),
            typeof(PlayerControl_Die_Patch),
            typeof(RoleBehaviour_OnAssign_Patch),
            typeof(HudManager_Start_GameStarted_Patch),
        };

        // starts the plugin and applies its patches.
        public override void Load()
        {
            Log = base.Log;

            EnforceCompatibility = Config.Bind(
                "Handshake",
                "EnforceCompatibility",
                false,
                "Kick players who are missing Atomic or have a mismatch mod set after a period of time. Leave false for hostonly mods.");

            Harmony harmony = new Harmony(Guid);
            foreach (Type patchType in PatchTypes)
            {
                harmony.CreateClassProcessor(patchType).Patch();
            }

            AtomicAPI.RegisterRpcMethods(typeof(CustomKillManager));

            Log.LogInfo("Atomic loaded.");
        }
    }
}
