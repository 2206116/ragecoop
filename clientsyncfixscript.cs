// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource.
// This script forces remote peds to face their travel direction while walking/running
// by setting the desired heading each tick and after key movement updates.
//
// What it does:
// - Installs Harmony Postfix patches on SyncedPed.WalkTo and SyncedPed.SmoothTransition
//   to call SET_PED_DESIRED_HEADING every update.
// - Adds a per-tick sweep as a safety net to apply desired heading to all remote peds.
//
// Why this fixes the issue:
// Movement tasks (RunTo/GoStraightTo) can override entity heading and cause “sideways run”.
// Forcing desired heading each frame aligns animation with motion without replacing tasks.

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
        private EventInfo _onTickEvent;
        private Delegate _onTickHandler;

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

                _tSyncedPed = clientAsm.GetType("RageCoop.Client.SyncedPed", throwOnError: true);
                _tEntityPool = clientAsm.GetType("RageCoop.Client.EntityPool", throwOnError: true);

                // Cache public/internal members via reflection
                _piIsLocal = _tSyncedPed.GetProperty("IsLocal", BindingFlags.Public | BindingFlags.Instance);
                _piSpeed = _tSyncedPed.GetProperty("Speed", BindingFlags.Public | BindingFlags.Instance);
                _piHeading = _tSyncedPed.GetProperty("Heading", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                _piMainPed = _tSyncedPed.GetProperty("MainPed", BindingFlags.Public | BindingFlags.Instance);
                _piIsAiming = _tSyncedPed.GetProperty("IsAiming", BindingFlags.NonPublic | BindingFlags.Instance);
                _fiPedsByID = _tEntityPool.GetField("PedsByID", BindingFlags.Public | BindingFlags.Static);

                if (_piIsLocal == null || _piSpeed == null || _piHeading == null || _piMainPed == null || _fiPedsByID == null)
                {
                    Logger?.Error("[SyncFix] Failed to bind SyncedPed/EntityPool members. Aborting.");
                    return;
                }

                _harmony = new Harmony("ragecoop.syncfix.heading");

                // Patch SyncedPed.WalkTo (private) – add Postfix that enforces desired heading after tasks run
                var miWalkTo = AccessTools.Method(_tSyncedPed, "WalkTo");
                if (miWalkTo != null)
                {
                    var postfix = typeof(SyncedPedPatches).GetMethod(nameof(SyncedPedPatches.WalkTo_Postfix), BindingFlags.Static | BindingFlags.NonPublic);
                    _harmony.Patch(miWalkTo, postfix: new HarmonyMethod(postfix));
                }
                else
                {
                    Logger?.Warning("[SyncFix] Could not locate SyncedPed.WalkTo; continuing with tick enforcement only.");
                }

                // Patch SyncedPed.SmoothTransition – add Postfix to reinforce desired heading every frame
                var miSmooth = AccessTools.Method(_tSyncedPed, "SmoothTransition");
                if (miSmooth != null)
                {
                    var postfix = typeof(SyncedPedPatches).GetMethod(nameof(SyncedPedPatches.SmoothTransition_Postfix), BindingFlags.Static | BindingFlags.NonPublic);
                    _harmony.Patch(miSmooth, postfix: new HarmonyMethod(postfix));
                }
                else
                {
                    Logger?.Warning("[SyncFix] Could not locate SyncedPed.SmoothTransition; continuing with tick enforcement only.");
                }

                // Subscribe to API.Events.OnTick as a safety net to cover any missed frames
                var apiEventsType = typeof(RageCoop.Client.Scripting.API.Events);
                _onTickEvent = apiEventsType.GetEvent("OnTick", BindingFlags.Public | BindingFlags.Static);
                if (_onTickEvent != null)
                {
                    _onTickHandler = Delegate.CreateDelegate(_onTickEvent.EventHandlerType, this, nameof(OnTickEnforceHeading));
                    _onTickEvent.AddEventHandler(null, _onTickHandler);
                }

                Logger?.Info("[SyncFix] Client-side heading enforcement installed.");
            }
            catch (Exception ex)
            {
                Logger?.Error("[SyncFix] Error during install:", ex);
            }
        }

        public override void OnStop()
        {
            try
            {
                if (_onTickEvent != null && _onTickHandler != null)
                {
                    _onTickEvent.RemoveEventHandler(null, _onTickHandler);
                    _onTickHandler = null;
                }
            }
            catch (Exception ex)
            {
                Logger?.Warning("[SyncFix] Failed to detach tick handler.", ex);
            }

            try
            {
                _harmony?.UnpatchAll("ragecoop.syncfix.heading");
            }
            catch (Exception ex)
            {
                Logger?.Warning("[SyncFix] Failed to unpatch Harmony.", ex);
            }
        }

        // Runs every client tick – enforces desired heading for all remote walking/running peds
        private void OnTickEnforceHeading()
        {
            try
            {
                var pedsDict = _fiPedsByID.GetValue(null) as IDictionary;
                if (pedsDict == null || pedsDict.Count == 0) return;

                foreach (DictionaryEntry kv in pedsDict)
                {
                    var sp = kv.Value; // SyncedPed instance (unknown type here)
                    if (sp == null) continue;

                    // Skip local ped
                    if (_piIsLocal.GetValue(sp) is bool isLocal && isLocal) continue;

                    // Only adjust on-foot movement (1..3)
                    var speedObj = _piSpeed.GetValue(sp);
                    if (speedObj is not byte speed || speed == 0 || speed >= 4) continue;

                    var pedObj = _piMainPed.GetValue(sp) as Ped;
                    if (pedObj == null || !pedObj.Exists()) continue;

                    // If aiming, prefer not to force heading (let strafe look direction be natural)
                    bool isAiming = false;
                    if (_piIsAiming != null)
                    {
                        try { isAiming = (bool)_piIsAiming.GetValue(sp); } catch { }
                    }
                    if (isAiming) continue;

                    var headingObj = _piHeading.GetValue(sp);
                    if (headingObj is not float heading) continue;

                    // Primary enforcement: make nav tasks desire this heading
                    Function.Call(Hash.SET_PED_DESIRED_HEADING, pedObj.Handle, heading);
                }
            }
            catch
            {
                // Avoid noisy logs each frame; this runs very frequently.
            }
        }

        // Harmony patches use reflection back into SyncedPed to avoid a hard dependency.
        private static class SyncedPedPatches
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
            private static void WalkTo_Postfix(object __instance)
            {
                try
                {
                    EnsureBind(__instance);

                    var speedObj = PI_Speed?.GetValue(__instance);
                    if (speedObj is not byte speed || speed == 0 || speed >= 4) return;

                    var ped = PI_MainPed?.GetValue(__instance) as Ped;
                    if (ped == null || !ped.Exists()) return;

                    bool isAiming = false;
                    if (PI_IsAiming != null)
                    {
                        try { isAiming = (bool)PI_IsAiming.GetValue(__instance); } catch { }
                    }
                    if (isAiming) return;

                    var headingObj = PI_Heading?.GetValue(__instance);
                    if (headingObj is not float heading) return;

                    Function.Call(Hash.SET_PED_DESIRED_HEADING, ped.Handle, heading);
                }
                catch
                {
                    // Swallow to avoid destabilizing client
                }
            }

            // After SmoothTransition nudges rotation, reinforce desired heading so nav tasks don't override it
            private static void SmoothTransition_Postfix(object __instance)
            {
                try
                {
                    EnsureBind(__instance);

                    var speedObj = PI_Speed?.GetValue(__instance);
                    if (speedObj is not byte speed || speed == 0 || speed >= 4) return;

                    var ped = PI_MainPed?.GetValue(__instance) as Ped;
                    if (ped == null || !ped.Exists()) return;

                    bool isAiming = false;
                    if (PI_IsAiming != null)
                    {
                        try { isAiming = (bool)PI_IsAiming.GetValue(__instance); } catch { }
                    }
                    if (isAiming) return;

                    var headingObj = PI_Heading?.GetValue(__instance);
                    if (headingObj is not float heading) return;

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
