// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
// Fix: remote peds face their travel direction while walking/running (C# 7.3 compatible).
//
// Strategy:
// - Harmony Transpiler on SyncedPed.WalkTo to replace the literal 0.0f targetHeading passed to
//   TASK_GO_STRAIGHT_TO_COORD with the instance's Heading (server-sent).
//   We detect the two consecutive float InputArgument(0.0f) constructions at the tail of the call
//   (targetHeading, distanceToSlide) and rewrite the first one to use get_Heading.
// - Postfix on SmoothTransition to reinforce desired heading during on-foot movement.
// - Per-tick safety net to apply SET_PED_DESIRED_HEADING for non-aiming, on-foot peds.
//
// Notes:
// - No patching of GTA.Native.Function.Call<T>. Only a transpiler on SyncedPed.WalkTo (non-generic).
// - C# 7.3 compatible (no modern pattern matching).
// - Includes minimal one-time logging to confirm the transpiler made a replacement.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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

        // Reflection cache used by tick safety net
        private static PropertyInfo PI_IsLocal, PI_Speed, PI_Heading, PI_MainPed, PI_IsAiming;
        private static FieldInfo FI_PedsByID;

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

                // Bind properties/fields used by safety net
                PI_IsLocal  = _tSyncedPed.GetProperty("IsLocal",  BindingFlags.Public | BindingFlags.Instance);
                PI_Speed    = _tSyncedPed.GetProperty("Speed",    BindingFlags.Public | BindingFlags.Instance);
                PI_Heading  = _tSyncedPed.GetProperty("Heading",  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PI_MainPed  = _tSyncedPed.GetProperty("MainPed",  BindingFlags.Public | BindingFlags.Instance);
                PI_IsAiming = _tSyncedPed.GetProperty("IsAiming", BindingFlags.NonPublic | BindingFlags.Instance);
                FI_PedsByID = _tEntityPool.GetField("PedsByID", BindingFlags.Public | BindingFlags.Static);

                if (PI_IsLocal == null || PI_Speed == null || PI_Heading == null || PI_MainPed == null || FI_PedsByID == null)
                {
                    if (Logger != null) Logger.Error("[SyncFix] Failed to bind SyncedPed/EntityPool members. Aborting.");
                    return;
                }

                _harmony = new Harmony("ragecoop.syncfix.walkto.heading");

                // Transpile SyncedPed.WalkTo (instance, no args)
                var miWalkTo = FindInstanceMethodNoArgs(_tSyncedPed, "WalkTo");
                if (miWalkTo != null)
                {
                    var transpiler = new HarmonyMethod(typeof(WalkToHeadingTranspiler).GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static));
                    _harmony.Patch(miWalkTo, transpiler: transpiler);
                }
                else
                {
                    if (Logger != null) Logger.Warning("[SyncFix] Could not locate SyncedPed.WalkTo; heading will rely on reinforcement.");
                }

                // Reinforce after SmoothTransition
                var miSmooth = FindInstanceMethodNoArgs(_tSyncedPed, "SmoothTransition");
                if (miSmooth != null)
                {
                    _harmony.Patch(miSmooth, postfix: new HarmonyMethod(typeof(SmoothReinforcePatch).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)));
                }
                else
                {
                    if (Logger != null) Logger.Warning("[SyncFix] Could not locate SyncedPed.SmoothTransition.");
                }

                // Per-tick safety net
                API.Events.OnTick += OnTickEnforceHeading;

                if (Logger != null)
                {
                    Logger.Info("[SyncFix] Installed. WalkTo replacements: " + WalkToHeadingTranspiler.ReplacementCount);
                    if (WalkToHeadingTranspiler.ReplacementCount == 0)
                        Logger.Warning("[SyncFix] No heading literal was replaced in WalkTo. If sprint still faces North, the IL pattern may differ. Tell me and I will tailor the matcher.");
                }
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
                    _harmony.UnpatchAll("ragecoop.syncfix.walkto.heading");
                }
            }
            catch (Exception ex)
            {
                if (Logger != null) Logger.Warning("[SyncFix] Failed to unpatch Harmony: " + ex);
            }
        }

        // Per-tick safety: ensure remote on-foot, non-aiming peds desire the server heading
        private void OnTickEnforceHeading()
        {
            try
            {
                var pedsDictObj = FI_PedsByID.GetValue(null);
                var pedsDict = pedsDictObj as IDictionary;
                if (pedsDict == null || pedsDict.Count == 0) return;

                foreach (DictionaryEntry kv in pedsDict)
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
            catch
            {
                // ignore per frame
            }
        }

        private static MethodInfo FindInstanceMethodNoArgs(Type t, string name)
        {
            return t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);
        }
    }

    // Transpiler: find the pair of InputArgument(float 0.0) used at the end of TASK_GO_STRAIGHT_TO_COORD
    // and replace the first (targetHeading) with `this.get_Heading()`.
    public static class WalkToHeadingTranspiler
    {
        public static int ReplacementCount = 0;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var list = new List<CodeInstruction>(instructions);

            // InputArgument .ctor(float)
            var ctorFloat = typeof(InputArgument).GetConstructor(new Type[] { typeof(float) });
            // SyncedPed.get_Heading
            // We don't have the type here, use the declaring type from the current method via try-catch.
            MethodInfo getHeading = null;

            // We’ll derive the declaring type from the target method if possible by scanning for callvirt to get_Heading later if needed.
            // Safer: assume property name "get_Heading" exists on the declaring type.
            // We'll fetch once we see a pattern and need it.

            for (int i = 0; i < list.Count - 3; i++)
            {
                // Look for: ldc.r4 0.0 ; newobj InputArgument(float)
                // followed immediately by: ldc.r4 0.0 ; newobj InputArgument(float)
                if (list[i].opcode == OpCodes.Ldc_R4 && IsZero(list[i].operand) &&
                    list[i + 1].opcode == OpCodes.Newobj && (ConstructorInfo)list[i + 1].operand == ctorFloat &&
                    list[i + 2].opcode == OpCodes.Ldc_R4 && IsZero(list[i + 2].operand) &&
                    list[i + 3].opcode == OpCodes.Newobj && (ConstructorInfo)list[i + 3].operand == ctorFloat)
                {
                    // Replace the first zero with `ldarg.0 ; callvirt instance float32 get_Heading()`
                    // Ensure we can resolve get_Heading from the method's declaring type
                    var declaringType = GetDeclaringTypeSafely(list);
                    if (declaringType != null && getHeading == null)
                    {
                        var prop = declaringType.GetProperty("Heading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (prop != null) getHeading = prop.GetGetMethod(true);
                    }

                    if (getHeading != null)
                    {
                        // Overwrite the 0.0 with ldarg.0 and insert callvirt get_Heading before the newobj
                        list[i] = new CodeInstruction(OpCodes.Ldarg_0);
                        list.Insert(i + 1, new CodeInstruction(OpCodes.Callvirt, getHeading));
                        ReplacementCount++;
                        // Skip past the inserted callvirt and the first newobj
                        i += 2;
                    }
                }
            }

            return list;
        }

        private static bool IsZero(object operand)
        {
            try
            {
                if (operand is float) return (float)operand == 0f;
                if (operand is double) return (double)operand == 0.0;
            }
            catch { }
            return false;
        }

        // Attempt to derive the declaring type from the IL stream by finding any callvirt on a method of an instance type.
        private static Type GetDeclaringTypeSafely(List<CodeInstruction> list)
        {
            for (int k = 0; k < list.Count; k++)
            {
                var ci = list[k];
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo)
                {
                    var mi = (MethodInfo)ci.operand;
                    if (mi.DeclaringType != null && !mi.IsStatic)
                    {
                        return mi.DeclaringType;
                    }
                }
            }
            // Fallback: cannot infer
            return null;
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
