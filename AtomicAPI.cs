using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Hazel;
using TMPro;
using UnityEngine;

namespace Atomic
{
    public static class AtomicAPI
    {
        private static readonly List<(string mod, string version)> _localMods = new();
        private static readonly ConditionalWeakTable<TMP_Text, HashSet<string>> _versionLines = new();

        public static event Action<byte, List<(string mod, string version)>> OnPlayerModded;
        public static event Action<byte> OnPlayerUnmodded;
        public static event Action OnLobbyFullyModded;
        public static event Action OnJoiningUnmoddedLobby;
        public static event Action<byte, string, string, string> OnModVersionMismatch;

        // adds or updates a local mod, then tells the lobby.
        public static void Register(string modName, string version)
        {
            for (int index = _localMods.Count - 1; index >= 0; index--)
            {
                if (_localMods[index].mod == modName)
                {
                    _localMods.RemoveAt(index);
                }
            }

            _localMods.Add((modName, version));
            AtomicPlugin.Log.LogInfo($"Registered mod: {modName} v{version}");
            NetworkManager.SendHandshake();
        }

        // adds a colored line once to a text object.
        public static void AddVersionLine(TMP_Text text, string name, string line, Color color)
        {
            if (text == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            HashSet<string> addedLines = _versionLines.GetValue(
                text,
                target => new HashSet<string>(StringComparer.Ordinal));

            if (addedLines.Contains(name))
            {
                return;
            }

            string colorCode = ColorUtility.ToHtmlStringRGB(color);
            string safeLine = line
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
            string coloredLine = "<color=#" + colorCode + ">" + safeLine + "</color>";

            if (string.IsNullOrEmpty(text.text))
            {
                text.text = coloredLine;
            }
            else
            {
                text.text += "\n" + coloredLine;
            }

            addedLines.Add(name);
        }

        // checks whether every player has reported their mods.
        public static bool IsLobbyFullyModded()
        {
            return LobbyTracker.IsFullyModded();
        }

        // checks whether any mods are registered locally.
        public static bool HasLocalMods()
        {
            return _localMods.Count > 0;
        }

        // returns the players who have not reported their mods.
        public static List<byte> GetUnmoddedPlayers()
        {
            return LobbyTracker.GetUnmoddedIds();
        }

        // checks whether every player has the same version as the local mod.
        public static bool IsModCompatible(string modName)
        {
            string localVersion = null;
            foreach ((string mod, string version) localMod in _localMods)
            {
                if (localMod.mod == modName)
                {
                    localVersion = localMod.version;
                    break;
                }
            }

            if (localVersion == null)
            {
                return true;
            }

            return LobbyTracker.AllPlayersHaveVersion(modName, localVersion);
        }

        // checks whether every local mod is compatible with all players.
        public static bool IsCompatibleToPlay()
        {
            foreach (var (mod, version) in _localMods)
            {
                if (!LobbyTracker.AllPlayersHaveVersion(mod, version))
                {
                    return false;
                }
            }

            return true;
        }

        // sets which function should handle a received rpc message.
        public static void RegisterRpcHandler(byte callId, Action<byte, MessageReader> handler)
        {
            NetworkManager.RegisterHandler(callId, handler);
        }

        // sends an rpc message with data you provide.
        public static void SendRpc(byte callId, Action<MessageWriter> writePayload)
        {
            NetworkManager.SendRpc(callId, writePayload);
        }

        // queues a function to register an il2cpp type.
        public static void RegisterIl2CppType(Action register)
        {
            Il2CppTypeRegistrar.Enqueue(register);
        }

        // registers all queued il2cpp types now.
        public static void FlushPendingIl2CppTypeRegistrations()
        {
            Il2CppTypeRegistrar.FlushAll();
        }

        // reserves settings rows for a menu.
        public static int ReserveSettingsRows(int menuInstanceId, int count)
        {
            return SettingsRowAllocator.ReserveRows(menuInstanceId, count);
        }

        // finds and registers rpc functions on this object.
        public static void RegisterRpcMethods(object target)
        {
            AtomicRpc.RegisterMethods(target);
        }

        // finds and registers rpc functions on this type.
        public static void RegisterRpcMethods(Type type)
        {
            AtomicRpc.RegisterMethods(type);
        }

        // sends an rpc message using its id.
        public static void SendRpcMethod(byte callId, params object[] args)
        {
            AtomicRpc.Send(callId, args);
        }

        // sends an rpc message using its key.
        public static void SendRpcMethod(string key, params object[] args)
        {
            AtomicRpc.Send(key, args);
        }

        // tries to kill the target. on the host, true means the kill happened.
        // on other clients, true means the request was sent. false means the kill was blocked.
        // only report a kill after it is confirmed.
        public static bool KillPlayer(PlayerControl killer, PlayerControl target, CustomKillOptions options = null)
        {
            return CustomKillManager.Kill(killer, target, options);
        }

        public static event Action OnGameStarted;
        public static event Action OnMeetingStarted;
        public static event Action<byte> OnPlayerDied;
        public static event Action<byte, string> OnRoleAssigned;

        // tells listeners that the game started.
        internal static void FireGameStarted()
        {
            OnGameStarted?.Invoke();
        }
        // tells listeners that a meeting started.
        internal static void FireMeetingStarted()
        {
            OnMeetingStarted?.Invoke();
        }
        // tells listeners that a player died.
        internal static void FirePlayerDied(byte playerId)
        {
            OnPlayerDied?.Invoke(playerId);
        }
        // tells listeners that a player got a role.
        internal static void FireRoleAssigned(byte playerId, string roleName)
        {
            OnRoleAssigned?.Invoke(playerId, roleName);
        }

        // returns the mods registered on the client.
        internal static IReadOnlyList<(string mod, string version)> GetLocalMods()
        {
            return _localMods;
        }

        // tells listeners about a player's mods and any version mismatch.
        internal static void FirePlayerModded(byte id, List<(string mod, string version)> mods)
        {
            OnPlayerModded?.Invoke(id, mods);

            foreach ((string mod, string version) remoteMod in mods)
            {
                (string mod, string version) localMod = default;
                foreach ((string mod, string version) registeredMod in _localMods)
                {
                    if (registeredMod.mod == remoteMod.mod)
                    {
                        localMod = registeredMod;
                        break;
                    }
                }

                if (localMod.mod != null && localMod.version != remoteMod.version)
                {
                    OnModVersionMismatch?.Invoke(
                        id, remoteMod.mod, localMod.version, remoteMod.version);
                }
            }

            if (LobbyTracker.IsFullyModded())
                OnLobbyFullyModded?.Invoke();
        }

        // tells listeners that a player has no mods.
        internal static void FirePlayerUnmodded(byte id)
        {
            OnPlayerUnmodded?.Invoke(id);
        }

        // tells listeners when joining a lobby with no mods.
        internal static void FireJoiningUnmoddedLobby()
        {
            OnJoiningUnmoddedLobby?.Invoke();
        }
    }
}
