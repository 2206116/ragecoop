// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
//
// Goal: Fix "peds face North" without modifying client source by patching at runtime.
// What this file does:
// 1) Force sprint task to use the server-sent Heading instead of 0.0f by overriding SyncedPed.WalkTo for Speed==3.
// 2) Ensure SyncedPed.Heading is always set on receive, even if the packet has 0 or stale heading:
//    - Postfix-patch Networking.PedSync to compute heading from packet velocity or position delta and write it into the SyncedPed.
// 3) As a last resort, keep ped facing aligned by setting Heading on the entity that SmoothTransition blends toward.
//
// C# 7.3 compatible. No generic Function.Call<T> patching. No use of TaskType/ReadPosition.
//
// You should see one-time install logs in client log upon connect.
// If you still see North, enable the DEBUG logs inside the PedSync Postfix to inspect computed headings.

using System;
using System.Collections;
using System.Collections.Generic;
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

        // Types we need
        private Type _tSyncedPed;
        private Type _tNetworking;
        private Type _tEntityPool;

        // Shared reflection cache for SyncedPed
        internal static PropertyInfo PI_Speed;
        internal static PropertyInfo PI_Heading;
        internal static PropertyInfo PI_MainPed;
        internal static PropertyInfo PI_Position;
        internal static PropertyInfo PI_Velocity;
        internal static MethodInfo   MI_Predict;
        internal static MethodInfo   MI_SmoothTransition;

        // EntityPool for resolving peds by id in PedSync postfix
        internal static MethodInfo   MI_GetPedByID;

        // Packet types
        private Type _tPackets;
        private Type _tPedSync;
        internal static PropertyInfo PK_ID;
        internal static PropertyInfo PK_OwnerID;
        internal static PropertyInfo PK_Position;
        internal static PropertyInfo PK_Velocity;
        internal static PropertyInfo PK_Speed;
        internal static PropertyInfo PK_Heading;

        public override void OnStart()
        {
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                var clientAsm = asms.FirstOrDefault(a => string.Equals(a.GetName().Name, "RageCoop.Client", StringComparison.OrdinalIgnoreCase));
                var coreAsm   = asms.FirstOrDefault(a => string.Equals(a.GetName().Name, "RageCoop.Core", StringComparison.OrdinalIgnoreCase));
                if (clientAsm == null || coreAsm == null)
                {
                    Logger?.Error("[ClientPatch] Required assemblies not found (RageCoop.Client/Core).");
                    return;
                }

                // Bind client types
                _tSyncedPed  = clientAsm.GetType("RageCoop.Client.SyncedPed", true);
                _tNetworking = clientAsm.GetType("RageCoop.Client.Networking", true);
                _tEntityPool = clientAsm.GetType("RageCoop.Client.EntityPool", true);

                // Bind packet types
                _tPackets = coreAsm.GetType("RageCoop.Core.Packets", true);
                _tPedSync = _tPackets.GetNestedType("PedSync", BindingFlags.NonPublic | BindingFlags.Public);
                if (_tPedSync == null)
                {
                    Logger?.Error("[ClientPatch] Could not find Packets.PedSync type.");
                    return;
                }

                // Bind SyncedPed reflection members
                PI_Speed           = _tSyncedPed.GetProperty("Speed",   BindingFlags.Public | BindingFlags.Instance);
                PI_Heading         = _tSyncedPed.GetProperty("Heading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PI_MainPed         = _tSyncedPed.GetProperty("MainPed", BindingFlags.Public | BindingFlags.Instance);
                PI_Position        = _tSyncedPed.GetProperty("Position",BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PI_Velocity        = _tSyncedPed.GetProperty("Velocity",BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                MI_Predict         = _tSyncedPed.GetMethod("Predict", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(Vector3) }, null);
                MI_SmoothTransition= _tSyncedPed.GetMethod("SmoothTransition", BindingFlags.NonPublic | BindingFlags.Instance);

                // EntityPool.GetPedByID(int)
                MI_GetPedByID = _tEntityPool.GetMethod("GetPedByID", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(int) }, null);

                // Packet property bindings
                PK_ID       = _tPedSync.GetProperty("ID",        BindingFlags.Public | BindingFlags.Instance);
                PK_OwnerID  = _tPedSync.GetProperty("OwnerID",   BindingFlags.Public | BindingFlags.Instance);
                PK_Position = _tPedSync.GetProperty("Position",  BindingFlags.Public | BindingFlags.Instance);
                PK_Velocity = _tPedSync.GetProperty("Velocity",  BindingFlags.Public | BindingFlags.Instance);
                PK_Speed    = _tPedSync.GetProperty("Speed",     BindingFlags.Public | BindingFlags.Instance);
                PK_Heading  = _tPedSync.GetProperty("Heading",   BindingFlags.Public | BindingFlags.Instance);

                if (PI_Speed == null || PI_Heading == null || PI_MainPed == null ||
                    PI_Position == null || PI_Velocity == null || MI_Predict == null || MI_SmoothTransition == null ||
                    MI_GetPedByID == null || PK_ID == null || PK_Position == null || PK_Velocity == null || PK_Speed == null || PK_Heading == null)
                {
                    Logger?.Error("[ClientPatch] Failed to bind required members.");
                    return;
                }

                _harmony = new Harmony("ragecoop.clientpatch.heading");

                // 1) Override sprint in SyncedPed.WalkTo (instance, no args)
                var miWalkTo = _tSyncedPed.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                          .FirstOrDefault(m => m.Name == "WalkTo" && m.GetParameters().Length == 0);
                if (miWalkTo != null)
                {
                    _harmony.Patch(miWalkTo, prefix: new HarmonyMethod(typeof(WalkToSprintOverride).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)));
                }
                else
                {
                    Logger?.Warning("[ClientPatch] Could not locate SyncedPed.WalkTo; sprint heading override not applied.");
                }

                // 2) Ensure Heading is set on receive: Postfix on Networking.PedSync(Packets.PedSync)
                var miNetPedSync = _tNetworking.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                                              .FirstOrDefault(m =>
                                              {
                                                  if (m.Name != "PedSync") return false;
                                                  var p = m.GetParameters();
                                                  return p.Length == 1 && p[0].ParameterType == _tPedSync;
                                              });
                if (miNetPedSync != null)
                {
                    _harmony.Patch(miNetPedSync, postfix: new HarmonyMethod(typeof(ReceivePedSyncPostfix).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)));
                }
                else
                {
                    Logger?.Warning("[ClientPatch] Could not locate Networking.PedSync; receive heading reinforcement not applied.");
                }

                Logger?.Info("[ClientPatch] Installed: WalkTo sprint override + receive heading reinforcement.");
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

        // Sprint override: ensure TASK_GO_STRAIGHT_TO_COORD uses Heading instead of 0.0f, and hard-set ped.Heading immediately.
        public static class WalkToSprintOverride
        {
            public static bool Prefix(object __instance)
            {
                try
                {
                    var speedObj = ClientHeadingPatch.PI_Speed.GetValue(__instance, null);
                    if (!(speedObj is byte)) return true;
                    var speed = (byte)speedObj;
                    if (speed != 3) return true; // only intercept sprint; let original handle other speeds

                    var ped = ClientHeadingPatch.PI_MainPed.GetValue(__instance, null) as Ped;
                    if (ped == null || !ped.Exists()) return true;

                    // predictPosition = Predict(Position) + Velocity
                    Vector3 pos = default(Vector3);
                    Vector3 vel = default(Vector3);
                    try { var p = ClientHeadingPatch.PI_Position.GetValue(__instance, null); if (p is Vector3) pos = (Vector3)p; } catch { }
                    try { var v = ClientHeadingPatch.PI_Velocity.GetValue(__instance, null); if (v is Vector3) vel = (Vector3)v; } catch { }

                    Vector3 predict = pos;
                    try { predict = (Vector3)ClientHeadingPatch.MI_Predict.Invoke(__instance, new object[] { pos }); } catch { }
                    Vector3 predictPosition = predict + vel;

                    float heading = ped.Heading;
                    try
                    {
                        var h = ClientHeadingPatch.PI_Heading.GetValue(__instance, null);
                        if (h is float) heading = (float)h;
                    }
                    catch { }

                    // Sprint with correct heading
                    Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle,
                                  predictPosition.X, predictPosition.Y, predictPosition.Z,
                                  3.0f, -1, heading, 0.0f);

                    Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, ped.Handle, 1.49f);
                    Function.Call(Hash.SET_PED_DESIRED_MOVE_BLEND_RATIO, ped.Handle, 1.0f);

                    // Hard-face the desired heading now; SmoothTransition will blend further
                    ped.Heading = heading;

                    // Maintain smoothing
                    try { ClientHeadingPatch.MI_SmoothTransition.Invoke(__instance, null); } catch { }

                    return false; // skip original sprint branch
                }
                catch
                {
                    return true;
                }
            }
        }

        // Postfix on Networking.PedSync to ensure SyncedPed.Heading is always correct even if packet had 0.0
        public static class ReceivePedSyncPostfix
        {
            // Track last received position for heading-from-delta fallback
            private static readonly Dictionary<int, Vector3> _lastPos = new Dictionary<int, Vector3>();

            public static void Postfix(object packet)
            {
                try
                {
                    if (packet == null) return;

                    int id = 0;
                    Vector3 pos = default(Vector3);
                    Vector3 vel = default(Vector3);
                    byte speed = 0;
                    float pktHeading = 0f;

                    try { id = (int)ClientHeadingPatch.PK_ID.GetValue(packet, null); } catch { }
                    try { pos = (Vector3)ClientHeadingPatch.PK_Position.GetValue(packet, null); } catch { }
                    try { vel = (Vector3)ClientHeadingPatch.PK_Velocity.GetValue(packet, null); } catch { }
                    try { speed = (byte)ClientHeadingPatch.PK_Speed.GetValue(packet, null); } catch { }
                    try { pktHeading = (float)ClientHeadingPatch.PK_Heading.GetValue(packet, null); } catch { }

                    // Only on-foot
                    if (speed == 0 || speed >= 4) { _lastPos[id] = pos; return; }

                    // Resolve SyncedPed
                    var c = ClientHeadingPatch.MI_GetPedByID.Invoke(null, new object[] { id });
                    if (c == null) { _lastPos[id] = pos; return; }

                    // If packet heading is 0 or obviously wrong, compute from velocity or delta
                    float useHeading = pktHeading;
                    var hsp = (float)Math.Sqrt(vel.X * vel.X + vel.Y * vel.Y);

                    if (useHeading == 0f)
                    {
                        if (hsp > 0.0025f)
                        {
                            useHeading = HeadingFromVec(vel);
                        }
                        else
                        {
                            Vector3 last;
                            if (_lastPos.TryGetValue(id, out last))
                            {
                                var dx = pos.X - last.X;
                                var dy = pos.Y - last.Y;
                                var dxy = (float)Math.Sqrt(dx * dx + dy * dy);
                                if (dxy > 0.001f)
                                {
                                    useHeading = HeadingFromVec(new Vector3(dx, dy, 0f));
                                }
                            }
                        }
                    }

                    if (useHeading < 0f) useHeading += 360f;
                    if (useHeading >= 360f) useHeading -= 360f;

                    // Write back into SyncedPed.Heading so SmoothTransition rotates toward it
                    try { ClientHeadingPatch.PI_Heading.SetValue(c, useHeading, null); } catch { }

                    // Remember last position for next delta
                    _lastPos[id] = pos;

                    // Optional debug:
                    // GTA.UI.Notification.Show($"Set heading {useHeading:N1} for ped {id} (spd={speed})");
                }
                catch
                {
                    // swallow
                }
            }

            private static float HeadingFromVec(Vector3 v)
            {
                // GTA: 0=N(+Y), 90=E(+X)
                var deg = (float)(Math.Atan2(v.X, v.Y) * (180.0 / Math.PI));
                return deg < 0 ? deg + 360f : deg;
            }
        }
    }
}
