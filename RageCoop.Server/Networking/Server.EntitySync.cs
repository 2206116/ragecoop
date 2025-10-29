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

        // NEW: last time we observed movement for a ped (used for speed hysteresis)
        private readonly Dictionary<int, long> _lastFootMovingAt = new();

        private static float HorizontalSpeed(GTA.Math.Vector3 v)
            => (float)Math.Sqrt(v.X * v.X + v.Y * v.Y);

        private static float HeadingFromVelocity(GTA.Math.Vector3 v)
        {
            var deg = (float)(Math.Atan2(v.X, v.Y) * (180.0 / Math.PI));
            if (deg < 0) deg += 360f;
            return deg;
        }

        // Trust client’s reported walk/run/sprint; only force stop when nearly stationary
        // Loosen the threshold so turns don’t get treated as a stop.
        private static byte NormalizeFootSpeed(byte reported, GTA.Math.Vector3 velocity, PedDataFlags flags)
        {
            if (reported >= 4) return reported; // vehicle states
            // Lower threshold further to avoid zeroing speed during sharp turns
            if (HorizontalSpeed(velocity) < 0.005f) return 0;
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

            // Hysteresis: keep last non-zero speed briefly when instantaneous velocity dips during a turn
            var now = Environment.TickCount64;
            var hsp = HorizontalSpeed(packet.Velocity);

            // Consider "moving" if server thinks speed > 0 OR velocity still reasonably high
            bool consideredMoving = (packet.Speed > 0) || (hsp > 0.03f);
            if (consideredMoving)
            {
                _lastFootMovingAt[packet.ID] = now;
            }
            else
            {
                // Within grace window? Keep the previous non-zero speed to avoid animation/task thrash
                if (_lastFootMovingAt.TryGetValue(packet.ID, out var lastMoveTs) && (now - lastMoveTs) < 200)
                {
                    if (_lastFootSpeed.TryGetValue(packet.ID, out var prevSpeed) && prevSpeed > 0)
                    {
                        packet.Speed = prevSpeed;
                    }
                }
            }

            // Movement state
            bool onFootMoving = packet.Speed > 0 && packet.Speed < 4 && hsp > 0.1f;

            // Force heading from motion when moving (and not aiming) so remote peds face their travel direction,
            // which prevents the sideways "lean" during turns.
            if (onFootMoving && !packet.Flags.HasPedFlag(PedDataFlags.IsAiming))
            {
                var motionHeading = HeadingFromVelocity(packet.Velocity);

                // Optional: small server-side damping to avoid micro jitter between packets.
                if (_lastMoveHeading.TryGetValue(packet.ID, out var lastHead))
                {
                    var delta = AngleDelta(motionHeading, lastHead);
                    if (delta > 2f)
                    {
                        // Nudge toward motion heading; client will still smooth further.
                        var step = Math.Min(delta, 15f);
                        // Determine shortest direction toward motionHeading
                        float dir = ((motionHeading - lastHead + 540f) % 360f) - 180f; // range [-180, 180]
                        packet.Heading = (lastHead + Math.Sign(dir) * step + 360f) % 360f;
                    }
                    else
                    {
                        packet.Heading = motionHeading;
                    }
                }
                else
                {
                    packet.Heading = motionHeading;
                }
            }
            // Else: do NOT override heading; let clients/Harmony patch derive facing from motion/aim

            _lastFootSpeed[packet.ID] = packet.Speed;

            if (onFootMoving)
            {
                // Store the heading we just decided to broadcast
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
