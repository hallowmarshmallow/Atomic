using System;
using HarmonyLib;
using Hazel;
using InnerNet;
using UnityEngine;

namespace Atomic
{
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    internal static class PlayerControl_HandleRpc_Patch
    {
        private static bool Prefix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            try
            {
                if (NetworkManager.TryDispatch(__instance, callId, reader)) return false;
            }
            catch (Exception e) { AtomicPlugin.Log.LogError("RPC dispatch failed: " + e); }
            return true;
        }
    }

    // OnPlayerJoined is protected in newer game versions, so it is referenced by
    // string name rather than nameof() to keep the patch compiling across versions.
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
    internal static class AmongUsClient_OnPlayerJoined_Patch
    {
        private static void Postfix(AmongUsClient __instance, ClientData data)
        {
            if (__instance == null) return;
            NetworkManager.SendHandshake();

            if (__instance.AmHost && data != null)
                KickTracker.TrackJoin(data.Id);
        }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
    internal static class AmongUsClient_OnPlayerLeft_Patch
    {
        private static void Postfix(ClientData data, DisconnectReasons reason)
        {
            if (data == null) return;
            KickTracker.Untrack(data.Id);

            if (data.Character == null || data.Character.Data == null) return;
            try
            {
                var pid = data.Character.Data.PlayerId;
                LobbyTracker.RemovePlayer(pid);
                AtomicAPI.FirePlayerUnmodded(pid);
            }
            catch (Exception e) { AtomicPlugin.Log.LogError("OnPlayerLeft tracker: " + e); }
        }
    }

    [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
    internal static class GameStartManager_Start_Patch
    {
        private static void Postfix()
        {
            var client = AmongUsClient.Instance;
            if (client == null) return;
            LobbyTracker.Clear();
            KickTracker.Clear();
            NetworkManager.SendHandshake();
            if (client.AmHost) return;
            GameStartManager_Update_Patch.StartCheck();
        }
    }

    [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
    internal static class GameStartManager_Update_Patch
    {
        private const float ResendIntervalSeconds = 2.5f;

        private static float _joinTime = -1f;
        private static bool _checking;
        private static bool _fired;
        private static float _nextResend = -1f;

        internal static void StartCheck()
        {
            _joinTime = Time.time;
            _checking = true;
            _fired = false;
            _nextResend = Time.time;
        }

        private static void Postfix()
        {
            if (Time.time >= _nextResend)
            {
                _nextResend = Time.time + ResendIntervalSeconds;
                NetworkManager.SendHandshake();
            }

            KickTracker.CheckPending();

            if (!_checking || _fired) return;
            if (Time.time - _joinTime < 15f) return;

            _checking = false;
            _fired = true;

            if (!LobbyTracker.HostIsModded() && AtomicAPI.HasLocalMods())
            {
                AtomicPlugin.Log.LogInfo("Host has no recorded Atomic handshake after the grace period — leaving unmodded lobby to avoid an unfair advantage.");
                AtomicAPI.FireJoiningUnmoddedLobby();
                AmongUsClient.Instance?.ExitGame();
            }
        }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
    internal static class AmongUsClient_OnGameJoined_Patch
    {
        private static void Postfix(AmongUsClient __instance)
        {
            if (__instance == null || __instance.AmHost) return;
            LobbyTracker.Clear();
            GameStartManager_Update_Patch.StartCheck();
            NetworkManager.SendHandshake();
        }
    }

    // Note: OnGameStarted was previously fired from IntroCutscene.CoBegin, which is a
    // coroutine (IEnumerator) entry point. Patching coroutine entry points is known to
    // segfault during Harmony detour installation on Linux IL2CPP (PAL_SEHException at
    // launch). HudManager.Start is a plain void MonoBehaviour callback that fires at the
    // same logical moment (round start) and is safe to detour.
    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    internal static class HudManager_Start_GameStarted_Patch
    {
        private static void Postfix()
        {
            try { AtomicAPI.FireGameStarted(); }
            catch (Exception e) { AtomicPlugin.Log.LogError("OnGameStarted event: " + e); }
        }
    }

    // MeetingHud.Start is private in newer game versions, so it is referenced by string
    // name. It is the all-client meeting hook (MeetingHud.ServerStart is host-only).
    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
    internal static class MeetingHud_Start_Patch
    {
        private static void Postfix()
        {
            try { AtomicAPI.FireMeetingStarted(); }
            catch (Exception e) { AtomicPlugin.Log.LogError("OnMeetingStarted event: " + e); }
        }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
    internal static class PlayerControl_MurderPlayer_Patch
    {
        private static void Postfix(PlayerControl target)
        {
            if (target == null || target.Data == null) return;
            try { AtomicAPI.FirePlayerDied(target.Data.PlayerId); }
            catch (Exception e) { AtomicPlugin.Log.LogError("OnPlayerDied event: " + e); }
        }
    }

    [HarmonyPatch(typeof(RoleBehaviour), nameof(RoleBehaviour.OnAssign))]
    internal static class RoleBehaviour_OnAssign_Patch
    {
        private static void Postfix(RoleBehaviour __instance, PlayerControl player)
        {
            if (__instance == null || player == null || player.Data == null) return;
            try { AtomicAPI.FireRoleAssigned(player.Data.PlayerId, __instance.GetType().Name); }
            catch (Exception e) { AtomicPlugin.Log.LogError("OnRoleAssigned event: " + e); }
        }
    }

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.FixedUpdate))]
    internal static class HudManager_FixedUpdate_Il2CppTypeRegistrar_Patch
    {
        private static void Prefix()
        {
            Il2CppTypeRegistrar.Tick();
        }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Die))]
    internal static class PlayerControl_Die_Patch
    {
        private static void Postfix(PlayerControl __instance)
        {
            if (__instance == null || __instance.Data == null) return;
            try { AtomicAPI.FirePlayerDied(__instance.Data.PlayerId); }
            catch (Exception e) { AtomicPlugin.Log.LogError("OnPlayerDied event: " + e); }
        }
    }
}
