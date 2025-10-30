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

        // Last time we observed movement for a ped (used for speed hysteresis)
        private readonly Dictionary<int, long> _lastFootMovingAt = new();

        // Track last known position per ped to recover heading when velocity is tiny
        private readonly Dictionary<int, GTA.Math.Vector3> _lastPos = new();

        private static float HorizontalSpeed(GTA.Math.Vector3 v)
            => (float)Math.Sqrt(v.X * v.X + v.Y * v.Y);

        private static float HeadingFromVector(GTA.Math.Vector3 v)
        {
            // GTA heading: 0° = North (+Y), 90° = East (+X), 180° = South (-Y), 270° = West (-X)
            var deg = (float)(Math.Atan2(v.X, v.Y) * (180.0 / Math.PI));
            if (deg < 0) deg += 360f;
            return deg;
        }

        private static float HeadingFromDelta(GTA.Math.Vector3 from, GTA.Math.Vector3 to)
        {
            var d = new GTA.Math.Vector3(to.X - from.X, to.Y - from.Y, 0f);
            return HeadingFromVector(d);
        }

        // Trust client’s reported walk/run/sprint; only force stop when nearly stationary
        // Loosened threshold so quick turns don’t get treated as a stop.
        private static byte NormalizeFootSpeed(byte reported, GTA.Math.Vector3 velocity, PedDataFlags flags)
        {
            if (reported >= 4) return reported; // vehicle states
            if (HorizontalSpeed(velocity) < 0.005f) return 0; // very close to still
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

            // Normalize speed conservatively (with hysteresis)
            packet.Speed = NormalizeFootSpeed(packet.Speed, packet.Velocity, packet.Flags);

            // Add in PedSync after packet.Speed normalized and before broadcasting:
            if (packet.Speed == 3)
            {
                // Map sprint to run to avoid TASK_GO_STRAIGHT_TO_COORD heading=0 on clients
                packet.Speed = 2;
            }

            var now = Environment.TickCount64;
            var hsp = HorizontalSpeed(packet.Velocity);

            // Hysteresis: keep last non-zero speed briefly when instantaneous velocity dips during a turn
            bool consideredMoving = (packet.Speed > 0) || (hsp > 0.01f);
            if (consideredMoving)
            {
                _lastFootMovingAt[packet.ID] = now;
            }
            else
            {
                if (_lastFootMovingAt.TryGetValue(packet.ID, out var lastMoveTs) && (now - lastMoveTs) < 200)
                {
                    if (_lastFootSpeed.TryGetValue(packet.ID, out var prevSpeed) && prevSpeed > 0)
                    {
                        packet.Speed = prevSpeed;
                    }
                }
            }

            // Movement state
            bool onFoot = packet.Speed > 0 && packet.Speed < 4;

            // Force heading from motion when on foot and not aiming.
            if (onFoot && !packet.Flags.HasPedFlag(PedDataFlags.IsAiming))
            {
                float heading;
                if (hsp > 0.0025f)
                {
                    // Prefer velocity-derived heading when it has signal
                    heading = HeadingFromVector(packet.Velocity);
                }
                else
                {
                    // Fall back to position delta since some clients may send near-zero velocity while still moving
                    if (_lastPos.TryGetValue(packet.ID, out var lastP))
                    {
                        var deltaXY = HorizontalSpeed(new GTA.Math.Vector3(packet.Position.X - lastP.X, packet.Position.Y - lastP.Y, 0f));
                        if (deltaXY > 0.001f)
                        {
                            heading = HeadingFromDelta(lastP, packet.Position);
                        }
                        else
                        {
                            // If no usable delta, keep previous heading if we have it
                            if (!_lastMoveHeading.TryGetValue(packet.ID, out heading))
                            {
                                heading = packet.Heading; // fallback to what client sent
                            }
                        }
                    }
                    else
                    {
                        heading = packet.Heading; // first sample, no delta yet
                    }
                }

                // Normalize 0..360 and apply
                if (heading < 0) heading += 360f;
                if (heading >= 360f) heading -= 360f;
                packet.Heading = heading;
            }

            // Persist state for next tick
            _lastFootSpeed[packet.ID] = packet.Speed;
            _lastPos[packet.ID] = packet.Position;

            // Save heading for delta smoothing next tick
            if (onFoot)
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
