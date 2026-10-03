using System;

namespace Atomic
{
    internal static class Il2CppTypeRegistrar
    {
        public static void Enqueue(Action register)
        {
            try { register(); }
            catch (Exception e) { AtomicPlugin.Log.LogError("Il2CppTypeRegistrar: " + e); }
        }

        public static void Tick() { }

        public static void FlushAll() { }
    }
}
