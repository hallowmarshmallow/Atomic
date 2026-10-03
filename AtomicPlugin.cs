using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace Atomic
{
    [BepInPlugin(Guid, "Atomic", Version)]
    public class AtomicPlugin : BasePlugin
    {
        public const string Guid = "atomic";
        public const string Version = "1.1.0";

        public static ManualLogSource Log;

        // Patch classes named by [Diagnostics] SkipPatchNames, matched as case-insensitive
        // substrings.
        private static string[] _skipPatterns = Array.Empty<string>();

        // Whether each patch is named in the log as it is attempted.
        private static bool _tracePatches;

        // When true, the host kicks players who never send a Atomic handshake
        // (unmodded/vanilla clients) or whose handshake is incompatible, after the grace
        // period. Leave false (default) for host-only mods so vanilla clients can join a modded
        // lobby.
        public static ConfigEntry<bool> EnforceCompatibility { get; private set; }

        // All patch types from Patches.cs, in the order they should be applied. No coroutine
        // (IEnumerator) entry points are patched: those segfault during Harmony detour
        // installation on Linux IL2CPP.
        private static readonly Type[] PatchTypes = new[]
        {
            // Core networking (must succeed)
            typeof(PlayerControl_HandleRpc_Patch),
            typeof(AmongUsClient_OnPlayerJoined_Patch),
            typeof(AmongUsClient_OnPlayerLeft_Patch),
            typeof(AmongUsClient_OnGameJoined_Patch),

            // Lobby lifecycle
            typeof(GameStartManager_Start_Patch),
            typeof(GameStartManager_Update_Patch),

            // HUD tick (il2cpp registrar)
            typeof(HudManager_FixedUpdate_Il2CppTypeRegistrar_Patch),

            // Game events
            typeof(MeetingHud_Start_Patch),
            typeof(PlayerControl_MurderPlayer_Patch),
            typeof(PlayerControl_Die_Patch),
            typeof(RoleBehaviour_OnAssign_Patch),

            // Game started (non coroutine, safe to detour)
            typeof(HudManager_Start_GameStarted_Patch),
        };

        public override void Load()
        {
            Log = base.Log;

            EnforceCompatibility = Config.Bind(
                "Handshake", "EnforceCompatibility", false,
                "Kick players who are missing Atomic or have a mismatched mod set after the handshake grace period. Leave false for host-only mods so vanilla/unmodded clients can join.");

            // Read before the patch loop, for the same reason MarshAPI reads its own:
            // a bisect that depends on surviving part of the load is not a bisect.
            _skipPatterns = Config.Bind(
                "Diagnostics", "SkipPatchNames", string.Empty,
                "Comma-separated substrings of patch class names to leave uninstalled, " +
                "matched case-insensitively. Set this to bisect a startup crash: the log names " +
                "every patch as it is attempted, so the last line before a crash is the patch " +
                "to skip. Empty installs everything, which is the supported configuration.")
                .Value
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(pattern => pattern.Trim())
                .Where(pattern => pattern.Length > 0)
                .ToArray();

            _tracePatches = Config.Bind(
                "Diagnostics", "PatchTrace", false,
                "Log each patch class by name as it is installed. Turns itself on whenever " +
                "SkipPatchNames is set, because that is a bisect and a bisect needs the names.")
                .Value || _skipPatterns.Length > 0;

            // The kill-freeze instrument: a one-line log at every stage of the custom
            // kill pipeline (CustomKillManager.PerformKillCore). Off by default; a
            // frozen "neutral/crewmate kill" session leaves the stage it hung in as
            // the last [KillTrace] line in BepInEx/LogOutput.log.
            CustomKillManager.TraceEnabled = Config.Bind(
                "Diagnostics", "KillTrace", false,
                "Log every stage of the custom kill pipeline. Diagnoses a game that " +
                "freezes on a kill: the last [KillTrace] line names the stage.")
                .Value;

            if (_skipPatterns.Length > 0)
                Log.LogWarning("Atomic: SkipPatchNames is set - patches matching " +
                               string.Join(", ", _skipPatterns) + " will NOT be installed.");

            var harmony = new Harmony(Guid);
            int applied = 0;
            int skipped = 0;

            foreach (var patchType in PatchTypes)
            {
                if (SafePatch(harmony, patchType))
                    applied++;
                else
                    skipped++;
            }

            if (skipped > 0)
                Log.LogWarning($"Atomic: {skipped} patch(es) skipped (see warnings above). " +
                               $"{applied}/{PatchTypes.Length} patches active.");
            else
                Log.LogInfo($"Atomic: all {applied} patches applied.");

            AtomicAPI.RegisterRpcMethods(typeof(CustomKillManager));

            Log.LogInfo("Atomic loaded.");
        }

        // Attempts to install a single Harmony patch class. Returns true on success.
        private bool SafePatch(Harmony harmony, Type patchType)
        {
            string name = patchType.Name;

            if (SkippedByConfig(name))
            {
                Log.LogWarning($"Skipping patch [{name}]: matched SkipPatchNames.");
                return false;
            }

            try
            {
                // Written before Harmony resolves anything: installation is where the
                // native side can die, and a crash takes the log's last line with it.
                if (_tracePatches) Log.LogInfo($"Atomic: patching [{name}]");

                if (!ValidatePatchTarget(patchType))
                {
                    Log.LogWarning($"Skipping patch [{name}]: target method not found in current game version.");
                    return false;
                }

                harmony.CreateClassProcessor(patchType).Patch();
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning($"Skipping patch [{name}]: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        // Whether name matches the configured skip list.
        private static bool SkippedByConfig(string name)
        {
            foreach (var pattern in _skipPatterns)
                if (name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        // Resolves the target type and method from [HarmonyPatch] attributes. Returns false if
        // the target type or method cannot be found.
        private static bool ValidatePatchTarget(Type patchType)
        {
            var attrs = HarmonyMethodExtensions.GetFromType(patchType);
            if (attrs == null || attrs.Count == 0) return true; // no target info, let Harmony decide

            var merged = HarmonyMethod.Merge(attrs);
            if (merged.declaringType == null) return true; // manual patch, skip pre-flight

            // Check that the declaring type is loadable
            try
            {
                // Force the IL2CPP type to initialise, if it doesn't exist this throws
                var _ = merged.declaringType.FullName;
            }
            catch
            {
                return false;
            }

            // If a method name is specified, check that it resolves
            if (!string.IsNullOrEmpty(merged.methodName))
            {
                try
                {
                    var method = AccessTools.Method(
                        merged.declaringType,
                        merged.methodName,
                        merged.argumentTypes);
                    if (method == null) return false;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }
    }
}
