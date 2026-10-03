using System.Collections.Generic;
using UnityEngine;

namespace Atomic
{
    internal static class KickTracker
    {
        private const float HandshakeTimeoutSeconds = 7f;
        private static readonly Dictionary<int, float> _pendingClients = new();

        public static void TrackJoin(int clientId)
        {
            var client = AmongUsClient.Instance;
            if (client == null || !client.AmHost || clientId == client.ClientId) return;
            _pendingClients[clientId] = Time.time + HandshakeTimeoutSeconds;
            AtomicPlugin.Log.LogInfo($"[Handshake] Waiting for client {clientId}.");
        }

        public static void Untrack(int clientId) => _pendingClients.Remove(clientId);

        public static void ConfirmHandshake(byte playerId, bool compatible, string reason)
        {
            var client = AmongUsClient.Instance;
            if (client == null || !client.AmHost) return;

            int? clientId = FindClientId(playerId);
            if (!clientId.HasValue) return;
            _pendingClients.Remove(clientId.Value);
            if (!compatible && ShouldKick())
                Kick(clientId.Value, $"incompatible client ({reason})");
        }

        public static void CheckPending()
        {
            var client = AmongUsClient.Instance;
            if (client == null || !client.AmHost || _pendingClients.Count == 0) return;

            var expired = new List<int>();
            foreach (var pair in _pendingClients)
                if (Time.time >= pair.Value) expired.Add(pair.Key);

            foreach (var clientId in expired)
            {
                _pendingClients.Remove(clientId);
                if (ShouldKick())
                    Kick(clientId, "missing Atomic handshake");
                else
                    AtomicPlugin.Log.LogInfo($"[Handshake] Client {clientId} has no Atomic; allowing join (compatibility enforcement disabled).");
            }
        }

        public static void Clear() => _pendingClients.Clear();

        private static int? FindClientId(byte playerId)
        {
            foreach (var player in PlayerControl.AllPlayerControls)
                if (player != null && player.Data != null && player.Data.PlayerId == playerId)
                    return player.OwnerId;
            return null;
        }

        private static bool ShouldKick() =>
            AtomicPlugin.EnforceCompatibility?.Value == true;

        private static void Kick(int clientId, string reason)
        {
            var client = AmongUsClient.Instance;
            if (client == null || !client.AmHost || clientId == client.ClientId) return;

            AtomicPlugin.Log.LogWarning($"[Handshake] Kicking client {clientId}: {reason}.");
            try { client.KickPlayer(clientId, false); }
            catch (System.Exception e) { AtomicPlugin.Log.LogError($"[Handshake] Kick failed for client {clientId}: {e}"); }
        }
    }
}
