using System;
using System.Collections.Generic;

namespace Atomic
{
    internal static class LobbyTracker
    {
        private static readonly Dictionary<byte, List<(string mod, string version)>> _players = new();

        // saves the mods reported by a player.
        public static void SetPlayerMods(byte playerId, List<(string mod, string version)> mods)
        {
            _players[playerId] = mods;
        }

        // removes a player from the saved mod list.
        public static void RemovePlayer(byte playerId)
        {
            _players.Remove(playerId);
        }

        // removes all saved player mod lists.
        public static void Clear()
        {
            _players.Clear();
        }

        // checks whether a player has reported their mods.
        public static bool IsPlayerModded(byte playerId)
        {
            return _players.ContainsKey(playerId);
        }

        // checks whether two mod lists match exactly.
        internal static bool HasExactModSet(IReadOnlyList<(string mod, string version)> remote, IReadOnlyList<(string mod, string version)> local)
        {
            if (remote.Count != local.Count)
            {
                return false;
            }

            foreach ((string mod, string version) localMod in local)
            {
                bool foundMod = false;

                foreach ((string mod, string version) remoteMod in remote)
                {
                    if (remoteMod.mod != localMod.mod)
                    {
                        continue;
                    }

                    foundMod = true;
                    if (remoteMod.version != localMod.version)
                    {
                        return false;
                    }

                    break;
                }

                if (!foundMod)
                {
                    return false;
                }
            }

            return true;
        }

        // checks whether every connected player has reported their mods.
        public static bool IsFullyModded()
        {
            if (PlayerControl.AllPlayerControls == null)
            {
                return false;
            }

            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            {
                if (player == null)
                {
                    continue;
                }

                if (player.Data == null)
                {
                    continue;
                }

                if (player.Data.Disconnected)
                {
                    continue;
                }

                if (!_players.ContainsKey(player.Data.PlayerId))
                {
                    return false;
                }
            }

            return true;
        }

        // returns the ids of connected players who have not reported their mods.
        public static List<byte> GetUnmoddedIds()
        {
            var result = new List<byte>();
            if (PlayerControl.AllPlayerControls == null)
            {
                return result;
            }

            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            {
                if (player == null || player.Data == null || player.Data.Disconnected)
                {
                    continue;
                }

                if (!_players.ContainsKey(player.Data.PlayerId))
                {
                    result.Add(player.Data.PlayerId);
                }
            }

            return result;
        }

        // checks whether all connected players have this mod version.
        public static bool AllPlayersHaveVersion(string modName, string version)
        {
            if (PlayerControl.AllPlayerControls == null)
            {
                return true;
            }

            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            {
                if (player == null || player.Data == null || player.Data.Disconnected)
                {
                    continue;
                }

                byte playerId = player.Data.PlayerId;
                List<(string mod, string version)> playerMods;
                if (!_players.TryGetValue(playerId, out playerMods))
                {
                    return false;
                }

                (string mod, string version) modEntry = playerMods.Find(
                    item => item.mod == modName);
                if (modEntry.mod == null || modEntry.version != version)
                {
                    return false;
                }
            }

            return true;
        }

        // checks whether the host has reported its mods.
        public static bool HostIsModded()
        {
            var client = AmongUsClient.Instance;
            if (client == null)
            {
                return false;
            }

            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            {
                if (player == null || player.Data == null)
                {
                    continue;
                }

                if (player.OwnerId == client.HostId)
                {
                    return _players.ContainsKey(player.Data.PlayerId);
                }
            }

            return false;
        }
    }
}
