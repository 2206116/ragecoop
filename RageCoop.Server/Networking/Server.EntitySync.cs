using Lidgren.Network;
using RageCoop.Core;
using RageCoop.Server.Scripting;
using System;
using System.Collections.Generic;

namespace RageCoop.Server
{
    public partial class Server
    {
        // Keep light state for heading derivation and hysteresis across ticks
        private readonly Dictionary<int, GTA.Math.Vector3> _lastPos = new();
        private readonly Dictionary<int, byte> _lastFootSpeed = new();
        private readonly Dictionary<int, long> _lastFootMovingAt = new();

        private static float HSpeed(GTA.Math.Vector3 v) => (float)Math.Sqrt(v.X * v.X + v.Y * v.Y);

        private static float HeadingFromVec(GTA.Math.Vector3 v)
        {
            // GTA: 0=N(+Y), 90=E(+X)
            var deg = (float)(Math.Atan2(v.X, v.Y) * (180.0 / Math.PI));
            return deg < 0 ? deg + 360f : deg;
        }

        private static float HeadingFromDelta(GTA.Math.Vector3 from, GTA.Math.Vector3 to)
            => HeadingFromVec(new GTA.Math.Vector3(to.X - from.X, to.Y - from.Y, 0f));

        // Only force stop when truly stationary; otherwise trust the client's walk/run intent
        private static byte NormalizeFootSpeed(byte reported, GTA.Math.Vector3 velocity)
        {
            if (reported >= 4) return reported; // vehicle states untouched
            return HSpeed(velocity) < 0.005f ? (byte)0 : reported;
        }

        private void PedSync(Packets.PedSync packet, Client client)
        {
            // Update authoritative entity state on the server
            QueueJob(() => Entities.Update(packet, client));

            bool isOwnerPlayer = packet.ID == client.Player.ID;
            if (isOwnerPlayer)
            {
                QueueJob(() => API.Events.InvokePlayerUpdate(client));
            }

            // Normalize on-foot speed with short hysteresis to avoid thrash on turns
            packet.Speed = NormalizeFootSpeed(packet.Speed, packet.Velocity);
            var now = Environment.TickCount64;
            var hsp = HSpeed(packet.Velocity);

            if ((packet.Speed > 0) || (hsp > 0.01f))
            {
                _lastFootMovingAt[packet.ID] = now;
            }
            else
            {
                if (_lastFootMovingAt.TryGetValue(packet.ID, out var lastTs) && (now - lastTs) < 200)
                {
                    if (_lastFootSpeed.TryGetValue(packet.ID, out var prev) && prev > 0)
                        packet.Speed = prev;
                }
            }

            // HARD SERVER-SIDE BYPASS: avoid the client sprint path that forces targetHeading = 0
            if (packet.Speed == 3)
            {
                // Optional debug so you can confirm it’s happening in your server log
                Logger?.Debug($"[PedSync] Mapping sprint->run for ped {packet.ID} (owner {packet.OwnerID}).");
                packet.Speed = 2;
            }

            // Derive heading from motion while on-foot (Speed 1..3)
            bool onFoot = packet.Speed > 0 && packet.Speed < 4;
            if (onFoot)
            {
                float newHeading;
                if (hsp > 0.0025f)
                {
                    newHeading = HeadingFromVec(packet.Velocity);
                }
                else
                {
                    if (_lastPos.TryGetValue(packet.ID, out var lastP))
                    {
                        var dxy = HSpeed(new GTA.Math.Vector3(packet.Position.X - lastP.X, packet.Position.Y - lastP.Y, 0f));
                        if (dxy > 0.001f) newHeading = HeadingFromDelta(lastP, packet.Position);
                        else
                        {
                            // keep the existing heading if we have nothing better
                            newHeading = packet.Heading;
                        }
                    }
                    else
                    {
                        newHeading = packet.Heading;
                    }
                }
                if (newHeading < 0) newHeading += 360f;
                if (newHeading >= 360f) newHeading -= 360f;
                packet.Heading = newHeading;
            }

            // Persist state for next tick
            _lastFootSpeed[packet.ID] = packet.Speed;
            _lastPos[packet.ID] = packet.Position;

            // Stream to nearby clients
            foreach (var c in ClientsByNetHandle.Values)
            {
                if (c.NetHandle == client.NetHandle) continue;

                if (isOwnerPlayer)
                {
                    if ((Settings.PlayerStreamingDistance != -1) &&
                        (packet.Position.DistanceTo(c.Player.Position) > Settings.PlayerStreamingDistance))
                        continue;
                }
                else
                {
                    if ((Settings.NpcStreamingDistance != -1) &&
                        (packet.Position.DistanceTo(c.Player.Position) > Settings.NpcStreamingDistance))
                        continue;
                }

                var msg = MainNetServer.CreateMessage();
                packet.Pack(msg);
                MainNetServer.SendMessage(msg, c.Connection, NetDeliveryMethod.UnreliableSequenced, (byte)ConnectionChannel.PedSync);
            }
        }

        private void VehicleSync(Packets.VehicleSync packet, Client client)
        {
            QueueJob(() => Entities.Update(packet, client));
            bool isPlayer = packet.ID == client.Player?.LastVehicle?.ID;

            foreach (var c in ClientsByNetHandle.Values)
            {
                if (c.NetHandle == client.NetHandle) continue;

                if (isPlayer)
                {
                    if ((Settings.PlayerStreamingDistance != -1) &&
                        (packet.Position.DistanceTo(c.Player.Position) > Settings.PlayerStreamingDistance))
                        continue;
                }
                else
                {
                    if ((Settings.NpcStreamingDistance != -1) &&
                        (packet.Position.DistanceTo(c.Player.Position) > Settings.NpcStreamingDistance))
                        continue;
                }

                var msg = MainNetServer.CreateMessage();
                packet.Pack(msg);
                MainNetServer.SendMessage(msg, c.Connection, NetDeliveryMethod.UnreliableSequenced, (byte)ConnectionChannel.VehicleSync);
            }
        }

        private void ProjectileSync(Packets.ProjectileSync packet, Client client)
        {
            Forward(packet, client, ConnectionChannel.ProjectileSync);
        }
    }
}
