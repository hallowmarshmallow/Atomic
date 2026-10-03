using System;
using System.Collections.Generic;

namespace Atomic
{
    internal static class RpcIdAllocator
    {
        private const int RangeStart = 212;
        private const int RangeEnd = 250;

        private static readonly List<string> _pendingKeys = new();
        private static Dictionary<string, byte> _finalized;

        // saves a key so it can receive an rpc id.
        public static void Reserve(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            if (_finalized != null)
            {
                AtomicPlugin.Log.LogError($"[RpcIdAllocator] '{key}' reserved after ids were already finalized; it may not match other peers.");
                return;
            }

            if (!_pendingKeys.Contains(key))
            {
                _pendingKeys.Add(key);
            }
        }

        // returns the rpc id assigned to this key.
        public static byte GetId(string key)
        {
            if (_finalized == null)
            {
                FinalizeIds();
            }

            byte id;
            if (_finalized.TryGetValue(key, out id))
            {
                return id;
            }

            AtomicPlugin.Log.LogError($"[RpcIdAllocator] '{key}' was never reserved.");
            return 0;
        }

        // assigns ids to all saved keys in a consistent order.
        private static void FinalizeIds()
        {
            List<string> sortedKeys = new List<string>(_pendingKeys);
            sortedKeys.Sort(StringComparer.Ordinal);

            _finalized = new Dictionary<string, byte>();
            int next = RangeStart;
            foreach (string key in sortedKeys)
            {
                if (next > RangeEnd)
                {
                    AtomicPlugin.Log.LogError($"[RpcIdAllocator] Ran out of RPC id space, '{key}' was not assigned.");
                    continue;
                }

                _finalized[key] = (byte)next;
                next++;
            }

            AtomicPlugin.Log.LogInfo("[RpcIdAllocator] Finalized RPC ids: " + string.Join(", ", _finalized.Keys));
        }
    }
}
