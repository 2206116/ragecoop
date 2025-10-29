// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
// This script forces remote peds to face their travel direction while walking/running.
//
// C# 7.3 compatible.
//
// Fix strategy:
// 1) Patch SyncedPed.WalkTo (Prefix/Postfix) to stash the instance's Heading in a thread-static slot.
// 2) Patch GTA.Native.Function.Call(Hash, params InputArgument[]) (Prefix) to detect
//    TASK_GO_STRAIGHT_TO_COORD invoked from WalkTo and replace targetHeading (arg index 6) with
//    the stored Heading value. This prevents the “always North” bug during sprint.
// 3) Reinforce heading each frame after movement updates as a safety net.

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using GTA;
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

        // Cached reflection members (instance)
        private PropertyInfo _piIsLocal;
        private PropertyInfo _piSpeed;
        private PropertyInfo _piHeading;
        private PropertyInfo _piMainPed;
        private PropertyInfo _piIsAiming;
        private FieldInfo _fiPedsByID;

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

                _piIsLocal = _tSyncedPed.GetProperty("IsLocal", BindingFlags.Public | BindingFlags.Instance);
                _piSpeed = _tSyncedPed.GetProperty("Speed", BindingFlags.Public | BindingFlags.Instance);
                _piHeading = _tSyncedPed.GetProperty("Heading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _piMainPed = _tSyncedPed.GetProperty("MainPed", BindingFlags.Public | BindingFlags.Instance);
                _piIsAiming = _tSyncedPed.GetProperty("IsAiming", BindingFlags.NonPublic | BindingFlags.Instance);
                _fiPedsByID = _tEntityPool.GetField("PedsByID", BindingFlags.Public | BindingFlags.Static);

                if (_piIsLocal == null || _piSpeed == null || _piHeading == null || _piMainPed == null || _fiPedsByID == null)
                {
                    Logger?.Error("[SyncFix] Failed to bind SyncedPed/EntityPool members. Aborting.");
                    return;
                }

                // Initialize static refs used in patches
                PatchState.PI_Speed = _piSpeed;
                PatchState.PI_Heading = _piHeading;
                PatchState.PI_MainPed = _piMainPed;
                PatchState.PI_IsAiming = _piIsAiming;
                PatchState.FI_PedsByID = _fiPedsByID;

                _harmony = new Harmony("ragecoop.syncfix.heading");

                // 1) WalkTo scope (Prefix/Postfix) to capture desired heading in thread-static
                var miWalkTo = AccessTools.Method(_tSyncedPed, "WalkTo");
                if (miWalkTo != null)
                {
                    var pre = new HarmonyMethod(typeof(WalkToScopePatch).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static));
                    var post = new HarmonyMethod(typeof(WalkToScopePatch).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static));
                    _harmony.Patch(miWalkTo, prefix: pre, postfix: post);
                }
                else
                {
                    Logger?.Warning("[SyncFix] Could not locate SyncedPed.WalkTo; heading override will rely on fallback methods.");
                }

                // 2) Intercept Function.Call(Hash, params InputArgument[]) and override targetHeading during sprint
                var miFuncCallVoid = AccessTools.Method(typeof(Function), "Call", new Type[] { typeof(Hash), typeof(InputArgument[]) });
                if (miFuncCallVoid != null)
                {
                    var pre = new HarmonyMethod(typeof(FunctionCallPatch).GetMethod("CallVoid_Prefix", BindingFlags.Public | BindingFlags.Static));
                    _harmony.Patch(miFuncCallVoid, prefix: pre);
                }
                else
                {
                    Logger?.Warning("[SyncFix] Could not patch GTA.Native.Function.Call(void).");
                }

                // 3) Safety net: per-tick desired heading enforcement for all remote on-foot peds
                API.Events.OnTick += OnTickEnforceHeading;

                Logger?.Info("[SyncFix] Client-side heading override installed.");
            }
            catch (Exception ex)
            {
                Logger?.Error("[SyncFix] Error during install: " + ex);
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
                Logger?.Warning("[SyncFix] Failed to detach tick handler: " + ex);
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
                Logger?.Warning("[SyncFix] Failed to unpatch Harmony: " + ex);
            }
        }

        // Safety net: enforce desired heading every tick for all remote peds with Speed 1..3 and not aiming
        private void OnTickEnforceHeading()
        {
            try
            {
                var pedsDictObj = PatchState.FI_PedsByID.GetValue(null);
                var pedsDict = pedsDictObj as IDictionary;
                if (pedsDict == null || pedsDict.Count == 0) return;

                foreach (DictionaryEntry kv in pedsDict)
                {
                    var sp = kv.Value; // SyncedPed
                    if (sp == null) continue;

                    object isLocalObj = PatchState.Get(_ => _.PI_IsLocal, sp, null);
                    if (isLocalObj is bool && ((bool)isLocalObj)) continue;

                    object speedObj = PatchState.Get(_ => _.PI_Speed, sp, null);
                    if (!(speedObj is byte)) continue;
                    var speed = (byte)speedObj;
                    if (speed == 0 || speed >= 4) continue;

                    var ped = PatchState.Get(_ => _.PI_MainPed, sp, null) as Ped;
                    if (ped == null || !ped.Exists()) continue;

                    // Avoid forcing heading while aiming (let strafe look direction be natural)
                    bool isAiming = false;
                    var aimObj = PatchState.Get(_ => _.PI_IsAiming, sp, null);
                    if (aimObj is bool) isAiming = (bool)aimObj;
                    if (isAiming) continue;

                    object headingObj = PatchState.Get(_ => _.PI_Heading, sp, null);
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

        // Static state holders used by patches
        internal static class PatchState
        {
            // Shared member references
            public static PropertyInfo PI_Speed;
            public static PropertyInfo PI_Heading;
            public static PropertyInfo PI_MainPed;
            public static PropertyInfo PI_IsLocal; // will be fetched per-instance in safety net via direct
            public static PropertyInfo PI_IsAiming;
            public static FieldInfo FI_PedsByID;

            [ThreadStatic] public static bool TLS_InWalkTo;
            [ThreadStatic] public static float TLS_DesiredHeading;

            // Helper to safely get a property value via a selector on this class fields
            public static object Get(Func<PatchState, PropertyInfo> sel, object instance, object defaultValue)
            {
                PropertyInfo pi = sel(PatchStateAccessor.Instance);
                try
                {
                    return pi != null ? pi.GetValue(instance, null) : defaultValue;
                }
                catch { return defaultValue; }
            }

            private class PatchStateAccessor : PatchState
            {
                public static readonly PatchState Instance = new PatchState();
            }
        }

        // Enter/leave WalkTo: stash per-instance Heading into thread-static so Function.Call patch can use it
        public static class WalkToScopePatch
        {
            public static void Prefix(object __instance)
            {
                try
                {
                    object headingObj = PatchState.PI_Heading != null ? PatchState.PI_Heading.GetValue(__instance, null) : null;
                    if (headingObj is float)
                    {
                        PatchState.TLS_DesiredHeading = (float)headingObj;
                        PatchState.TLS_InWalkTo = true;
                    }
                    else
                    {
                        PatchState.TLS_InWalkTo = false;
                    }
                }
                catch
                {
                    PatchState.TLS_InWalkTo = false;
                }
            }

            public static void Postfix()
            {
                PatchState.TLS_InWalkTo = false;
            }
        }

        // Intercept Function.Call for TASK_GO_STRAIGHT_TO_COORD and override targetHeading (arg[6]) when inside WalkTo
        public static class FunctionCallPatch
        {
            public static void CallVoid_Prefix(ref Hash hash, ref InputArgument[] arguments)
            {
                try
                {
                    if (!PatchState.TLS_InWalkTo) return;
                    if (hash != Hash.TASK_GO_STRAIGHT_TO_COORD) return;
                    if (arguments == null || arguments.Length < 8) return;

                    // Replace targetHeading = arguments[6] with the stashed desired heading
                    float desired = PatchState.TLS_DesiredHeading;
                    arguments[6] = new InputArgument(desired);
                }
                catch
                {
                    // Do nothing if we fail; safer to let original call proceed
                }
            }
        }
    }

    // Extra patches to reinforce heading after movement — left public for Harmony visibility and C# 7.3 compat
    public static class SyncedPedPatches
    {
        private static PropertyInfo PI_Speed, PI_Heading, PI_MainPed, PI_IsAiming;

        private static void EnsureBind(object instance)
        {
            if (PI_Speed != null) return;
            var tSyncedPed = instance.GetType();
            PI_Speed = tSyncedPed.GetProperty("Speed", BindingFlags.Public | BindingFlags.Instance);
            PI_Heading = tSyncedPed.GetProperty("Heading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            PI_MainPed = tSyncedPed.GetProperty("MainPed", BindingFlags.Public | BindingFlags.Instance);
            PI_IsAiming = tSyncedPed.GetProperty("IsAiming", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        // After WalkTo issues movement tasks, push desired heading so the ped faces its motion
        public static void WalkTo_Postfix(object __instance)
        {
            try
            {
                EnsureBind(__instance);

                object speedObj = PI_Speed != null ? PI_Speed.GetValue(__instance, null) : null;
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

                object headingObj = PI_Heading != null ? PI_Heading.GetValue(__instance, null) : null;
                if (!(headingObj is float)) return;
                var heading = (float)headingObj;

                Function.Call(Hash.SET_PED_DESIRED_HEADING, ped.Handle, heading);
            }
            catch { }
        }

        // After SmoothTransition nudges rotation, reinforce desired heading so nav tasks don't re-orient it
        public static void SmoothTransition_Postfix(object __instance)
        {
            try
            {
                EnsureBind(__instance);

                object speedObj = PI_Speed != null ? PI_Speed.GetValue(__instance, null) : null;
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

                object headingObj = PI_Heading != null ? PI_Heading.GetValue(__instance, null) : null;
                if (!(headingObj is float)) return;
                var heading = (float)headingObj;

                Function.Call(Hash.SET_PED_DESIRED_HEADING, ped.Handle, heading);
            }
            catch { }
        }
    }
}
