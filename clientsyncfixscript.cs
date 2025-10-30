// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
// Fix: remote peds face their travel direction while walking/running (C# 7.3 compatible).
//
// Strategy (no generic method patching; safe with this runtime):
// 1) Patch SyncedPed.WalkTo (Postfix) and immediately re-issue TASK_GO_STRAIGHT_TO_COORD
//    with the correct targetHeading (server-sent Heading) for Speed==2 (run) and Speed==3 (sprint).
//    We reconstruct the original predictPosition using SyncedPed.Predict(Position) + Velocity by reflection.
//    This overrides the original call that used targetHeading=0 (North).
// 2) Patch SyncedPed.SmoothTransition (Postfix) to reinforce desired heading during on-foot motion.
// 3) Add a per-tick safety net to apply SET_PED_DESIRED_HEADING for all remote on-foot, non-aiming peds.
//
// Notes:
// - Avoids patching GTA.Native.Function.Call<T> or any generic methods (which throw NotSupported in this environment).
// - C# 7.3 compatible: no modern pattern features.
// - Keep your server-side hysteresis/heading-from-motion changes; this script complements them.

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

        public override void OnStart()
        {
            try
            {
                var clientAsm = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "RageCoop.Client", StringComparison.OrdinalIgnoreCase));

                if (clientAsm == null)
                {
                    if (Logger != null) Logger.Error("[SyncFix] Could not find RageCoop.Client assembly. Aborting.");
                    return;
                }

                _tSyncedPed = clientAsm.GetType("RageCoop.Client.SyncedPed", true);
                _tEntityPool = clientAsm.GetType("RageCoop.Client.EntityPool", true);

                // Cache members via reflection
                State.PI_IsLocal   = _tSyncedPed.GetProperty("IsLocal",   BindingFlags.Public | BindingFlags.Instance);
                State.PI_Speed     = _tSyncedPed.GetProperty("Speed",     BindingFlags.Public | BindingFlags.Instance);
                State.PI_Heading   = _tSyncedPed.GetProperty("Heading",   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                State.PI_MainPed   = _tSyncedPed.GetProperty("MainPed",   BindingFlags.Public | BindingFlags.Instance);
                State.PI_IsAiming  = _tSyncedPed.GetProperty("IsAiming",  BindingFlags.NonPublic | BindingFlags.Instance);
                State.PI_Position  = _tSyncedPed.GetProperty("Position",  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                State.PI_Velocity  = _tSyncedPed.GetProperty("Velocity",  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                State.MI_Predict   = _tSyncedPed.GetMethod("Predict", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(Vector3) }, null);
                State.FI_PedsByID  = _tEntityPool.GetField("PedsByID", BindingFlags.Public | BindingFlags.Static);

                if (State.PI_IsLocal == null || State.PI_Speed == null || State.PI_Heading == null || State.PI_MainPed == null ||
                    State.PI_Position == null || State.PI_Velocity == null || State.FI_PedsByID == null)
                {
                    if (Logger != null) Logger.Error("[SyncFix] Failed to bind SyncedPed/EntityPool members. Aborting.");
                    return;
                }

                _harmony = new Harmony("ragecoop.syncfix.heading");

                // Patch SyncedPed.WalkTo (instance, no args): Postfix override with correct heading
                var miWalkTo = FindInstanceMethodNoArgs(_tSyncedPed, "WalkTo");
                if (miWalkTo != null)
                {
                    _harmony.Patch(miWalkTo,
                        postfix: new HarmonyMethod(typeof(SyncedPedPatches).GetMethod("WalkTo_Postfix", BindingFlags.Public | BindingFlags.Static)));
                }
                else
                {
                    if (Logger != null) Logger.Warning("[SyncFix] Could not locate SyncedPed.WalkTo; relying on SmoothTransition and tick safety net.");
                }

                // Patch SyncedPed.SmoothTransition Postfix
                var miSmooth = FindInstanceMethodNoArgs(_tSyncedPed, "SmoothTransition");
                if (miSmooth != null)
                {
                    _harmony.Patch(miSmooth,
                        postfix: new HarmonyMethod(typeof(SyncedPedPatches).GetMethod("SmoothTransition_Postfix", BindingFlags.Public | BindingFlags.Static)));
                }
                else
                {
                    if (Logger != null) Logger.Warning("[SyncFix] Could not locate SyncedPed.SmoothTransition.");
                }

                // Safety net: per-tick enforcement
                API.Events.OnTick += OnTickEnforceHeading;

                if (Logger != null) Logger.Info("[SyncFix] Client-side heading override installed (post-move override).");
            }
            catch (Exception ex)
            {
                if (Logger != null) Logger.Error("[SyncFix] Error during install: " + ex);
            }
        }

        public override void OnStop()
        {
            try
            {
                API.Events.OnTick -= OnTickEnforceHeading;
            }
            catch (Exception ex)
            {
                if (Logger != null) Logger.Warning("[SyncFix] Failed to detach tick handler: " + ex);
            }

            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchAll("ragecoop.syncfix.heading");
                }
            }
            catch (Exception ex)
            {
                if (Logger != null) Logger.Warning("[SyncFix] Failed to unpatch Harmony: " + ex);
            }
        }

        // Per-tick safety net for remote on-foot peds
        private void OnTickEnforceHeading()
        {
            try
            {
                var pedsDictObj = State.FI_PedsByID.GetValue(null);
                var pedsDict = pedsDictObj as IDictionary;
                if (pedsDict == null || pedsDict.Count == 0) return;

                foreach (DictionaryEntry kv in pedsDict)
                {
                    var sp = kv.Value; // SyncedPed instance
                    if (sp == null) continue;

                    var isLocalObj = State.PI_IsLocal.GetValue(sp, null);
                    if (isLocalObj is bool && ((bool)isLocalObj)) continue;

                    var speedObj = State.PI_Speed.GetValue(sp, null);
                    if (!(speedObj is byte)) continue;
                    var speed = (byte)speedObj;
                    if (speed == 0 || speed >= 4) continue;

                    var ped = State.PI_MainPed.GetValue(sp, null) as Ped;
                    if (ped == null || !ped.Exists()) continue;

                    bool isAiming = false;
                    if (State.PI_IsAiming != null)
                    {
                        try
                        {
                            var aimObj = State.PI_IsAiming.GetValue(sp, null);
                            if (aimObj is bool) isAiming = (bool)aimObj;
                        }
                        catch { }
                    }
                    if (isAiming) continue;

                    var headingObj = State.PI_Heading.GetValue(sp, null);
                    if (!(headingObj is float)) continue;
                    var heading = (float)headingObj;

                    Function.Call(Hash.SET_PED_DESIRED_HEADING, ped.Handle, heading);
                }
            }
            catch
            {
                // Silent per frame
            }
        }

        // Helpers: find instance methods safely
        private static MethodInfo FindInstanceMethodNoArgs(Type t, string name)
        {
            return t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);
        }

        // Shared reflection info
        internal static class State
        {
            public static PropertyInfo PI_IsLocal;
            public static PropertyInfo PI_Speed;
            public static PropertyInfo PI_Heading;
            public static PropertyInfo PI_MainPed;
            public static PropertyInfo PI_IsAiming;
            public static PropertyInfo PI_Position;
            public static PropertyInfo PI_Velocity;
            public static MethodInfo   MI_Predict;
            public static FieldInfo    FI_PedsByID;
        }
    }

    // Reinforcement + override patches on SyncedPed methods (public for Harmony)
    public static class SyncedPedPatches
    {
        private static PropertyInfo PI_Speed, PI_Heading, PI_MainPed, PI_IsAiming, PI_Position, PI_Velocity;
        private static MethodInfo MI_Predict;

        private static void EnsureBind(object instance)
        {
            if (PI_Speed != null) return;
            var tSyncedPed = instance.GetType();
            PI_Speed     = tSyncedPed.GetProperty("Speed",     BindingFlags.Public | BindingFlags.Instance);
            PI_Heading   = tSyncedPed.GetProperty("Heading",   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            PI_MainPed   = tSyncedPed.GetProperty("MainPed",   BindingFlags.Public | BindingFlags.Instance);
            PI_IsAiming  = tSyncedPed.GetProperty("IsAiming",  BindingFlags.NonPublic | BindingFlags.Instance);
            PI_Position  = tSyncedPed.GetProperty("Position",  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            PI_Velocity  = tSyncedPed.GetProperty("Velocity",  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            MI_Predict   = tSyncedPed.GetMethod("Predict", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(Vector3) }, null);
        }

        // After WalkTo issues movement tasks, immediately re-issue GoStraight with correct heading for run/sprint
        public static void WalkTo_Postfix(object __instance)
        {
            try
            {
                EnsureBind(__instance);

                var speedObj = PI_Speed != null ? PI_Speed.GetValue(__instance, null) : null;
                if (!(speedObj is byte)) return;
                var speed = (byte)speedObj;

                // Only on-foot run/sprint
                if (speed != 2 && speed != 3) return;

                var ped = PI_MainPed != null ? PI_MainPed.GetValue(__instance, null) as Ped : null;
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

                var headingObj = PI_Heading != null ? PI_Heading.GetValue(__instance, null) : null;
                if (!(headingObj is float)) return;
                var heading = (float)headingObj;

                // Reconstruct predictPosition = Predict(Position) + Velocity
                Vector3 pos = default(Vector3);
                Vector3 vel = default(Vector3);
                try
                {
                    var posObj = PI_Position != null ? PI_Position.GetValue(__instance, null) : null;
                    if (posObj is Vector3) pos = (Vector3)posObj;
                }
                catch { }
                try
                {
                    var velObj = PI_Velocity != null ? PI_Velocity.GetValue(__instance, null) : null;
                    if (velObj is Vector3) vel = (Vector3)velObj;
                }
                catch { }

                Vector3 predict = pos;
                try
                {
                    if (MI_Predict != null) predict = (Vector3)MI_Predict.Invoke(__instance, new object[] { pos });
                }
                catch { }

                Vector3 target = predict + vel;

                // Re-issue GoStraight with correct heading. Use speed: 3.0 for sprint, 2.0 for run.
                float moveSpeed = (speed == 3) ? 3.0f : 2.0f;
                Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle, target.X, target.Y, target.Z, moveSpeed, -1, heading, 0.0f);

                // Also set desired heading as a nudge
                Function.Call(Hash.SET_PED_DESIRED_HEADING, ped.Handle, heading);
            }
            catch
            {
                // Swallow to avoid destabilizing the client script
            }
        }

        // After SmoothTransition, reinforce desired heading for on-foot, non-aiming peds
        public static void SmoothTransition_Postfix(object __instance)
        {
            try
            {
                EnsureBind(__instance);

                var speedObj = PI_Speed != null ? PI_Speed.GetValue(__instance, null) : null;
                if (!(speedObj is byte)) return;
                var speed = (byte)speedObj;
                if (speed == 0 || speed >= 4) return;

                var ped = PI_MainPed != null ? PI_MainPed.GetValue(__instance, null) as Ped : null;
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

                var headingObj = PI_Heading != null ? PI_Heading.GetValue(__instance, null) : null;
                if (!(headingObj is float)) return;
                var heading = (float)headingObj;

                Function.Call(Hash.SET_PED_DESIRED_HEADING, ped.Handle, heading);
            }
            catch { }
        }
    }
}
