using Lidgren.Network;
using RageCoop.Core;
using RageCoop.Core.Scripting;
using RageCoop.Server.Scripting;
using System;

namespace RageCoop.Server
{
    public partial class Server
    {
        // Shared constant with the client resource (do not change unless you also change the client)
        private const int VoicePingEvent = unchecked((int)0x6A1B4F2D);

        private void Listen()
        {
            NetIncomingMessage msg = null;
            while (!_stopping)
            {
                try
                {
                    msg = MainNetServer.WaitMessage(200);
                    ProcessMessage(msg);
                }
                catch (Exception ex)
                {
                    Logger?.Error("Error processing message");
                    Logger?.Error(ex);
                    if (msg != null)
                    {
                        DisconnectAndLog(msg.SenderConnection, PacketType.Unknown, ex);
                    }
                }
            }
            Logger?.Info("Server is shutting down!");
            MainNetServer.Shutdown("Server is restarting.");
            BaseScript.OnStop();
            Resources.UnloadAll();
        }

        private void ProcessMessage(NetIncomingMessage message)
        {
            Client sender;
            if (message == null) { return; }
            switch (message.MessageType)
            {
                case NetIncomingMessageType.ConnectionApproval:
                    {
                        Logger?.Info($"New incoming connection from: [{message.SenderConnection.RemoteEndPoint}]");
                        if (message.ReadByte() != (byte)PacketType.Handshake)
                        {
                            Logger?.Info($"IP [{message.SenderConnection.RemoteEndPoint.Address}] was blocked, reason: Wrong packet!");
                            message.SenderConnection.Deny("Wrong packet!");
                        }
                        else
                        {
                            try
                            {
                                GetHandshake(message.SenderConnection, message.GetPacket<Packets.Handshake>());
                            }
                            catch (Exception e)
                            {
                                Logger?.Info($"IP [{message.SenderConnection.RemoteEndPoint.Address}] was blocked, reason: {e.Message}");
                                Logger?.Error(e);
                                message.SenderConnection.Deny(e.Message);
                            }
                        }
                        break;
                    }
                case NetIncomingMessageType.StatusChanged:
                    {
                        // Get sender client
                        if (!ClientsByNetHandle.TryGetValue(message.SenderConnection.RemoteUniqueIdentifier, out sender))
                        {
                            break;
                        }
                        NetConnectionStatus status = (NetConnectionStatus)message.ReadByte();

                        if (status == NetConnectionStatus.Disconnected)
                        {

                            PlayerDisconnected(sender);
                        }
                        else if (status == NetConnectionStatus.Connected)
                        {
                            PlayerConnected(sender);

                            try
                            {
                                sender.SendCustomEventQueued(CustomEvents.OnPlayerDied, "Resources are loading, ~o~please wait...");
                            }
                            catch (Exception ex)
                            {
                                Logger?.Error("Failed to send loading notification to client: " + ex.Message);
                            }
                            QueueJob(() => API.Events.InvokePlayerConnected(sender));

                            Resources.SendTo(sender);
                        }
                        break;
                    }
                case NetIncomingMessageType.Data:
                    {
                        if (ClientsByNetHandle.TryGetValue(message.SenderConnection.RemoteUniqueIdentifier, out sender))
                        {
                            var type = (PacketType)message.ReadByte();
                            switch (type)
                            {
                                case PacketType.Response:
                                {
                                    int id = message.ReadInt32();
                                    if (PendingResponses.TryGetValue(id, out var callback))
                                    {
                                        callback((PacketType)message.ReadByte(), message);
                                        PendingResponses.TryRemove(id, out _);
                                    }
                                    break;
                                }
                                case PacketType.Request:
                                {
                                    int id = message.ReadInt32();
                                    var reqType = (PacketType)message.ReadByte();
                                    if (RequestHandlers.TryGetValue(reqType, out var handler))
                                    {
                                        var response = MainNetServer.CreateMessage();
                                        response.Write((byte)PacketType.Response);
                                        response.Write(id);
                                        handler(message, sender).Pack(response);
                                        MainNetServer.SendMessage(response, message.SenderConnection, NetDeliveryMethod.ReliableOrdered);
                                    }
                                    else
                                    {
                                        Logger.Warning("Did not find a request handler of type: " + reqType);
                                    }
                                    break;
                                }
                                default:
                                {
                                    if (type.IsSyncEvent())
                                    {
                                        // Always relay sync events (even with P2P)
                                        try
                                        {
                                            var toSend = MainNetServer.Connections.Exclude(message.SenderConnection);
                                            if (toSend.Count != 0)
                                            {
                                                var outgoingMessage = MainNetServer.CreateMessage();
                                                outgoingMessage.Write((byte)type);
                                                outgoingMessage.Write(message.ReadBytes(message.LengthBytes - 1));
                                                MainNetServer.SendMessage(outgoingMessage, toSend, NetDeliveryMethod.UnreliableSequenced, (byte)ConnectionChannel.SyncEvents);
                                            }
                                        }
                                        catch (Exception e)
                                        {
                                            DisconnectAndLog(message.SenderConnection, type, e);
                                        }
                                    }
                                    else
                                    {
                                        HandlePacket(type, message, sender);
                                    }
                                    break;
                                }
                            }

                        }
                        break;
                    }
                case NetIncomingMessageType.ErrorMessage:
                case NetIncomingMessageType.WarningMessage:
                case NetIncomingMessageType.DebugMessage:
                case NetIncomingMessageType.VerboseDebugMessage:
                    // drop to save resources
                    break;
                case NetIncomingMessageType.UnconnectedData:
                    {
                        if (message.ReadByte() == (byte)PacketType.PublicKeyRequest)
                        {
                            var msg = MainNetServer.CreateMessage();
                            var p = new Packets.PublicKeyResponse();
                            Security.GetPublicKey(out p.Modulus, out p.Exponent);
                            p.Pack(msg);
                            MainNetServer.SendUnconnectedMessage(msg, message.SenderEndPoint);
                        }
                    }
                    break;
                default:
                    Logger?.Error(string.Format("Unhandled type: {0} {1} bytes {2} | {3}", message.MessageType, message.LengthBytes, message.DeliveryMethod, message.SequenceChannel));
                    break;
            }

            MainNetServer.Recycle(message);
        }

