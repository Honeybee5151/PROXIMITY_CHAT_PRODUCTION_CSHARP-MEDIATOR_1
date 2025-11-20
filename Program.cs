using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Concurrent;
using NAudio.Lame;
using System.IO;
using Concentus.Structs;
using Concentus.Enums;
using System.Net;
using System.Collections.Generic;

namespace ConsoleApp1
{
 // Data structures for communication
 public class MicrophoneInfo
 {
     public string Id { get; set; }
     public string Name { get; set; }
     public string Description { get; set; }
     public bool IsDefault { get; set; }
     public bool IsEnabled { get; set; }
 }

 public class AudioData
 {
     public float Level { get; set; }
     public float Peak { get; set; }
     public bool IsActive { get; set; }
     public DateTime Timestamp { get; set; }
 }

 public class OpusAudioProcessor
 {
     // REQUIRED: Field declarations at class level
     private OpusEncoder encoder;
     private OpusDecoder decoder;
     private const int SAMPLE_RATE = 48000; // Opus works best at 48kHz
     private const int CHANNELS = 1;
     private const int FRAME_SIZE = 960; // 20ms at 48kHz

     public OpusAudioProcessor()
     {
         try
         {
             encoder = new OpusEncoder(SAMPLE_RATE, CHANNELS, OpusApplication.OPUS_APPLICATION_VOIP);
             decoder = new OpusDecoder(SAMPLE_RATE, CHANNELS);

             // Only set properties that actually exist
             encoder.Bitrate = 24000;
             encoder.Complexity = 5;
      
             Console.Error.WriteLine("OpusAudioProcessor initialized successfully");
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"Failed to initialize Opus: {ex.Message}");
             encoder = null;
             decoder = null;
         }
     }
  
