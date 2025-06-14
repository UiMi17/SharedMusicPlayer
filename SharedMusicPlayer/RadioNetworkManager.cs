using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Steamworks.Data;
using Steamworks;
using UnityEngine;

public class RadioNetworkManager : MonoBehaviour
{
    public CockpitRadio cockpitRadio;
    private RadioSenderSocket _senderSocket;
    private RadioReceiverClient _receiverClient;
    private ulong _targetSteamId;
    private bool _isSender;
    private bool _isReceiver;
    private const int RetryDelaySeconds = 2;
    private const int MaxRetryAttempts = 3;

    void Update()
    {
        SteamClient.RunCallbacks();

        if (_isSender && _senderSocket != null)
        {
            _senderSocket.Receive();
        }

        if (_isReceiver && _receiverClient != null)
        {
            _receiverClient.Receive();
        }
    }

    public void StopAll()
    {
        _isSender = false;
        _isReceiver = false;

        if (_senderSocket != null)
        {
            Debug.Log("[RadioNetworkManager]: Closing sender socket");
            _senderSocket.Close();
            _senderSocket = null;
        }

        if (_receiverClient != null)
        {
            Debug.Log("[RadioNetworkManager]: Closing receiver client");
            _receiverClient.Close();
            _receiverClient = null;
        }

        Debug.Log("[RadioNetworkManager]: Stopped all network activity");
    }

    private void OnDestroy()
    {
        _senderSocket?.Close();
        _receiverClient?.Close();
    }