        private void HandlePacket(PacketType type, NetIncomingMessage msg, Client sender)
        {
            try
            {
                switch (type)
                {
                    case PacketType.PedSync:
                        PedSync(msg.GetPacket<Packets.PedSync>(), sender);
                        break;

                    case PacketType.VehicleSync:
                        VehicleSync(msg.GetPacket<Packets.VehicleSync>(), sender);
                        break;

                    case PacketType.ProjectileSync:
                        ProjectileSync(msg.GetPacket<Packets.ProjectileSync>(), sender);
                        break;

                    case PacketType.ChatMessage:
                    {
                        Packets.ChatMessage packet = new((b) =>
                        {
                            return Security.Decrypt(b, sender.EndPoint);
                        });
                        packet.Deserialize(msg);
                        ChatMessageReceived(packet.Username, packet.Message, sender);
                        break;
                    }

                    case PacketType.Voice:
                    {
                        if (Settings.UseVoice)
                        {
                            try
                            {
                                var toSend = MainNetServer.Connections.Exclude(msg.SenderConnection);

                                if (msg.LengthBytes - msg.PositionInBytes < sizeof(int))
                                {
                                    break;
                                }

                                int wireId = msg.ReadInt32();
                                int remaining = msg.LengthBytes - msg.PositionInBytes;
                                if (remaining < sizeof(int))
                                {
                                    break;
                                }

                                byte[] tail = msg.ReadBytes(remaining);

                                int recorded;
                                int audioOffset;
                                int audioLen;

                                if (remaining >= 8)
                                {
                                    // Try new layout first
                                    int possibleLen = BitConverter.ToInt32(tail, 0);
                                    int trailingRecorded = BitConverter.ToInt32(tail, remaining - 4);

                                    if (possibleLen >= 0 && possibleLen <= remaining - 8)
                                    {
                                        // Future: [len][PCM][Recorded]
                                        audioOffset = 4;
                                        audioLen = possibleLen;
                                        recorded = Math.Min(trailingRecorded, audioLen);
                                    }
                                    else
                                    {
                                        // Legacy: [PCM][Recorded]
                                        audioOffset = 0;
                                        audioLen = remaining - 4;
                                        recorded = Math.Min(trailingRecorded, audioLen);
                                    }
                                }
                                else
                                {
                                    break;
                                }

                                if (audioLen <= 0 || recorded <= 0)
                                {
                                    break;
                                }

                                // Resolve forward ID (fallback to sender's ped)
                                int forwardId = wireId;
                                if (!ClientsByID.ContainsKey(forwardId) && sender?.Player != null)
                                {
                                    forwardId = sender.Player.ID;
                                }

                                // Forward voice to other clients (if any)
                                if (toSend.Count != 0)
                                {
                                    var outMsg = MainNetServer.CreateMessage();
                                    outMsg.Write((byte)PacketType.Voice);
                                    outMsg.Write(forwardId);
                                    outMsg.Write(recorded);
                                    outMsg.Write(tail, audioOffset, recorded);
                                    outMsg.Write(recorded);
                                    MainNetServer.SendMessage(outMsg, toSend, NetDeliveryMethod.ReliableOrdered, (byte)ConnectionChannel.Voice);
                                }

                                // Voice activity detection (16-bit PCM LE)
                                bool hasVoice = HasVoice(tail, audioOffset, recorded, 600, 8);
                                if (hasVoice)
                                {
                                    // Broadcast a small “voice ping” custom event to all clients (including the talker)
                                    // Args: int id, string username, int recorded
                                    if (ClientsByID.TryGetValue(forwardId, out var talker))
                                    {
                                        API.SendCustomEventQueued(null, VoicePingEvent, forwardId, talker.Username, recorded);
                                    }
                                    else
                                    {
                                        API.SendCustomEventQueued(null, VoicePingEvent, forwardId, "", recorded);
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Logger?.Error("Voice: relay failed", e);
                                DisconnectAndLog(msg.SenderConnection, type, e);
                            }
                        }
                        break;
                    }

                    case PacketType.CustomEvent:
                    {
                        Packets.CustomEvent packet = new Packets.CustomEvent();
                        packet.Deserialize(msg);
                        QueueJob(() => API.Events.InvokeCustomEventReceived(packet, sender));
                        break;
                    }

                    default:
                        Logger?.Error("Unhandled Data / Packet type");
                        break;
                }
            }
            catch (Exception e)
            {
                DisconnectAndLog(sender.Connection, type, e);
            }
        }

        // Simple VAD: return true if enough samples exceed amplitude threshold
        private static bool HasVoice(byte[] data, int offset, int count, short ampThreshold = 600, int minHits = 8)
        {
            if (data == null || count < 4 || offset < 0 || offset + count > data.Length) return false;
            int end = offset + count - 1;
            int hits = 0;

            // assume 16-bit PCM, little-endian
            for (int i = offset; i < end; i += 2)
            {
                short sample = (short)(data[i] | (data[i + 1] << 8));
                if (sample > ampThreshold || sample < -ampThreshold)
                {
                    if (++hits >= minHits) return true;
                }
            }
            return false;
        }
    }
}
