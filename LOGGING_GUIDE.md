# In-Game Logging System Guide

## Overview

The mod now includes an in-game terminal-style logger similar to SteamQueries.exe. This allows you to see logs in real-time while playing, making multiplayer debugging much easier.

## Features

- **In-Game Console**: Press **F8** to toggle the console on/off
- **Terminal-Style Format**: Logs display with timestamps and log levels (INF, DBG, WRN, ERR)
- **Color-Coded**: Different log levels have different colors for easy identification
- **Auto-Scrolling**: Console automatically scrolls to show the latest logs
- **Memory Efficient**: Limited to 500 log entries to prevent memory issues

## Log Levels

- **DBG** (Debug) - Gray - Detailed debugging information
- **INF** (Info) - White - General information
- **WRN** (Warn) - Orange - Warning messages
- **ERR** (Error) - Red - Error messages

## Usage

### Basic Logging

```csharp
// Using the global Logger (recommended)
using static SharedMusicPlayer.Logger;

Log("This is an info message");
LogDebug("This is a debug message");
LogWarn("This is a warning");
LogError("This is an error");
```

### With Category

```csharp
// Specify a category for better organization
Log("Connection established", "MusicNetworkManager");
LogError("Failed to send file", "MusicSenderSocket");
```

### Direct InGameLogger Usage

```csharp
// For more control
InGameLogger.AddLog(LogLevel.Info, "Category", "Message");
InGameLogger.AddLog(LogLevel.Debug, "Category", "Message");
InGameLogger.AddLog(LogLevel.Warn, "Category", "Message");
InGameLogger.AddLog(LogLevel.Error, "Category", "Message");
```

## Console Controls

- **F8**: Toggle console visibility
- Console appears in the bottom half of the screen
- Automatically scrolls to show latest logs
- Can be toggled on/off at any time during gameplay

## Migration from Debug.Log

Replace existing Debug.Log calls:

**Before:**
```csharp
Debug.Log("[MusicNetworkManager]: Sender started");
Debug.LogWarning("[MusicNetworkManager]: Connection failed");
Debug.LogError("[MusicNetworkManager]: Critical error occurred");
```

**After:**
```csharp
Logger.Log("Sender started", "MusicNetworkManager");
Logger.LogWarn("Connection failed", "MusicNetworkManager");
Logger.LogError("Critical error occurred", "MusicNetworkManager");
```

Or using the global using:
```csharp
Log("Sender started", "MusicNetworkManager");
LogWarn("Connection failed", "MusicNetworkManager");
LogError("Critical error occurred", "MusicNetworkManager");
```

## Format

Logs are displayed in the format:
```
[HH:mm:ss.fff LEVEL] Category: Message
```

Example:
```
[17:29:24.123 INF] MusicNetworkManager: Sender started for SteamID 76561198373327162
[17:29:25.456 WRN] MusicNetworkManager: Connection retry attempt 2
[17:29:26.789 ERR] MusicNetworkManager: Failed to create socket after 5 attempts
```

## Notes

- The logger automatically falls back to Unity's Debug.Log if the console UI fails to initialize
- Logs are stored in memory (max 500 entries) - older logs are automatically removed
- The console is hidden by default - press F8 to show it
- Console persists across scene changes (DontDestroyOnLoad)
