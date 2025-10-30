// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
// Purpose: Fix peds facing North when sprinting by overriding SyncedPed.WalkTo’s sprint branch at runtime.
// Approach (C# 7.3 compatible):
// - Harmony Prefix on RageCoop.Client.SyncedPed.WalkTo. For Speed == 3 (sprint), we:
//   * Reconstruct predictPosition = Predict(Position) + Velocity via reflection.
//   * Call TASK_GO_STRAIGHT_TO_COORD with targetHeading = this.Heading (instead of 0.0f).
//   * Optionally reinforce facing by setting ped.Heading = Heading.
//   * Call SmoothTransition() to preserve the original smoothing behavior.
//   * Return false to skip the original WalkTo (only when sprinting). Other speeds fall through to original code.
//
// Notes:
// - No generic Function.Call<T> patching (avoids MonoMod NotSupported errors).
// - No use of Ped.ReadPosition or TaskType (not available in resource context).
// - If you also want to enforce heading during running (Speed == 2), you can add a similar override or a Postfix
//   to set ped.Heading = Heading after the RunTo call, but this file focuses on the sprint fix.

using System;
using System.Linq;
using System.Reflection;
using GTA;
using GTA.Math;
using GTA.Native;
using HarmonyLib;
using RageCoop.Client.Scripting;

namespace RageCoop.Resources.ClientPatch
{
    public class ClientHeadingPatch : ClientScript
    {
        private Harmony _harmony;
        private Type _tSyncedPed;

        // Reflection cache (shared)
        internal static PropertyInfo PI_Speed;
        internal static PropertyInfo PI_Heading;
        internal static PropertyInfo PI_MainPed;
        internal static PropertyInfo PI_Position;
        internal static PropertyInfo PI_Velocity;
        internal static MethodInfo   MI_Predict;
        internal static MethodInfo   MI_SmoothTransition;

        public override void OnStart()
        {
            try
            {
                var clientAsm = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "RageCoop.Client", StringComparison.OrdinalIgnoreCase));

                if (clientAsm == null)
                {
                    Logger?.Error("[ClientPatch] RageCoop.Client assembly not found.");
                    return;
                }

                _tSyncedPed = clientAsm.GetType("RageCoop.Client.SyncedPed", true);

                // Bind required members
                PI_Speed           = _tSyncedPed.GetProperty("Speed",   BindingFlags.Public | BindingFlags.Instance);
                PI_Heading         = _tSyncedPed.GetProperty("Heading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PI_MainPed         = _tSyncedPed.GetProperty("MainPed", BindingFlags.Public | BindingFlags.Instance);
                PI_Position        = _tSyncedPed.GetProperty("Position",BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PI_Velocity        = _tSyncedPed.GetProperty("Velocity",BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                MI_Predict         = _tSyncedPed.GetMethod("Predict", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(Vector3) }, null);
                MI_SmoothTransition= _tSyncedPed.GetMethod("SmoothTransition", BindingFlags.NonPublic | BindingFlags.Instance);

                if (PI_Speed == null || PI_Heading == null || PI_MainPed == null || PI_Position == null || PI_Velocity == null || MI_Predict == null || MI_SmoothTransition == null)
                {
                    Logger?.Error("[ClientPatch] Failed to bind SyncedPed members (Speed/Heading/MainPed/Position/Velocity/Predict/SmoothTransition).");
                    return;
                }

                _harmony = new Harmony("ragecoop.clientpatch.heading");
                var miWalkTo = _tSyncedPed.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                          .FirstOrDefault(m => m.Name == "WalkTo" && m.GetParameters().Length == 0);
                if (miWalkTo == null)
                {
                    Logger?.Error("[ClientPatch] Could not locate SyncedPed.WalkTo.");
                    return;
                }

                _harmony.Patch(miWalkTo,
                    prefix: new HarmonyMethod(typeof(WalkToSprintOverride).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)));

                Logger?.Info("[ClientPatch] Installed: WalkTo sprint override with correct heading.");
            }
            catch (Exception ex)
            {
                Logger?.Error("[ClientPatch] Install error: " + ex);
            }
        }

        public override void OnStop()
        {
            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchAll("ragecoop.clientpatch.heading");
                }
            }
            catch (Exception ex)
            {
                Logger?.Warning("[ClientPatch] Unpatch error: " + ex);
            }
        }

        public static class WalkToSprintOverride
        {
            public static bool Prefix(object __instance)
            {
                try
                {
                    // Read speed; only intercept sprint (3). Other speeds fall through to original.
                    var speedObj = ClientHeadingPatch.PI_Speed.GetValue(__instance, null);
                    if (!(speedObj is byte)) return true;
                    var speed = (byte)speedObj;
                    if (speed != 3) return true; // let original handle walk/run/idle

                    var ped = ClientHeadingPatch.PI_MainPed.GetValue(__instance, null) as Ped;
                    if (ped == null || !ped.Exists()) return true;

                    // Recompute predictPosition = Predict(Position) + Velocity
                    Vector3 pos = default(Vector3);
                    Vector3 vel = default(Vector3);
                    try
                    {
                        var posObj = ClientHeadingPatch.PI_Position.GetValue(__instance, null);
                        if (posObj is Vector3) pos = (Vector3)posObj;
                    }
                    catch { }
                    try
                    {
                        var velObj = ClientHeadingPatch.PI_Velocity.GetValue(__instance, null);
                        if (velObj is Vector3) vel = (Vector3)velObj;
                    }
                    catch { }

                    Vector3 predict = pos;
                    try
                    {
                        predict = (Vector3)ClientHeadingPatch.MI_Predict.Invoke(__instance, new object[] { pos });
                    }
                    catch { }

                    Vector3 predictPosition = predict + vel;

                    // Use server-sent heading for sprint
                    float heading = ped.Heading;
                    try
                    {
                        var hObj = ClientHeadingPatch.PI_Heading.GetValue(__instance, null);
                        if (hObj is float) heading = (float)hObj;
                    }
                    catch { }

                    // Re-issue sprint task with correct targetHeading
                    // TASK_GO_STRAIGHT_TO_COORD(ped, x,y,z, speed, timeout, targetHeading, distanceToSlide)
                    Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle,
                                  predictPosition.X, predictPosition.Y, predictPosition.Z,
                                  3.0f, -1, heading, 0.0f);

                    // Preserve original sprint tunables
                    Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, ped.Handle, 1.49f);
                    Function.Call(Hash.SET_PED_DESIRED_MOVE_BLEND_RATIO, ped.Handle, 1.0f);

                    // Hard reinforce facing right away (AI can ignore desired heading while moving)
                    ped.Heading = heading;

                    // Keep smoothing like original
                    try { ClientHeadingPatch.MI_SmoothTransition.Invoke(__instance, null); } catch { }

                    // Skip original WalkTo (only for sprint)
                    return false;
                }
                catch
                {
                    // If anything goes wrong, run original code
                    return true;
                }
            }
        }
    }
}
