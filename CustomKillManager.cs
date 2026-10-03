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

        // sets the maximum number of nested kills.
        private const int MaxKillDepth = 2;

        private static int _killDepth;

        // tries to kill a player. on the host, true means the kill happened.
        // on other clients, true means the request was sent.
        public static bool Kill(PlayerControl killer, PlayerControl target, CustomKillOptions options = null)
        {
            if (killer == null || killer.Data == null || target == null || target.Data == null)
            {
                return false;
            }

            if (target.Data.IsDead || target.Data.Disconnected)
            {
                return false;
            }

            if (options == null)
            {
                options = new CustomKillOptions();
            }

            AmongUsClient client = AmongUsClient.Instance;
            if (client != null && client.AmHost)
            {
                if (!PerformKill(killer, target, options))
                {
                    return false;
                }

                AtomicAPI.SendRpcMethod(RpcConfirmKey, killer.Data.PlayerId, target.Data.PlayerId,
                    options.CreateDeadBody, options.TeleportKiller, options.PlayKillSound, options.ShowKillAnimation);
                return true;
            }

            AtomicAPI.SendRpcMethod(RpcRequestKey, killer.Data.PlayerId, target.Data.PlayerId,
                options.CreateDeadBody, options.TeleportKiller, options.PlayKillSound, options.ShowKillAnimation);
            return true;
        }

        // handles a kill request sent to the host.
        [AtomicRpc(RpcRequestKey)]
        private static void OnRequestCustomKill(byte senderId, byte killerId, byte targetId, bool createDeadBody, bool teleportKiller, bool playKillSound, bool showKillAnimation)
        {
            AmongUsClient client = AmongUsClient.Instance;
            if (client == null || !client.AmHost)
            {
                return;
            }

            PlayerControl killer = FindPlayer(killerId);
            PlayerControl target = FindPlayer(targetId);
            if (killer == null || target == null || target.Data == null || target.Data.IsDead || target.Data.Disconnected)
            {
                return;
            }

            var options = new CustomKillOptions
            {
                CreateDeadBody = createDeadBody,
                TeleportKiller = teleportKiller,
                PlayKillSound = playKillSound,
                ShowKillAnimation = showKillAnimation,
            };

            // confirms the kill only if the host allows it.
            if (!PerformKill(killer, target, options))
            {
                return;
            }

            AtomicAPI.SendRpcMethod(RpcConfirmKey, killerId, targetId, createDeadBody, teleportKiller, playKillSound, showKillAnimation);
        }

        // performs a kill confirmed by the host.
        [AtomicRpc(RpcConfirmKey)]
        private static void OnConfirmCustomKill(byte senderId, byte killerId, byte targetId, bool createDeadBody, bool teleportKiller, bool playKillSound, bool showKillAnimation)
        {
            AmongUsClient client = AmongUsClient.Instance;
            if (client != null && client.AmHost)
            {
                return;
            }

            PlayerControl killer = FindPlayer(killerId);
            PlayerControl target = FindPlayer(targetId);
            if (killer == null || target == null || target.Data == null || target.Data.IsDead)
            {
                return;
            }

            PerformKill(killer, target, new CustomKillOptions
            {
                CreateDeadBody = createDeadBody,
                TeleportKiller = teleportKiller,
                PlayKillSound = playKillSound,
                ShowKillAnimation = showKillAnimation,
            });
        }

        // kills the target if they are still alive.
        private static bool PerformKill(PlayerControl killer, PlayerControl target, CustomKillOptions options)
        {
            if (target == null || target.Data == null || target.Data.IsDead)
            {
                return false;
            }

            // stops too many nested kills to prevent a loop.
            if (_killDepth >= MaxKillDepth)
            {
                AtomicPlugin.Log.LogError("CustomKillManager.PerformKill: kill depth " + _killDepth + " exceeded; blocking loop" + target.Data.PlayerId);
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

        // checks kill rules, then carries out the kill.
        private static bool PerformKillCore(PlayerControl killer, PlayerControl target, CustomKillOptions options)
        {
            // runs the usual kill checks, including the medic shield, so other mods can block this kill.
            // reflection avoids a dependency cycle with MarshAPI.
            try
            {
                if (!RaiseMurderEvent("RaiseBeforeMurder", killer, target))
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                AtomicPlugin.Log.LogError("CustomKillManager.BeforeMurder gate failed: " + e);
            }

            // an event handler may have already killed the target, so check again before continuing.
            if (target == null || target.Data == null || target.Data.IsDead || target.Data.Disconnected)
            {
                return false;
            }

            try
            {
                if (options.CreateDeadBody)
                {
                    SpawnDeadBody(killer, target);
                }

                if (options.PlayKillSound)
                {
                    if (killer.AmOwner && killer.KillSfx != null)
                    {
                        SoundManager.Instance?.PlaySound(killer.KillSfx, false, 0.8f);
                    }
                }

                if (options.ShowKillAnimation)
                {
                    if (target.AmOwner)
                    {
                        try
                        {
                            HudManager.Instance?.KillOverlay?.ShowKillAnimation(killer.Data, target.Data);
                        }
                        catch (Exception e)
                        {
                            AtomicPlugin.Log.LogError("CustomKillManager.ShowKillAnimation failed: " + e);
                        }
                    }
                }

                // sets the ghost layer only if it exists; -1 is not a valid layer.
                int ghostLayer = LayerMask.NameToLayer("Ghost");
                if (ghostLayer >= 0) target.gameObject.layer = ghostLayer;

                target.Die(DeathReason.Kill, killer);

                try
                {
                    RaiseMurderEvent("RaiseAfterMurder", killer, target);
                }
                catch (Exception e)
                {
                    AtomicPlugin.Log.LogError("CustomKillManager.AfterMurder failed: " + e);
                }

                if (options.TeleportKiller)
                {
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
            }
            catch (Exception e)
            {
                AtomicPlugin.Log.LogError("CustomKillManager.PerformKill failed: " + e);
                return false;
            }

            return true;
        }

        // creates a dead body at the target's position.
        private static void SpawnDeadBody(PlayerControl killer, PlayerControl target)
        {
            KillAnimation anim = null;
            if (killer.KillAnimations != null)
            {
                foreach (KillAnimation candidate in killer.KillAnimations)
                {
                    if (candidate == null || candidate.bodyPrefab == null)
                    {
                        continue;
                    }

                    anim = candidate;
                    break;
                }
            }

            if (anim == null)
            {
                AtomicPlugin.Log.LogWarning("CustomKillManager.SpawnDeadBody: no KillAnimation with a bodyPrefab found on killer.KillAnimations, skipping dead body");
                return;
            }

            var body = UnityEngine.Object.Instantiate(anim.bodyPrefab);
            body.ParentId = target.Data.PlayerId;

            if (body.MyRend != null)
            {
                try
                {
                    target.SetPlayerMaterialColors(body.MyRend);
                }
                catch (Exception e)
                {
                    AtomicPlugin.Log.LogError("CustomKillManager.SpawnDeadBody color failed: " + e);
                }
            }

            Vector3 pos = target.transform.position + anim.BodyOffset;
            pos.z = pos.y / 1000f;
            body.transform.position = pos;
        }

        // calls MarshAPI's murder event by name to avoid a dependency cycle.
        private static bool RaiseMurderEvent(string raiser, PlayerControl killer, PlayerControl target)
        {
            Type events = System.Type.GetType("MarshAPI.GameEvents, MarshAPI");
            if (events == null)
            {
                return true;
            }

            System.Reflection.MethodInfo method = events.GetMethod(raiser,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (method == null)
            {
                return true;
            }

            object cancelledResult = method.Invoke(null, new object[] { killer, target });
            if (cancelledResult is bool cancelled)
            {
                return cancelled;
            }

            return true;
        }

        // finds a player by their id.
        private static PlayerControl FindPlayer(byte playerId)
        {
            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            {
                if (player == null || player.Data == null)
                {
                    continue;
                }

                if (player.Data.PlayerId == playerId)
                {
                    return player;
                }
            }

            return null;
        }
    }
}
