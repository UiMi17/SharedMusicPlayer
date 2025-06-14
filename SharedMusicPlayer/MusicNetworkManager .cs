using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using Steamworks.Data;
using System.Threading.Tasks;
using System.Collections;
using VTOLVR.Multiplayer;

namespace SharedMusicPlayer
{
    public class MusicNetworkManager : MonoBehaviour
    {
        private const int ChunkSize = 64 * 1024; // 64KB
        private const int MaxRetryAttempts = 5;
        private const float RetryDelaySeconds = 2f;

        private MusicSenderSocket _senderSocket;
        private MusicReceiverClient _receiverClient;

        private ulong _targetSteamId;
        private string[] _filesToSend;

        private bool _isSender = false;
        private bool _isReceiver = false;

        private VTOLMPBriefingRoomUI briefingUI;

        void Update()
        {
            SteamClient.RunCallbacks();

            if (_isSender && _senderSocket != null)
            {
                _senderSocket.Receive();
                _senderSocket.Update();
            }

            if (_isReceiver && _receiverClient != null)
            {
                _receiverClient.Receive();
            }
        }

        public async void StartSender(ulong targetSteamId, string[] files)
        {
            if (_isSender)
            {
                Debug.LogWarning("[MusicNetworkManager]: Sender already running");
                return;
            }

            _targetSteamId = targetSteamId;
            _filesToSend = files;
            _isSender = true;
            _isReceiver = false;

            int attempt = 0;
            while (attempt < MaxRetryAttempts)
            {
                try
                {
                    if (!SteamClient.IsValid)
                    {
                        Debug.LogError("[MusicNetworkManager]: Steam client not initialized!");
                        return;
                    }

                    Debug.Log("[MusicNetworkManager]: Attempting to create relay sender socket...");
                    _senderSocket = SteamNetworkingSockets.CreateRelaySocket<MusicSenderSocket>(1337);
                    Debug.Log("[MusicNetworkManager]: Created relay sender socket");
                    break;
                }
                catch (Exception ex)
                {
                    attempt++;
                    Debug.LogError($"[MusicNetworkManager]: Failed to create relay socket (attempt {attempt}): {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));
                }
            }

            if (_senderSocket == null)
            {
                Debug.LogError("[MusicNetworkManager]: Could not create sender socket after multiple attempts");
                _isSender = false;
                return;
            }

            Debug.Log($"[MusicNetworkManager]: Queuing {_filesToSend.Length} file(s) for sending to SteamID {_targetSteamId}");
            foreach (var file in _filesToSend)
            {
                _senderSocket.QueueFileForSending(file, _targetSteamId);
            }

            Debug.Log("[MusicNetworkManager]: Started sender");
        }

        public async void StartReceiver(ulong targetSteamId)
        {
            if (_isReceiver)
            {
                Debug.LogWarning("[MusicNetworkManager]: Receiver already running");
                return;
            }

            _targetSteamId = targetSteamId;
            _isReceiver = true;
            _isSender = false;

            if (!SteamClient.IsValid)
            {
                Debug.LogError("[MusicNetworkManager]: Steam client not initialized!");
                return;
            }

            _receiverClient = await TryCreateSocketWithRetries(
                () => SteamNetworkingSockets.ConnectRelay<MusicReceiverClient>(_targetSteamId, 1337),
                $"connect relay to sender {_targetSteamId}"
            );

            if (_receiverClient == null)
            {
                _isReceiver = false;
                return;
            }

            Debug.Log("[MusicNetworkManager]: Started receiver");
        }

        private async Task<T> TryCreateSocketWithRetries<T>(Func<T> createFunc, string description) where T : class
        {
            int attempt = 0;
            while (attempt < MaxRetryAttempts)
            {
                try
                {
                    Debug.Log($"[MusicNetworkManager]: Attempting to {description} (attempt {attempt + 1})...");
                    var socket = createFunc();
                    Debug.Log($"[MusicNetworkManager]: Successfully {description.ToLower()}");
                    return socket;
                }
                catch (Exception ex)
                {
                    attempt++;
                    Debug.LogError($"[MusicNetworkManager]: Failed to {description.ToLower()} (attempt {attempt}): {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));
                }
            }

            Debug.LogError($"[MusicNetworkManager]: Could not {description.ToLower()} after {MaxRetryAttempts} attempts");
            return null;
        }

        public void StopAll()
        {
            _isSender = false;
            _isReceiver = false;

            if (_senderSocket != null)
            {
                Debug.Log("[MusicNetworkManager]: Closing sender socket");
                _senderSocket.Close();
                _senderSocket = null;
            }

            if (_receiverClient != null)
            {
                Debug.Log("[MusicNetworkManager]: Closing receiver client");
                _receiverClient.Close();
                _receiverClient = null;
            }

            Debug.Log("[MusicNetworkManager]: Stopped all network activity");
        }

        public IEnumerator BeginReceiveAndWaitCoroutine(ulong senderSteamId)
        {
            StartReceiver(senderSteamId);

            briefingUI = FindObjectOfType<VTOLMPBriefingRoomUI>();
            if (briefingUI != null)
            {
                var cg = briefingUI.updatingContentObj.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    cg.interactable = true;
                    cg.blocksRaycasts = true;
                    cg.alpha = 1f;
                }
                SMPUIUtils.SetUpdatingText(briefingUI, "Loading Music Files (0%)");
                briefingUI.updatingContentObj.SetActive(true);
            }

            // Waiting for client with timeout of 10 seconds
            float timeout = 10f;
            float elapsed = 0f;
            while (_receiverClient == null && elapsed < timeout)
            {
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }

            if (_receiverClient == null)
            {
                Debug.LogError("[MusicNetworkManager]: Receiver client not initialized, aborting wait.");
                yield break;
            }

            Debug.Log("[MusicNetworkManager]: BeginReceiveAndWait started — waiting for all files...");

            // Waiting for all files
            while (!_receiverClient.AreAllFilesReceived())
            {
                if (_receiverClient.TotalExpectedChunks > 0)
                {
                    float progress = Mathf.Clamp01((float)_receiverClient.ReceivedChunks / _receiverClient.TotalExpectedChunks);
                    if (briefingUI != null && briefingUI.updatingContentProgress != null)
                    {
                        briefingUI.updatingContentProgress.localScale = new Vector3(progress, 1f, 1f);
                    }

                        int percent = Mathf.RoundToInt(progress * 100f);
                        SMPUIUtils.SetUpdatingText(briefingUI, $"Loading Music Files {_receiverClient.CompletedFileCount + 1}/{_receiverClient.ExpectedFileCount} ({percent}%)");
                }

                yield return new WaitForSeconds(0.05f);
            }

            if (briefingUI != null)
            {
                var cg = briefingUI.updatingContentObj.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    cg.interactable = false;
                    cg.blocksRaycasts = false;
                    cg.alpha = 1f;
                }

                briefingUI.updatingContentObj.SetActive(false);
            }

            Debug.Log($"[MusicNetworkManager]: All files received successfully.");

            StopAll();
        }