     public byte[] EncodeToOpus(byte[] rawPcmData)
     {
         if (encoder == null)
         {
             Console.Error.WriteLine("Opus encoder not available, returning raw PCM");
             return rawPcmData;
         }

         try
         {
             // Convert byte array to short array (16-bit PCM)
             short[] pcmSamples = new short[rawPcmData.Length / 2];
             Buffer.BlockCopy(rawPcmData, 0, pcmSamples, 0, rawPcmData.Length);

             // Opus output buffer
             byte[] opusOutput = new byte[4000]; // Max Opus packet size

             // CORRECTED: Use proper Encode method signature
             int encodedBytes = encoder.Encode(pcmSamples, 0, pcmSamples.Length, opusOutput, 0, opusOutput.Length);

             if (encodedBytes < 0)
             {
                 Console.Error.WriteLine($"Opus encoding failed with error code: {encodedBytes}");
                 return rawPcmData;
             }

             // Return only the used bytes
             byte[] result = new byte[encodedBytes];
             Array.Copy(opusOutput, 0, result, 0, encodedBytes);

             Console.Error.WriteLine($"Opus: Encoded {rawPcmData.Length} PCM bytes → {encodedBytes} Opus bytes");
             return result;
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"Opus encoding error: {ex.Message}");
             return rawPcmData; // Fallback to raw PCM
         }
     }

     public byte[] DecodeFromOpus(byte[] opusData)
     {
         if (decoder == null)
         {
             Console.Error.WriteLine("Opus decoder not available, returning original data");
             return opusData;
         }

         try
         {
             // Decode buffer (enough for 20ms at 48kHz)
             short[] pcmOutput = new short[FRAME_SIZE * CHANNELS];

             // CORRECTED: Use proper Decode method signature
             int decodedSamples = decoder.Decode(opusData, 0, opusData.Length, pcmOutput, 0, pcmOutput.Length, false);

             if (decodedSamples < 0)
             {
                 Console.Error.WriteLine($"Opus decoding failed with error code: {decodedSamples}");
                 return new byte[0];
             }

             // Convert short array back to byte array
             byte[] result = new byte[decodedSamples * 2]; // 2 bytes per sample
             Buffer.BlockCopy(pcmOutput, 0, result, 0, result.Length);

             Console.Error.WriteLine($"Opus: Decoded {opusData.Length} Opus bytes → {result.Length} PCM bytes");
             return result;
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"Opus decoding error: {ex.Message}");
             return new byte[0]; // Return empty on error
         }
     }
 }

 // UDP-ONLY Proximity Chat Manager
 public class ProximityChatManager : IDisposable
 {
     // Inner classes for UDP communication
     public class UdpAuthRequest
     {
         public string PlayerId { get; set; }
         public string VoiceId { get; set; }
         public string Command { get; set; } = "AUTH";
     }

     public class UdpPriorityCommand
     {
         public string PlayerId { get; set; }
         public string SettingType { get; set; }
         public string Value { get; set; }
         public string Command { get; set; } = "PRIORITY";
     }

     #region Fields

     private WaveInEvent waveIn;
     private MMDeviceEnumerator deviceEnumerator;
     private MMDevice selectedDevice;
     private List<MicrophoneInfo> availableMicrophones;
     private OpusAudioProcessor opusProcessor;
     private VoiceManager voiceManager;
     private DateTime lastAudioSent = DateTime.MinValue;
     private const int AUDIO_SEND_INTERVAL_MS = 20; // Faster for UDP

     private List<short> pcmFrameBuffer = new List<short>(960);  // ← Use List, not Queue!
     private const int OPUS_FRAME_SIZE = 960;
     public bool IsUIActive { get; private set; } = true;
     // Audio processing
     private volatile float currentLevel;
     private volatile float peakLevel;
     private volatile float smoothedLevel;//
     private int audioUpdateCounter = 0;
     private const int AUDIO_UPDATE_SKIP = 50;

     // UDP Communication
     private UdpClient udpClient;
     private IPEndPoint serverEndpoint;
     private volatile bool isConnectedToServer;
     private string serverId;
     private string storedVoiceId;
     private bool isAuthenticated = false;
     
     // Audio queue for UDP
     private readonly ConcurrentQueue<byte[]> outgoingOpusData = new ConcurrentQueue<byte[]>();
     private Task udpSenderTask;
     private readonly CancellationTokenSource udpCancellation = new CancellationTokenSource();

     // ActionScript communication
     private ActionScriptBridge actionScriptBridge;
     
     // Settings
     public bool IsMicrophoneEnabled { get; private set; }
     public string SelectedMicrophoneId { get; private set; }
     public float MicrophoneSensitivity { get; set; } = 1.0f;
     public float NoiseGate { get; set; } = 0.005f; // Lower for better sensitivity

     public bool allowAudioTransmission = true;

     // Connection management
     public event EventHandler<string> ConnectionStatusChanged;
     private DateTime lastConnectionCheck = DateTime.Now;
     private readonly object reconnectionLock = new object();
     private bool isReconnecting = false;
     private string storedServerAddress;
     private int storedPort;

     #endregion

     #region Constructor and Initialization

     public ProximityChatManager()
     {
         deviceEnumerator = new MMDeviceEnumerator();
         availableMicrophones = new List<MicrophoneInfo>();
         actionScriptBridge = new ActionScriptBridge();
         opusProcessor = new OpusAudioProcessor();
         currentLevel = 0f;
         peakLevel = 0f;
         smoothedLevel = 0f;

         RefreshMicrophones();
     }

     #endregion

     #region UDP Communication

     private async Task UdpSenderWorker()
     {
         Console.Error.WriteLine("[UDP_SENDER] Starting UDP sender worker");
      
         while (!udpCancellation.Token.IsCancellationRequested)
         {
             try
             {
                 // Connection health check
                 var now = DateTime.Now;
                 if ((now - lastConnectionCheck).TotalSeconds >= 30)
                 {
                     await SendPingToServer();
                     lastConnectionCheck = now;
                 }

                 // Send audio data
                 if (outgoingOpusData.TryDequeue(out var opusData) && isConnectedToServer && isAuthenticated)
                 {
                     await SendVoiceDataUdp(opusData);
                 }

                 await Task.Delay(5, udpCancellation.Token); // Very frequent for low latency
             }
             catch (Exception ex)
             {
                 Console.Error.WriteLine($"[UDP_SENDER] Error: {ex.Message}");
                 await Task.Delay(100, udpCancellation.Token);
             }
         }
      
         Console.Error.WriteLine("[UDP_SENDER] UDP sender worker stopped");
     }

     private async Task SendVoiceDataUdp(byte[] opusData)
     {
         try
         {
             // NEW: [2 bytes playerId][Opus data]
             ushort playerIdShort = ushort.Parse(serverId);
             byte[] voicePacket = new byte[2 + opusData.Length];
      
             // Player ID (2 bytes)
             byte[] playerIdBytes = BitConverter.GetBytes(playerIdShort);
             Array.Copy(playerIdBytes, 0, voicePacket, 0, 2);
      
             // Opus audio data
             Array.Copy(opusData, 0, voicePacket, 2, opusData.Length);
      
             await udpClient.SendAsync(voicePacket, voicePacket.Length);
      
             Console.Error.WriteLine($"[UDP_VOICE] Sent {opusData.Length} Opus bytes (2-byte header)");
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"[UDP_VOICE] Error: {ex.Message}");
             isConnectedToServer = false;
         }
     }

     private async Task SendPingToServer()
     {
         try
         {
             byte[] pingPacket = Encoding.UTF8.GetBytes("PING");
             await udpClient.SendAsync(pingPacket, pingPacket.Length, serverEndpoint);
             Console.Error.WriteLine("[UDP_PING] Sent ping to server");
             Console.Error.WriteLine($"[DEBUG] Sending PING to server from port {((IPEndPoint)udpClient.Client.LocalEndPoint).Port}");
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"[UDP_PING] Error: {ex.Message}");
         }
     }

     public async Task SendAuthenticationUdp(string playerId, string voiceId)
     {
         try
         {
             Console.Error.WriteLine($"[UDP_AUTH_DEBUG] Starting authentication...");
             Console.Error.WriteLine($"[UDP_AUTH_DEBUG] PlayerID: {playerId}");
             Console.Error.WriteLine($"[UDP_AUTH_DEBUG] VoiceID: {voiceId}");
             Console.Error.WriteLine($"[UDP_AUTH_DEBUG] Server endpoint: {serverEndpoint}");
  
             var authRequest = new UdpAuthRequest
             {
                 PlayerId = playerId,
                 VoiceId = voiceId,
                 Command = "AUTH"
             };

             string jsonData = JsonSerializer.Serialize(authRequest);
             Console.Error.WriteLine($"[UDP_AUTH_DEBUG] JSON: {jsonData}");
  
             byte[] authPacket = Encoding.UTF8.GetBytes("AUTH" + jsonData);
             Console.Error.WriteLine($"[UDP_AUTH_DEBUG] Packet size: {authPacket.Length} bytes");

             await udpClient.SendAsync(authPacket, authPacket.Length, serverEndpoint);
             Console.Error.WriteLine($"[UDP_AUTH_DEBUG] Authentication packet sent successfully!");
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"[UDP_AUTH_DEBUG] ERROR: {ex.Message}");
         }
     }

     public async Task SendPrioritySettingUdp(string settingType, string value)
     {
         try
         {
             var priorityCommand = new UdpPriorityCommand
             {
                 PlayerId = serverId,
                 SettingType = settingType,
                 Value = value,
                 Command = "PRIORITY"
             };

             string jsonData = JsonSerializer.Serialize(priorityCommand);
             byte[] priorityPacket = Encoding.UTF8.GetBytes("PRIO" + jsonData);

             await udpClient.SendAsync(priorityPacket, priorityPacket.Length, serverEndpoint);
             Console.Error.WriteLine($"[UDP_PRIORITY] Sent priority setting: {settingType}={value}");
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"[UDP_PRIORITY] Error: {ex.Message}");
         }
     }

     #endregion

     #region Microphone Management

     public List<MicrophoneInfo> GetAvailableMicrophones()
     {
         return availableMicrophones;
     }

     public void SetAudioTransmission(bool enabled)
     {
         allowAudioTransmission = enabled;
         Console.Error.WriteLine($"[AUDIO] Audio transmission {(enabled ? "enabled" : "disabled")}");
     }

     public void RefreshMicrophones()
     {
         try
         {
             availableMicrophones.Clear();
             var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
             var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

             foreach (var device in devices)
             {
                 var micInfo = new MicrophoneInfo
                 {
                     Id = device.ID,
                     Name = device.FriendlyName,
                     Description = device.DeviceFriendlyName,
                     IsDefault = device.ID == defaultDevice?.ID,
                     IsEnabled = device.State == DeviceState.Active
                 };
                 availableMicrophones.Add(micInfo);
             }

             actionScriptBridge.SendMicrophoneList(availableMicrophones);
  
             // DON'T auto-select default - let Flash handle it
             // if (!SelectDefaultMicrophone(false)) { ... } ← Remove this
  
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"Error refreshing microphones: {ex.Message}");
         }
     }

     public bool SelectMicrophone(string microphoneId, bool stopCurrent = true)
     {
         Console.Error.WriteLine($"[DEBUG] SelectMicrophone called with: {microphoneId}");
         Console.Error.WriteLine($"[DEBUG] Call stack: {new System.Diagnostics.StackTrace()}");
         try
         {
             Console.Error.WriteLine($"[MIC] Selecting microphone: {microphoneId}");

             ForceCleanupCurrentMicrophone();

             var device = deviceEnumerator.GetDevice(microphoneId);
             if (device == null || device.State != DeviceState.Active)
             {
                 return false;
             }

             selectedDevice = device;
             SelectedMicrophoneId = microphoneId;

             actionScriptBridge.SendSelectedMicrophone(microphoneId);
             return true;
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"[MIC] Error selecting microphone: {ex.Message}");
             return false;
         }
     }

     public bool SelectDefaultMicrophone(bool stopCurrent = true)
     {
         var defaultMic = availableMicrophones.FirstOrDefault(m => m.IsDefault);
         return defaultMic != null && SelectMicrophone(defaultMic.Id, stopCurrent);
     }

     #endregion

     #region Audio Recording with Opus Encoding

     public void StartMicrophone()
     {
         Console.Error.WriteLine("[MIC] Starting microphone...");

         if (selectedDevice == null)
         {
             // Try saved microphone first
             if (!string.IsNullOrEmpty(SelectedMicrophoneId) && SelectMicrophone(SelectedMicrophoneId, false))
             {
                 Console.Error.WriteLine($"[MIC] Using saved microphone: {SelectedMicrophoneId}");
             }
             else if (!SelectDefaultMicrophone(false)) // fallback
             {
                 Console.Error.WriteLine("[MIC] No microphone available");
                 return;
             }
         }

         try
         {
             int deviceNumber = GetDeviceNumber(SelectedMicrophoneId);

             waveIn = new WaveInEvent
             {
                 DeviceNumber = deviceNumber,
                 WaveFormat = new WaveFormat(48000, 16, 1), // 48kHz for Opus
                 BufferMilliseconds = 20 // 20ms chunks for Opus
             };

             waveIn.DataAvailable += OnAudioDataAvailable;
             waveIn.RecordingStopped += OnRecordingStopped;
             waveIn.StartRecording();

             IsMicrophoneEnabled = true;
             audioUpdateCounter = 0;

             actionScriptBridge.SendMicrophoneStatus(true);
             Console.Error.WriteLine("[MIC] Microphone started successfully");
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"[MIC] Error starting microphone: {ex.Message}");
         }
     }

     public void StopMicrophone(bool sendStatus = true)
     {
         try
         {
             waveIn?.StopRecording();
             waveIn?.Dispose();
             waveIn = null;

             IsMicrophoneEnabled = false;
             currentLevel = 0f;
             smoothedLevel = 0f;
             peakLevel = 0f;

             if (sendStatus)
             {
                 actionScriptBridge.SendMicrophoneStatus(false);
             }
          
             Console.Error.WriteLine("[MIC] Microphone stopped");
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"[MIC] Error stopping microphone: {ex.Message}");
         }
     }
 
    private void OnAudioDataAvailable(object sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded < 2) return;

        // Convert incoming bytes to shorts
        int sampleCount = e.BytesRecorded / 2;
        short[] samples = new short[sampleCount];
        Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

        // Add to frame buffer
        pcmFrameBuffer.AddRange(samples);

        // Process complete 960-sample frames
        while (pcmFrameBuffer.Count >= OPUS_FRAME_SIZE)
        {
            // Extract exactly 960 samples
            short[] frame = pcmFrameBuffer.GetRange(0, OPUS_FRAME_SIZE).ToArray();
            pcmFrameBuffer.RemoveRange(0, OPUS_FRAME_SIZE);

            // Calculate audio level for this frame
            short maxSample = 0;
            foreach (short sample in frame)
            {
                short abs = Math.Abs(sample);
                if (abs > maxSample) maxSample = abs;
            }

            // Apply modest boost (NOT 10x!)
            const float BOOST_GAIN = 3.0f; // Reduced from 10x
            for (int i = 0; i < frame.Length; i++)
            {
                float boosted = frame[i] * BOOST_GAIN;
                boosted = Math.Max(-32767, Math.Min(32767, boosted));
                frame[i] = (short)boosted;
            }

            // Convert back to bytes
            byte[] frameBytes = new byte[OPUS_FRAME_SIZE * 2];
            Buffer.BlockCopy(frame, 0, frameBytes, 0, frameBytes.Length);

            // Check noise gate
            float level = (maxSample * BOOST_GAIN) / 32768f;

            // --- 👇 ADDED VISUALIZER LOGIC 👇 ---
            
            // Use a smoothing factor for a more visually stable bar
            const float SMOOTHING_FACTOR = 0.4f; 
            const int VISUALIZER_UPDATE_SKIP = 3; // Send update every 3 data events (~16 updates/sec)

            // 1. Update and smooth the level
            currentLevel = level; 
            smoothedLevel = (smoothedLevel * (1.0f - SMOOTHING_FACTOR)) + (currentLevel * SMOOTHING_FACTOR);

            // 2. Rate Limit the UI update
            audioUpdateCounter++;

            if (IsUIActive && audioUpdateCounter >= VISUALIZER_UPDATE_SKIP)
            {
                // Send the smoothed level to Flash with the CMD: protocol
                actionScriptBridge.SendAudioLevel(smoothedLevel);
                
                // Peak level management (optional, for peak hold feature)
                peakLevel = Math.Max(peakLevel, currentLevel);
                peakLevel *= 0.85f; // Decay the peak slightly

                audioUpdateCounter = 0; // Reset counter
            }

            // --- 👆 END OF ADDED VISUALIZER LOGIC 👆 ---

            if (level > NoiseGate && allowAudioTransmission && isConnectedToServer)
            {
                // Encode to Opus
                byte[] opusData = opusProcessor.EncodeToOpus(frameBytes);
                outgoingOpusData.Enqueue(opusData);
            }
        }
    }

     private void ForceCleanupCurrentMicrophone()
     {
         try
         {
             if (waveIn != null)
             {
                 waveIn.DataAvailable -= OnAudioDataAvailable;
                 waveIn.RecordingStopped -= OnRecordingStopped;

                 try { waveIn.StopRecording(); } catch { }
                 try { waveIn.Dispose(); } catch { }
                 waveIn = null;
             }

             if (selectedDevice != null)
             {
                 try { selectedDevice.Dispose(); } catch { }
                 selectedDevice = null;
             }

             IsMicrophoneEnabled = false;
             currentLevel = 0f;
             smoothedLevel = 0f;
             peakLevel = 0f;

             // Clear audio queue
             while (outgoingOpusData.TryDequeue(out _)) { }

             Console.Error.WriteLine("[MIC] Microphone cleanup completed");
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"[MIC] Cleanup error: {ex.Message}");
         }
     }

     private void OnRecordingStopped(object sender, StoppedEventArgs e)
     {
         Console.Error.WriteLine("[MIC] Recording stopped");
     }

     private int GetDeviceNumber(string deviceId)
     {
         for (int i = 0; i < WaveInEvent.DeviceCount; i++)
         {
             var capabilities = WaveInEvent.GetCapabilities(i);

             if (selectedDevice != null &&
                 capabilities.ProductName.Contains(selectedDevice.FriendlyName.Split(' ')[0]))
             {
                 return i;
             }
         }

         return 0;
     }

     #endregion

     #region UDP Server Connection

     public async Task<bool> ConnectToServer(string serverAddress, int port, string playerId, string voiceId)
     {
         Console.Error.WriteLine($"[UDP_CONNECT] Connecting to {serverAddress}:{port}");
         try
         {
             lock (reconnectionLock)
             {
                 if (isConnectedToServer)
                 {
                     Console.Error.WriteLine("[UDP_CONNECT] Already connected");
                     return true;
                 }
             }

             // USE THE VOICEMANAGER'S UDP CLIENT - DON'T CREATE A NEW ONE!
             udpClient = voiceManager.GetUdpClient();

             if (udpClient == null)
             {
                 Console.Error.WriteLine("[UDP_CONNECT] ERROR: VoiceManager UDP client not available!");
                 return false;
             }

             serverEndpoint = new IPEndPoint(IPAddress.Parse(serverAddress), port);

             serverId = playerId;
             storedVoiceId = voiceId;
             storedServerAddress = serverAddress;
             storedPort = port;

             // REMOVE THIS LINE - Can't ping after Connect():
             // await SendPingToServer();

             // Start UDP sender worker
             udpSenderTask = Task.Run(UdpSenderWorker, udpCancellation.Token);

             isConnectedToServer = true;
             isAuthenticated = true;
             lastConnectionCheck = DateTime.Now;

             ConnectionStatusChanged?.Invoke(this, "Connected (UDP)");
             Console.Error.WriteLine($"[UDP_CONNECT] Connected successfully");
             return true;
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"[UDP_CONNECT] Error: {ex.Message}");
             ConnectionStatusChanged?.Invoke(this, $"Connection failed: {ex.Message}");
             return false;
         }
     }

     public void DisconnectFromServer()
     {
         try
         {
             isConnectedToServer = false;
             isAuthenticated = false;
          
             udpClient = null;

             ConnectionStatusChanged?.Invoke(this, "Disconnected");
             Console.Error.WriteLine("[UDP_DISCONNECT] Disconnected from server");
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"[UDP_DISCONNECT] Error: {ex.Message}");
         }
     }

     public async Task SendServerMessage(string message)
     {
         // For UDP, we'll send priority settings instead
         var parts = message.Split(':');
         if (parts.Length >= 2)
         {
             await SendPrioritySettingUdp(parts[0], parts[1]);
         }
     }

     public void OnConnectionLost()
     {
         Console.Error.WriteLine("[UDP] Connection lost event triggered");
         isConnectedToServer = false;
         isAuthenticated = false;
      
         while (outgoingOpusData.TryDequeue(out _)) { }
      
         if (!string.IsNullOrEmpty(storedServerAddress))
         {
             _ = Task.Run(async () => await ConnectToServer(storedServerAddress, storedPort, serverId, storedVoiceId));
         }
     }

     #endregion

     #region Public API Methods

     public void SetMicrophoneSensitivity(float sensitivity)
     {
         MicrophoneSensitivity = Math.Max(0.1f, Math.Min(5.0f, sensitivity));
     }

     public void SetUIState(bool isActive)
     {
         IsUIActive = isActive;
         Console.Error.WriteLine($"[UI] UI state changed to: {(isActive ? "Active" : "Inactive")}");
     }

     public void SetNoiseGate(float threshold)
     {
         NoiseGate = Math.Max(0.0f, Math.Min(1.0f, threshold));
     }

     public AudioData GetCurrentAudioData()
     {
         return new AudioData
         {
             Level = smoothedLevel,
             Peak = peakLevel,
             IsActive = currentLevel > NoiseGate,
             Timestamp = DateTime.Now
         };
     }

     public void SetVoiceManager(VoiceManager vm)
     {
         voiceManager = vm;
         Console.Error.WriteLine("[LINK] VoiceManager linked to ProximityChatManager");
     }

     public bool IsMicrophoneActive()
     {
         return IsMicrophoneEnabled && currentLevel > NoiseGate;
     }

     public bool IsConnected => isConnectedToServer;
     public bool UseUdpVoice => true; // Always UDP now

     #endregion

     #region IDisposable

     public void Dispose()
     {
         StopMicrophone();
         DisconnectFromServer();

         udpCancellation.Cancel();
         try
         {
             udpSenderTask?.Wait(1000);
         }
         catch { }

         deviceEnumerator?.Dispose();
         selectedDevice?.Dispose();
         actionScriptBridge?.Dispose();
         udpCancellation.Dispose();
     }

     #endregion
 }

 // ActionScript Bridge - unchanged
 public class ActionScriptBridge : IDisposable
 {
     private float lastSentLevel = 0f;
     private int levelUpdateCounter = 0;

     public ActionScriptBridge()
     {
     }

     public void SendMicrophoneList(List<MicrophoneInfo> microphones)
     {
         try
         {
             // FIX: Use Console.Error.WriteLine with CMD: prefix
             Console.Error.WriteLine($"CMD:MIC_COUNT:{microphones.Count}");

             foreach (var mic in microphones)
             {
                 Console.Error.WriteLine($"CMD:MIC_DEVICE:{mic.Id}|{mic.Name}|{mic.IsDefault}");
             }

             var defaultMic = microphones.FirstOrDefault(m => m.IsDefault);
             if (defaultMic != null)
             {
                 Console.Error.WriteLine($"CMD:DEFAULT_MIC:{defaultMic.Id}");
             }
         }
         catch { }
     }

     public void SendSelectedMicrophone(string microphoneId)
     {
         try
         {
             // ADD CMD: prefix
             Console.Error.WriteLine($"CMD:SELECTED_MIC:{microphoneId ?? "none"}");
         }
         catch { }
     }

     public void SendAudioLevel(float level)
     {
         try
         {
             // ADD CMD: prefix
             Console.Error.WriteLine($"CMD:AUDIO_LEVEL:{level.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
         }
         catch
         {
         }
     }

     public void SendMicrophoneStatus(bool isEnabled)
     {
         try
         {
             // ADD CMD: prefix
             Console.Error.WriteLine($"CMD:MIC_STATUS:{isEnabled}");
         }
         catch { }
     }

     public void Dispose()
     {
     }
 }

 // Main Program - updated for UDP
 class Program
 {
     private static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

     static async Task Main(string[] args)
     {
         var chatManager = new ProximityChatManager();
         var actionScriptBridge = new ActionScriptBridge();
         var voiceManager = new VoiceManager(actionScriptBridge);

         // Link the managers
         chatManager.SetVoiceManager(voiceManager);
         voiceManager.SetChatManagerReference(chatManager);

         // DON'T start voice receiver here - wait for CONNECT_VOICE command
         // Remove the StartVoiceReceiver call from here

         _ = Task.Run(async () => await ListenForCommands(chatManager, voiceManager, cancellationTokenSource.Token));

         Console.Error.WriteLine("🚀🚀🚀 VERSION 14:55 BUILD LOADED 🚀🚀🚀");
         Console.Error.WriteLine("Commands:");
         Console.Error.WriteLine("  CONNECT_VOICE:serverIP:playerID:voiceID");
         Console.Error.WriteLine("  START_MIC / STOP_MIC");
         Console.Error.WriteLine("  SET_INCOMING_VOLUME:0.5");

         Console.CancelKeyPress += (sender, e) =>
         {
             e.Cancel = true;
             cancellationTokenSource.Cancel();
             _ = Task.Run(async () =>
             {
                 await Task.Delay(3000);
                 Environment.Exit(0);
             });
         };

         try
         {
             while (!cancellationTokenSource.Token.IsCancellationRequested)
             {
                 await Task.Delay(1000, cancellationTokenSource.Token);
             }
         }
         catch (OperationCanceledException)
         {
             Console.Error.WriteLine("Shutting down...");
         }
         finally
         {
             chatManager.Dispose();
             voiceManager.Dispose();
         }
     }

     private static async Task ListenForCommands(ProximityChatManager chatManager, VoiceManager voiceManager,
        CancellationToken cancellationToken)
    {
        try
        {
            Console.Error.WriteLine("[COMMAND_LISTENER] Listening for commands...");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    string command = Console.ReadLine();
                    
                    // ✅ ADD: Trace raw input immediately
                    Console.Error.WriteLine($"[C# RAW INPUT] Received: '{command}'");
                    
                    if (string.IsNullOrEmpty(command)) continue;

                    // ✅ ADD: Trim whitespace to prevent parsing issues
                    command = command.Trim();
                    
                    var parts = command.Split(':');
                    
                    // ✅ ADD: Trace parsed command
                    Console.Error.WriteLine($"[C# PARSED] Command: '{parts[0]}' | Args: {parts.Length - 1}");

                    switch (parts[0])
                    {
                        case "START_MIC":
                            Console.Error.WriteLine("### EXECUTING: START_MIC ###");
                            chatManager.StartMicrophone();
                            break;

                        case "UI_ON":
                            Console.Error.WriteLine("### EXECUTING: UI_ON ###");
                            chatManager.SetUIState(true);
                            Console.Error.WriteLine("🖥️ UI activated - audio level updates enabled");
                            break;

                        case "UI_OFF":
                            Console.Error.WriteLine("### EXECUTING: UI_OFF ###");
                            chatManager.SetUIState(false);
                            Console.Error.WriteLine("🖥️ UI deactivated - audio level updates disabled");
                            break;

                        case "CONNECT_VOICE":
                            Console.Error.WriteLine("### EXECUTING: CONNECT_VOICE ###");
                            if (parts.Length >= 4)
                            {
                                string serverIP = parts[1];
                                string playerID = parts[2];
                                string voiceID = parts[3];

                                Console.Error.WriteLine($"🔌 Connecting UDP voice for player {playerID}...");

                                voiceManager.StoreConnectionDetails(serverIP, playerID, voiceID);

                                if (await voiceManager.StartVoiceReceiver())
                                {
                                    Console.Error.WriteLine("🎉 UDP Voice receiver started!");
                                    await voiceManager.SendAuthenticationToServer(serverIP, 2051, playerID, voiceID);
                                    bool connected = await chatManager.ConnectToServer(serverIP, 2051, playerID, voiceID);

                                    if (connected)
                                    {
                                        Console.Error.WriteLine("✅ UDP voice connection established!");
                                    }
                                }
                            }
                            break;

                        case "STOP_MIC":
                            Console.Error.WriteLine("### EXECUTING: STOP_MIC ###");
                            chatManager.StopMicrophone();
                            break;

                        case "ENABLE_AUDIO_TRANSMISSION":
                            Console.Error.WriteLine("### EXECUTING: ENABLE_AUDIO_TRANSMISSION ###");
                            chatManager.SetAudioTransmission(true);
                            break;

                        case "DISABLE_AUDIO_TRANSMISSION":
                            Console.Error.WriteLine("### EXECUTING: DISABLE_AUDIO_TRANSMISSION ###");
                            chatManager.SetAudioTransmission(false);
                            break;

                        case "SET_INCOMING_VOLUME":
                            Console.Error.WriteLine("### EXECUTING: SET_INCOMING_VOLUME ###");
                            if (parts.Length > 1)
                            {
                                if (float.TryParse(parts[1], out float volume))
                                {
                                    voiceManager.SetIncomingVolume(volume);
                                    Console.Error.WriteLine($"🔊 Set incoming volume to {volume}");
                                }
                                else
                                {
                                    Console.Error.WriteLine($"❌ Invalid volume value: {parts[1]}");
                                }
                            }
                            break;

                        case "SELECT_MIC":
                            Console.Error.WriteLine($"### EXECUTING: SELECT_MIC with ID: {parts[1]} ###");
                            if (parts.Length > 1)
                            {
                                bool wasRunning = chatManager.IsMicrophoneEnabled;
                                bool success = chatManager.SelectMicrophone(parts[1]);
                                Console.Error.WriteLine($"✅ SELECT_MIC result: {success}");

                                if (success && wasRunning)
                                {
                                    chatManager.StartMicrophone();
                                }
                            }
                            break;

                        case "GET_MICS":
                            Console.Error.WriteLine("### EXECUTING: GET_MICS ###");
                            chatManager.RefreshMicrophones();
                            break;

                        case "TEST_OPUS":
                            Console.Error.WriteLine("🧪 Testing Opus compression...");
                            var testAudio = new byte[1920]; // 20ms of 48kHz audio
                            new Random().NextBytes(testAudio);
                            var opusProcessor = new OpusAudioProcessor();
                            var compressed = opusProcessor.EncodeToOpus(testAudio);
                            var decompressed = opusProcessor.DecodeFromOpus(compressed);
                            Console.Error.WriteLine($"   Original: {testAudio.Length} bytes");
                            Console.Error.WriteLine($"   Compressed: {compressed.Length} bytes ({(float)compressed.Length/testAudio.Length*100:F1}%)");
                            Console.Error.WriteLine($"   Decompressed: {decompressed.Length} bytes");
                            break;

                        case "HELP":
                            Console.Error.WriteLine("🎮 Available Commands:");
                            Console.Error.WriteLine("  CONNECT_VOICE:IP:playerID:voiceID - Connect to voice server");
                            Console.Error.WriteLine("  START_MIC / STOP_MIC - Control microphone");
                            Console.Error.WriteLine("  SET_INCOMING_VOLUME:0.5 - Set playback volume (0.0-1.0)");
                            Console.Error.WriteLine("  SET_MIC_SENSITIVITY:1.0 - Set mic sensitivity");
                            Console.Error.WriteLine("  SET_NOISE_GATE:0.01 - Set noise gate threshold");
                            Console.Error.WriteLine("  PRIORITY_SETTING:type:value - Send priority command");
                            Console.Error.WriteLine("  STATUS - Show system status");
                            Console.Error.WriteLine("  TEST_OPUS - Test compression");
                            Console.Error.WriteLine("  EXIT / QUIT - Shutdown");
                            break;

                        case "EXIT":
                        case "QUIT":
                            Console.Error.WriteLine("👋 Shutting down UDP voice system...");
                            cancellationTokenSource.Cancel();
                            return;

                        default:
                            Console.Error.WriteLine($"❓ Unknown command: {parts[0]} (type HELP for commands)");
                            break;
                     }
                 }
                 catch (Exception ex)
                 {
                     Console.Error.WriteLine($"❌ Command error: {ex.Message}");
                     Thread.Sleep(100);
                 }
             }
         }
         catch (OperationCanceledException)
         {
             Console.Error.WriteLine("Command listener stopped");//22
         }
     }
 }
}