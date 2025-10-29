using Lidgren.Network;
using RageCoop.Core;
using System.Net;

namespace RageCoop.Server
{
    public partial class Server
    {
        private void HolePunch(Client host, Client client)
        {
            // Symmetric hole punch: both peers will actively connect.

            // Send to host (target = client)
            Send(new Packets.HolePunchInit
            {
                Connect = true,
                TargetID = client.Player.ID,
                TargetInternal = client.InternalEndPoint?.ToString(),
                TargetExternal = client.EndPoint?.ToString()
            }, host, ConnectionChannel.Default, NetDeliveryMethod.ReliableOrdered);

            // Send to client (target = host)
            Send(new Packets.HolePunchInit
            {
                Connect = true,
                TargetID = host.Player.ID,
                TargetInternal = host.InternalEndPoint?.ToString(),
                TargetExternal = host.EndPoint?.ToString()
            }, client, ConnectionChannel.Default, NetDeliveryMethod.ReliableOrdered);

        }
    }
}