        // === MusicSenderSocket ===
        private class MusicSenderSocket : SocketManager
        {
            private const float AckTimeoutSeconds = 1f;

            private struct FileSendInfo
            {
                public ulong TargetSteamId;
                public string FileName;
                public byte[] FileBytes;
                public int TotalChunks;
                public int SentChunks;
            }

            private readonly Queue<FileSendInfo> _filesToSend = new();
            private readonly Dictionary<ulong, Connection> _connections = new();
            private readonly Dictionary<ulong, bool> _fileCountSent = new();

            private int _waitingForAckChunkIndex = -1;
            private FileSendInfo _currentFile;
            private float _ackTimer = 0f;
            private byte[] _lastChunkData;

            private int _totalFilesCount = 0;

            public void QueueFileForSending(string filePath, ulong targetSteamId)
            {
                if (!File.Exists(filePath))
                {
                    Debug.LogError($"[MusicSenderSocket]: File not found: {filePath}");
                    return;
                }

                string extension = Path.GetExtension(filePath).ToLower();
                if (extension != ".mp3")
                {
                    Debug.LogWarning($"[MusicSenderSocket]: Skipping file '{filePath}' - only MP3 files are allowed.");
                    return;
                }

                var fileBytes = File.ReadAllBytes(filePath);
                var fileName = Path.GetFileName(filePath);
                int totalChunks = (int)Math.Ceiling(fileBytes.Length / (double)ChunkSize);

                Debug.Log($"[MusicSenderSocket]: Queuing file '{fileName}' of size {fileBytes.Length} bytes in {totalChunks} chunk(s) to send to {targetSteamId}");

                var fileInfo = new FileSendInfo
                {
                    TargetSteamId = targetSteamId,
                    FileName = fileName,
                    FileBytes = fileBytes,
                    TotalChunks = totalChunks,
                    SentChunks = 0
                };

                _filesToSend.Enqueue(fileInfo);
                _totalFilesCount++;

                TrySendNextFile();
            }

