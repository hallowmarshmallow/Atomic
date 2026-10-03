using System;
using UnityEngine;

namespace Atomic
{
    public sealed class CustomKillOptions
    {
        public bool CreateDeadBody = true;
        public bool TeleportKiller = true;
        public bool PlayKillSound = true;
        public bool ShowKillAnimation = true;
        public MurderResultFlags ResultFlags = MurderResultFlags.Succeeded;
    }

    public static class CustomKillManager
    {
        private const string RpcRequestKey = "atomic.RequestCustomKill";
        private const string RpcConfirmKey = "atomic.ConfirmCustomKill";

        // How deep one kill may nest inside another.
        private const int MaxKillDepth = 2;

        private static int _killDepth;

        // Set from [Diagnostics] KillTrace (see AtomicPlugin). Default off.
        internal static bool TraceEnabled;

        private static void Trace(string message)
        {
            if (TraceEnabled) AtomicPlugin.Log.LogInfo("[KillTrace] " + message);
        }

        // Runs a custom kill. Returns whether a kill actually happened.
        public static bool Kill(PlayerControl killer, PlayerControl target, CustomKillOptions options = null)
        {
            if (killer == null || killer.Data == null || target == null || target.Data == null) return false;
            if (target.Data.IsDead || target.Data.Disconnected) return false;

            options ??= new CustomKillOptions();

            var client = AmongUsClient.Instance;
            if (client != null && client.AmHost)
            {
                if (!PerformKill(killer, target, options)) return false;

                AtomicAPI.SendRpcMethod(RpcConfirmKey, killer.Data.PlayerId, target.Data.PlayerId,
                    options.CreateDeadBody, options.TeleportKiller, options.PlayKillSound, options.ShowKillAnimation);
                return true;
            }

            AtomicAPI.SendRpcMethod(RpcRequestKey, killer.Data.PlayerId, target.Data.PlayerId,
                options.CreateDeadBody, options.TeleportKiller, options.PlayKillSound, options.ShowKillAnimation);
            return true;
        }

        [AtomicRpc(RpcRequestKey)]
        private static void OnRequestCustomKill(byte senderId, byte killerId, byte targetId, bool createDeadBody, bool teleportKiller, bool playKillSound, bool showKillAnimation)
        {
            var client = AmongUsClient.Instance;
            if (client == null || !client.AmHost) return;

            var killer = FindPlayer(killerId);
            var target = FindPlayer(targetId);
            if (killer == null || target == null || target.Data == null || target.Data.IsDead || target.Data.Disconnected) return;

            var options = new CustomKillOptions
            {
                CreateDeadBody = createDeadBody,
                TeleportKiller = teleportKiller,
                PlayKillSound = playKillSound,
                ShowKillAnimation = showKillAnimation,
            };

            // Only echo a kill the local gate let through; see the remarks on Kill.
            if (!PerformKill(killer, target, options)) return;
            AtomicAPI.SendRpcMethod(RpcConfirmKey, killerId, targetId, createDeadBody, teleportKiller, playKillSound, showKillAnimation);
        }

        [AtomicRpc(RpcConfirmKey)]
        private static void OnConfirmCustomKill(byte senderId, byte killerId, byte targetId, bool createDeadBody, bool teleportKiller, bool playKillSound, bool showKillAnimation)
        {
            var client = AmongUsClient.Instance;
            if (client != null && client.AmHost) return;

            var killer = FindPlayer(killerId);
            var target = FindPlayer(targetId);
            if (killer == null || target == null || target.Data == null || target.Data.IsDead) return;

            PerformKill(killer, target, new CustomKillOptions
            {
                CreateDeadBody = createDeadBody,
                TeleportKiller = teleportKiller,
                PlayKillSound = playKillSound,
                ShowKillAnimation = showKillAnimation,
            });
        }

        // Performs the kill and reports whether it happened. False means a guard stopped it, a
        // dead target, the BeforeMurder gate (the Medic shield), or a kill nested too deep, and
        // the caller must not tell anyone a kill occurred.
        private static bool PerformKill(PlayerControl killer, PlayerControl target, CustomKillOptions options)
        {
            if (target == null || target.Data == null || target.Data.IsDead) return false;

            // A subscriber to the murder gate that kills through the same gate
            // re-enters this method; uncapped that recursion never returns (see
            // MaxKillDepth).
            if (_killDepth >= MaxKillDepth)
            {
                AtomicPlugin.Log.LogError("CustomKillManager.PerformKill: kill depth " + _killDepth +
                    " exceeded; blocking nested kill of player " + target.Data.PlayerId +
                    " (a BeforeMurder subscriber is killing through the kill event).");
                return false;
            }

            _killDepth++;
            try
            {
                return PerformKillCore(killer, target, options);
            }
            finally
            {
                _killDepth--;
            }
        }

