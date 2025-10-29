// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
// Fix: remote peds face their travel direction while walking/running (C# 7.3 compatible).
//
// Strategy (no generic method patching; avoids MonoMod NotSupportedException):
// 1) Patch SyncedPed.WalkTo (Postfix) and, when sprinting (Speed == 3), immediately re-issue
//    TASK_GO_STRAIGHT_TO_COORD with the server-provided Heading so the targetHeading is NOT 0.
//    We use a tiny forward offset from current ped position as the coord target to avoid fighting the current path.
// 2) Reinforce heading after WalkTo and SmoothTransition using SET_PED_DESIRED_HEADING.
// 3) Add a per-tick safety net to keep non-aiming, on-foot peds aligned.
//
// Notes:
// - No C# 9 features used. No patching of GTA.Native.Function.Call generic/void overloads.
// - Logger.Warning overload uses a single string.
// - If your build previously tried to patch Function.Call<T>, remove that; Harmony cannot patch generic
//   method definitions in this environment and will throw NotSupportedException via MonoMod.

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
                State.PI_IsLocal  = _tSyncedPed.GetProperty("IsLocal",  BindingFlags.Public | BindingFlags.Instance);
                State.PI_Speed    = _tSyncedPed.GetProperty("Speed",    BindingFlags.Public | BindingFlags.Instance);
                State.PI_Heading  = _tSyncedPed.GetProperty("Heading",  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                State.PI_MainPed  = _tSyncedPed.GetProperty("MainPed",  BindingFlags.Public | BindingFlags.Instance);
                State.PI_IsAiming = _tSyncedPed.GetProperty("IsAiming", BindingFlags.NonPublic | BindingFlags.Instance);
                State.FI_PedsByID = _tEntityPool.GetField("PedsByID", BindingFlags.Public | BindingFlags.Static);

                if (State.PI_IsLocal == null || State.PI_Speed == null || State.PI_Heading == null || State.PI_MainPed == null || State.FI_PedsByID == null)
                {
                    if (Logger != null) Logger.Error("[SyncFix] Failed to bind SyncedPed/EntityPool members. Aborting.");
                    return;
                }

                _harmony = new Harmony("ragecoop.syncfix.heading");

                // Patch SyncedPed.WalkTo and SyncedPed.SmoothTransition (instance methods with no args)
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

                if (Logger != null) Logger.Info("[SyncFix] Client-side heading override installed (no generic patches).");
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

        // Safety net: enforce desired heading every tick for all remote peds with Speed 1..3 and not aiming
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
                // Silent; runs every frame
            }
        }

        // Helper: find instance method with no parameters (public or non-public)
        private static MethodInfo FindInstanceMethodNoArgs(Type t, string name)
        {
            return t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);
        }

        // Shared reflection info (C# 7.3-safe).
        internal static class State
        {
            public static PropertyInfo PI_IsLocal;
            public static PropertyInfo PI_Speed;
            public static PropertyInfo PI_Heading;
            public static PropertyInfo PI_MainPed;
            public static PropertyInfo PI_IsAiming;
            public static FieldInfo    FI_PedsByID;
        }
    }

    // Harmony patches to reinforce heading and override sprint’s forced North heading
    public static class SyncedPedPatches
    {
        private static PropertyInfo PI_Speed, PI_Heading, PI_MainPed, PI_IsAiming;

        private static void EnsureBind(object instance)
        {
            if (PI_Speed != null) return;
            var tSyncedPed = instance.GetType();
            PI_Speed    = tSyncedPed.GetProperty("Speed",    BindingFlags.Public | BindingFlags.Instance);
            PI_Heading  = tSyncedPed.GetProperty("Heading",  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            PI_MainPed  = tSyncedPed.GetProperty("MainPed",  BindingFlags.Public | BindingFlags.Instance);
            PI_IsAiming = tSyncedPed.GetProperty("IsAiming", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        // After WalkTo issues movement tasks, immediately re-issue sprint task with correct targetHeading when Speed==3.
        public static void WalkTo_Postfix(object __instance)
        {
            try
            {
                EnsureBind(__instance);

                var speedObj = PI_Speed != null ? PI_Speed.GetValue(__instance, null) : null;
                if (!(speedObj is byte)) return;
                var speed = (byte)speedObj;

                // Only on-foot and sprinting
                if (speed != 3) return;

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

                // Tiny forward offset to keep the path intact while updating the targetHeading parameter.
                // This avoids relying on SyncedPed.Predict(Position), which isn't accessible.
                Vector3 pos = ped.Position;
                Vector3 fwd = ped.ForwardVector;
                var dest = pos + (fwd * 0.25f); // small step ahead

                // TASK_GO_STRAIGHT_TO_COORD(ped, x, y, z, speed, timeout, targetHeading, distanceToSlide)
                Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle, dest.X, dest.Y, dest.Z, 3.0f, -1, heading, 0.0f);

                // Also set desired heading as an extra nudge
                Function.Call(Hash.SET_PED_DESIRED_HEADING, ped.Handle, heading);
            }
            catch
            {
                // Swallow to avoid destabilizing the client script
            }
        }

        // After SmoothTransition nudges rotation, reinforce desired heading so nav tasks don't re-orient it
        public static void SmoothTransition_Postfix(object __instance)
        {
            try
            {
                EnsureBind(__instance);

                var speedObj = PI_Speed != null ? PI_Speed.GetValue(__instance, null) : null;
                if (!(speedObj is byte)) return;
                var speed = (byte)speedObj;
                if (speed == 0 || speed >= 4) return; // only on-foot

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