            private void SendFileCount(Connection connection, ulong targetSteamId)
            {
                int fileCount = _totalFilesCount;

                using var countMs = new MemoryStream();
                using var writer = new BinaryWriter(countMs);
                writer.Write("FILE_COUNT");
                writer.Write(fileCount);
                writer.Flush();

                connection.SendMessage(countMs.ToArray(), SendType.Reliable);
                Debug.Log($"[MusicSenderSocket]: Sent FILE_COUNT={fileCount} to {targetSteamId}");
            }

            public override void OnConnectionChanged(Connection connection, ConnectionInfo info)
            {
                if (info.State == ConnectionState.Connected)
                {
                    Debug.Log($"[MusicSenderSocket]: Connected to {info.Identity.SteamId}");
                    _connections[info.Identity.SteamId] = connection;

                    if (!_fileCountSent.TryGetValue(info.Identity.SteamId, out bool sent) || !sent)
                    {
                        Debug.Log($"[MusicSenderSocket]: About to send FILE_COUNT to {info.Identity.SteamId}");
                        SendFileCount(connection, info.Identity.SteamId);
                        _fileCountSent[info.Identity.SteamId] = true;
                    }

                    TrySendNextFile();
                }
                else if (info.State == ConnectionState.ClosedByPeer || info.State == ConnectionState.ProblemDetectedLocally)
                {
                    Debug.Log($"[MusicSenderSocket]: Disconnected from {info.Identity.SteamId}");
                    _connections.Remove(info.Identity.SteamId);
                    _fileCountSent.Remove(info.Identity.SteamId);
                }

                base.OnConnectionChanged(connection, info);
            }

