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

        // saves the function that handles this rpc id.
        public static void RegisterHandler(byte callId, Action<byte, MessageReader> handler)
        {
            _handlers[callId] = handler;
        }

        // handles an incoming rpc message if Atomic owns its id.
        public static bool TryDispatch(PlayerControl sender, byte callId, MessageReader reader)
        {
            AtomicRpc.EnsureFlushed();
            if (sender == null)
            {
                return false;
            }

            if (sender.Data == null)
            {
                return false;
            }

            byte senderId = sender.Data.PlayerId;

            if (callId == RpcHandshake)
            {
                HandleHandshake(senderId, reader);
                return true;
            }

            Action<byte, MessageReader> handler;
            if (!_handlers.TryGetValue(callId, out handler))
            {
                return false;
            }

            handler(senderId, reader);
            return true;
        }

        // sends an rpc message to the lobby.
        public static void SendRpc(byte callId, Action<MessageWriter> writePayload)
        {
            AmongUsClient client = AmongUsClient.Instance;
            PlayerControl localPlayer = PlayerControl.LocalPlayer;

            if (client == null || localPlayer == null)
            {
                return;
            }

            try
            {
                MessageWriter writer = client.StartRpcImmediately(
                    localPlayer.NetId, callId, SendOption.Reliable, -1);

                if (writePayload != null)
                {
                    writePayload(writer);
                }

                client.FinishRpcImmediately(writer);
            }
            catch (Exception e)
            {
                AtomicPlugin.Log.LogError("SendRpc failed: " + e);
            }
        }

        // sends this client's mod list to the lobby.
        public static void SendHandshake()
        {
            AmongUsClient client = AmongUsClient.Instance;
            PlayerControl localPlayer = PlayerControl.LocalPlayer;

            if (client == null || localPlayer == null || localPlayer.Data == null)
            {
                return;
            }

            List<(string mod, string version)> mods = new List<(string mod, string version)>(
                AtomicAPI.GetLocalMods());
            LobbyTracker.SetPlayerMods(localPlayer.Data.PlayerId, mods);

            try
            {
                MessageWriter writer = client.StartRpcImmediately(
                    localPlayer.NetId, RpcHandshake, SendOption.Reliable, -1);
                writer.Write(HandshakeProtocolVersion);
                writer.Write(AtomicPlugin.Version);
                writer.Write((byte)mods.Count);

                foreach (var modInfo in mods)
                {
                    writer.Write(modInfo.mod);
                    writer.Write(modInfo.version);
                }

                client.FinishRpcImmediately(writer);
                AtomicPlugin.Log.LogDebug($"Handshake sent: protocol={HandshakeProtocolVersion}, mods={mods.Count}.");
            }
            catch (Exception e)
            {
                AtomicPlugin.Log.LogError("SendHandshake failed: " + e);
            }
        }

        // reads and checks another player's mod list.
        public static void HandleHandshake(byte senderId, MessageReader reader)
        {
            try
            {
                byte protocolVersion = reader.ReadByte();
                string remoteAtomicVersion = reader.ReadString();
                byte modCount = reader.ReadByte();

                if (modCount > MaxAdvertisedMods)
                {
                    throw new InvalidOperationException($"invalid mod count {modCount}");
                }

                var mods = new List<(string mod, string version)>(modCount);
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < modCount; i++)
                {
                    string modName = reader.ReadString();
                    string modVersion = reader.ReadString();

                    if (string.IsNullOrWhiteSpace(modName) || string.IsNullOrWhiteSpace(modVersion))
                    {
                        throw new InvalidOperationException("invalid or duplicate mod entry");
                    }

                    if (!names.Add(modName))
                    {
                        throw new InvalidOperationException("invalid or duplicate mod entry");
                    }

                    mods.Add((modName, modVersion));
                }

                LobbyTracker.SetPlayerMods(senderId, mods);
                AtomicAPI.FirePlayerModded(senderId, mods);

                bool protocolMatches = protocolVersion == HandshakeProtocolVersion;
                bool atomicVersionMatches = remoteAtomicVersion == AtomicPlugin.Version;
                bool modListMatches = LobbyTracker.HasExactModSet(mods, AtomicAPI.GetLocalMods());
                bool compatible = protocolMatches && atomicVersionMatches && modListMatches;

                string reason = null;
                if (!compatible)
                {
                    reason = $"protocol={protocolVersion}, Atomic={remoteAtomicVersion}, mod set mismatch";
                }

                KickTracker.ConfirmHandshake(senderId, compatible, reason);

                AtomicPlugin.Log.LogInfo(
                    $"Handshake from player {senderId}: protocol={protocolVersion}, Atomic={remoteAtomicVersion}, mods={mods.Count}, compatible={compatible}.");
            }
            catch (Exception e)
            {
                AtomicPlugin.Log.LogWarning($"Rejected malformed handshake from player {senderId}: {e.Message}");
                KickTracker.ConfirmHandshake(senderId, false, "malformed handshake");
            }
        }
    }
}
