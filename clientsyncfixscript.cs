// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
// Fix: remote peds face their travel direction while walking/running (C# 7.3 compatible).
//
// Strategy:
// - Harmony Prefix on SyncedPed.WalkTo: fully override the original to issue the same movement tasks
//   but pass the server-sent Heading for run/sprint (fixes "face North").
// - Postfix on SmoothTransition: reinforce heading for on-foot, non-aiming peds.
// - Per-tick safety net: apply SET_PED_DESIRED_HEADING for remote on-foot, non-aiming peds.
//
// Notes:
// - Avoids Ped.ReadPosition, TaskType, and IsTaskActive (not available to resource scripts).
// - No patching of generic Function.Call<T> (avoids MonoMod NotSupported).
// - C# 7.3 compatible.

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using GTA;
using GTA.Math;
using GTA.Native;
using HarmonyLib;
using RageCoop.Client.Scripting;

namespace RageCoop.Resources.SyncFix
{
    public class Main : ClientScript
    {
        private Harmony _harmony;
        private Type _tSyncedPed;
        private Type _tEntityPool;

        // Reflection cache
        internal static PropertyInfo PI_IsLocal;
        internal static PropertyInfo PI_Speed;
        internal static PropertyInfo PI_Heading;
        internal static PropertyInfo PI_MainPed;
        internal static PropertyInfo PI_IsAiming;
        internal static PropertyInfo PI_IsInStealthMode;
        internal static PropertyInfo PI_Position;
        internal static PropertyInfo PI_Velocity;
        internal static MethodInfo   MI_Predict;
        internal static MethodInfo   MI_SmoothTransition;
        internal static FieldInfo    FI_PedsByID;

        public override void OnStart()
        {
            try
            {
                var clientAsm = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "RageCoop.Client", StringComparison.OrdinalIgnoreCase));

                if (clientAsm == null)
                {
                    Logger?.Error("[SyncFix] Could not find RageCoop.Client assembly. Aborting.");
                    return;
                }

                _tSyncedPed = clientAsm.GetType("RageCoop.Client.SyncedPed", true);
                _tEntityPool = clientAsm.GetType("RageCoop.Client.EntityPool", true);

                // Bind members we'll need
                PI_IsLocal         = _tSyncedPed.GetProperty("IsLocal",         BindingFlags.Public | BindingFlags.Instance);
                PI_Speed           = _tSyncedPed.GetProperty("Speed",           BindingFlags.Public | BindingFlags.Instance);
                PI_Heading         = _tSyncedPed.GetProperty("Heading",         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PI_MainPed         = _tSyncedPed.GetProperty("MainPed",         BindingFlags.Public | BindingFlags.Instance);
                PI_IsAiming        = _tSyncedPed.GetProperty("IsAiming",        BindingFlags.NonPublic | BindingFlags.Instance);
                PI_IsInStealthMode = _tSyncedPed.GetProperty("IsInStealthMode", BindingFlags.NonPublic | BindingFlags.Instance);
                PI_Position        = _tSyncedPed.GetProperty("Position",        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PI_Velocity        = _tSyncedPed.GetProperty("Velocity",        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                MI_Predict         = _tSyncedPed.GetMethod("Predict", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(Vector3) }, null);
                MI_SmoothTransition= _tSyncedPed.GetMethod("SmoothTransition", BindingFlags.NonPublic | BindingFlags.Instance);
                FI_PedsByID        = _tEntityPool.GetField("PedsByID", BindingFlags.Public | BindingFlags.Static);

                if (PI_IsLocal == null || PI_Speed == null || PI_Heading == null || PI_MainPed == null ||
                    PI_Position == null || PI_Velocity == null || FI_PedsByID == null || MI_SmoothTransition == null)
                {
                    Logger?.Error("[SyncFix] Failed to bind SyncedPed/EntityPool members. Aborting.");
                    return;
                }

                _harmony = new Harmony("ragecoop.syncfix.walkto.override");

                // Override WalkTo entirely (instance, no args). Prefix returns false to skip original.
                var miWalkTo = FindInstanceMethodNoArgs(_tSyncedPed, "WalkTo");
                if (miWalkTo != null)
                {
                    _harmony.Patch(miWalkTo,
                        prefix: new HarmonyMethod(typeof(WalkToOverridePatch).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)));
                }
                else
                {
                    Logger?.Warning("[SyncFix] Could not locate SyncedPed.WalkTo; this patch cannot apply.");
                }

                // Reinforce after SmoothTransition
                var miSmooth = FindInstanceMethodNoArgs(_tSyncedPed, "SmoothTransition");
                if (miSmooth != null)
                {
                    _harmony.Patch(miSmooth,
                        postfix: new HarmonyMethod(typeof(SmoothReinforcePatch).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)));
                }