            public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
            {
                byte[] managedData = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(data, managedData, 0, size);

                using var ms = new MemoryStream(managedData);
                using var reader = new BinaryReader(ms);

                string messageType = reader.ReadString();

                Debug.Log($"[MusicSenderSocket]: Received message of type '{messageType}' from {identity.SteamId}");

                if (messageType == "ACK")
                {
                    int ackChunkIndex = reader.ReadInt32();
                    Debug.Log($"[MusicSenderSocket]: Received ACK for chunk {ackChunkIndex}");

                    if (_waitingForAckChunkIndex == ackChunkIndex)
                    {
                        Debug.Log($"[MusicSenderSocket]: ACK matched waiting chunk index {_waitingForAckChunkIndex}, proceeding...");
                        _waitingForAckChunkIndex = -1;
                        _ackTimer = 0f;
                        _lastChunkData = null;

                        _currentFile.SentChunks++;
                        if (_currentFile.SentChunks >= _currentFile.TotalChunks)
                        {
                            Debug.Log($"[MusicSenderSocket]: Finished sending file {_currentFile.FileName}");
                            TrySendNextFile(); // Proceed to next file
                        }
                        else
                        {
                            SendNextChunk(); // Proceed with next chunk
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[MusicSenderSocket]: ACK chunk index {ackChunkIndex} does not match waiting chunk index {_waitingForAckChunkIndex}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[MusicSenderSocket]: Unknown message type '{messageType}' received");
                }

                base.OnMessage(connection, identity, data, size, messageNum, recvTime, channel);
            }

            public void Update()
            {
                if (_waitingForAckChunkIndex != -1)
                {
                    _ackTimer += Time.deltaTime;
                    if (_ackTimer >= AckTimeoutSeconds)
                    {
                        Debug.LogWarning($"[MusicSenderSocket]: ACK timeout for chunk {_waitingForAckChunkIndex}, resending...");
                        ResendChunk();
                    }
                }
            }

            private void TrySendNextFile()
            {
                if (_waitingForAckChunkIndex != -1)
                {
                    Debug.Log("[MusicSenderSocket]: Waiting for ACK, cannot start new file send");
                    return; // Waiting for ACK
                }

                if (_filesToSend.Count == 0)
                {
                    Debug.Log("[MusicSenderSocket]: No files to send");
                    return;
                }

                _currentFile = _filesToSend.Dequeue();
                Debug.Log($"[MusicSenderSocket]: Dequeued file '{_currentFile.FileName}' to send");

                if (!_connections.TryGetValue(_currentFile.TargetSteamId, out var connection))
                {
                    Debug.Log("[MusicSenderSocket]: Waiting for connection to send file...");
                    _filesToSend.Enqueue(_currentFile);
                    return;
                }

                SendNextChunk();
            }

            private void SendNextChunk()
            {
                if (_currentFile.SentChunks >= _currentFile.TotalChunks)
                {
                    Debug.Log("[MusicSenderSocket]: All chunks sent for current file");
                    return;
                }

                int chunkIndex = _currentFile.SentChunks;
                int offset = chunkIndex * ChunkSize;
                int size = Math.Min(ChunkSize, _currentFile.FileBytes.Length - offset);

                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);

                writer.Write("DATA");
                writer.Write(_currentFile.FileName);
                writer.Write(_currentFile.FileBytes.Length);
                writer.Write(chunkIndex);
                writer.Write(_currentFile.TotalChunks);
                writer.Write(_currentFile.FileBytes, offset, size);
                writer.Flush();

                _lastChunkData = ms.ToArray();

                var connection = _connections[_currentFile.TargetSteamId];
                var result = connection.SendMessage(_lastChunkData, SendType.Reliable);

                if (result == Result.OK)
                {
                    Debug.Log($"[MusicSenderSocket]: Sent chunk {chunkIndex + 1} / {_currentFile.TotalChunks} for file {_currentFile.FileName}");
                    _waitingForAckChunkIndex = chunkIndex;
                    _ackTimer = 0f;
                }
                else
                {
                    Debug.LogError("[MusicSenderSocket]: Failed to send chunk!");
                }
            }

            private void ResendChunk()
            {
                if (_lastChunkData == null || !_connections.TryGetValue(_currentFile.TargetSteamId, out var connection))
                {
                    Debug.LogError("[MusicSenderSocket]: Cannot resend chunk — no data or connection.");
                    return;
                }

                Debug.Log($"[MusicSenderSocket]: Resending chunk {_waitingForAckChunkIndex}");
                var result = connection.SendMessage(_lastChunkData, SendType.Reliable);
                if (result == Result.OK)
                {
                    Debug.Log($"[MusicSenderSocket]: Resent chunk {_waitingForAckChunkIndex}");
                }
                else
                {
                    Debug.LogError("[MusicSenderSocket]: Failed to resend chunk!");
                }
                _ackTimer = 0f;
            }
        }

        // === MusicReceiverClient ===
        private class MusicReceiverClient : ConnectionManager
        {
            private Connection _hostConnection;

            public bool _isAllFilesReceived = false;

            private int _expectedFileCount = -1;
            private int _completedFilesCount = 0;

            public int TotalExpectedChunks { get; private set; } = 0;
            public int ReceivedChunks { get; private set; } = 0;

            private class ReceivingFileInfo
            {
                public string FileName;
                public int FileSize;
                public int TotalChunks;
                public Dictionary<int, byte[]> Chunks = new();
                public int ReceivedChunksCount => Chunks.Count;
            }

