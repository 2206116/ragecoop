using Lidgren.Network;
using RageCoop.Core;
using RageCoop.Server.Scripting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;

using Timer = System.Timers.Timer;

namespace RageCoop.Server
{

    /// <summary>
    /// The instantiable RageCoop server class
    /// </summary>
    public partial class Server
    {
        /// <summary>
        /// The API for controlling server and hooking events.
        /// </summary>
        public API API { get; private set; }
        internal readonly BaseScript BaseScript;
        internal readonly Settings Settings;
        internal NetServer MainNetServer;
        internal ServerEntities Entities;

        internal readonly Dictionary<Command, Action<CommandContext>> Commands = new();
        internal readonly Dictionary<long, Client> ClientsByNetHandle = new();
        internal readonly Dictionary<string, Client> ClientsByName = new();
        internal readonly Dictionary<int, Client> ClientsByID = new();
        internal Client _hostClient;

        private readonly ConcurrentDictionary<int, FileTransfer> InProgressFileTransfers = new ConcurrentDictionary<int, FileTransfer>();
        internal Resources Resources;
        internal Logger Logger;
        internal Security Security;
        private bool _stopping = false;
        private readonly Thread _listenerThread;
        private readonly Timer _announceTimer = new();
        private readonly Timer _playerUpdateTimer = new();
        private readonly Timer _antiAssholesTimer = new();
        private readonly Timer _updateTimer = new();
        private readonly Worker _worker;
        private readonly HashSet<char> _allowedCharacterSet;
        private readonly ConcurrentDictionary<int, Action<PacketType, NetIncomingMessage>> PendingResponses = new();
        internal Dictionary<PacketType, Func<NetIncomingMessage, Client, Packet>> RequestHandlers = new();

        // New: per-client transfer serialization to avoid multiple concurrent FileTransferRequests
        // which can cause clients to create zero-length placeholders if their responses are lost.
        private readonly ConcurrentDictionary<string, object> _fileTransferLocks = new ConcurrentDictionary<string, object>();

        /// <summary>
        /// Get the current server version
        /// </summary>
        public static readonly Version Version = typeof(Server).Assembly.GetName().Version;
        /// <summary>
        /// Instantiate a server.
        /// </summary>
        /// <param name="settings"></param>
        /// <param name="logger"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public Server(Settings settings, Logger logger = null)
        {
            Settings = settings;
            if (settings == null) { throw new ArgumentNullException("Server settings cannot be null!"); }
            Logger = logger;
            if (Logger != null) { Logger.LogLevel = Settings.LogLevel; }
            API = new API(this);
            Resources = new Resources(this);
            Security = new Security(Logger);
            Entities = new ServerEntities(this);
            BaseScript = new BaseScript(this);
            _allowedCharacterSet = new HashSet<char>(Settings.AllowedUsernameChars.ToCharArray());


            _worker = new Worker("ServerWorker", Logger);

            _listenerThread = new Thread(() => Listen());

            _announceTimer.Interval = 1;
            _announceTimer.Elapsed += (s, e) =>
            {
                _announceTimer.Interval = 10000;
                _announceTimer.Stop();
                Announce();
                _announceTimer.Start();
            };

            _playerUpdateTimer.Interval = 1000;
            _playerUpdateTimer.Elapsed += (s, e) => SendPlayerUpdate();


            _antiAssholesTimer.Interval = 5000;
            _antiAssholesTimer.Elapsed += (s, e) => KickAssholes();


            _updateTimer.Interval = 1;
            _updateTimer.Elapsed += (s, e) =>
            {
                _updateTimer.Interval = 1000 * 60 * 10; // 10 minutes
                _updateTimer.Stop();
                CheckUpdate();
                _updateTimer.Start();
            };
        }


