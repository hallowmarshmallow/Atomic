using System.Collections.Generic;
using UnityEngine;

namespace Atomic
{
    internal static class KickTracker
    {
        private const float HandshakeTimeoutSeconds = 7f;
        private static readonly Dictionary<int, float> _pendingClients = new();

        // starts waiting for a joining client to send its handshake.
        public static void TrackJoin(int clientId)
        {
            AmongUsClient client = AmongUsClient.Instance;
            if (client == null || !client.AmHost || clientId == client.ClientId)
            {
                return;
            }

            _pendingClients[clientId] = Time.time + HandshakeTimeoutSeconds;
            AtomicPlugin.Log.LogInfo($"[Handshake] Waiting for client {clientId}.");
        }

        // stops waiting for this client.
        public static void Untrack(int clientId)
        {
            _pendingClients.Remove(clientId);
        }

        // stops waiting and kicks the client if its mod list is incompatible.
        public static void ConfirmHandshake(byte playerId, bool compatible, string reason)
        {
            AmongUsClient client = AmongUsClient.Instance;
            if (client == null || !client.AmHost)
            {
                return;
            }

            int? clientId = FindClientId(playerId);
            if (!clientId.HasValue)
            {
                return;
            }

            _pendingClients.Remove(clientId.Value);
            if (!compatible && ShouldKick())
                Kick(clientId.Value, $"incompatible client ({reason})");
        }

        // checks whether any clients have taken too long to send a handshake.
        public static void CheckPending()
        {
            AmongUsClient client = AmongUsClient.Instance;
            if (client == null || !client.AmHost || _pendingClients.Count == 0)
            {
                return;
            }

            List<int> expiredClients = new List<int>();
            foreach (var pair in _pendingClients)
            {
                if (Time.time >= pair.Value)
                {
                    expiredClients.Add(pair.Key);
                }
            }

            foreach (int clientId in expiredClients)
            {
                _pendingClients.Remove(clientId);
                if (ShouldKick())
                    Kick(clientId, "missing Atomic handshake");
                else
                    AtomicPlugin.Log.LogInfo($"[Handshake] Client {clientId} has no Atomic; allowing join (compatibility enforcement disabled).");
            }
        }

        // stops waiting for every client.
        public static void Clear()
        {
            _pendingClients.Clear();
        }

        // finds the client that owns this player id.
        private static int? FindClientId(byte playerId)
        {
            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            {
                if (player == null || player.Data == null)
                {
                    continue;
                }

                if (player.Data.PlayerId == playerId)
                {
                    return player.OwnerId;
                }
            }

            return null;
        }

        // checks whether compatibility enforcement is turned on.
        private static bool ShouldKick()
        {
            return AtomicPlugin.EnforceCompatibility?.Value == true;
        }

        // removes a client from the lobby.
        private static void Kick(int clientId, string reason)
        {
            AmongUsClient client = AmongUsClient.Instance;
            if (client == null || !client.AmHost || clientId == client.ClientId)
            {
                return;
            }

            AtomicPlugin.Log.LogWarning($"[Handshake] Kicking client {clientId}: {reason}.");
            try
            {
                client.KickPlayer(clientId, false);
            }
            catch (System.Exception e)
            {
                AtomicPlugin.Log.LogError($"[Handshake] Kick failed for client {clientId}: {e}");
            }
        }
    }
}