            private readonly Dictionary<string, ReceivingFileInfo> _receivingFiles = new();

            public bool AreAllFilesReceived()
            {
                return _isAllFilesReceived;
            }

            public int CompletedFileCount => _completedFilesCount;
            public int ExpectedFileCount => _expectedFileCount;

            public override void OnConnected(ConnectionInfo info)
            {

                Debug.Log("[MusicReceiverClient]: Connected to host.");
                _hostConnection = Connection;

                base.OnConnected(info);
            }


            public override void OnDisconnected(ConnectionInfo info)
            {
                Debug.Log("[MusicReceiverClient]: Disconnected from host.");
                _receivingFiles.Clear();

                base.OnDisconnected(info);
            }

            public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
            {
                byte[] managedData = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(data, managedData, 0, size);

                using var ms = new MemoryStream(managedData);
                using var reader = new BinaryReader(ms);

                string messageType = reader.ReadString();

                if (messageType == "FILE_COUNT")
                {
                    _expectedFileCount = reader.ReadInt32();
                    Debug.Log($"[MusicReceiverClient]: Sender will send {_expectedFileCount} file(s)");
                }
                else if (messageType == "DATA")
                {
                    if (_expectedFileCount == -1)
                    {
                        Debug.LogWarning("[MusicReceiverClient]: Received DATA before FILE_COUNT, ignoring.");
                        return;
                    }

                    string fileName = reader.ReadString();
                    int fileSize = reader.ReadInt32();
                    int chunkIndex = reader.ReadInt32();
                    int totalChunks = reader.ReadInt32();
                    byte[] chunkData = reader.ReadBytes(size - (int)ms.Position);

                    if (!_receivingFiles.TryGetValue(fileName, out var fileInfo))
                    {
                        fileInfo = new ReceivingFileInfo
                        {
                            FileName = fileName,
                            FileSize = fileSize,
                            TotalChunks = totalChunks
                        };
                        _receivingFiles[fileName] = fileInfo;

                        TotalExpectedChunks += totalChunks;
                    }

                    if (!fileInfo.Chunks.ContainsKey(chunkIndex))
                    {
                        fileInfo.Chunks[chunkIndex] = chunkData;
                        ReceivedChunks++;
                    }

                    // Sending an ACK
                    using var ackMs = new MemoryStream();
                    using var ackWriter = new BinaryWriter(ackMs);
                    ackWriter.Write("ACK");
                    ackWriter.Write(chunkIndex);
                    ackWriter.Flush();
                    _hostConnection.SendMessage(ackMs.ToArray(), SendType.Reliable);

                    // Validation of file's receivement 
                    if (fileInfo.ReceivedChunksCount == totalChunks)
                    {
                        // Saving file
                        string directoryPath = Path.Combine(VTResources.gameRootDirectory, "SharedRadioMusic");

                        if (_completedFilesCount == 0)
                        {
                            if (File.Exists(directoryPath))
                            {
                                File.Delete(directoryPath);
                            }

                            if (Directory.Exists(directoryPath))
                            {
                                Directory.Delete(directoryPath, true);
                            }

                            Directory.CreateDirectory(directoryPath);
                        }

                        string savePath = Path.Combine(directoryPath, fileName); 

                        using var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write);
                        for (int i = 0; i < totalChunks; i++)
                        {
                            fs.Write(fileInfo.Chunks[i], 0, fileInfo.Chunks[i].Length);
                        }

                        Debug.Log($"[MusicReceiverClient]: Saved received file to: {savePath}");

                        _receivingFiles.Remove(fileName);
                        _completedFilesCount++;

                        // If all files were received successfully - _isAllFilesReceived = true;
                        if (_expectedFileCount >= 0 && _completedFilesCount >= _expectedFileCount)
                        {
                            _isAllFilesReceived = true;
                            Debug.Log("[MusicReceiverClient]: All files received.");
                        }
                    }
                }
                else
                {
                    Debug.LogWarning($"[MusicReceiverClient]: Unknown message type '{messageType}'");
                }

                base.OnMessage(data, size, messageNum, recvTime, channel);
            }
        }
    }
}