    public async void StartSender(ulong targetSteamId)
    {
        if (_isSender)
        {
            Debug.LogWarning("[RadioNetworkManager]: Sender already running");
            return;
        }

        _targetSteamId = targetSteamId;
        _isSender = true;
        _isReceiver = false;

        int attempt = 0;
        while (attempt < MaxRetryAttempts)
        {
            try
            {
                if (!SteamClient.IsValid)
                {
                    Debug.LogError("[RadioNetworkManager]: Steam client not initialized!");
                    return;
                }

                Debug.Log("[RadioNetworkManager]: Attempting to create relay sender socket...");
                _senderSocket = SteamNetworkingSockets.CreateRelaySocket<RadioSenderSocket>(1338); 
                Debug.Log("[RadioNetworkManager]: Created relay sender socket");
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                Debug.LogError($"[RadioNetworkManager]: Failed to create relay socket (attempt {attempt}): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));
            }
        }

        if (_senderSocket == null)
        {
            Debug.LogError("[RadioNetworkManager]: Could not create sender socket after multiple attempts");
            _isSender = false;
            return;
        }

        Debug.Log($"[RadioNetworkManager]: Started sender for SteamID {_targetSteamId}");
    }

    public async void StartReceiver(ulong targetSteamId)
    {
        if (_isReceiver)
        {
            Debug.LogWarning("[RadioNetworkManager]: Receiver already running");
            return;
        }

        _targetSteamId = targetSteamId;
        _isReceiver = true;
        _isSender = false;

        if (!SteamClient.IsValid)
        {
            Debug.LogError("[RadioNetworkManager]: Steam client not initialized!");
            return;
        }

        _receiverClient = await TryCreateSocketWithRetries(
            () => SteamNetworkingSockets.ConnectRelay<RadioReceiverClient>(_targetSteamId, 1338),
            $"connect relay to sender {_targetSteamId}"
        );

        if (_receiverClient == null)
        {
            _isReceiver = false;
            return;
        }

        Debug.Log("[RadioNetworkManager]: Started receiver");
    }

    private async Task<T> TryCreateSocketWithRetries<T>(Func<T> createSocket, string operation) where T : class
    {
        int attempt = 0;
        while (attempt < MaxRetryAttempts)
        {
            try
            {
                Debug.Log($"[RadioNetworkManager]: Attempting to {operation} (attempt {attempt + 1})...");
                var socket = createSocket();
                return socket;
            }
            catch (Exception ex)
            {
                attempt++;
                Debug.LogError($"[RadioNetworkManager]: Failed to {operation} (attempt {attempt}): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));
            }
        }

        Debug.LogError($"[RadioNetworkManager]: Could not {operation} after multiple attempts");
        return null;
    }

    public void SendPlayCommand(int songIdx)
    {
        _senderSocket?.QueueCommand("PLAY", songIdx, _targetSteamId);
    }

    public void SendNextSongCommand()
    {
        _senderSocket?.QueueCommand("NEXT", 0, _targetSteamId);
    }

    public void SendPrevSongCommand()
    {
        _senderSocket?.QueueCommand("PREV", 0, _targetSteamId);
    }

    public void SendStopSongCommand()
    {
        _senderSocket?.QueueCommand("STOP", 0, _targetSteamId);
    }

    private class RadioSenderSocket : SocketManager
    {
        private struct CommandInfo
        {
            public ulong TargetSteamId;
            public string CommandType;
            public int SongIdx;
        }

        private readonly Queue<CommandInfo> _commandsToSend = new();
        private readonly Dictionary<ulong, Connection> _connections = new();

        public void QueueCommand(string commandType, int songIdx, ulong targetSteamId)
        {
            var commandInfo = new CommandInfo
            {
                TargetSteamId = targetSteamId,
                CommandType = commandType,
                SongIdx = songIdx
            };

            _commandsToSend.Enqueue(commandInfo);
            Debug.Log($"[RadioSenderSocket]: Queued {commandType} command for {targetSteamId}");
            TrySendNextCommand();
        }

        public override void OnConnectionChanged(Connection connection, ConnectionInfo info)
        {
            if (info.State == ConnectionState.Connected)
            {
                Debug.Log($"[RadioSenderSocket]: Connected to {info.Identity.SteamId}");
                _connections[info.Identity.SteamId] = connection;
                TrySendNextCommand();
            }
            else if (info.State == ConnectionState.ClosedByPeer || info.State == ConnectionState.ProblemDetectedLocally)
            {
                Debug.Log($"[RadioSenderSocket]: Disconnected from {info.Identity.SteamId}");
                _connections.Remove(info.Identity.SteamId);
            }

            base.OnConnectionChanged(connection, info);
        }

        private void TrySendNextCommand()
        {
            if (_commandsToSend.Count == 0)
            {
                Debug.Log("[RadioSenderSocket]: No commands to send");
                return;
            }

            var command = _commandsToSend.Dequeue();
            if (!_connections.TryGetValue(command.TargetSteamId, out var connection))
            {
                Debug.Log("[RadioSenderSocket]: Waiting for connection to send command...");
                _commandsToSend.Enqueue(command);
                return;
            }

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(command.CommandType);
            if (command.CommandType == "PLAY")
            {
                writer.Write(command.SongIdx);
            }
            writer.Flush();

            var result = connection.SendMessage(ms.ToArray(), SendType.Reliable);
            if (result == Result.OK)
            {
                Debug.Log($"[RadioSenderSocket]: Sent {command.CommandType} command to {command.TargetSteamId}");
            }
            else
            {
                Debug.LogError($"[RadioSenderSocket]: Failed to send {command.CommandType} command!");
            }
        }

        public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            Debug.Log("[RadioSenderSocket]: Received unexpected message, ignoring");
        }
    }

    private class RadioReceiverClient : ConnectionManager
    {
        private RadioNetworkManager _radioSyncNet;

        public override void OnConnected(ConnectionInfo info)
        {
            Debug.Log("[RadioReceiverClient]: Connected to host.");
            base.OnConnected(info);
        }

        public override void OnDisconnected(ConnectionInfo info)
        {
            Debug.Log("[RadioReceiverClient]: Disconnected from host.");
            base.OnDisconnected(info);
        }

        public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            byte[] managedData = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(data, managedData, 0, size);

            using var ms = new MemoryStream(managedData);
            using var reader = new BinaryReader(ms);

            string commandType = reader.ReadString();
            Debug.Log($"[RadioReceiverClient]: Received {commandType} command");

            switch (commandType)
            {
                case "PLAY":
                    int songIdx = reader.ReadInt32();
                    _radioSyncNet.cockpitRadio.songIdx = songIdx;
                    _radioSyncNet.cockpitRadio.PlayButton();
                    break;
                case "NEXT":
                    _radioSyncNet.cockpitRadio.NextSong();
                    break;
                case "PREV":
                    _radioSyncNet.cockpitRadio.PrevSong();
                    break;
                default:
                    Debug.LogWarning($"[RadioReceiverClient]: Unknown command type '{commandType}'");
                    break;
            }

            base.OnMessage(data, size, messageNum, recvTime, channel);
        }
    }
}