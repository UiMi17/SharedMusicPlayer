using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using Steamworks.Data;
using System.Threading.Tasks;
using System.Collections;
using VTOLVR.Multiplayer;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace SharedMusicPlayer
{ 
    public class MusicNetworkManager : MonoBehaviour
    { 
        private const int ChunkSize = 64 * 1024; // 64KB
        private const int MaxRetryAttempts = 5;
        private const float RetryDelaySeconds = 2f;
        private const int MusicNetworkPort = 1337;
        private const float ReceiverTimeoutSeconds = 10f;

        private MusicSenderSocket _senderSocket;
        private MusicReceiverClient _receiverClient;

        private ulong _targetSteamId;
        private string[] _filesToSend;

        private bool _isSender = false;
        private bool _isReceiver = false;

        private VTOLMPBriefingRoomUI briefingUI;

        private bool _ownerWatcherActive = false;
        private string[] _filesToSendOnJoin;
        private readonly HashSet<ulong> _sentRecipients = new HashSet<ulong>();
        
        // Store original file lists per target SteamId to persist across socket recreations
        private readonly Dictionary<ulong, string[]> _originalFilesPerTarget = new Dictionary<ulong, string[]>();

        // Cancellation tracking
        private bool _isDownloadCancelled = false;
        private List<string> _downloadedFilesInSession = new List<string>();
        private Coroutine _receiveCoroutine = null;
        private List<string> _expectedFileNames = new List<string>();
        private GameObject _cancelButton = null;
        private Text _cancelInstructionText = null; // Static text for cancel instruction

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

            // Check for ESC key or VR controller button to cancel download (only if download is in progress)
            if (!_isDownloadCancelled && _isReceiver && _receiverClient != null && !_receiverClient.AreAllFilesReceived())
            {
                // ESC key (works in both flat screen and VR)
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    Logger.Log("ESC key pressed - cancelling download", "MusicNetworkManager");
                    CancelDownload();
                    return;
                }

                // VR Controller buttons - try common cancel/back buttons
                // Button 1 is typically B button (back/cancel) on most VR controllers
                // Button 2 is typically A button (primary action)
                // We'll use Button 1 (B button) as it's commonly used for cancel/back
                if (Input.GetButtonDown("joystick button 1") || 
                    Input.GetButtonDown("joystick button 2") || // Fallback: A button
                    Input.GetKeyDown(KeyCode.JoystickButton1) || 
                    Input.GetKeyDown(KeyCode.JoystickButton2))
                {
                    Logger.Log("VR controller button pressed - cancelling download", "MusicNetworkManager");
                    CancelDownload();
                    return;
                }

                // Also check for grip buttons as alternative (less common but some users might prefer)
                // Grip buttons are typically axis-based, but some controllers map them to buttons
                // Left grip: joystick button 4, Right grip: joystick button 5
                if (Input.GetButtonDown("joystick button 4") || 
                    Input.GetButtonDown("joystick button 5") ||
                    Input.GetKeyDown(KeyCode.JoystickButton4) || 
                    Input.GetKeyDown(KeyCode.JoystickButton5))
                {
                    Logger.Log("VR controller grip button pressed - cancelling download", "MusicNetworkManager");
                    CancelDownload();
                    return;
                }
            }
        }

        public async void StartSender(ulong targetSteamId, string[] files)
        {
            if (_isSender)
            {
                Logger.LogWarn("Sender already running", "MusicNetworkManager");
                return;
            }

            Logger.Log($"Starting sender to SteamID {targetSteamId} with {files.Length} file(s)", "MusicNetworkManager");
            _targetSteamId = targetSteamId;
            _filesToSend = files;
            
            // Store original file list for this target to persist across socket recreations
            _originalFilesPerTarget[targetSteamId] = files;
            
            _isSender = true;
            _isReceiver = false;

            int attempt = 0;
            while (attempt < MaxRetryAttempts)
            {
                try
                {
                    if (!SteamClient.IsValid)
                    {
                        Logger.LogError("Steam client not initialized!", "MusicNetworkManager");
                        Debug.LogError("[MusicNetworkManager]: Steam client not initialized!");
                        return;
                    }

                    Logger.Log($"Attempting to create relay sender socket (attempt {attempt + 1}/{MaxRetryAttempts})...", "MusicNetworkManager");
                    Debug.Log("[MusicNetworkManager]: Attempting to create relay sender socket...");
                    _senderSocket = SteamNetworkingSockets.CreateRelaySocket<MusicSenderSocket>(MusicNetworkPort);
                    Logger.Log("Relay sender socket created successfully", "MusicNetworkManager");
                    Debug.Log("[MusicNetworkManager]: Created relay sender socket");
                    break;
                }
                catch (Exception ex)
                {
                    attempt++;
                    Logger.LogError($"Failed to create relay socket (attempt {attempt}): {ex.Message}", "MusicNetworkManager");
                    Debug.LogError($"[MusicNetworkManager]: Failed to create relay socket (attempt {attempt}): {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));
                }
            }

            if (_senderSocket == null)
            {
                Logger.LogError($"Could not create sender socket after {MaxRetryAttempts} attempts", "MusicNetworkManager");
                Debug.LogError("[MusicNetworkManager]: Could not create sender socket after multiple attempts");
                _isSender = false;
                return;
            }

            Logger.Log($"Queuing {_filesToSend.Length} file(s) for sending to SteamID {_targetSteamId}", "MusicNetworkManager");
            Debug.Log($"[MusicNetworkManager]: Queuing {_filesToSend.Length} file(s) for sending to SteamID {_targetSteamId}");
            
            // Set parent manager reference and original files in socket so it can rebuild queue on reconnection
            _senderSocket.SetParentManager(this);
            _senderSocket.SetOriginalFilesForTarget(targetSteamId, _filesToSend);
            
            foreach (var file in _filesToSend)
            {
                _senderSocket.QueueFileForSending(file, targetSteamId);
            }

            Logger.Log("Sender started successfully", "MusicNetworkManager");
            Debug.Log("[MusicNetworkManager]: Started sender");
        }

        public async void StartReceiver(ulong targetSteamId)
        {
            if (_isReceiver)
            {
                Logger.LogWarn("Receiver already running", "MusicNetworkManager");
                Debug.LogWarning("[MusicNetworkManager]: Receiver already running");
                return;
            }

            Logger.Log($"Starting receiver from SteamID {targetSteamId}", "MusicNetworkManager");
            _targetSteamId = targetSteamId;
            _isReceiver = true;
            _isSender = false;

            if (!SteamClient.IsValid)
            {
                Logger.LogError("Steam client not initialized!", "MusicNetworkManager");
                Debug.LogError("[MusicNetworkManager]: Steam client not initialized!");
                return;
            }

            _receiverClient = await TryCreateSocketWithRetries(
                () => SteamNetworkingSockets.ConnectRelay<MusicReceiverClient>(_targetSteamId, MusicNetworkPort),
                $"connect relay to sender {_targetSteamId}"
            );

            if (_receiverClient == null)
            {
                Logger.LogError($"Failed to start receiver after retries", "MusicNetworkManager");
                _isReceiver = false;
                return;
            }

            // Set parent manager reference for tracking downloaded files
            _receiverClient.SetParentManager(this);

            Logger.Log("Receiver started successfully", "MusicNetworkManager");
            Debug.Log("[MusicNetworkManager]: Started receiver");
        }

        private async Task<T> TryCreateSocketWithRetries<T>(Func<T> createFunc, string description) where T : class
        {
            int attempt = 0;
            while (attempt < MaxRetryAttempts)
            {
                try
                {
                    Logger.Log($"Attempting to {description} (attempt {attempt + 1}/{MaxRetryAttempts})...", "MusicNetworkManager");
                    Debug.Log($"[MusicNetworkManager]: Attempting to {description} (attempt {attempt + 1})...");
                    var socket = createFunc();
                    Logger.Log($"Successfully {description.ToLower()}", "MusicNetworkManager");
                    Debug.Log($"[MusicNetworkManager]: Successfully {description.ToLower()}");
                    return socket;
                }
                catch (Exception ex)
                {
                    attempt++;
                    Logger.LogError($"Failed to {description.ToLower()} (attempt {attempt}): {ex.Message}", "MusicNetworkManager");
                    Debug.LogError($"[MusicNetworkManager]: Failed to {description.ToLower()} (attempt {attempt}): {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));
                }
            }

            Logger.LogError($"Could not {description.ToLower()} after {MaxRetryAttempts} attempts", "MusicNetworkManager");
            Debug.LogError($"[MusicNetworkManager]: Could not {description.ToLower()} after {MaxRetryAttempts} attempts");
            return null;
        }

        public void StopAll()
        {
            Logger.Log("Stopping all network activity...", "MusicNetworkManager");
            _isSender = false;
            _isReceiver = false;

            StopOwnerWatcher();
            _sentRecipients.Clear();
            
            // Note: We keep _originalFilesPerTarget to allow reconnection with full file list
            // It will be cleared when a new sender is started for a different target

            if (_senderSocket != null)
            {
                Logger.Log("Closing sender socket", "MusicNetworkManager");
                Debug.Log("[MusicNetworkManager]: Closing sender socket");
                _senderSocket.Close();
                _senderSocket = null;
            }

            if (_receiverClient != null)
            {
                Logger.Log("Closing receiver client", "MusicNetworkManager");
                Debug.Log("[MusicNetworkManager]: Closing receiver client");
                _receiverClient.Close();
                _receiverClient = null;
            }

            Logger.Log("All network activity stopped", "MusicNetworkManager");
            Debug.Log("[MusicNetworkManager]: Stopped all network activity");
        }

        public void CancelDownload()
        {
            Logger.Log("Cancelling download...", "MusicNetworkManager");
            Debug.Log("[MusicNetworkManager]: Cancelling download");

            // Set cancellation flag
            _isDownloadCancelled = true;

            // Stop all network activity
            StopAll();

            // Stop the receive coroutine if running
            if (_receiveCoroutine != null)
            {
                StopCoroutine(_receiveCoroutine);
                _receiveCoroutine = null;
                Logger.Log("Stopped receive coroutine", "MusicNetworkManager");
            }

            // Stop WaitThenEnter coroutine if running
            Patch_EnterVehicle_BlockCopilot.StopWaitThenEnterCoroutine();

            // Clear downloaded files from this session
            if (_downloadedFilesInSession != null && _downloadedFilesInSession.Count > 0)
            {
                string sharedPath = Path.Combine(VTResources.gameRootDirectory, "SharedRadioMusic");
                foreach (var fileName in _downloadedFilesInSession)
                {
                    try
                    {
                        string filePath = Path.Combine(sharedPath, fileName);
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                            Logger.Log($"Deleted downloaded file: {fileName}", "MusicNetworkManager");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarn($"Failed to delete file '{fileName}': {ex.Message}", "MusicNetworkManager");
                    }
                }
                _downloadedFilesInSession.Clear();
            }

            // Set flags to prevent entry
            SharedMusicState.IsCopilotReadyToEnter = false;
            SharedMusicState.IsDownloadCancelled = true;

            // Update UI to show cancellation message before hiding
            if (briefingUI != null && briefingUI.updatingContentObj != null)
            {
                SMPUIUtils.SetUpdatingText(briefingUI, "Download cancelled");
                // Wait a moment to show the message, then hide
                StartCoroutine(HideModalAfterDelay(2f));
            }

            // Destroy cancel button and instruction text
            if (_cancelButton != null)
            {
                Destroy(_cancelButton);
                _cancelButton = null;
            }
            if (_cancelInstructionText != null)
            {
                Destroy(_cancelInstructionText.gameObject);
                _cancelInstructionText = null;
            }

            // Reset cancellation state after all cleanup is complete
            // Use a coroutine to ensure this happens after modal is hidden
            StartCoroutine(ResetCancellationStateAfterDelay(2.5f));

            // Clear expected file names
            _expectedFileNames.Clear();

            // Reset playlist build flag so it rebuilds on retry (for copilot)
            if (SharedRadioController.Instance != null)
            {
                SharedRadioController.Instance.RebuildPlaylist();
                Logger.Log("Reset playlist build flag for retry", "MusicNetworkManager");
            }

            // Reset pilot's music state if this is the sender (pilot side)
            if (_isSender)
            {
                ResetPilotMusicState();
            }

            Logger.Log("Download cancelled successfully", "MusicNetworkManager");
            Debug.Log("[MusicNetworkManager]: Download cancelled successfully");
        }

        private void ResetPilotMusicState()
        {
            try
            {
                var cockpitRadio = UnityEngine.Object.FindObjectOfType<CockpitRadio>();
                if (cockpitRadio != null)
                {
                    // Stop music if playing
                    cockpitRadio.StopPlayingSong();
                    
                    // Reset song index to 0
                    var songIdxField = typeof(CockpitRadio).GetField("songIdx", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (songIdxField != null)
                    {
                        songIdxField.SetValue(cockpitRadio, 0);
                        Logger.Log("Reset pilot music state: stopped and set index to 0", "MusicNetworkManager");
                    }

                    // Reset SharedRadioController index
                    if (SharedRadioController.Instance != null)
                    {
                        SharedRadioController.Instance.SetCurrentIndex(0);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to reset pilot music state: {ex}", "MusicNetworkManager");
            }
        }

        private void OnDestroy()
        {
            StopAll();
        }

        public void StartOwnerWatcher(string[] files)
        {
            Logger.Log($"Starting owner watcher with {files.Length} file(s)", "MusicNetworkManager");
            _filesToSendOnJoin = files;

            if (_ownerWatcherActive)
            {
                Logger.LogWarn("Owner watcher already active", "MusicNetworkManager");
                Debug.Log("[MusicNetworkManager]: Owner watcher already active");
                return;
            }

            var scene = FindObjectOfType<VTOLMPSceneManager>();
            if (scene == null)
            {
                Logger.LogWarn("VTOLMPSceneManager not found; cannot start owner watcher", "MusicNetworkManager");
                Debug.LogWarning("[MusicNetworkManager]: VTOLMPSceneManager not found; cannot start owner watcher");
                return;
            }

            var mySlot = scene.GetSlot(scene.localPlayer);
            var ownerSlot = scene.GetMCOwnerSlot(mySlot);
            if (ownerSlot != mySlot)
            {
                Logger.Log("Not the owner; owner watcher not needed", "MusicNetworkManager");
                Debug.Log("[MusicNetworkManager]: Not the owner; owner watcher not needed");
                return;
            }

            scene.OnSlotUpdated += HandleOnSlotUpdated;
            _ownerWatcherActive = true;
            Logger.Log("Owner watcher started successfully", "MusicNetworkManager");
            Debug.Log("[MusicNetworkManager]: Owner watcher started");
            HandleOnSlotUpdated(null);
        }

        private void StopOwnerWatcher()
        {
            if (!_ownerWatcherActive)
                return;

            Logger.Log("Stopping owner watcher...", "MusicNetworkManager");
            var scene = FindObjectOfType<VTOLMPSceneManager>();
            if (scene != null)
            {
                scene.OnSlotUpdated -= HandleOnSlotUpdated;
            }
            _ownerWatcherActive = false;
            _filesToSendOnJoin = null;
            Logger.Log("Owner watcher stopped", "MusicNetworkManager");
            Debug.Log("[MusicNetworkManager]: Owner watcher stopped");
        }

        private void HandleOnSlotUpdated(VTOLMPSceneManager.VehicleSlot _)
        {
            if (!_ownerWatcherActive || _isSender)
                return;

            var scene = FindObjectOfType<VTOLMPSceneManager>();
            if (scene == null)
                return;

            var mySlot = scene.GetSlot(scene.localPlayer);
            if (mySlot == null)
                return;

            var ownerSlot = scene.GetMCOwnerSlot(mySlot);

            ulong? currentOwnerId = (ownerSlot != null && ownerSlot.player != null) ? (ulong?)ownerSlot.player.steamUser.Id : null;
            if (SharedMusicState.LibraryOwnerSteamId != currentOwnerId)
            {
                Logger.Log($"Detected ownership change. Old={SharedMusicState.LibraryOwnerSteamId} New={currentOwnerId}", "MusicNetworkManager");
                Debug.Log($"[MusicNetworkManager]: Detected ownership change. Old={SharedMusicState.LibraryOwnerSteamId} New={currentOwnerId}");
                SharedMusicState.LibraryOwnerSteamId = currentOwnerId;

                if (currentOwnerId != null && currentOwnerId != BDSteamClient.mySteamID)
                {
                    return;
                }

                Logger.Log("Clearing sent recipients list due to ownership change", "MusicNetworkManager");
                _sentRecipients.Clear();
            }

            if (ownerSlot != mySlot)
            {
                return;
            }

            var baseSlot = scene.GetMCBaseSlot(mySlot);
            ulong? otherId = null;
            for (int i = 0; i < baseSlot.mcSlotCount; i++)
            {
                var s = scene.GetSlot(baseSlot.slotID + i);
                if (s != null && s.player != null && s.player != mySlot.player)
                {
                    otherId = s.player.steamUser.Id;
                    break;
                }
            }

            if (otherId != null)
            {
                if (_sentRecipients.Contains(otherId.Value))
                {
                    return;
                }

                string[] filesToSend = _filesToSendOnJoin;
                try
                {
                    string localPath = Path.GetFullPath(GameSettings.RADIO_MUSIC_PATH);
                    var localFiles = Directory.Exists(localPath) ? Directory.GetFiles(localPath, "*.mp3") : Array.Empty<string>();
                    if (localFiles.Length > 0)
                    {
                        Array.Sort(localFiles, StringComparer.OrdinalIgnoreCase);
                        filesToSend = localFiles;
                    }
                    else
                    {
                        string sharedPath = Path.Combine(VTResources.gameRootDirectory, "SharedRadioMusic");
                        Directory.CreateDirectory(sharedPath);
                        var sharedFiles = Directory.GetFiles(sharedPath, "*.mp3");
                        Array.Sort(sharedFiles, StringComparer.OrdinalIgnoreCase);
                        filesToSend = sharedFiles;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[MusicNetworkManager]: Failed to build file list for sending on owner change/join: " + ex);
                }

                if (filesToSend != null && filesToSend.Length > 0)
                {
                    Logger.Log($"Other crew detected ({otherId}). Sending {filesToSend.Length} music file(s).", "MusicNetworkManager");
                    Debug.Log($"[MusicNetworkManager]: Other crew detected ({otherId}). Sending {filesToSend.Length} music file(s).");
                    StartSender((ulong)otherId, filesToSend);
                    _sentRecipients.Add(otherId.Value);
                }
            }
        }

        public IEnumerator BeginReceiveAndWaitCoroutine(ulong senderSteamId)
        {
            // Reset cancellation state for new download session
            _isDownloadCancelled = false;
            _downloadedFilesInSession.Clear();
            _expectedFileNames.Clear();
            SharedMusicState.IsDownloadCancelled = false;

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

                // Create cancel instruction text (static, doesn't get updated)
                CreateCancelInstructionText();
                
                // Create cancel button when modal is shown
                CreateCancelButton();
            }

            // Waiting for client with timeout
            float timeout = ReceiverTimeoutSeconds;
            float elapsed = 0f;
            while (_receiverClient == null && elapsed < timeout)
            {
                // Check for cancellation
                if (_isDownloadCancelled)
                {
                    Logger.Log("Download cancelled while waiting for receiver client", "MusicNetworkManager");
                    yield break;
                }

                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }

            if (_receiverClient == null)
            {
                Logger.LogError("Receiver client not initialized, aborting wait.", "MusicNetworkManager");
                Debug.LogError("[MusicNetworkManager]: Receiver client not initialized, aborting wait.");
                yield break;
            }

            Logger.Log("BeginReceiveAndWait started — waiting for all files...", "MusicNetworkManager");
            Debug.Log("[MusicNetworkManager]: BeginReceiveAndWait started — waiting for all files...");

            // Waiting for all files
            while (_receiverClient != null && !_receiverClient.AreAllFilesReceived())
            {
                // Check for cancellation
                if (_isDownloadCancelled)
                {
                    Logger.Log("Download cancelled, exiting coroutine", "MusicNetworkManager");
                    yield break;
                }

                if (_receiverClient.TotalExpectedChunks > 0)
                {
                    float progress = Mathf.Clamp01((float)_receiverClient.ReceivedChunks / _receiverClient.TotalExpectedChunks);
                    if (briefingUI != null && briefingUI.updatingContentProgress != null)
                    {
                        briefingUI.updatingContentProgress.localScale = new Vector3(progress, 1f, 1f);
                    }

                        int percent = Mathf.RoundToInt(progress * 100f);
                        // Only update progress text, cancel instruction stays static
                        SMPUIUtils.SetUpdatingText(briefingUI, $"Loading Music Files {_receiverClient.CompletedFileCount + 1}/{_receiverClient.ExpectedFileCount} ({percent}%)");
                }

                yield return new WaitForSeconds(0.05f);
            }

            // Log when we exit the while loop
            if (_receiverClient == null)
            {
                Logger.LogWarn("Receiver client became null while waiting for files, exiting coroutine", "MusicNetworkManager");
                yield break;
            }

            Logger.Log($"Exited file waiting loop - AreAllFilesReceived={_receiverClient.AreAllFilesReceived()}, CompletedFileCount={_receiverClient.CompletedFileCount}, ExpectedFileCount={_receiverClient.ExpectedFileCount}", "MusicNetworkManager");

            // Check for cancellation before completing
            if (_isDownloadCancelled)
            {
                Logger.Log("Download was cancelled, not setting ready flag", "MusicNetworkManager");
                yield break;
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

            // Destroy cancel button and instruction text
            if (_cancelButton != null)
            {
                Destroy(_cancelButton);
                _cancelButton = null;
            }
            if (_cancelInstructionText != null)
            {
                Destroy(_cancelInstructionText.gameObject);
                _cancelInstructionText = null;
            }

            Logger.Log("All files received successfully.", "MusicNetworkManager");
            Debug.Log($"[MusicNetworkManager]: All files received successfully.");

            // Set ready flag BEFORE stopping network (important for entry permission)
            // This MUST be set even if StopAll() is called from elsewhere
            if (!_isDownloadCancelled)
            {
                // Rebuild playlist now that all files are downloaded (for copilot)
                if (SharedRadioController.Instance != null)
                {
                    SharedRadioController.Instance.RebuildPlaylist();
                    Logger.Log("Rebuilt playlist after all files received", "MusicNetworkManager");
                }

                SharedMusicState.IsCopilotReadyToEnter = true;
                Logger.Log("Set IsCopilotReadyToEnter = true", "MusicNetworkManager");
                Debug.Log("[MusicNetworkManager]: Set IsCopilotReadyToEnter = true");
            }
            else
            {
                Logger.LogWarn("Download was cancelled, NOT setting IsCopilotReadyToEnter", "MusicNetworkManager");
            }

            StopAll();
            
            // Double-check: ensure flag is still set after StopAll (in case it was cleared)
            if (!_isDownloadCancelled && !SharedMusicState.IsCopilotReadyToEnter)
            {
                Logger.LogWarn("IsCopilotReadyToEnter was cleared, re-setting it", "MusicNetworkManager");
                SharedMusicState.IsCopilotReadyToEnter = true;
            }
        }

        private IEnumerator HideModalAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (briefingUI != null && briefingUI.updatingContentObj != null)
            {
                briefingUI.updatingContentObj.SetActive(false);
            }
        }

        private IEnumerator ResetCancellationStateAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            // Reset all cancellation flags after cleanup is complete
            SharedMusicState.IsDownloadCancelled = false;
            _isDownloadCancelled = false;
            
            // Reset entering vehicle state in the patch
            Patch_EnterVehicle_BlockCopilot.ResetEnteringVehicleState();
            
            Logger.Log("Cancellation state reset - ready for new download attempt", "MusicNetworkManager");
        }

        private void CreateCancelInstructionText()
        {
            // Don't create if already exists
            if (_cancelInstructionText != null)
            {
                return;
            }

            if (briefingUI == null || briefingUI.updatingContentObj == null)
            {
                return;
            }

            try
            {
                // Find existing text component to copy font settings
                Text existingText = briefingUI.updatingContentObj.GetComponentInChildren<Text>(includeInactive: true);
                
                // Create GameObject for cancel instruction text
                GameObject instructionObj = new GameObject("CancelInstructionText");
                instructionObj.SetActive(true);
                RectTransform instructionRect = instructionObj.AddComponent<RectTransform>();
                
                Transform parentTransform = briefingUI.updatingContentObj.transform;
                instructionObj.transform.SetParent(parentTransform, false);
                
                // Get parent RectTransform to understand bounds
                RectTransform parentRect = parentTransform as RectTransform;
                float parentHeight = parentRect != null ? parentRect.rect.height : 200f;
                
                // Position relative to the main text component
                // Find the main text component to position relative to it
                Text mainText = briefingUI.updatingContentObj.GetComponentInChildren<Text>(includeInactive: true);
                if (mainText != null)
                {
                    RectTransform mainTextRect = mainText.GetComponent<RectTransform>();
                    if (mainTextRect != null)
                    {
                        // Position instruction text below main text, using same horizontal anchors
                        instructionRect.anchorMin = new Vector2(0.5f, 0f);
                        instructionRect.anchorMax = new Vector2(0.5f, 0f);
                        instructionRect.pivot = new Vector2(0.5f, 0.5f);
                        instructionRect.sizeDelta = new Vector2(Mathf.Min(400, parentRect != null ? parentRect.rect.width - 20 : 400), 30);
                        
                        // Calculate position: center horizontally, position above cancel button
                        // Cancel button is at y=80, so place instruction at y=120 (40px above button)
                        // But ensure it's within modal bounds (at least 10px from edges)
                        float yPos = 120f;
                        if (parentRect != null && yPos + 15 > parentHeight - 10)
                        {
                            // Adjust if too high - place it lower but still above button
                            yPos = Mathf.Max(100f, parentHeight - 50f);
                        }
                        instructionRect.anchoredPosition = new Vector2(0, yPos);
                    }
                    else
                    {
                        // Fallback: center horizontally, position above cancel button
                        instructionRect.anchorMin = new Vector2(0.5f, 0f);
                        instructionRect.anchorMax = new Vector2(0.5f, 0f);
                        instructionRect.pivot = new Vector2(0.5f, 0.5f);
                        instructionRect.sizeDelta = new Vector2(Mathf.Min(400, parentRect != null ? parentRect.rect.width - 20 : 400), 30);
                        instructionRect.anchoredPosition = new Vector2(0, 120);
                    }
                }
                else
                {
                    // Fallback: center horizontally, position above cancel button
                    instructionRect.anchorMin = new Vector2(0.5f, 0f);
                    instructionRect.anchorMax = new Vector2(0.5f, 0f);
                    instructionRect.pivot = new Vector2(0.5f, 0.5f);
                    instructionRect.sizeDelta = new Vector2(Mathf.Min(400, parentRect != null ? parentRect.rect.width - 20 : 400), 30);
                    instructionRect.anchoredPosition = new Vector2(0, 120);
                }

                var layoutEl = instructionObj.AddComponent<LayoutElement>();
                layoutEl.ignoreLayout = true;

                // Add Text component
                Text instructionText = instructionObj.AddComponent<Text>();
                
                // Copy font settings from existing text if available
                if (existingText != null)
                {
                    instructionText.font = existingText.font;
                    instructionText.fontSize = existingText.fontSize;
                }
                else
                {
                    instructionText.fontSize = 16;
                }
                
                instructionText.text = "Press ESC or B button to cancel";
                instructionText.color = UnityEngine.Color.white;
                instructionText.alignment = TextAnchor.MiddleCenter;
                instructionText.raycastTarget = false; // Don't block clicks
                
                // Ensure text is visible - make it slightly larger and ensure it's on top
                if (instructionText.fontSize < 14)
                {
                    instructionText.fontSize = 14;
                }
                
                // Ensure the GameObject is active and visible
                instructionObj.SetActive(true);
                CanvasGroup cg = instructionObj.GetComponent<CanvasGroup>();
                if (cg == null)
                {
                    cg = instructionObj.AddComponent<CanvasGroup>();
                }
                cg.alpha = 1f;
                cg.interactable = false;
                cg.blocksRaycasts = false;

                instructionObj.transform.SetAsLastSibling();

                // Store reference
                _cancelInstructionText = instructionText;

                Logger.Log($"Cancel instruction text created successfully at position {instructionRect.anchoredPosition}, size {instructionRect.sizeDelta}", "MusicNetworkManager");
                Debug.Log($"[MusicNetworkManager]: Cancel instruction text created at {instructionRect.anchoredPosition}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to create cancel instruction text: {ex}", "MusicNetworkManager");
            }
        }

        private void CreateCancelButton()
        {
            // Don't create if already exists
            if (_cancelButton != null)
            {
                return;
            }

            if (briefingUI == null)
            {
                Logger.LogWarn("Cannot create cancel button - briefingUI is null", "MusicNetworkManager");
                return;
            }

            if (briefingUI.updatingContentObj == null)
            {
                Logger.LogWarn("Cannot create cancel button - updatingContentObj is null", "MusicNetworkManager");
                return;
            }

            try
            {
                // Find existing text component in updatingContentObj to copy font settings
                Text existingText = briefingUI.updatingContentObj.GetComponentInChildren<Text>(includeInactive: true);
                
                // Create button GameObject
                GameObject cancelButtonObj = new GameObject("CancelDownloadButton");
                cancelButtonObj.SetActive(true); // Ensure it's active
                RectTransform buttonRect = cancelButtonObj.AddComponent<RectTransform>();
                
                Transform parentTransform = briefingUI.updatingContentObj.transform;
                cancelButtonObj.transform.SetParent(parentTransform, false);
                
                // Move button to end of sibling list so it renders on top
                cancelButtonObj.transform.SetAsLastSibling();
                
                // Ensure button is on the same layer as parent
                cancelButtonObj.layer = parentTransform.gameObject.layer;

                // Get the RectTransform of the parent to understand its size
                RectTransform parentRect = parentTransform as RectTransform;
                float parentHeight = parentRect != null ? parentRect.rect.height : 200f;
                
                // Configure RectTransform - position below the progress text/content
                buttonRect.sizeDelta = new Vector2(150, 50);
                // Anchor to center horizontally, bottom vertically
                buttonRect.anchorMin = new Vector2(0.5f, 0f);
                buttonRect.anchorMax = new Vector2(0.5f, 0f);
                buttonRect.pivot = new Vector2(0.5f, 0f);
                // Position above bottom edge of modal (not screen bottom)
                // Place it ~80px from bottom of modal content area
                buttonRect.anchoredPosition = new Vector2(0, 80);

                var btnLayoutEl = cancelButtonObj.AddComponent<LayoutElement>();
                btnLayoutEl.ignoreLayout = true;

                // Add Image component for button background
                UnityEngine.UI.Image buttonImage = cancelButtonObj.AddComponent<UnityEngine.UI.Image>();
                buttonImage.color = UnityEngine.Color.red;
                buttonImage.raycastTarget = true; // Ensure it can receive clicks

                // Add Button component
                Button button = cancelButtonObj.AddComponent<Button>();
                button.targetGraphic = buttonImage;
                button.interactable = true; // Ensure button is interactable
                
                // Configure button navigation (important for VR)
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                
                // Try to find an existing button to copy settings from
                Button existingButton = briefingUI.updatingContentObj.GetComponentInParent<Button>();
                if (existingButton != null)
                {
                    // Copy transition settings if available
                    button.transition = existingButton.transition;
                    button.colors = existingButton.colors;
                }
                
                // Ensure the button is part of the Canvas hierarchy for VR interaction
                // The Canvas should already have GraphicRaycaster for VR pointer interaction
                Canvas canvas = briefingUI.canvasTf != null ? briefingUI.canvasTf.GetComponent<Canvas>() : null;
                if (canvas == null)
                {
                    // Try to find canvas in parent hierarchy
                    canvas = briefingUI.updatingContentObj.GetComponentInParent<Canvas>();
                }
                if (canvas != null)
                {
                    // Ensure GraphicRaycaster exists for VR interaction
                    if (canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
                    {
                        canvas.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                    }
                }
                
                // Double-check CanvasGroup settings - ensure it's not blocking
                var cg = briefingUI.updatingContentObj.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    cg.interactable = true;
                    cg.blocksRaycasts = true;
                    cg.ignoreParentGroups = false;
                }

                // Create child GameObject for text (standard Unity UI button structure)
                GameObject textObj = new GameObject("Text");
                RectTransform textRect = textObj.AddComponent<RectTransform>();
                textObj.transform.SetParent(cancelButtonObj.transform, false);
                
                // Configure text RectTransform to fill button
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.sizeDelta = Vector2.zero;
                textRect.anchoredPosition = Vector2.zero;

                // Add Text component to child
                Text buttonText = textObj.AddComponent<Text>();
                
                // Copy font settings from existing text if available
                if (existingText != null)
                {
                    buttonText.font = existingText.font;
                }
                
                buttonText.text = "Cancel";
                buttonText.color = UnityEngine.Color.white;
                buttonText.fontSize = 20;
                buttonText.alignment = TextAnchor.MiddleCenter;
                buttonText.raycastTarget = false; // Text shouldn't block clicks

                // Add click handler with logging for debugging
                button.onClick.AddListener(() => 
                { 
                    Logger.Log("Cancel button clicked!", "MusicNetworkManager");
                    Debug.Log("[MusicNetworkManager]: Cancel button clicked!");
                    CancelDownload(); 
                });
                
                // Test: Also add a pointer enter/exit handler to verify interaction
                // This helps debug if the button is receiving pointer events
                var eventTrigger = cancelButtonObj.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                var pointerEnter = new UnityEngine.EventSystems.EventTrigger.Entry();
                pointerEnter.eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter;
                pointerEnter.callback.AddListener((data) => { Logger.Log("Button pointer enter", "MusicNetworkManager"); });
                eventTrigger.triggers.Add(pointerEnter);
                
                var pointerClick = new UnityEngine.EventSystems.EventTrigger.Entry();
                pointerClick.eventID = UnityEngine.EventSystems.EventTriggerType.PointerClick;
                pointerClick.callback.AddListener((data) => 
                { 
                    Logger.Log("Button pointer click detected via EventTrigger", "MusicNetworkManager");
                    CancelDownload(); 
                });
                eventTrigger.triggers.Add(pointerClick);

                // Store reference
                _cancelButton = cancelButtonObj;

                Logger.Log("Cancel button created successfully", "MusicNetworkManager");
                Debug.Log("[MusicNetworkManager]: Cancel button created successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to create cancel button: {ex}", "MusicNetworkManager");
                Debug.LogError($"[MusicNetworkManager]: Failed to create cancel button: {ex}");
            }
        }

        // === MusicSenderSocket ===
        private class MusicSenderSocket : SocketManager
        {
            private const float AckTimeoutSeconds = 1f;

            private struct FileSendInfo
            {
                public ulong TargetSteamId;
                public string FileName;
                public string FilePath;
                public byte[] FileBytes;
                public int TotalChunks;
                public int SentChunks;
                public int FileHash;
                public int FileSize;
            }

            private readonly Queue<FileSendInfo> _filesToSend = new();
            private readonly Dictionary<ulong, Connection> _connections = new();
            private readonly Dictionary<ulong, bool> _fileCountSent = new();
            private readonly Dictionary<ulong, bool> _hashListSent = new();
            private readonly Dictionary<ulong, HashSet<string>> _filesToSendAfterHashCheck = new();
            // Backup of original file list to rebuild _filesToSend on reconnection
            private readonly List<FileSendInfo> _originalFilesList = new();
            // Store original file paths per target SteamId (from manager level)
            private readonly Dictionary<ulong, string[]> _originalFilePathsPerTarget = new Dictionary<ulong, string[]>();
            // Reference to parent manager to access original files list
            private MusicNetworkManager _parentManager;

            /// <summary>
            /// Sets the parent manager reference so socket can access manager's original files list
            /// </summary>
            public void SetParentManager(MusicNetworkManager manager)
            {
                _parentManager = manager;
            }

            /// <summary>
            /// Sets the original file paths for a target SteamId. This is called by the manager
            /// to ensure we can rebuild the queue with ALL original files on reconnection.
            /// </summary>
            public void SetOriginalFilesForTarget(ulong targetSteamId, string[] filePaths)
            {
                _originalFilePathsPerTarget[targetSteamId] = filePaths;
                Logger.Log($"Set original files for target {targetSteamId}: {filePaths.Length} file(s)", "MusicSenderSocket");
            }
            
            /// <summary>
            /// Gets the original file paths for a target, checking both socket's storage and manager's storage
            /// </summary>
            private string[] GetOriginalFilesForTarget(ulong targetSteamId)
            {
                // First check socket's own storage
                if (_originalFilePathsPerTarget.TryGetValue(targetSteamId, out var socketFiles))
                {
                    return socketFiles;
                }
                
                // Fallback to manager's storage if available
                if (_parentManager != null && _parentManager._originalFilesPerTarget.TryGetValue(targetSteamId, out var managerFiles))
                {
                    Logger.Log($"Retrieved original files from manager for target {targetSteamId}: {managerFiles.Length} file(s)", "MusicSenderSocket");
                    // Also store in socket for future use
                    _originalFilePathsPerTarget[targetSteamId] = managerFiles;
                    return managerFiles;
                }
                
                return null;
            }

            private int _waitingForAckChunkIndex = -1;
            private FileSendInfo _currentFile;
            private float _ackTimer = 0f;
            private byte[] _lastChunkData;

            private int _totalFilesCount = 0;
            
            private static int ComputeFullFileHash(string filePath)
            {
                try
                {
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        byte[] buffer = new byte[64 * 1024]; // 64KB chunks
                        int totalRead = 0;
                        uint crc = 0xFFFFFFFFu;
                        
                        int read;
                        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            for (int i = 0; i < read; i++)
                            {
                                crc ^= buffer[i];
                                for (int b = 0; b < 8; b++)
                                {
                                    uint mask = (crc & 1) != 0 ? 0xEDB88320u : 0u;
                                    crc = (crc >> 1) ^ mask;
                                }
                            }
                            totalRead += read;
                        }
                        return (int)~crc;
                    }
                }
                catch { return 0; }
            }

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

                var fileName = Path.GetFileName(filePath);
                var fileInfo = new FileInfo(filePath);
                int fileSize = (int)fileInfo.Length;
                int fileHash = ComputeFullFileHash(filePath);

                Logger.Log($"Queuing file '{fileName}' (hash={fileHash}, size={fileSize}) for hash check to {targetSteamId}", "MusicSenderSocket");
                Debug.Log($"[MusicSenderSocket]: Queuing file '{fileName}' (hash={fileHash}, size={fileSize}) for hash check to {targetSteamId}");

                var sendInfo = new FileSendInfo
                {
                    TargetSteamId = targetSteamId,
                    FileName = fileName,
                    FilePath = filePath,
                    FileBytes = null, // Will load when needed
                    TotalChunks = 0, // Will calculate when needed
                    SentChunks = 0,
                    FileHash = fileHash,
                    FileSize = fileSize
                };

                _filesToSend.Enqueue(sendInfo);
                _totalFilesCount++;
                
                // Keep backup of original file list for reconnection
                bool alreadyExists = false;
                foreach (var existing in _originalFilesList)
                {
                    if (existing.FilePath == filePath && existing.TargetSteamId == targetSteamId)
                    {
                        alreadyExists = true;
                        break;
                    }
                }
                if (!alreadyExists)
                {
                    _originalFilesList.Add(sendInfo);
                }
            }
            
            private void SendHashList(Connection connection, ulong targetSteamId)
            {
                if (!_connections.TryGetValue(targetSteamId, out var conn))
                    return;
                    
                Logger.Log($"Sending hash list for {_filesToSend.Count} file(s) to {targetSteamId}", "MusicSenderSocket");
                
                using var hashMs = new MemoryStream();
                using var writer = new BinaryWriter(hashMs);
                writer.Write("HASH_LIST");
                writer.Write(_filesToSend.Count);
                
                foreach (var fileInfo in _filesToSend)
                {
                    writer.Write(fileInfo.FileName);
                    writer.Write(fileInfo.FileSize);
                    writer.Write(fileInfo.FileHash);
                }
                writer.Flush();
                
                conn.SendMessage(hashMs.ToArray(), SendType.Reliable);
                Logger.Log($"Sent hash list to {targetSteamId}", "MusicSenderSocket");
            }

            private void SendFileCount(Connection connection, ulong targetSteamId)
            {
                // Only send file count after hash check is complete
                if (!_filesToSendAfterHashCheck.ContainsKey(targetSteamId))
                    return;
                    
                int fileCount = _filesToSendAfterHashCheck[targetSteamId].Count;

                using var countMs = new MemoryStream();
                using var writer = new BinaryWriter(countMs);
                writer.Write("FILE_COUNT");
                writer.Write(fileCount);
                writer.Flush();

                connection.SendMessage(countMs.ToArray(), SendType.Reliable);
                Logger.Log($"Sent FILE_COUNT={fileCount} to {targetSteamId}", "MusicSenderSocket");
                Debug.Log($"[MusicSenderSocket]: Sent FILE_COUNT={fileCount} to {targetSteamId}");
            }

            public override void OnConnectionChanged(Connection connection, ConnectionInfo info)
            {
                if (info.State == ConnectionState.Connected)
                {
                    Logger.Log($"Connected to {info.Identity.SteamId}", "MusicSenderSocket");
                    Debug.Log($"[MusicSenderSocket]: Connected to {info.Identity.SteamId}");
                    _connections[info.Identity.SteamId] = connection;

                    // First send hash list, wait for response
                    if (!_hashListSent.TryGetValue(info.Identity.SteamId, out bool hashSent) || !hashSent)
                    {
                        // ALWAYS rebuild _filesToSend from original file list when sending hash list
                        // This ensures we offer ALL files, not just the filtered ones from previous connection
                        // Use original file paths from manager level to ensure ALL files are included
                        Logger.Log($"Preparing to send hash list - current queue has {_filesToSend.Count} file(s), checking for original file list...", "MusicSenderSocket");
                        
                        var originalFilePaths = GetOriginalFilesForTarget(info.Identity.SteamId);
                        if (originalFilePaths != null && originalFilePaths.Length > 0)
                        {
                            Logger.Log($"Rebuilding file queue from original file list for {info.Identity.SteamId} ({originalFilePaths.Length} file(s))", "MusicSenderSocket");
                            // Clear existing queue (may contain filtered files from previous connection)
                            _filesToSend.Clear();
                            // Re-queue ALL original files - receiver will determine which ones it needs
                            int queuedCount = 0;
                            foreach (var filePath in originalFilePaths)
                            {
                                if (File.Exists(filePath))
                                {
                                    QueueFileForSending(filePath, info.Identity.SteamId);
                                    queuedCount++;
                                }
                                else
                                {
                                    Logger.LogWarn($"Original file no longer exists: {filePath}, skipping", "MusicSenderSocket");
                                }
                            }
                            Logger.Log($"Queue rebuilt with {queuedCount} file(s) from original list of {originalFilePaths.Length}", "MusicSenderSocket");
                        }
                        else if (_originalFilesList.Count > 0)
                        {
                            // Fallback to socket's backup list if manager-level list not available
                            Logger.Log($"Rebuilding file queue from socket backup for {info.Identity.SteamId} ({_originalFilesList.Count} file(s))", "MusicSenderSocket");
                            _filesToSend.Clear();
                            int addedCount = 0;
                            foreach (var fileInfo in _originalFilesList)
                            {
                                if (fileInfo.TargetSteamId == info.Identity.SteamId)
                                {
                                    // Create fresh copy without file bytes (will be loaded when needed)
                                    var freshFileInfo = new FileSendInfo
                                    {
                                        TargetSteamId = fileInfo.TargetSteamId,
                                        FileName = fileInfo.FileName,
                                        FilePath = fileInfo.FilePath,
                                        FileBytes = null,
                                        TotalChunks = 0,
                                        SentChunks = 0,
                                        FileHash = fileInfo.FileHash,
                                        FileSize = fileInfo.FileSize
                                    };
                                    _filesToSend.Enqueue(freshFileInfo);
                                    addedCount++;
                                }
                            }
                            Logger.Log($"Queue rebuilt with {addedCount} file(s) from socket backup list", "MusicSenderSocket");
                        }
                        else
                        {
                            Logger.LogWarn($"No original file list found for {info.Identity.SteamId} - cannot rebuild queue! Current queue has {_filesToSend.Count} file(s). Manager={_parentManager != null}", "MusicSenderSocket");
                        }
                        
                        Logger.Log($"Sending HASH_LIST to {info.Identity.SteamId}", "MusicSenderSocket");
                        SendHashList(connection, info.Identity.SteamId);
                        _hashListSent[info.Identity.SteamId] = true;
                    }
                    else if (_filesToSendAfterHashCheck.ContainsKey(info.Identity.SteamId))
                    {
                        // Hash check complete, send file count and start sending files
                        if (!_fileCountSent.TryGetValue(info.Identity.SteamId, out bool sent) || !sent)
                        {
                            Logger.Log($"Sending FILE_COUNT to {info.Identity.SteamId}", "MusicSenderSocket");
                            SendFileCount(connection, info.Identity.SteamId);
                            _fileCountSent[info.Identity.SteamId] = true;
                        }
                        TrySendNextFile();
                    }
                }
                else if (info.State == ConnectionState.ClosedByPeer || info.State == ConnectionState.ProblemDetectedLocally)
                {
                    Logger.Log($"Disconnected from {info.Identity.SteamId}", "MusicSenderSocket");
                    Debug.Log($"[MusicSenderSocket]: Disconnected from {info.Identity.SteamId}");
                    _connections.Remove(info.Identity.SteamId);
                    _fileCountSent.Remove(info.Identity.SteamId);
                    _hashListSent.Remove(info.Identity.SteamId);
                    _filesToSendAfterHashCheck.Remove(info.Identity.SteamId);
                    
                    // Clear current file being sent if it was for this connection
                    if (_currentFile.TargetSteamId == info.Identity.SteamId)
                    {
                        _waitingForAckChunkIndex = -1;
                        _ackTimer = 0f;
                        _lastChunkData = null;
                        // Don't re-queue current file - we'll rebuild from original list on reconnection
                        // This ensures we always offer ALL files, not just partially sent ones
                    }
                    
                    // Clear filtered queue for this target - will be rebuilt from original list on reconnection
                    // This ensures we always offer ALL files in hash list, not just filtered ones
                    // Remove all files for this target from the queue
                    var tempQueue = new Queue<FileSendInfo>();
                    while (_filesToSend.Count > 0)
                    {
                        var item = _filesToSend.Dequeue();
                        if (item.TargetSteamId != info.Identity.SteamId)
                        {
                            tempQueue.Enqueue(item);
                        }
                    }
                    // Restore queue with only files for other targets
                    while (tempQueue.Count > 0)
                    {
                        _filesToSend.Enqueue(tempQueue.Dequeue());
                    }
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
                        Logger.Log($"Finished sending file {_currentFile.FileName}", "MusicSenderSocket");
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
                else if (messageType == "NEED_FILES")
                {
                    // Receiver sent list of files they need
                    int needCount = reader.ReadInt32();
                    var neededFiles = new HashSet<string>();
                    for (int i = 0; i < needCount; i++)
                    {
                        neededFiles.Add(reader.ReadString());
                    }
                    
                    Logger.Log($"Receiver needs {needCount} file(s) out of {_filesToSend.Count} total", "MusicSenderSocket");
                    
                    // Store which files to send
                    _filesToSendAfterHashCheck[identity.SteamId] = neededFiles;
                    
                    // Load file bytes only for files that need to be sent
                    var filesToKeep = new Queue<FileSendInfo>();
                    foreach (var fileInfo in _filesToSend)
                    {
                        if (neededFiles.Contains(fileInfo.FileName))
                        {
                            // Load file bytes now
                            var fileBytes = File.ReadAllBytes(fileInfo.FilePath);
                            // Create new struct instance with updated values
                            var updatedFileInfo = new FileSendInfo
                            {
                                TargetSteamId = fileInfo.TargetSteamId,
                                FileName = fileInfo.FileName,
                                FilePath = fileInfo.FilePath,
                                FileBytes = fileBytes,
                                TotalChunks = (int)Math.Ceiling(fileBytes.Length / (double)ChunkSize),
                                SentChunks = 0,
                                FileHash = fileInfo.FileHash,
                                FileSize = fileInfo.FileSize
                            };
                            filesToKeep.Enqueue(updatedFileInfo);
                            Logger.Log($"Will send file '{fileInfo.FileName}' (hash={fileInfo.FileHash})", "MusicSenderSocket");
                        }
                        else
                        {
                            Logger.Log($"Skipping file '{fileInfo.FileName}' - receiver already has it (hash={fileInfo.FileHash})", "MusicSenderSocket");
                        }
                    }
                    _filesToSend.Clear();
                    foreach (var fileInfo in filesToKeep)
                    {
                        _filesToSend.Enqueue(fileInfo);
                    }
                    
                    // Now send file count (even if 0) and start sending if needed
                    if (_connections.TryGetValue(identity.SteamId, out var conn))
                    {
                        SendFileCount(conn, identity.SteamId);
                        _fileCountSent[identity.SteamId] = true;
                        if (_filesToSend.Count > 0)
                        {
                            TrySendNextFile();
                        }
                        else
                        {
                            Logger.Log("No files to send - receiver already has all files", "MusicSenderSocket");
                        }
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
                
                // Ensure file bytes are loaded
                if (_currentFile.FileBytes == null && !string.IsNullOrEmpty(_currentFile.FilePath))
                {
                    _currentFile.FileBytes = File.ReadAllBytes(_currentFile.FilePath);
                    _currentFile.TotalChunks = (int)Math.Ceiling(_currentFile.FileBytes.Length / (double)ChunkSize);
                }
                
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
                    Logger.LogDebug($"Sent chunk {chunkIndex + 1} / {_currentFile.TotalChunks} for file {_currentFile.FileName}", "MusicSenderSocket");
                    Debug.Log($"[MusicSenderSocket]: Sent chunk {chunkIndex + 1} / {_currentFile.TotalChunks} for file {_currentFile.FileName}");
                    _waitingForAckChunkIndex = chunkIndex;
                    _ackTimer = 0f;
                }
                else
                {
                    Logger.LogError("Failed to send chunk!", "MusicSenderSocket");
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
            private MusicNetworkManager _parentManager;

            public bool _isAllFilesReceived = false;

            private int _expectedFileCount = -1;
            private int _completedFilesCount = 0;

            public int TotalExpectedChunks { get; private set; } = 0;
            public int ReceivedChunks { get; private set; } = 0;

            public void SetParentManager(MusicNetworkManager manager)
            {
                _parentManager = manager;
            }
            
            private static int ComputeFullFileHash(string filePath)
            {
                try
                {
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        byte[] buffer = new byte[64 * 1024]; // 64KB chunks
                        uint crc = 0xFFFFFFFFu;
                        
                        int read;
                        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            for (int i = 0; i < read; i++)
                            {
                                crc ^= buffer[i];
                                for (int b = 0; b < 8; b++)
                                {
                                    uint mask = (crc & 1) != 0 ? 0xEDB88320u : 0u;
                                    crc = (crc >> 1) ^ mask;
                                }
                            }
                        }
                        return (int)~crc;
                    }
                }
                catch { return 0; }
            }

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
                Logger.Log("Connected to host", "MusicReceiverClient");
                Debug.Log("[MusicReceiverClient]: Connected to host.");
                _hostConnection = Connection;
                
                // If we have pending files from a previous connection, log it
                if (_receivingFiles.Count > 0)
                {
                    Logger.Log($"Reconnected - {_receivingFiles.Count} file(s) still in progress, {_completedFilesCount}/{_expectedFileCount} completed", "MusicReceiverClient");
                }

                base.OnConnected(info);
            }


            public override void OnDisconnected(ConnectionInfo info)
            {
                Logger.Log("Disconnected from host", "MusicReceiverClient");
                Debug.Log("[MusicReceiverClient]: Disconnected from host.");
                
                // Don't clear receiving files on disconnect - they might reconnect and continue
                // Only clear if we've already received all files
                if (_isAllFilesReceived)
                {
                    _receivingFiles.Clear();
                    Logger.Log("Clearing receiving files - all files already received", "MusicReceiverClient");
                }
                else
                {
                    Logger.Log($"Connection lost but files still pending ({_completedFilesCount}/{_expectedFileCount}) - keeping receiving files for potential reconnection", "MusicReceiverClient");
                }

                base.OnDisconnected(info);
            }

            public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
            {
                byte[] managedData = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(data, managedData, 0, size);

                using var ms = new MemoryStream(managedData);
                using var reader = new BinaryReader(ms);

                string messageType = reader.ReadString();

                if (messageType == "HASH_LIST")
                {
                    int fileCount = reader.ReadInt32();
                    Logger.Log($"Received hash list for {fileCount} file(s), comparing with local files...", "MusicReceiverClient");
                    
                    // Store expected file names for tracking
                    if (_parentManager != null)
                    {
                        _parentManager._expectedFileNames.Clear();
                    }
                    
                    var neededFiles = new List<string>();
                    string sharedPath = Path.Combine(VTResources.gameRootDirectory, "SharedRadioMusic");
                    string localRadioPath = GameSettings.RADIO_MUSIC_PATH;
                    
                    for (int i = 0; i < fileCount; i++)
                    {
                        string fileName = reader.ReadString();
                        int remoteSize = reader.ReadInt32();
                        int remoteHash = reader.ReadInt32();
                        
                        // Store expected file name for tracking
                        if (_parentManager != null && !_parentManager._expectedFileNames.Contains(fileName))
                        {
                            _parentManager._expectedFileNames.Add(fileName);
                        }
                        
                        bool needsFile = true;
                        string localFilePath = null;
                        
                        // Check SharedRadioMusic first, then local RadioMusic
                        string sharedFilePath = Path.Combine(sharedPath, fileName);
                        string localRadioFilePath = Path.Combine(localRadioPath, fileName);
                        
                        if (File.Exists(sharedFilePath))
                        {
                            localFilePath = sharedFilePath;
                        }
                        else if (File.Exists(localRadioFilePath))
                        {
                            localFilePath = localRadioFilePath;
                        }
                        
                        if (localFilePath != null)
                        {
                            try
                            {
                                var fileInfo = new FileInfo(localFilePath);
                                if (fileInfo.Length == remoteSize)
                                {
                                    int localHash = ComputeFullFileHash(localFilePath);
                                    if (localHash == remoteHash)
                                    {
                                        needsFile = false;
                                        Logger.Log($"File '{fileName}' already exists and matches (hash={localHash})", "MusicReceiverClient");
                                        
                                        // Copy to SharedRadioMusic if it's in local RadioMusic
                                        if (localFilePath == localRadioFilePath)
                                        {
                                            try
                                            {
                                                Directory.CreateDirectory(sharedPath);
                                                File.Copy(localFilePath, sharedFilePath, true);
                                                Logger.Log($"Copied '{fileName}' from local RadioMusic to SharedRadioMusic", "MusicReceiverClient");
                                            }
                                            catch (Exception ex)
                                            {
                                                Logger.LogWarn($"Failed to copy '{fileName}' to SharedRadioMusic: {ex.Message}", "MusicReceiverClient");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        Logger.Log($"File '{fileName}' exists but hash mismatch (local={localHash}, remote={remoteHash})", "MusicReceiverClient");
                                    }
                                }
                                else
                                {
                                    Logger.Log($"File '{fileName}' exists but size mismatch (local={fileInfo.Length}, remote={remoteSize})", "MusicReceiverClient");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarn($"Error checking file '{fileName}': {ex.Message}", "MusicReceiverClient");
                            }
                        }
                        else
                        {
                            Logger.Log($"File '{fileName}' not found locally, will download", "MusicReceiverClient");
                        }
                        
                        if (needsFile)
                        {
                            neededFiles.Add(fileName);
                        }
                    }
                    
                    // Send back list of files we need
                    using var needMs = new MemoryStream();
                    using var needWriter = new BinaryWriter(needMs);
                    needWriter.Write("NEED_FILES");
                    needWriter.Write(neededFiles.Count);
                    foreach (var fileName in neededFiles)
                    {
                        needWriter.Write(fileName);
                    }
                    needWriter.Flush();
                    
                    _hostConnection.SendMessage(needMs.ToArray(), SendType.Reliable);
                    Logger.Log($"Sent NEED_FILES response: need {neededFiles.Count} out of {fileCount} file(s)", "MusicReceiverClient");
                    
                    // If no files needed, mark as complete
                    if (neededFiles.Count == 0)
                    {
                        _isAllFilesReceived = true;
                        Logger.Log("All files already exist locally, no download needed", "MusicReceiverClient");
                    }
                }
                else if (messageType == "FILE_COUNT")
                {
                    _expectedFileCount = reader.ReadInt32();
                    Logger.Log($"Sender will send {_expectedFileCount} file(s)", "MusicReceiverClient");
                    Debug.Log($"[MusicReceiverClient]: Sender will send {_expectedFileCount} file(s)");
                    
                    // If 0 files, mark as complete immediately
                    if (_expectedFileCount == 0)
                    {
                        _isAllFilesReceived = true;
                        Logger.Log("No files to receive - all files already exist locally", "MusicReceiverClient");
                    }
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
                        Directory.CreateDirectory(directoryPath); // Ensure directory exists

                        string savePath = Path.Combine(directoryPath, fileName);
                        
                        // Delete existing file if it exists (we're replacing it)
                        if (File.Exists(savePath))
                        {
                            try
                            {
                                File.Delete(savePath);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarn($"Failed to delete existing file '{fileName}': {ex.Message}", "MusicReceiverClient");
                            }
                        } 

                        using var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write);
                        for (int i = 0; i < totalChunks; i++)
                        {
                            fs.Write(fileInfo.Chunks[i], 0, fileInfo.Chunks[i].Length);
                        }

                        Logger.Log($"Saved received file to: {savePath}", "MusicReceiverClient");
                        Debug.Log($"[MusicReceiverClient]: Saved received file to: {savePath}");

                        // Track downloaded file for cancellation cleanup
                        if (_parentManager != null && !_parentManager._downloadedFilesInSession.Contains(fileName))
                        {
                            _parentManager._downloadedFilesInSession.Add(fileName);
                        }

                        _receivingFiles.Remove(fileName);
                        _completedFilesCount++;

                        // If all files were received successfully - _isAllFilesReceived = true;
                        Logger.Log($"File completed: {fileName}, CompletedFileCount={_completedFilesCount}, ExpectedFileCount={_expectedFileCount}", "MusicReceiverClient");
                        if (_expectedFileCount >= 0 && _completedFilesCount >= _expectedFileCount)
                        {
                            _isAllFilesReceived = true;
                            Logger.Log("All files received successfully", "MusicReceiverClient");
                            Debug.Log("[MusicReceiverClient]: All files received.");
                            
                            // Immediately set the ready flag when all files are received
                            // This ensures entry permission even if coroutine is interrupted
                            if (_parentManager != null && !_parentManager._isDownloadCancelled)
                            {
                                SharedMusicState.IsCopilotReadyToEnter = true;
                                Logger.Log("Set IsCopilotReadyToEnter = true (from MusicReceiverClient)", "MusicReceiverClient");
                            }
                        }
                        else
                        {
                            Logger.Log($"Not all files received yet: {_completedFilesCount}/{_expectedFileCount}", "MusicReceiverClient");
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