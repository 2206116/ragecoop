using GTA;
using GTA.Math;
using Lidgren.Network;
using System.Collections.Generic;
using System;
using System.IO;

namespace RageCoop.Core
{
    internal partial class Packets
    {

        public class VehicleSync : Packet
        {
            public override PacketType Type => PacketType.VehicleSync;
            public int ID { get; set; }

            public int OwnerID { get; set; }

            public VehicleDataFlags Flags { get; set; }

            public Vector3 Position { get; set; }

            public Quaternion Quaternion { get; set; }
            // public Vector3 Rotation { get; set; }

            public Vector3 Velocity { get; set; }

            public Vector3 RotationVelocity { get; set; }

            public float ThrottlePower { get; set; }
            public float BrakePower { get; set; }
            public float SteeringAngle { get; set; }
            public float DeluxoWingRatio { get; set; } = -1;

            #region FULL-SYNC
            public int ModelHash { get; set; }

            public float EngineHealth { get; set; }

            public byte[] Colors { get; set; }

            public Dictionary<int, int> Mods { get; set; }

            public VehicleDamageModel DamageModel { get; set; }

            public byte LandingGear { get; set; }
            public byte RoofState { get; set; }



            public VehicleLockStatus LockStatus { get; set; }

            public int Livery { get; set; } = -1;

            public byte RadioStation { get; set; } = 255;
            public string LicensePlate { get; set; }
            #endregion

            protected override void Serialize(NetOutgoingMessage m)
            {
                m.Write(ID);
                m.Write(OwnerID);
                m.Write((ushort)Flags);
                m.Write(Position);
                m.Write(Quaternion);
                m.Write(Velocity);
                m.Write(RotationVelocity);
                m.Write(ThrottlePower);
                m.Write(BrakePower);
                m.Write(SteeringAngle);

                if (Flags.HasVehFlag(VehicleDataFlags.IsDeluxoHovering))
                {
                    m.Write(DeluxoWingRatio);
                }

                if (Flags.HasVehFlag(VehicleDataFlags.IsFullSync))
                {
                    m.Write(ModelHash);
                    m.Write(EngineHealth);

                    if (Flags.HasVehFlag(VehicleDataFlags.IsAircraft))
                    {
                        m.Write(LandingGear);
                    }
                    if (Flags.HasVehFlag(VehicleDataFlags.HasRoof))
                    {
                        m.Write(RoofState);
                    }

                    // Defensive: Validate Colors array
                    if (Colors == null || Colors.Length < 2)
                        throw new InvalidOperationException("VehicleSync.Serialize: Colors array is null or too short.");
                    m.Write(Colors[0]);
                    m.Write(Colors[1]);

                    // Defensive: Limit mod count
                    const int MAX_MODS = 64; // Set to a reasonable value for your game
                    int modCount = Mods != null ? Math.Min(Mods.Count, MAX_MODS) : 0;
                    m.Write((short)modCount);

                    if (Mods != null)
                    {
                        int written = 0;
                        foreach (var mod in Mods)
                        {
                            if (written++ >= MAX_MODS) break;
                            m.Write(mod.Key);
                            m.Write(mod.Value);
                        }
                    }

                    if (!DamageModel.Equals(default(VehicleDamageModel)))
                    {
                        m.Write(true);
                        m.Write(DamageModel.BrokenDoors);
                        m.Write(DamageModel.OpenedDoors);
                        m.Write(DamageModel.BrokenWindows);
                        m.Write(DamageModel.BurstedTires);
                        m.Write(DamageModel.LeftHeadLightBroken);
                        m.Write(DamageModel.RightHeadLightBroken);
                    }
                    else
                    {
                        m.Write(false);
                    }

                    m.Write((byte)LockStatus);
                    m.Write(RadioStation);
                    m.Write(LicensePlate);
                    m.Write((byte)(Livery + 1));
                }
            }

            public override void Deserialize(NetIncomingMessage m)
            {
                ID = m.ReadInt32();
                OwnerID = m.ReadInt32();
                Flags = (VehicleDataFlags)m.ReadUInt16();
                Position = m.ReadVector3();
                Quaternion = m.ReadQuaternion();
                Velocity = m.ReadVector3();
                RotationVelocity = m.ReadVector3();
                ThrottlePower = m.ReadFloat();
                BrakePower = m.ReadFloat();
                SteeringAngle = m.ReadFloat();

                if (Flags.HasVehFlag(VehicleDataFlags.IsDeluxoHovering))
                {
                    DeluxoWingRatio = m.ReadFloat();
                }

                if (Flags.HasVehFlag(VehicleDataFlags.IsFullSync))
                {
                    ModelHash = m.ReadInt32();
                    EngineHealth = m.ReadFloat();

                    if (Flags.HasVehFlag(VehicleDataFlags.IsAircraft))
                    {
                        LandingGear = m.ReadByte();
                    }
                    if (Flags.HasVehFlag(VehicleDataFlags.HasRoof))
                    {
                        RoofState = m.ReadByte();
                    }

                    // Defensive: Validate Colors array
                    byte vehColor1 = m.ReadByte();
                    byte vehColor2 = m.ReadByte();
                    Colors = new byte[] { vehColor1, vehColor2 };

                    // Defensive: Limit mod count
                    Mods = new Dictionary<int, int>();
                    short vehModCount = m.ReadInt16();
                    const int MAX_MODS = 64;
                    if (vehModCount < 0 || vehModCount > MAX_MODS)
                        throw new InvalidDataException($"VehicleSync: Invalid mod count: {vehModCount}");

                    for (int i = 0; i < vehModCount; i++)
                    {
                        Mods.Add(m.ReadInt32(), m.ReadInt32());
                    }

                    if (m.ReadBoolean())
                    {
                        DamageModel = new VehicleDamageModel()
                        {
                            BrokenDoors = m.ReadByte(),
                            OpenedDoors = m.ReadByte(),
                            BrokenWindows = m.ReadByte(),
                            BurstedTires = m.ReadInt16(),
                            LeftHeadLightBroken = m.ReadByte(),
                            RightHeadLightBroken = m.ReadByte()
                        };
                    }

                    LockStatus = (VehicleLockStatus)m.ReadByte();
                    RadioStation = m.ReadByte();
                    LicensePlate = m.ReadString();
                    Livery = m.ReadByte() - 1;
                }
            }
        }
    }
}
