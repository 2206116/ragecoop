using Lidgren.Network;
using RageCoop.Core;
using RageCoop.Server.Scripting;
using System;
using System.Collections.Generic;

namespace RageCoop.Server
{
    public partial class Server
    {
        // Track last known on-foot speed and heading per ped (kept for future use)
        private readonly Dictionary<int, byte> _lastFootSpeed = new();
        private readonly Dictionary<int, float> _lastMoveHeading = new();
        private readonly Dictionary<int, long> _lastMoveHintAt = new();

        private static float HorizontalSpeed(GTA.Math.Vector3 v)
            => (float)Math.Sqrt(v.X * v.X + v.Y * v.Y);

        private static float HeadingFromVelocity(GTA.Math.Vector3 v)
        {
            var deg = (float)(Math.Atan2(v.X, v.Y) * (180.0 / Math.PI));
            if (deg < 0) deg += 360f;
            return deg;
        }

        // Trust client’s reported walk/run/sprint; only force stop when nearly stationary
        private static byte NormalizeFootSpeed(byte reported, GTA.Math.Vector3 velocity, PedDataFlags flags)
        {
            if (reported >= 4) return reported; // vehicle states
                                                // Lower threshold so we don't prematurely force stop during deceleration
            if (HorizontalSpeed(velocity) < 0.03f) return 0;
            return reported;
        }

        private static float AngleDelta(float a, float b)
        {
            float d = a - b;
            while (d > 180f) d -= 360f;
            while (d < -180f) d += 360f;
            return Math.Abs(d);
        }

        private void PedSync(Packets.PedSync packet, Client client)
        {
            QueueJob(() => Entities.Update(packet, client));

            bool isPlayer = packet.ID == client.Player.ID;
            if (isPlayer)
            {
                QueueJob(() => API.Events.InvokePlayerUpdate(client));
            }

            // Normalize speed conservatively
            packet.Speed = NormalizeFootSpeed(packet.Speed, packet.Velocity, packet.Flags);

            // Movement state
            bool onFootMoving = packet.Speed > 0 && packet.Speed < 4 && HorizontalSpeed(packet.Velocity) > 0.1f;

            // Do NOT override heading here; let clients/Harmony patch derive facing from motion

            _lastFootSpeed[packet.ID] = packet.Speed;

            if (onFootMoving)
            {
                _lastMoveHeading[packet.ID] = packet.Heading;
            }
            else
            {
                _lastMoveHeading.Remove(packet.ID);
            }

            foreach (var c in ClientsByNetHandle.Values)
            {
                if (c.NetHandle == client.NetHandle) { continue; }

                if (isPlayer)
                {
                    if ((Settings.PlayerStreamingDistance != -1) && (packet.Position.DistanceTo(c.Player.Position) > Settings.PlayerStreamingDistance))
                        continue;
                }
                else if ((Settings.NpcStreamingDistance != -1) && (packet.Position.DistanceTo(c.Player.Position) > Settings.NpcStreamingDistance))
                    continue;

                var outgoingMessage = MainNetServer.CreateMessage();
                packet.Pack(outgoingMessage);
                MainNetServer.SendMessage(outgoingMessage, c.Connection, NetDeliveryMethod.UnreliableSequenced, (byte)ConnectionChannel.PedSync);
            }
        }

        private void VehicleSync(Packets.VehicleSync packet, Client client)
        {
            QueueJob(() => Entities.Update(packet, client));
            bool isPlayer = packet.ID == client.Player?.LastVehicle?.ID;

            foreach (var c in ClientsByNetHandle.Values)
            {
                if (c.NetHandle == client.NetHandle) { continue; }
                if (isPlayer)
                {
                    if ((Settings.PlayerStreamingDistance != -1) && (packet.Position.DistanceTo(c.Player.Position) > Settings.PlayerStreamingDistance))
                    {
                        continue;
                    }
                }
                else if ((Settings.NpcStreamingDistance != -1) && (packet.Position.DistanceTo(c.Player.Position) > Settings.NpcStreamingDistance))
                {
                    continue;
                }
                NetOutgoingMessage outgoingMessage = MainNetServer.CreateMessage();
                packet.Pack(outgoingMessage);
                MainNetServer.SendMessage(outgoingMessage, c.Connection, NetDeliveryMethod.UnreliableSequenced, (byte)ConnectionChannel.VehicleSync);
            }
        }

        private void ProjectileSync(Packets.ProjectileSync packet, Client client)
        {
            Forward(packet, client, ConnectionChannel.ProjectileSync);
        }
    }
}