        /// <summary>
        /// Spawn threads and start the server
        /// </summary>
        public void Start()
        {
            Logger?.Info("================");
            Logger?.Info($"Listening port: {Settings.Port}");
            Logger?.Info($"Server version: {Version}");
            Logger?.Info($"Compatible client version: {Version.ToString(3)}");
            Logger?.Info($"Runtime: {CoreUtils.GetInvariantRID()} => {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
            Logger?.Info("================");
            Logger?.Info($"Listening addresses:");
            foreach (NetworkInterface netInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                Logger?.Info($"[{netInterface.Description}]:");
                IPInterfaceProperties ipProps = netInterface.GetIPProperties();
                foreach (UnicastIPAddressInformation addr in ipProps.UnicastAddresses)
                {
                    Logger.Info(string.Join(", ", addr.Address));
                }
                Logger.Info("");
            }
            if (Settings.UseZeroTier)
            {
                Logger?.Info($"Joining ZeroTier network: " + Settings.ZeroTierNetworkID);
                if (ZeroTierHelper.Join(Settings.ZeroTierNetworkID) == null)
                {
                    throw new Exception("Failed to obtain ZeroTier network IP");
                }
            }
            else if (Settings.UseP2P)
            {
                Logger?.Warning("ZeroTier is not enabled, P2P connection may not work as expected.");
            }

            // 623c92c287cc392406e7aaaac1c0f3b0 = RAGECOOP
            NetPeerConfiguration config = new("623c92c287cc392406e7aaaac1c0f3b0")
            {
                Port = Settings.Port,
                MaximumConnections = Settings.MaxPlayers,
                EnableUPnP = false,
                AutoFlushSendQueue = true,
                PingInterval = 5
            };

            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
            config.EnableMessageType(NetIncomingMessageType.UnconnectedData);

            MainNetServer = new NetServer(config);
            MainNetServer.Start();
            BaseScript.API = API;
            BaseScript.OnStart();
            Resources.LoadAll();
            _listenerThread.Start();
            Logger?.Info("Listening for clients");

            _playerUpdateTimer.Enabled = true;
            if (Settings.AnnounceSelf)
            {
                _announceTimer.Enabled = true;
            }
            if (Settings.AutoUpdate)
            {
                _updateTimer.Enabled = true;
            }
            _antiAssholesTimer.Enabled = true;


        }
        /// <summary>
        /// Terminate threads and stop the server
        /// </summary>
        public void Stop()
        {
            Logger?.Flush();
            Logger?.Dispose();
            _stopping = true;
            _listenerThread.Join();
            _playerUpdateTimer.Enabled = false;
            _announceTimer.Enabled = false;
            _worker.Dispose();
        }
        internal void QueueJob(Action job)
        {
            _worker.QueueJob(job);
        }

        // Send a message to targets or all players
        internal void ChatMessageReceived(string name, string message, Client sender = null)
        {
            if (message.StartsWith('/'))
            {
                string[] cmdArgs = message.Split(" ");
                string cmdName = cmdArgs[0].Remove(0, 1);
                QueueJob(() => API.Events.InvokeOnCommandReceived(cmdName, cmdArgs, sender));
                return;
            }
            message = message.Replace("~", "");

            QueueJob(() => API.Events.InvokeOnChatMessage(message, sender));

            foreach (var c in ClientsByNetHandle.Values)
            {
                var msg = MainNetServer.CreateMessage();
                var crypt = new Func<string, byte[]>((s) =>
                {
                    return Security.Encrypt(s.GetBytes(), c.EndPoint);
                });
                new Packets.ChatMessage(crypt)
                {
                    Username = name,
                    Message = message
                }.Pack(msg);
                MainNetServer.SendMessage(msg, c.Connection, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.Chat);
            }
        }
        internal void SendChatMessage(string name, string message, Client target)
        {
            if (target == null) { return; }
            var msg = MainNetServer.CreateMessage();
            new Packets.ChatMessage(new Func<string, byte[]>((s) =>
            {
                return Security.Encrypt(s.GetBytes(), target.EndPoint);
            }))
            {
                Username = name,
                Message = message,
            }.Pack(msg);
            MainNetServer.SendMessage(msg, target.Connection, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.Chat);
        }

        internal void RegisterCommand(string name, string usage, short argsLength, Action<CommandContext> callback)
        {
            Command command = new(name) { Usage = usage, ArgsLength = argsLength };

            if (Commands.ContainsKey(command))
            {
                throw new Exception("Command \"" + command.Name + "\" was already been registered!");
            }

            Commands.Add(command, callback);
        }
        internal void RegisterCommand(string name, Action<CommandContext> callback)
        {
            Command command = new(name);

            if (Commands.ContainsKey(command))
            {
                throw new Exception("Command \"" + command.Name + "\" was already been registered!");
            }

            Commands.Add(command, callback);
        }

        internal void RegisterCommands<T>()
        {
            IEnumerable<MethodInfo> commands = typeof(T).GetMethods().Where(method => method.GetCustomAttributes(typeof(Command), false).Any());

            foreach (MethodInfo method in commands)
            {
                Command attribute = method.GetCustomAttribute<Command>(true);

                RegisterCommand(attribute.Name, attribute.Usage, attribute.ArgsLength, (Action<CommandContext>)Delegate.CreateDelegate(typeof(Action<CommandContext>), method));
            }
        }
        internal T GetResponse<T>(Client client, Packet request, ConnectionChannel channel = ConnectionChannel.RequestResponse, int timeout = 5000) where T : Packet, new()
        {
            if (Thread.CurrentThread == _listenerThread)
            {
                throw new InvalidOperationException("Cannot wait for response from the listener thread!");
            }

            var received = new AutoResetEvent(false);
            T response = new T();
            var id = NewRequestID();
            PendingResponses[id] = (type, m) =>
            {
                response.Deserialize(m);
                received.Set();
            };

            var msg = MainNetServer.CreateMessage();
            msg.Write((byte)PacketType.Request);
            msg.Write(id);
            request.Pack(msg);
            MainNetServer.SendMessage(msg, client.Connection, NetDeliveryMethod.ReliableOrdered, (int)channel);
            if (received.WaitOne(timeout))
            {
                return response;
            }

            return null;
        }
        internal void SendFile(string path, string name, Client client, Action<float> updateCallback = null)
        {
            var fs = File.OpenRead(path);
            SendFile(fs, name, client, NewFileID(), updateCallback);
            fs.Close();
            fs.Dispose();
        }
// Replace the existing SendFile(Stream...) method with this updated implementation.

        internal void SendFile(Stream stream, string name, Client client, int id = default, Action<float> updateCallback = null, bool disposeInputStream = false)
        {
            const int chunkSize = 16 * 1024; // 16 KB chunks (tuneable)
            const int interChunkDelayMs = 2; // small delay between chunks to avoid saturating client
            const int maxAttempts = 3; // number of attempts for whole-file retry
            const int baseTimeoutMs = 5000; // base wait for completion

            MemoryStream sendStream = null;
            try
            {
                // Ensure we try to rewind the input stream before copying it.
                lock (stream)
                {
                    try
                    {
                        if (stream.CanSeek) stream.Seek(0, SeekOrigin.Begin);
                    }
                    catch { /* ignore if stream can't be rewound */ }

                    sendStream = new MemoryStream();
                    try
                    {
                        stream.CopyTo(sendStream);
                    }
                    catch (Exception ex)
                    {
                        Logger?.Error($"Failed copying input stream for file \"{name}\" to memory", ex);
                        // cleanup early
                        try { if (disposeInputStream) { stream.Close(); stream.Dispose(); } } catch { }
                        return;
                    }
                    sendStream.Seek(0, SeekOrigin.Begin);
                }

                // If we copied zero bytes, don't attempt to send empty file.
                if (sendStream.Length == 0)
                {
                    Logger?.Error($"Input stream for file \"{name}\" produced 0 bytes; aborting transfer to {client?.Username}");
                    try { if (disposeInputStream) { stream.Close(); stream.Dispose(); } } catch { }
                    try { sendStream.Close(); sendStream.Dispose(); } catch { }
                    return;
                }

                id = id == default ? NewFileID() : id;
                var total = sendStream.Length;
                Logger?.Debug($"Requesting file transfer:{name}, {total} bytes (fileID={id})");

                // Per-client serialization: ensure only one outstanding file request/transfer per client to avoid the client
                // creating many zero-length placeholders when responses are lost or delayed.
                string lockKey = client?.Username ?? client?.GetHashCode().ToString() ?? "unknown_client";
                object transferLock = _fileTransferLocks.GetOrAdd(lockKey, _ => new object());

                lock (transferLock)
                {
                    // Ask client whether it wants the file
                    var fileReq = new Packets.FileTransferRequest()
                    {
                        FileLength = total,
                        Name = name,
                        ID = id,
                    };

                    // dynamic request timeout based on file size (cap at 2 minutes)
                    int requestTimeoutMs = Math.Min(120_000, 2000 + (int)Math.Ceiling(total / (double)chunkSize) * 100);
                    Logger?.Trace($"Requesting file transfer:{name}, {total} bytes (fileID={id}) - waiting up to {requestTimeoutMs}ms for client response");

                    var fileResp = GetResponse<Packets.FileTransferResponse>(client, fileReq, ConnectionChannel.File, requestTimeoutMs);

                    if (fileResp == null)
                    {
                        Logger?.Trace($"No FileTransferResponse from {client.Username} for \"{name}\" within {requestTimeoutMs}ms - skipping");
                        try { if (disposeInputStream) { stream.Close(); stream.Dispose(); } } catch { }
                        try { sendStream.Close(); sendStream.Dispose(); } catch { }
                        return;
                    }

                    Logger?.Trace($"FileTransferResponse from {client.Username}: ID={fileResp.ID}, Response={fileResp.Response}");
                    if (fileResp.Response != FileResponse.NeedToDownload)
                    {
                        Logger?.Info($"Skipping file transfer \"{name}\" to {client.Username} (client response: {fileResp.Response})");
                        try { if (disposeInputStream) { stream.Close(); stream.Dispose(); } } catch { }
                        try { sendStream.Close(); sendStream.Dispose(); } catch { }
                        return;
                    }

                    int attempt = 0;
                    bool succeeded = false;
                    int chunksCount = (int)Math.Ceiling(total / (double)chunkSize);
                    // dynamic timeout: base + ~100ms per chunk, capped at 2 minutes
                    int completionTimeoutMs = Math.Min(120_000, baseTimeoutMs + Math.Max(0, chunksCount) * 100);

                    while (attempt < maxAttempts && !succeeded)
                    {
                        attempt++;
                        Logger?.Trace($"Sending file \"{name}\" to {client.Username} (attempt {attempt}/{maxAttempts}) - {total} bytes in {chunksCount} chunks");

                        var transfer = new FileTransfer() { ID = id, Name = name };
                        InProgressFileTransfers.TryAdd(id, transfer);

                        int readTotal = 0;
                        try
                        {
                            // rewind for each attempt
                            try { sendStream.Seek(0, SeekOrigin.Begin); } catch { /* ignore */ }

                            int thisRead;
                            do
                            {
                                byte[] chunk = new byte[chunkSize];
                                thisRead = sendStream.Read(chunk, 0, chunk.Length);
                                if (thisRead <= 0) break;

                                readTotal += thisRead;
                                if (thisRead != chunk.Length)
                                {
                                    Logger?.Trace($"Purged chunk to {thisRead} bytes");
                                    Array.Resize(ref chunk, thisRead);
                                }

                                // 1) Raw chunk (original behavior) - keep for clients handling top-level FileTransferChunk
                                try
                                {
                                    Send(
                                        new Packets.FileTransferChunk() { ID = id, FileChunk = chunk },
                                        client, ConnectionChannel.File, NetDeliveryMethod.ReliableOrdered);
                                }
                                catch (Exception ex)
                                {
                                    Logger?.Error($"Failed sending raw file chunk to {client?.Username} for file {name}", ex);
                                }

                                // 2) Wrapped-in-Request for clients that dispatch inner-request packets
                                try
                                {
                                    var reqMsg = MainNetServer.CreateMessage();
                                    reqMsg.Write((byte)PacketType.Request);
                                    reqMsg.Write(NewRequestID()); // transient request id
                                    var chunkPacket = new Packets.FileTransferChunk() { ID = id, FileChunk = chunk };
                                    chunkPacket.Pack(reqMsg);
                                    MainNetServer.SendMessage(reqMsg, client.Connection, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.File);
                                }
                                catch (Exception ex)
                                {
                                    Logger?.Error($"Failed sending wrapped file chunk to {client?.Username} for file {name}", ex);
                                }

                                transfer.Progress = total == 0 ? 1f : (float)readTotal / (float)total;
                                updateCallback?.Invoke(transfer.Progress);

                                if (interChunkDelayMs > 0) Thread.Sleep(interChunkDelayMs);
                            } while (thisRead > 0);

                            // Small pause to let client process final chunks and respond
                            Thread.Sleep(200 + Math.Min(2000, chunksCount / 4));

                            // Ask client to finalize and report completion — give a dynamic timeout based on file size
                            var completeResp = GetResponse<Packets.FileTransferResponse>(client, new Packets.FileTransferComplete() { ID = id }, ConnectionChannel.File, completionTimeoutMs);
                            if (completeResp == null || completeResp.Response != FileResponse.Completed)
                            {
                                Logger?.Trace($"File transfer to {client.Username} did not report complete on attempt {attempt}: {name}");
                                if (attempt < maxAttempts)
                                {
                                    Logger?.Trace($"Retrying file transfer \"{name}\" to {client.Username} after short pause...");
                                    Thread.Sleep(500);
                                }
                                else
                                {
                                    Logger?.Error($"File transfer to {client.Username} failed after {maxAttempts} attempts: {name}");
                                }
                            }
                            else
                            {
                                Logger?.Debug($"All file chunks sent and client reported completion: {name}");
                                succeeded = true;
                            }
                        }
                        finally
                        {
                            InProgressFileTransfers.TryRemove(id, out _);
                        }
                    } // attempts

                    if (!succeeded)
                    {
                        Logger?.Warning($"Giving up sending \"{name}\" to {client.Username} after {maxAttempts} attempts.");
                    }
                } // lock(transferLock)
            }
            catch (Exception ex)
            {
                Logger?.Error($"Error while sending file \"{name}\" to {client?.Username}", ex);
            }
            finally
            {
                // Always dispose the in-memory copy
                if (sendStream != null)
                {
                    try { sendStream.Close(); sendStream.Dispose(); } catch { }
                }

                // Optionally dispose the input stream if the caller requested it (API.RequestSharedFile passes true).
                if (disposeInputStream)
                {
                    try { stream.Close(); stream.Dispose(); } catch { }
                }
            }
        }
        internal int NewFileID()
        {
            int ID = 0;
            while ((ID == 0)
                || InProgressFileTransfers.ContainsKey(ID))
            {
                byte[] rngBytes = new byte[4];

                RandomNumberGenerator.Create().GetBytes(rngBytes);

                // Convert the bytes into an integer
                ID = BitConverter.ToInt32(rngBytes, 0);
            }
            return ID;
        }
        private int NewRequestID()
        {
            int ID = 0;
            while ((ID == 0)
                || PendingResponses.ContainsKey(ID))
            {
                byte[] rngBytes = new byte[4];

                RandomNumberGenerator.Create().GetBytes(rngBytes);

                // Convert the bytes into an integer
                ID = BitConverter.ToInt32(rngBytes, 0);
            }
            return ID;
        }
        internal void Send(Packet p, Client client, ConnectionChannel channel = ConnectionChannel.Default, NetDeliveryMethod method = NetDeliveryMethod.UnreliableSequenced)
        {
            NetOutgoingMessage outgoingMessage = MainNetServer.CreateMessage();
            p.Pack(outgoingMessage);
            MainNetServer.SendMessage(outgoingMessage, client.Connection, method, (int)channel);
        }
        internal void Forward(Packet p, Client except, ConnectionChannel channel = ConnectionChannel.Default, NetDeliveryMethod method = NetDeliveryMethod.UnreliableSequenced)
        {
            NetOutgoingMessage outgoingMessage = MainNetServer.CreateMessage();
            p.Pack(outgoingMessage);
            MainNetServer.SendToAll(outgoingMessage, except.Connection, method, (int)channel);
        }
        internal void SendToAll(Packet p, ConnectionChannel channel = ConnectionChannel.Default, NetDeliveryMethod method = NetDeliveryMethod.UnreliableSequenced)
        {
            NetOutgoingMessage outgoingMessage = MainNetServer.CreateMessage();
            p.Pack(outgoingMessage);
            MainNetServer.SendToAll(outgoingMessage, method, (int)channel);
        }
    }
}
