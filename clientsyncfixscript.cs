// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
// This script forces remote peds to face their travel direction while walking/running
// by setting the desired heading each tick and after key movement updates.
//
// Compatible with C# 7.3 (no C# 9.0 patterns used).
//
// What it does:
// - Installs Harmony Postfix patches on SyncedPed.WalkTo and SyncedPed.SmoothTransition
//   to call SET_PED_DESIRED_HEADING after movement logic.
// - Adds a per-tick sweep to apply desired heading to all remote on-foot peds.

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

        // Cached reflection members
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
                // Locate the RageCoop.Client assembly and types
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

                // Cache members via reflection
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

                _harmony = new Harmony("ragecoop.syncfix.heading");

                // Patch SyncedPed.WalkTo (private) – Postfix: enforce desired heading after tasks run
                var miWalkTo = AccessTools.Method(_tSyncedPed, "WalkTo");
                if (miWalkTo != null)
                {
                    var postfix = typeof(SyncedPedPatches).GetMethod("WalkTo_Postfix", BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(miWalkTo, postfix: new HarmonyMethod(postfix));
                }
                else
                {
                    Logger?.Warning("[SyncFix] Could not locate SyncedPed.WalkTo; continuing with tick enforcement only.");
                }

                // Patch SyncedPed.SmoothTransition – Postfix: reinforce desired heading each update
                var miSmooth = AccessTools.Method(_tSyncedPed, "SmoothTransition");
                if (miSmooth != null)
                {
                    var postfix = typeof(SyncedPedPatches).GetMethod("SmoothTransition_Postfix", BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(miSmooth, postfix: new HarmonyMethod(postfix));
                }
                else
                {
                    Logger?.Warning("[SyncFix] Could not locate SyncedPed.SmoothTransition; continuing with tick enforcement only.");
                }

                // Subscribe to client tick to cover any missed frames
                API.Events.OnTick += OnTickEnforceHeading;

                Logger?.Info("[SyncFix] Client-side heading enforcement installed.");
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

        // Runs every client tick – enforces desired heading for all remote walking/running peds
        private void OnTickEnforceHeading()
        {
            try
            {
                var pedsDictObj = _fiPedsByID.GetValue(null);
                var pedsDict = pedsDictObj as IDictionary;
                if (pedsDict == null || pedsDict.Count == 0) return;

                foreach (DictionaryEntry kv in pedsDict)
                {
                    var sp = kv.Value; // SyncedPed instance
                    if (sp == null) continue;

                    // Skip local ped
                    var isLocalObj = _piIsLocal.GetValue(sp, null);
                    if (isLocalObj is bool && ((bool)isLocalObj)) continue;

                    // Only adjust on-foot movement (1..3)
                    var speedObj = _piSpeed.GetValue(sp, null);
                    if (!(speedObj is byte)) continue;
                    var speed = (byte)speedObj;
                    if (speed == 0 || speed >= 4) continue;

                    var pedObj = _piMainPed.GetValue(sp, null) as Ped;
                    if (pedObj == null || !pedObj.Exists()) continue;

                    // If aiming, prefer not to force heading (let strafe look direction be natural)
                    bool isAiming = false;
                    if (_piIsAiming != null)
                    {
                        try
                        {
                            var aimObj = _piIsAiming.GetValue(sp, null);
                            if (aimObj is bool) isAiming = (bool)aimObj;
                        }
                        catch { }
                    }
                    if (isAiming) continue;

                    var headingObj = _piHeading.GetValue(sp, null);
                    if (!(headingObj is float)) continue;
                    var heading = (float)headingObj;

                    // Primary enforcement: make nav tasks desire this heading
                    Function.Call(Hash.SET_PED_DESIRED_HEADING, pedObj.Handle, heading);
                }
            }
            catch
            {
                // Avoid noisy logs each frame
            }
        }

        // Harmony patches use reflection into SyncedPed to avoid a hard dependency.
        public static class SyncedPedPatches
        {
            // Cached members – looked up on first use
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
                catch
                {
                    // Swallow to avoid destabilizing client
                }
            }

            // After SmoothTransition nudges rotation, reinforce desired heading so nav tasks don't override it
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
                catch
                {
                    // Swallow to avoid destabilizing client
                }
            }
        }
    }
}
