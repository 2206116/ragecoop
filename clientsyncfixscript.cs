// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
// Fix: remote peds face their travel direction while walking/running (C# 7.3 compatible).
//
// Strategy:
// 1) Patch SyncedPed.WalkTo (Prefix/Postfix) to stash the current Heading in a thread-static slot.
// 2) Patch GTA.Native.Function.Call(Hash, params InputArgument[]) to detect TASK_GO_STRAIGHT_TO_COORD
//    invoked during WalkTo and replace targetHeading (argument index 6) with the stashed Heading.
// 3) Reinforce heading after WalkTo and SmoothTransition, plus a per-tick safety net.
//
// Notes:
// - Avoids C# 9 features and AccessTools.Method to prevent AmbiguousMatchException.
// - Uses reflection filters (by name + param count/types) to select exact overloads.
// - Logger.Warning overloads are respected (single string).

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

                // 1) WalkTo scope (Prefix/Postfix) to capture desired heading in thread-static
                var miWalkTo = FindInstanceMethodNoArgs(_tSyncedPed, "WalkTo");
                if (miWalkTo != null)
                {
                    _harmony.Patch(miWalkTo,
                        prefix:  new HarmonyMethod(typeof(WalkToScopePatch).GetMethod("Prefix",  BindingFlags.Public | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(WalkToScopePatch).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)));

                    // Also reinforce after WalkTo completes
                    _harmony.Patch(miWalkTo,
                        postfix: new HarmonyMethod(typeof(SyncedPedPatches).GetMethod("WalkTo_Postfix", BindingFlags.Public | BindingFlags.Static)));
                }
                else
                {
                    if (Logger != null) Logger.Warning("[SyncFix] Could not locate SyncedPed.WalkTo; heading override will rely on fallback methods.");
                }

                // 2) Intercept Function.Call(Hash, params InputArgument[]) and override targetHeading
                var miCallVoid = FindFunctionCallVoid();
                if (miCallVoid != null)
                {
                    _harmony.Patch(miCallVoid,
                        prefix: new HarmonyMethod(typeof(FunctionCallPatch).GetMethod("CallVoid_Prefix", BindingFlags.Public | BindingFlags.Static)));
                }
                else
                {
                    if (Logger != null) Logger.Warning("[SyncFix] Could not patch GTA.Native.Function.Call(void).");
                }

                var miCallGeneric = FindFunctionCallGeneric();
                if (miCallGeneric != null)
                {
                    _harmony.Patch(miCallGeneric,
                        prefix: new HarmonyMethod(typeof(FunctionCallPatch).GetMethod("CallVoid_Prefix", BindingFlags.Public | BindingFlags.Static)));
                }

                // 3) Reinforce heading after updates
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

                if (Logger != null) Logger.Info("[SyncFix] Client-side heading override installed.");
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

        // Helper: find GTA.Native.Function.Call(Hash, InputArgument[]) (void)
        private static MethodInfo FindFunctionCallVoid()
        {
            var methods = typeof(Function).GetMethods(BindingFlags.Public | BindingFlags.Static);
            foreach (var m in methods)
            {
                if (m.Name != "Call") continue;
                if (m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length != 2) continue;
                if (ps[0].ParameterType != typeof(Hash)) continue;
                if (ps[1].ParameterType != typeof(InputArgument[])) continue;
                if (m.ReturnType != typeof(void)) continue;
                return m;
            }
            return null;
        }

        // Helper: find GTA.Native.Function.Call<T>(Hash, InputArgument[]) (generic definition)
        private static MethodInfo FindFunctionCallGeneric()
        {
            var methods = typeof(Function).GetMethods(BindingFlags.Public | BindingFlags.Static);
            foreach (var m in methods)
            {
                if (m.Name != "Call") continue;
                if (!m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length != 2) continue;
                if (ps[0].ParameterType != typeof(Hash)) continue;
                if (ps[1].ParameterType != typeof(InputArgument[])) continue;
                return m;
            }
            return null;
        }

        // Shared state and cached reflection info (C# 7.3-safe).
        internal static class State
        {
            public static PropertyInfo PI_IsLocal;
            public static PropertyInfo PI_Speed;
            public static PropertyInfo PI_Heading;
            public static PropertyInfo PI_MainPed;
            public static PropertyInfo PI_IsAiming;
            public static FieldInfo    FI_PedsByID;

            [ThreadStatic] public static bool  TLS_InWalkTo;
            [ThreadStatic] public static float TLS_DesiredHeading;
        }

        // Enter/leave WalkTo: stash per-instance Heading into thread-static so Function.Call patch can use it
        public static class WalkToScopePatch
        {
            public static void Prefix(object __instance)
            {
                try
                {
                    var headingObj = State.PI_Heading != null ? State.PI_Heading.GetValue(__instance, null) : null;
                    if (headingObj is float)
                    {
                        State.TLS_DesiredHeading = (float)headingObj;
                        State.TLS_InWalkTo = true;
                    }
                    else
                    {
                        State.TLS_InWalkTo = false;
                    }
                }
                catch
                {
                    State.TLS_InWalkTo = false;
                }
            }

            public static void Postfix()
            {
                State.TLS_InWalkTo = false;
            }
        }

        // Intercept Function.Call for TASK_GO_STRAIGHT_TO_COORD and override targetHeading (args[6]) when inside WalkTo
        public static class FunctionCallPatch
        {
            public static void CallVoid_Prefix(Hash hash, InputArgument[] arguments)
            {
                try
                {
                    if (!State.TLS_InWalkTo) return;
                    if (hash != Hash.TASK_GO_STRAIGHT_TO_COORD) return;
                    if (arguments == null || arguments.Length < 8) return;

                    // TASK_GO_STRAIGHT_TO_COORD(ped, x, y, z, speed, timeout, targetHeading, distanceToSlide)
                    arguments[6] = new InputArgument(State.TLS_DesiredHeading);
                }
                catch
                {
                    // No-op on failure
                }
            }
        }
    }

    // Extra patches to reinforce heading after movement — public for Harmony visibility, C# 7.3 compatible
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

        // After WalkTo issues movement tasks, push desired heading so the ped faces its motion
        public static void WalkTo_Postfix(object __instance)
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

        // After SmoothTransition nudges rotation, reinforce desired heading so nav tasks don't re-orient it
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
