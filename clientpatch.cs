// Build this as a client resource DLL and include Harmony (HarmonyLib) in the resource package.
//
// Hard client-side fix for "faces North":
// - Completely avoids the sprint code path (which hard-codes targetHeading=0.0) by converting Speed=3 to Speed=2
//   in two places:
//   1) On receive (Networking.PedSync): coerce incoming packet Speed from 3->2 before the ped updates.
//   2) In SyncedPed.WalkTo: if Speed is 3, flip it to 2 right before the switch so the RunTo branch executes.
// - Also ensures SyncedPed.Heading is set from packet or derived from motion (velocity/delta) on receive.
//
// C# 7.3 compatible. No patching of generic Function.Call<T>. No TaskType/ReadPosition usage.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GTA;
using GTA.Math;
using HarmonyLib;
using RageCoop.Client.Scripting;

namespace RageCoop.Resources.ClientPatch
{
    public class ClientHeadingPatch : ClientScript
    {
        private Harmony _harmony;

        // Types
        private Type _tSyncedPed;
        private Type _tNetworking;
        private Type _tEntityPool;
        private Type _tPackets;
        private Type _tPedSync;

        // SyncedPed members
        internal static PropertyInfo PI_IsLocal;
        internal static PropertyInfo PI_Speed;
        internal static PropertyInfo PI_Heading;
        internal static PropertyInfo PI_MainPed;

        // EntityPool access
        internal static MethodInfo MI_GetPedByID;

        // Packets.PedSync members
        internal static PropertyInfo PK_ID;
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

                _tSyncedPed  = clientAsm.GetType("RageCoop.Client.SyncedPed", true);
                _tNetworking = clientAsm.GetType("RageCoop.Client.Networking", true);
                _tEntityPool = clientAsm.GetType("RageCoop.Client.EntityPool", true);

                _tPackets = coreAsm.GetType("RageCoop.Core.Packets", true);
                _tPedSync = _tPackets.GetNestedType("PedSync", BindingFlags.NonPublic | BindingFlags.Public);

                // Bind SyncedPed members
                PI_IsLocal  = _tSyncedPed.GetProperty("IsLocal",  BindingFlags.Public | BindingFlags.Instance);
                PI_Speed    = _tSyncedPed.GetProperty("Speed",    BindingFlags.Public | BindingFlags.Instance);
                PI_Heading  = _tSyncedPed.GetProperty("Heading",  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PI_MainPed  = _tSyncedPed.GetProperty("MainPed",  BindingFlags.Public | BindingFlags.Instance);
                if (PI_IsLocal == null || PI_Speed == null || PI_Heading == null || PI_MainPed == null)
                {
                    Logger?.Error("[ClientPatch] Failed to bind SyncedPed members.");
                    return;
                }

                // EntityPool.GetPedByID(int)
                MI_GetPedByID = _tEntityPool.GetMethod("GetPedByID", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(int) }, null);

                // Packet members
                PK_ID       = _tPedSync.GetProperty("ID",        BindingFlags.Public | BindingFlags.Instance);
                PK_Position = _tPedSync.GetProperty("Position",  BindingFlags.Public | BindingFlags.Instance);
                PK_Velocity = _tPedSync.GetProperty("Velocity",  BindingFlags.Public | BindingFlags.Instance);
                PK_Speed    = _tPedSync.GetProperty("Speed",     BindingFlags.Public | BindingFlags.Instance);
                PK_Heading  = _tPedSync.GetProperty("Heading",   BindingFlags.Public | BindingFlags.Instance);

                if (MI_GetPedByID == null || PK_ID == null || PK_Position == null || PK_Velocity == null || PK_Speed == null || PK_Heading == null)
                {
                    Logger?.Error("[ClientPatch] Failed to bind PedSync/EntityPool members.");
                    return;
                }

                _harmony = new Harmony("ragecoop.clientpatch.force-run-and-heading");

                // 1) Patch Networking.PedSync(Packets.PedSync) to coerce Speed and set Heading
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
                    Logger?.Warning("[ClientPatch] Networking.PedSync not found; receive coercion not applied.");
                }

                // 2) Patch SyncedPed.WalkTo() to flip Speed=3->2 just before the switch
                var miWalkTo = _tSyncedPed.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                          .FirstOrDefault(m => m.Name == "WalkTo" && m.GetParameters().Length == 0);
                if (miWalkTo != null)
                {
                    _harmony.Patch(miWalkTo, prefix: new HarmonyMethod(typeof(WalkToForceRunPrefix).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)));
                }
                else
                {
                    Logger?.Warning("[ClientPatch] SyncedPed.WalkTo not found; run coercion not applied.");
                }

                Logger?.Info("[ClientPatch] Installed: Receive Speed/Heading coercion + WalkTo run-forcing prefix.");
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
                    _harmony.UnpatchAll("ragecoop.clientpatch.force-run-and-heading");
                }
            }
            catch (Exception ex)
            {
                Logger?.Warning("[ClientPatch] Unpatch error: " + ex);
            }
        }

        // Postfix: after the client processes a PedSync, enforce Heading and flip Sprint->Run for remote peds
        public static class ReceivePedSyncPostfix
        {
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

                    // Resolve SyncedPed
                    var sp = ClientHeadingPatch.MI_GetPedByID.Invoke(null, new object[] { id });
                    if (sp == null) { _lastPos[id] = pos; return; }

                    // Flip sprint to run for all remote peds so client never enters the sprint branch
                    try
                    {
                        var isLocalObj = ClientHeadingPatch.PI_IsLocal.GetValue(sp, null);
                        bool isLocal = (isLocalObj is bool) && (bool)isLocalObj;
                        if (!isLocal && speed == 3)
                        {
                            ClientHeadingPatch.PI_Speed.SetValue(sp, (byte)2, null);
                        }
                    }
                    catch { }

                    // Ensure Heading is non-zero and reflects motion if packet provided 0
                    float useHeading = pktHeading;
                    float hsp = (float)Math.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
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
                                if (dxy > 0.001f) useHeading = HeadingFromVec(new Vector3(dx, dy, 0f));
                            }
                        }
                    }
                    if (useHeading < 0f) useHeading += 360f;
                    if (useHeading >= 360f) useHeading -= 360f;

                    try { ClientHeadingPatch.PI_Heading.SetValue(sp, useHeading, null); } catch { }

                    _lastPos[id] = pos;
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

        // Prefix: right before SyncedPed.WalkTo’s switch(Speed), force Speed=2 when Speed==3
        public static class WalkToForceRunPrefix
        {
            public static void Prefix(object __instance)
            {
                try
                {
                    var speedObj = ClientHeadingPatch.PI_Speed.GetValue(__instance, null);
                    if (!(speedObj is byte)) return;
                    var speed = (byte)speedObj;
                    if (speed == 3)
                    {
                        ClientHeadingPatch.PI_Speed.SetValue(__instance, (byte)2, null);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