        private static bool PerformKillCore(PlayerControl killer, PlayerControl target, CustomKillOptions options)
        {
            // The vanilla murder pipeline runs its guards here (the Medic shield among them),
            // so a custom kill that skipped MurderPlayer would skip them too. This raises the
            // event the MurderPlayer patch raises, so a subscriber sees one path either way and a
            // cancelled event cancels this kill. Raised by reflection: Atomic cannot reference
            // MarshAPI.
            try
            {
                Trace("gate killer=" + killer.Data.PlayerId + " target=" + target.Data.PlayerId);
                if (!RaiseMurderEvent("RaiseBeforeMurder", killer, target))
                {
                    Trace("cancelled by the BeforeMurder gate");
                    return false;
                }
            }
            catch (Exception e)
            {
                AtomicPlugin.Log.LogError("CustomKillManager.BeforeMurder gate failed: " + e);
            }

            // A gate subscriber may have killed the target itself (Lovers' heartbreak
            // fires through this same path the frame a lover dies). Proceeding would
            // double-kill a player whose Data already says dead.
            if (target == null || target.Data == null || target.Data.IsDead || target.Data.Disconnected)
            {
                Trace("skip: target died inside the BeforeMurder gate");
                return false;
            }

            try
            {
                if (options.CreateDeadBody)
                {
                    Trace("dead body");
                    SpawnDeadBody(killer, target);
                }

                if (options.PlayKillSound && killer.AmOwner && killer.KillSfx != null)
                {
                    Trace("kill sound");
                    SoundManager.Instance?.PlaySound(killer.KillSfx, false, 0.8f);
                }

                if (options.ShowKillAnimation && target.AmOwner)
                {
                    Trace("kill overlay");
                    try { HudManager.Instance?.KillOverlay?.ShowKillAnimation(killer.Data, target.Data); }
                    catch (Exception e) { AtomicPlugin.Log.LogError("CustomKillManager.ShowKillAnimation failed: " + e); }
                }

                // Die() ghosts the player itself; this layer write is only a visual
                // assist, and NameToLayer answers -1 on a build without the layer, 
                // assigning -1 is a native error, not a no-op.
                int ghostLayer = LayerMask.NameToLayer("Ghost");
                if (ghostLayer >= 0) target.gameObject.layer = ghostLayer;

                Trace("Die");
                target.Die(DeathReason.Kill, killer);

                try
                {
                    Trace("after-murder event");
                    RaiseMurderEvent("RaiseAfterMurder", killer, target);
                }
                catch (Exception e)
                {
                    AtomicPlugin.Log.LogError("CustomKillManager.AfterMurder failed: " + e);
                }

                if (options.TeleportKiller)
                {
                    Trace("teleport killer");
                    Vector2 pos = target.GetTruePosition();
                    if (killer.NetTransform != null)
                    {
                        killer.NetTransform.SnapTo(pos);
                        killer.NetTransform.RpcSnapTo(pos);
                    }
                    else
                    {
                        killer.transform.position = new Vector3(pos.x, pos.y, killer.transform.position.z);
                    }
                }

                Trace("done");
            }
            catch (Exception e)
            {
                AtomicPlugin.Log.LogError("CustomKillManager.PerformKill failed: " + e);
                return false;
            }

            return true;
        }

        private static void SpawnDeadBody(PlayerControl killer, PlayerControl target)
        {
            KillAnimation anim = null;
            if (killer.KillAnimations != null)
            {
                foreach (var candidate in killer.KillAnimations)
                {
                    if (candidate == null || candidate.bodyPrefab == null) continue;
                    anim = candidate;
                    break;
                }
            }

            if (anim == null)
            {
                AtomicPlugin.Log.LogWarning("CustomKillManager.SpawnDeadBody: no KillAnimation with a bodyPrefab found on killer.KillAnimations, skipping dead body.");
                return;
            }

            var body = UnityEngine.Object.Instantiate(anim.bodyPrefab);
            body.ParentId = target.Data.PlayerId;

            if (body.MyRend != null)
            {
                try { target.SetPlayerMaterialColors(body.MyRend); }
                catch (Exception e) { AtomicPlugin.Log.LogError("CustomKillManager.SpawnDeadBody color failed: " + e); }
            }

            Vector3 pos = target.transform.position + anim.BodyOffset;
            pos.z = pos.y / 1000f;
            body.transform.position = pos;
        }

        // Calls MarshAPI.GameEvents' internal murder raisers by name. Reflection because this
        // assembly sits underneath MarshAPI in the project graph; a direct call would be a
        // cycle.
        private static bool RaiseMurderEvent(string raiser, PlayerControl killer, PlayerControl target)
        {
            var events = System.Type.GetType("MarshAPI.GameEvents, MarshAPI");
            if (events == null) return true; // API absent: nothing to cancel through

            var method = events.GetMethod(raiser,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (method == null) return true;

            var cancelledObj = method.Invoke(null, new object[] { killer, target });
            return cancelledObj is bool cancelled ? cancelled : true;
        }

        private static PlayerControl FindPlayer(byte playerId)
        {
            foreach (var p in PlayerControl.AllPlayerControls)
                if (p != null && p.Data != null && p.Data.PlayerId == playerId)
                    return p;
            return null;
        }
    }
}
