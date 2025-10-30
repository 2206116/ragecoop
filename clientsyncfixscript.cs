// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
// Fix: remote peds face their travel direction while walking/running (C# 7.3 compatible).
//
// Strategy:
// - Robust Harmony Transpiler on SyncedPed.WalkTo that rewrites the InputArgument[] element at index 6
//   (targetHeading for TASK_GO_STRAIGHT_TO_COORD) to use this.Heading, regardless of how the float is loaded
//   (ldc.r4, ldc.i4.0+conv.r4, etc.) or how the params array is built.
// - Postfix on SmoothTransition to reinforce heading for on-foot, non-aiming peds.
// - Per-tick safety net to apply SET_PED_DESIRED_HEADING for on-foot, non-aiming peds.
//
// Notes:
// - We do NOT patch any generic methods (avoids MonoMod NotSupportedExceptions).
// - C# 7.3 compatible.
// - After install you should see a log "WalkTo index-6 replacements: N" (N >= 1).
//   If N=0, please paste your log and we’ll widen the matcher again.

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

        // Reflection cache for the safety net
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
                    Logger.Info("[SyncFix] Installed. WalkTo index-6 replacements: " + WalkToHeadingTranspiler.ReplacementCount);
                    if (WalkToHeadingTranspiler.ReplacementCount == 0)
                        Logger.Warning("[SyncFix] No index-6 heading element was replaced in WalkTo. Share this log and we’ll tailor the matcher.");
                }
            }
            catch (Exception ex)
            {
                if (Logger != null) Logger.Error("[SyncFix] Error during install: " + ex);
            }
        }

        public override void OnStop()
        {
            try { API.Events.OnTick -= OnTickEnforceHeading; }
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
            catch { }
        }

        private static MethodInfo FindInstanceMethodNoArgs(Type t, string name)
        {
            return t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);
        }
    }

    // Robust Transpiler:
    // We scan the array initialization sequence for InputArgument[] in WalkTo and look for
    //   dup; ldc.i4.s 6; ... ; newobj InputArgument::.ctor(float32); stelem.ref
    // When we detect index==6, we replace the value-producing sequence just before newobj
    // with: ldarg.0 ; callvirt instance float32 get_Heading()
    public static class WalkToHeadingTranspiler
    {
        public static int ReplacementCount = 0;

        public static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var codes = new List<CodeInstruction>(instructions);
            var ctorFloat = typeof(InputArgument).GetConstructor(new Type[] { typeof(float) });

            // Resolve get_Heading on the declaring type of WalkTo
            MethodInfo getHeading = null;
            var declType = original.DeclaringType;
            if (declType != null)
            {
                var prop = declType.GetProperty("Heading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null) getHeading = prop.GetGetMethod(true);
            }

            if (getHeading == null || ctorFloat == null)
            {
                // Nothing we can do
                return codes;
            }

            for (int i = 0; i < codes.Count - 5; i++)
            {
                // Pattern: dup ; ldc.i4.* 6 ; <any number of instructions that push a float> ; newobj InputArgument(float) ; stelem.ref
                if (codes[i].opcode == OpCodes.Dup &&
                    IsLoadConstI4WithValue(codes[i + 1], 6))
                {
                    // Find the next 'newobj InputArgument(float)'
                    int j = i + 2;
                    int newObjIndex = -1;
                    for (; j < Math.Min(i + 25, codes.Count); j++)
                    {
                        if (codes[j].opcode == OpCodes.Newobj && codes[j].operand is ConstructorInfo && (ConstructorInfo)codes[j].operand == ctorFloat)
                        {
                            newObjIndex = j;
                            break;
                        }
                    }
                    if (newObjIndex == -1) continue;

                    // We expect stelem.ref right after
                    int k = newObjIndex + 1;
                    if (k >= codes.Count) continue;
                    bool hasStelem = (codes[k].opcode == OpCodes.Stelem_Ref || codes[k].opcode == OpCodes.Stelem);
                    if (!hasStelem) continue;

                    // Replace whatever value-producing sequence exists between (i+2 .. newObjIndex-1)
                    // with: ldarg.0 ; callvirt get_Heading
                    // Implementation: remove those slots and inject our two instructions at i+2
                    // Ensure we don't break label/branch targets: insert first, then NOP the old value-producing range.

                    // Insert ldarg.0; callvirt get_Heading before newobj
                    codes.Insert(newObjIndex, new CodeInstruction(OpCodes.Callvirt, getHeading));
                    codes.Insert(newObjIndex, new CodeInstruction(OpCodes.Ldarg_0));

                    // NOP previous value-producing segment
                    for (int p = i + 2; p < newObjIndex; p++)
                    {
                        // Only clear simple constants and math; avoid wrecking labels
                        if (codes[p].labels != null && codes[p].labels.Count > 0) continue;
                        if (codes[p].opcode.FlowControl == FlowControl.Next)
                        {
                            codes[p].opcode = OpCodes.Nop;
                            codes[p].operand = null;
                        }
                    }

                    ReplacementCount++;
                    // Advance past the edited segment
                    i = newObjIndex + 1;
                }
            }

            return codes;
        }

        private static bool IsLoadConstI4WithValue(CodeInstruction ci, int value)
        {
            if (ci.opcode == OpCodes.Ldc_I4 && ci.operand is int && (int)ci.operand == value) return true;
            if (value == 6 && (ci.opcode == OpCodes.Ldc_I4_6)) return true;
            if (value >= -1 && value <= 8)
            {
                // Handle short forms
                switch (value)
                {
                    case -1: return ci.opcode == OpCodes.Ldc_I4_M1;
                    case 0: return ci.opcode == OpCodes.Ldc_I4_0;
                    case 1: return ci.opcode == OpCodes.Ldc_I4_1;
                    case 2: return ci.opcode == OpCodes.Ldc_I4_2;
                    case 3: return ci.opcode == OpCodes.Ldc_I4_3;
                    case 4: return ci.opcode == OpCodes.Ldc_I4_4;
                    case 5: return ci.opcode == OpCodes.Ldc_I4_5;
                    case 6: return ci.opcode == OpCodes.Ldc_I4_6;
                    case 7: return ci.opcode == OpCodes.Ldc_I4_7;
                    case 8: return ci.opcode == OpCodes.Ldc_I4_8;
                }
            }
            // Also handle ldc.i4.s
            if (ci.opcode == OpCodes.Ldc_I4_S && ci.operand is sbyte && (sbyte)ci.operand == (sbyte)value) return true;
            return false;
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
