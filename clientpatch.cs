// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
//
// Hard fix: fully override SyncedPed.WalkTo for on-foot states (Speed 1..3) so all native movement
// tasks use the current server-sent Heading instead of letting the original sprint branch pass 0.0f.
// We call TASK_GO_STRAIGHT_TO_COORD for walk/run/sprint with the correct heading parameter, and
// then immediately force ped.Heading to that value. We also invoke SmoothTransition to preserve
// original smoothing behavior.
//
// C# 7.3 compatible. No Function.Call<T> generic patches. No TaskType/ReadPosition usage.

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

        // Reflection cache for SyncedPed
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

                // Bind members
                PI_Speed           = _tSyncedPed.GetProperty("Speed",   BindingFlags.Public | BindingFlags.Instance);
                PI_Heading         = _tSyncedPed.GetProperty("Heading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PI_MainPed         = _tSyncedPed.GetProperty("MainPed", BindingFlags.Public | BindingFlags.Instance);
                PI_Position        = _tSyncedPed.GetProperty("Position",BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PI_Velocity        = _tSyncedPed.GetProperty("Velocity",BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                MI_Predict         = _tSyncedPed.GetMethod("Predict", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(Vector3) }, null);
                MI_SmoothTransition= _tSyncedPed.GetMethod("SmoothTransition", BindingFlags.NonPublic | BindingFlags.Instance);

                if (PI_Speed == null || PI_Heading == null || PI_MainPed == null ||
                    PI_Position == null || PI_Velocity == null || MI_Predict == null || MI_SmoothTransition == null)
                {
                    Logger?.Error("[ClientPatch] Failed to bind SyncedPed members (Speed/Heading/MainPed/Position/Velocity/Predict/SmoothTransition).");
                    return;
                }

                _harmony = new Harmony("ragecoop.clientpatch.walkto.override.all");
                var miWalkTo = _tSyncedPed.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                          .FirstOrDefault(m => m.Name == "WalkTo" && m.GetParameters().Length == 0);
                if (miWalkTo == null)
                {
                    Logger?.Error("[ClientPatch] Could not locate SyncedPed.WalkTo.");
                    return;
                }

                _harmony.Patch(miWalkTo,
                    prefix: new HarmonyMethod(typeof(WalkToOverrideAll).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)));

                Logger?.Info("[ClientPatch] Installed: Full WalkTo override for on-foot (uses correct heading).");
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
                    _harmony.UnpatchAll("ragecoop.clientpatch.walkto.override.all");
                }
            }
            catch (Exception ex)
            {
                Logger?.Warning("[ClientPatch] Unpatch error: " + ex);
            }
        }

        public static class WalkToOverrideAll
        {
            public static bool Prefix(object __instance)
            {
                try
                {
                    // Read ped and state
                    var ped = ClientHeadingPatch.PI_MainPed.GetValue(__instance, null) as Ped;
                    if (ped == null || !ped.Exists()) return true; // fallback to original

                    var speedObj = ClientHeadingPatch.PI_Speed.GetValue(__instance, null);
                    if (!(speedObj is byte)) return true;
                    var speed = (byte)speedObj;

                    // Only override on-foot (1..3). Let original handle vehicles/others.
                    if (speed == 0 || speed >= 4) return true;

                    // Compute predictPosition = Predict(Position) + Velocity
                    Vector3 pos = default(Vector3);
                    Vector3 vel = default(Vector3);
                    try { var p = ClientHeadingPatch.PI_Position.GetValue(__instance, null); if (p is Vector3) pos = (Vector3)p; } catch { }
                    try { var v = ClientHeadingPatch.PI_Velocity.GetValue(__instance, null); if (v is Vector3) vel = (Vector3)v; } catch { }

                    Vector3 predict = pos;
                    try { predict = (Vector3)ClientHeadingPatch.MI_Predict.Invoke(__instance, new object[] { pos }); } catch { }
                    Vector3 predictPosition = predict + vel;

                    // Desired heading (server-sent)
                    float heading = ped.Heading;
                    try
                    {
                        var h = ClientHeadingPatch.PI_Heading.GetValue(__instance, null);
                        if (h is float) heading = (float)h;
                    }
                    catch { }

                    // Thresholds roughly match original behavior (using ped.Position instead of ReadPosition)
                    Vector3 cur = ped.Position;
                    float dx = predictPosition.X - cur.X;
                    float dy = predictPosition.Y - cur.Y;
                    float dz = predictPosition.Z - cur.Z;
                    float rangeSq = dx * dx + dy * dy + dz * dz;

                    // Execute movement with correct heading for all on-foot speeds
                    switch (speed)
                    {
                        case 1:
                            // Walk: small threshold, slow move. Use GO_STRAIGHT_TO_COORD so we can pass heading.
                            if (!ped.IsWalking || rangeSq > 0.25f)
                            {
                                float moveSpeed = 1.0f;
                                Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle,
                                              predictPosition.X, predictPosition.Y, predictPosition.Z,
                                              moveSpeed, -1, heading, 0.0f);

                                // Blend ratio roughly follows original nrange
                                float nrange = rangeSq * 2f;
                                if (nrange > 1.0f) nrange = 1.0f;
                                Function.Call(Hash.SET_PED_DESIRED_MOVE_BLEND_RATIO, ped.Handle, nrange);
                            }
                            break;

                        case 2:
                            // Run: medium threshold
                            if (!ped.IsRunning || rangeSq > 0.50f)
                            {
                                float moveSpeed = 2.0f;
                                Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle,
                                              predictPosition.X, predictPosition.Y, predictPosition.Z,
                                              moveSpeed, -1, heading, 0.0f);
                                Function.Call(Hash.SET_PED_DESIRED_MOVE_BLEND_RATIO, ped.Handle, 1.0f);
                            }
                            break;

                        case 3:
                            // Sprint: original forced 0.0f heading; we pass the correct heading here
                            if (!ped.IsSprinting || rangeSq > 0.75f)
                            {
                                Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle,
                                              predictPosition.X, predictPosition.Y, predictPosition.Z,
                                              3.0f, -1, heading, 0.0f);
                                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, ped.Handle, 1.49f);
                                Function.Call(Hash.SET_PED_DESIRED_MOVE_BLEND_RATIO, ped.Handle, 1.0f);
                            }
                            break;
                    }

                    // Hard-enforce facing immediately (AI may ignore desired heading while moving)
                    ped.Heading = heading;

                    // Keep original smoothing behavior
                    try { ClientHeadingPatch.MI_SmoothTransition.Invoke(__instance, null); } catch { }

                    // Skip original WalkTo entirely (we handled all on-foot branches)
                    return false;
                }
                catch
                {
                    // If anything fails, let original execute
                    return true;
                }
            }
        }
    }
}
