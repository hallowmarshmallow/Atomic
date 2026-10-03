using System;

namespace Atomic
{
    internal static class Il2CppTypeRegistrar
    {
        // runs a function that registers an il2cpp type.
        public static void Enqueue(Action register)
        {
            try
            {
                register();
            }
            catch (Exception e)
            {
                AtomicPlugin.Log.LogError("Il2CppTypeRegistrar: " + e);
            }
        }

        // does nothing for now.
        public static void Tick() { }

        // does nothing for now.
        public static void FlushAll() { }
    }
}
