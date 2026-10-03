using System;
using System.Collections.Generic;
using Hazel;

namespace Atomic
{
    internal static class NetworkManager
    {
        public const byte RpcHandshake = 211;
        private const byte HandshakeProtocolVersion = 1;
        private const byte MaxAdvertisedMods = 32;

        private static readonly Dictionary<byte, Action<byte, MessageReader>> _handlers = new();

        public static void RegisterHandler(byte callId, Action<byte, MessageReader> handler) => _handlers[callId] = handler;

        public static bool TryDispatch(PlayerControl sender, byte callId, MessageReader reader)
        {
            AtomicRpc.EnsureFlushed();
            if (sender == null || sender.Data == null) return false;

            if (callId == RpcHandshake)
            {
                HandleHandshake(sender.Data.PlayerId, reader);
                return true;
            }

            if (!_handlers.TryGetValue(callId, out var handler)) return false;
            handler(sender.Data.PlayerId, reader);
            return true;
        }

        public static void SendRpc(byte callId, Action<MessageWriter> writePayload)
        {
            var client = AmongUsClient.Instance;
            var local = PlayerControl.LocalPlayer;
            if (client == null || local == null) return;

            try
            {
                var writer = client.StartRpcImmediately(local.NetId, callId, SendOption.Reliable, -1);
                writePayload?.Invoke(writer);
                client.FinishRpcImmediately(writer);
            }
            catch (Exception e) { AtomicPlugin.Log.LogError("SendRpc failed: " + e); }
        }

        public static void SendHandshake()
        {
            var client = AmongUsClient.Instance;
            var local = PlayerControl.LocalPlayer;
            if (client == null || local == null || local.Data == null) return;

            var mods = new List<(string mod, string version)>(AtomicAPI.GetLocalMods());
            LobbyTracker.SetPlayerMods(local.Data.PlayerId, mods);

            try
            {
                var writer = client.StartRpcImmediately(local.NetId, RpcHandshake, SendOption.Reliable, -1);
                writer.Write(HandshakeProtocolVersion);
                writer.Write(AtomicPlugin.Version);
                writer.Write((byte)mods.Count);
                foreach (var (mod, version) in mods)
                {
                    writer.Write(mod);
                    writer.Write(version);
                }
                client.FinishRpcImmediately(writer);
                AtomicPlugin.Log.LogDebug($"Handshake sent: protocol={HandshakeProtocolVersion}, mods={mods.Count}.");
            }
            catch (Exception e) { AtomicPlugin.Log.LogError("SendHandshake failed: " + e); }
        }

        public static void HandleHandshake(byte senderId, MessageReader reader)
        {
            try
            {
                byte protocol = reader.ReadByte();
                string atomicVersion = reader.ReadString();
                byte count = reader.ReadByte();
                if (count > MaxAdvertisedMods) throw new InvalidOperationException($"invalid mod count {count}");

                var mods = new List<(string mod, string version)>(count);
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    string mod = reader.ReadString();
                    string version = reader.ReadString();
                    if (string.IsNullOrWhiteSpace(mod) || string.IsNullOrWhiteSpace(version) || !names.Add(mod))
                        throw new InvalidOperationException("invalid or duplicate mod entry");
                    mods.Add((mod, version));
                }

                LobbyTracker.SetPlayerMods(senderId, mods);
                AtomicAPI.FirePlayerModded(senderId, mods);

                bool compatible = protocol == HandshakeProtocolVersion && atomicVersion == AtomicPlugin.Version &&
                                  LobbyTracker.HasExactModSet(mods, AtomicAPI.GetLocalMods());
                KickTracker.ConfirmHandshake(senderId, compatible,
                    compatible ? null : $"protocol={protocol}, Atomic={atomicVersion}, mod set mismatch");

                AtomicPlugin.Log.LogInfo($"Handshake from player {senderId}: protocol={protocol}, Atomic={atomicVersion}, mods={mods.Count}, compatible={compatible}.");
            }
            catch (Exception e)
            {
                AtomicPlugin.Log.LogWarning($"Rejected malformed handshake from player {senderId}: {e.Message}");
                KickTracker.ConfirmHandshake(senderId, false, "malformed handshake");
            }
        }
    }
}