                // Per-tick safety net
                API.Events.OnTick += OnTickEnforceHeading;

                Logger?.Info("[SyncFix] Installed WalkTo override + heading reinforcement (no ReadPosition/TaskType usage).");
            }
            catch (Exception ex)
            {
                Logger?.Error("[SyncFix] Error during install: " + ex);
            }
        }

        public override void OnStop()
        {
            try { API.Events.OnTick -= OnTickEnforceHeading; } catch (Exception ex) { Logger?.Warning("[SyncFix] Failed to detach tick handler: " + ex); }

            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchAll("ragecoop.syncfix.walkto.override");
                }
            }
            catch (Exception ex)
            {
                Logger?.Warning("[SyncFix] Failed to unpatch Harmony: " + ex);
            }
        }

        // Per-tick safety: enforce desired heading for remote, on-foot, non-aiming peds
        private void OnTickEnforceHeading()
        {
            try
            {
                var pedsDictObj = FI_PedsByID.GetValue(null);
                var pedsDict = pedsDictObj as System.Collections.IDictionary;
                if (pedsDict == null || pedsDict.Count == 0) return;

                foreach (System.Collections.DictionaryEntry kv in pedsDict)
                {
                    var sp = kv.Value; // SyncedPed instance
                    if (sp == null) continue;

                    var isLocalObj = PI_IsLocal.GetValue(sp, null);
                    if (isLocalObj is bool && ((bool)isLocalObj)) continue;

                    var speedObj = PI_Speed.GetValue(sp, null);
                    if (!(speedObj is byte)) continue;
                    var speed = (byte)speedObj;
                    if (speed == 0 || speed >= 4) continue;

                    var ped = PI_MainPed.GetValue(sp, null) as Ped;
                    if (ped == null || !ped.Exists()) continue;

                    bool isAiming = false;
                    if (PI_IsAiming != null)
                    {
                        try
                        {
                            var aimObj = PI_IsAiming.GetValue(sp, null);
                            if (aimObj is bool) isAiming = (bool)aimObj;
                        }
                        catch { }
                    }
                    if (isAiming) continue;

                    var headingObj = PI_Heading.GetValue(sp, null);
                    if (!(headingObj is float)) continue;
                    var heading = (float)headingObj;

                    Function.Call(Hash.SET_PED_DESIRED_HEADING, ped.Handle, heading);
                }
            }
            catch { }
        }

        private static MethodInfo FindInstanceMethodNoArgs(Type t, string name)
        {
            return t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);
        }
    }

    // Prefix override for SyncedPed.WalkTo (no Ped.ReadPosition or TaskType calls)
    public static class WalkToOverridePatch
    {
        public static bool Prefix(object __instance)
        {
            try
            {
                // Read required state via reflection
                var ped = Main.PI_MainPed.GetValue(__instance, null) as Ped;
                if (ped == null || !ped.Exists()) return true; // fall back to original if something's off

                // Clear tasks as original does
                ped.Task.ClearAll();

                // Set stealth movement flag
                bool stealth = false;
                if (Main.PI_IsInStealthMode != null)
                {
                    try
                    {
                        var stealthObj = Main.PI_IsInStealthMode.GetValue(__instance, null);
                        if (stealthObj is bool) stealth = (bool)stealthObj;
                    }
                    catch { }
                }
                Function.Call(Hash.SET_PED_STEALTH_MOVEMENT, ped, stealth, 0);

                // Compute predictPosition = Predict(Position) + Velocity
                Vector3 pos = default(Vector3);
                Vector3 vel = default(Vector3);
                try
                {
                    var posObj = Main.PI_Position.GetValue(__instance, null);
                    if (posObj is Vector3) pos = (Vector3)posObj;
                }
                catch { }
                try
                {
                    var velObj = Main.PI_Velocity.GetValue(__instance, null);
                    if (velObj is Vector3) vel = (Vector3)velObj;
                }
                catch { }

                Vector3 predict = pos;
                try
                {
                    if (Main.MI_Predict != null) predict = (Vector3)Main.MI_Predict.Invoke(__instance, new object[] { pos });
                }
                catch { }

                Vector3 predictPosition = predict + vel;

                // Distance squared between predicted and current (use Ped.Position, not ReadPosition)
                Vector3 cur = ped.Position;
                float dx = predictPosition.X - cur.X;
                float dy = predictPosition.Y - cur.Y;
                float dz = predictPosition.Z - cur.Z;
                float range = dx * dx + dy * dy + dz * dz;

                // Read speed and heading
                var speedObj = Main.PI_Speed.GetValue(__instance, null);
                if (!(speedObj is byte)) return true; // fallback
                byte speed = (byte)speedObj;

                var headingObj = Main.PI_Heading.GetValue(__instance, null);
                float heading = (headingObj is float) ? (float)headingObj : ped.Heading;

                // Aiming?
                bool isAiming = false;
                if (Main.PI_IsAiming != null)
                {
                    try
                    {
                        var aimObj = Main.PI_IsAiming.GetValue(__instance, null);
                        if (aimObj is bool) isAiming = (bool)aimObj;
                    }
                    catch { }
                }

                // Recreate original behavior but fix heading for run/sprint
                switch (speed)
                {
                    case 1:
                        if (!ped.IsWalking || range > 0.25f)
                        {
                            float nrange = range * 2f;
                            if (nrange > 1.0f) nrange = 1.0f;

                            ped.Task.GoStraightTo(predictPosition);
                            Function.Call(Hash.SET_PED_DESIRED_MOVE_BLEND_RATIO, ped.Handle, nrange);
                        }
                        break;

                    case 2:
                        if (!ped.IsRunning || range > 0.50f)
                        {
                            ped.Task.RunTo(predictPosition, true);
                            Function.Call(Hash.SET_PED_DESIRED_MOVE_BLEND_RATIO, ped.Handle, 1.0f);

                            // Reinforce facing while running (not aiming)
                            if (!isAiming)
                            {
                                Function.Call(Hash.SET_PED_DESIRED_HEADING, ped.Handle, heading);
                            }
                        }
                        break;

                    case 3:
                        if (!ped.IsSprinting || range > 0.75f)
                        {
                            // IMPORTANT: pass server-sent heading here (fixes North lock)
                            Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle,
                                predictPosition.X, predictPosition.Y, predictPosition.Z,
                                3.0f, -1, heading, 0.0f);

                            Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, ped.Handle, 1.49f);
                            Function.Call(Hash.SET_PED_DESIRED_MOVE_BLEND_RATIO, ped.Handle, 1.0f);

                            if (!isAiming)
                            {
                                Function.Call(Hash.SET_PED_DESIRED_HEADING, ped.Handle, heading);
                            }
                        }
                        break;

                    default:
                        // Minimal idle handling without TaskType
                        ped.Task.StandStill(200);
                        break;
                }

                // Call SmoothTransition like the original did, to keep positional smoothing
                try { Main.MI_SmoothTransition.Invoke(__instance, null); } catch { }

                // Skip original WalkTo
                return false;
            }
            catch
            {
                // In case anything fails, let the original run
                return true;
            }
        }
    }

    // Reinforce heading after SmoothTransition (on-foot, not aiming)
    public static class SmoothReinforcePatch
    {
        private static PropertyInfo PI_Speed, PI_Heading, PI_MainPed, PI_IsAiming;

        private static void Ensure(object inst)
        {
            if (PI_Speed != null) return;
            var t = inst.GetType();
            PI_Speed    = t.GetProperty("Speed",    BindingFlags.Public | BindingFlags.Instance);
            PI_Heading  = t.GetProperty("Heading",  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            PI_MainPed  = t.GetProperty("MainPed",  BindingFlags.Public | BindingFlags.Instance);
            PI_IsAiming = t.GetProperty("IsAiming", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        public static void Postfix(object __instance)
        {
            try
            {
                Ensure(__instance);

                var speedObj = PI_Speed.GetValue(__instance, null);
                if (!(speedObj is byte)) return;
                var speed = (byte)speedObj;
                if (speed == 0 || speed >= 4) return;

                var ped = PI_MainPed.GetValue(__instance, null) as Ped;
                if (ped == null || !ped.Exists()) return;

                bool isAiming = false;
                if (PI_IsAiming != null)
                {
                    try
                    {
                        var aimObj = PI_IsAiming.GetValue(__instance, null);
                        if (aimObj is bool) isAiming = (bool)aimObj;
                    }
                    catch { }
                }
                if (isAiming) return;

                var headingObj = PI_Heading.GetValue(__instance, null);
                if (!(headingObj is float)) return;
                var heading = (float)headingObj;

                Function.Call(Hash.SET_PED_DESIRED_HEADING, ped.Handle, heading);
            }
            catch { }
        }
    }
}
