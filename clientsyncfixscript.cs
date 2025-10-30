// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
// Fix: remote peds face their travel direction while walking/running (C# 7.3 compatible).
//
// Strategy (safe for this runtime; no generic method patching):
// 1) Patch SyncedPed.WalkTo:
//    - Prefix: mark we're in WalkTo and stash the current desired Heading (thread-static).
//    - Postfix: reinforce desired heading after WalkTo runs.
// 2) Patch ONLY GTA.Native.Function.Call(Hash, InputArgument[]) (non-generic):
//    - Prefix: if inside WalkTo and hash == TASK_GO_STRAIGHT_TO_COORD, replace targetHeading (argument index 6)
//      with the stashed Heading so the ped doesn't lock to North.
// 3) Patch SyncedPed.SmoothTransition (Postfix) to keep desired heading aligned while on-foot.
// 4) Per-tick safety net applies SET_PED_DESIRED_HEADING to all remote on-foot peds that are not aiming.
//
// Notes:
// - Do NOT patch the generic Function.Call<T> overload; it triggers a NotSupportedException in this runtime.
// - C# 7.3 compatible (no newer pattern features).
// - Logger.Warning overloads take a single string.

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

                // Patch SyncedPed.WalkTo (instance, no args): Prefix for scope + Postfix reinforcement
                var miWalkTo = FindInstanceMethodNoArgs(_tSyncedPed, "WalkTo");
                if (miWalkTo != null)
                {
                    _harmony.Patch(miWalkTo,
                        prefix:  new HarmonyMethod(typeof(WalkToScopePatch).GetMethod("Prefix",  BindingFlags.Public | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(SyncedPedPatches).GetMethod("WalkTo_Postfix", BindingFlags.Public | BindingFlags.Static)));
                }
                else
                {
                    if (Logger != null) Logger.Warning("[SyncFix] Could not locate SyncedPed.WalkTo; relying on SmoothTransition and tick safety net.");
                }

                // Patch ONLY the non-generic Function.Call(Hash, InputArgument[]) to override targetHeading
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

                if (Logger != null) Logger.Info("[SyncFix] Client-side heading override installed (void overload patch only).");
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

        // Helpers: find methods safely (avoid AmbiguousMatchException)
        private static MethodInfo FindInstanceMethodNoArgs(Type t, string name)
        {
            return t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);
        }

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

        // Shared reflection info + thread-static scope
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

        // Mark the WalkTo scope and stash desired heading for Function.Call patch
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
        }
    }

    // Patch GTA.Native.Function.Call(void) to override TASK_GO_STRAIGHT_TO_COORD's targetHeading during WalkTo
    public static class FunctionCallPatch
    {
        public static void CallVoid_Prefix(Hash hash, InputArgument[] arguments)
        {
            try
            {
                if (!Main.State.TLS_InWalkTo) return;
                if (hash != Hash.TASK_GO_STRAIGHT_TO_COORD) return;
                if (arguments == null || arguments.Length < 8) return;

                // TASK_GO_STRAIGHT_TO_COORD(ped, x, y, z, speed, timeout, targetHeading, distanceToSlide)
                arguments[6] = new InputArgument(Main.State.TLS_DesiredHeading);
            }
            catch
            {
                // No-op on failure
            }
            finally
            {
                // Ensure scope flag resets for this call to avoid leaking into other invocations during the same tick
                Main.State.TLS_InWalkTo = false;
            }
        }
    }

    // Reinforcement patches on SyncedPed methods (public for Harmony)
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

        // After WalkTo completes, reinforce desired heading so AI nav doesn't fight us
        public static void WalkTo_Postfix(object __instance)
        {
            try
            {
                EnsureBind(__instance);

                var speedObj = PI_Speed != null ? PI_Speed.GetValue(__instance, null) : null;
                if (!(speedObj is byte)) return;
                var speed = (byte)speedObj;
                if (speed == 0 || speed >= 4) return; // on-foot only

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
