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
using WebRtcVadSharp;
using RNNoise.NET;

namespace ConsoleApp1
{
 // Simple file logger for diagnosing voice disconnects
 public static class VoiceLog
 {
     private static readonly string LogPath = Path.Combine(
         AppDomain.CurrentDomain.BaseDirectory, "voice_log.txt");
     private static readonly object Lock = new object();

     public static void Write(string message)
     {
         try
         {
             lock (Lock)
             {
                 File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
             }
         }
         catch { } // Never crash the app for logging
     }

     public static void Init()
     {
         try
         {
             // Reset log file on each launch
             File.WriteAllText(LogPath, $"=== Voice Log Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
         }
         catch { }
     }
 }

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

             // Match Discord/Mumble quality: 64kbps (Discord default), max complexity
             encoder.Bitrate = 64000;
             encoder.Complexity = 10;
             encoder.UseInbandFEC = true;
             encoder.PacketLossPercent = 15;
             encoder.UseDTX = true;

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

             // Console.Error.WriteLine($"Opus: Encoded {rawPcmData.Length} PCM bytes → {encodedBytes} Opus bytes");
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

     private WasapiCapture wasapiCapture;
     private WaveFormat targetFormat = new WaveFormat(48000, 16, 1);
     private MediaFoundationResampler resampler;
     private IWaveProvider resamplerSource;
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

    // WebRTC Voice Activity Detection
    private WebRtcVad voiceActivityDetector;
    private bool vadAvailable = false;
    private bool lastVadResult = false;
    private int silenceFrameCount = 0;
    private const int SILENCE_FRAMES_BEFORE_MUTE = 8; // ~160ms of silence before cutting off
    private int vadSpeechFrames = 0;
    private int vadSilenceFrames = 0;
    private int vadPacketsSent = 0;
    private DateTime lastVadDiagTime = DateTime.MinValue;

    // Self-speaking icon tracking (for testing)
    private bool selfSpeakingIconShown = false;
    private int selfSilentFrames = 0;
    private const int SELF_SILENT_DEBOUNCE = 15; // ~300ms, same as VoiceManager

    // Speaker icon mode: "all" = everyone, "others" = hide self, "off" = no icons
    private volatile string speakerIconMode = "all";

    // AGC gain smoothing (prevents gain pumping between frames)
    private float smoothedAgcGain = 1.0f;
    private const float AGC_ATTACK = 0.3f;   // fast gain reduction (don't clip)
    private const float AGC_RELEASE = 0.05f;  // slow gain increase (natural sounding)

    // RNNoise ML denoiser
    private Denoiser rnnDenoiser;
    private bool rnnDenoiserAvailable = false;

    // Smooth noise gate envelope
    private float gateEnvelope = 0f;
    private const float GATE_ATTACK = 0.95f;   // fast open
    private const float GATE_RELEASE = 0.05f;  // slow close

     // Connection management
     public event EventHandler<string> ConnectionStatusChanged;
     private DateTime lastConnectionCheck = DateTime.Now;
     private readonly object reconnectionLock = new object();
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

         // Initialize WebRTC VAD - 48kHz, 20ms frames
         try
         {
             voiceActivityDetector = new WebRtcVad()
             {
                 OperatingMode = OperatingMode.VeryAggressive,
                 FrameLength = WebRtcVadSharp.FrameLength.Is20ms,
                 SampleRate = WebRtcVadSharp.SampleRate.Is48kHz
             };
             vadAvailable = true;
             Console.Error.WriteLine("[VAD] WebRTC VAD initialized successfully");
         }
         catch (Exception ex)
         {
             vadAvailable = false;
             voiceActivityDetector = null;
             Console.Error.WriteLine($"[VAD] WebRTC VAD failed to load, using noise gate fallback: {ex.Message}");
         }

         // Initialize RNNoise ML denoiser
         try
         {
             rnnDenoiser = new Denoiser();
             rnnDenoiserAvailable = true;
             Console.Error.WriteLine("[DENOISE] RNNoise ML denoiser initialized");
         }
         catch (Exception ex)
         {
             rnnDenoiserAvailable = false;
             rnnDenoiser = null;
             Console.Error.WriteLine($"[DENOISE] RNNoise failed to load: {ex.Message}");
         }

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
                 // Connection health check — ping every 10s, re-auth if 3 PONGs missed
                 var now = DateTime.Now;
                 if ((now - lastConnectionCheck).TotalSeconds >= 10)
                 {
                     if (voiceManager != null)
                     {
                         bool connectionDead = voiceManager.OnPingSent();
                         if (connectionDead)
                         {
                             VoiceLog.Write("HEALTH: 3 PONGs missed — re-authenticating");
                             Console.Error.WriteLine("[UDP_HEALTH] 3 PONGs missed — re-authenticating...");
                             voiceManager.ResetPongTracking();
                             await voiceManager.ReAuthenticate();
                         }
                     }
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
                   await udpClient.SendAsync(voicePacket, voicePacket.Length, serverEndpoint);
       
              // Console.Error.WriteLine($"[UDP_VOICE] Sent {opusData.Length} Opus bytes (2-byte header)");
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
         // Clear self-speaking icon when transmission disabled
         if (!enabled && selfSpeakingIconShown && serverId != null)
         {
             Console.Error.WriteLine($"CMD:SILENT:{serverId}");
             selfSpeakingIconShown = false;
             selfSilentFrames = 0;
         }
         Console.Error.WriteLine($"[AUDIO] Audio transmission {(enabled ? "enabled" : "disabled")}");
     }

     public void SetSpeakerIconMode(string mode)
     {
         speakerIconMode = mode;
         voiceManager?.SetSpeakerIconMode(mode);
         // Clear self icon if switching away from "all"
         if (mode != "all" && selfSpeakingIconShown && serverId != null)
         {
             Console.Error.WriteLine($"CMD:SILENT:{serverId}");
             selfSpeakingIconShown = false;
             selfSilentFrames = 0;
         }
         Console.Error.WriteLine($"[AUDIO] Speaker icon mode set to: {mode}");
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
             wasapiCapture = new WasapiCapture(selectedDevice, false, 20);
             Console.Error.WriteLine($"[MIC] WASAPI device format: {wasapiCapture.WaveFormat}");

             wasapiCapture.DataAvailable += OnWasapiDataAvailable;
             wasapiCapture.RecordingStopped += OnRecordingStopped;
             wasapiCapture.StartRecording();

             IsMicrophoneEnabled = true;
             audioUpdateCounter = 0;

             actionScriptBridge.SendMicrophoneStatus(true);
             Console.Error.WriteLine("[MIC] Microphone started successfully (WASAPI)");
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
             // Clear self-speaking icon immediately
             if (selfSpeakingIconShown && serverId != null)
             {
                 Console.Error.WriteLine($"CMD:SILENT:{serverId}");
                 selfSpeakingIconShown = false;
                 selfSilentFrames = 0;
             }

             wasapiCapture?.StopRecording();
             wasapiCapture?.Dispose();
             wasapiCapture = null;

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
 
    private void OnWasapiDataAvailable(object sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        var captureFormat = wasapiCapture.WaveFormat;

        // If already 48kHz/16-bit/mono, pass through directly
        if (captureFormat.SampleRate == 48000 && captureFormat.BitsPerSample == 16 && captureFormat.Channels == 1)
        {
            OnAudioDataAvailable(sender, e);
            return;
        }

        // Convert from device format to 48kHz/16-bit/mono
        try
        {
            using var inputStream = new RawSourceWaveStream(e.Buffer, 0, e.BytesRecorded, captureFormat);
            using var resampler = new MediaFoundationResampler(inputStream, targetFormat);
            resampler.ResamplerQuality = 60;

            byte[] resampledBuffer = new byte[e.BytesRecorded * 4]; // generous buffer
            int bytesRead = resampler.Read(resampledBuffer, 0, resampledBuffer.Length);

            if (bytesRead > 0)
            {
                var convertedArgs = new WaveInEventArgs(resampledBuffer, bytesRead);
                OnAudioDataAvailable(sender, convertedArgs);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MIC] Resample error: {ex.Message}");
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

            // === AUDIO PROCESSING CHAIN ===
            // Pipeline: RNNoise first (on natural levels) → then adaptive gain
            // This matches Discord's approach: denoise clean audio, then boost

            // 1. RNNoise ML denoising on raw mic audio (natural levels)
            if (rnnDenoiserAvailable && rnnDenoiser != null)
            {
                try
                {
                    float[] floatBuf = new float[OPUS_FRAME_SIZE];
                    for (int i = 0; i < OPUS_FRAME_SIZE; i++)
                        floatBuf[i] = frame[i] / 32768f;

                    rnnDenoiser.Denoise(floatBuf.AsSpan());

                    for (int i = 0; i < OPUS_FRAME_SIZE; i++)
                        frame[i] = (short)Math.Max(-32767, Math.Min(32767, floatBuf[i] * 32767f));
                }
                catch { /* RNNoise failure — use undenoised audio */ }
            }

            // 2. VAD-gated AGC: only boost during speech, prevents noise pumping
            // WebRTC AGC2 uses RNN-VAD to gate gain measurement — we use a simpler
            // RMS threshold: if frame is quiet (no speech), don't compute new gain,
            // just let smoothedAgcGain decay toward 1.0
            double cleanSumSq = 0;
            foreach (short s in frame) cleanSumSq += (double)s * s;
            double cleanRms = Math.Sqrt(cleanSumSq / frame.Length) / 32768.0;

            const float TARGET_RMS = 0.50f;
            const float MAX_GAIN = 8.0f;
            const float AGC_SPEECH_THRESHOLD = 0.01f; // RMS below this = not speech, don't update gain

            if (cleanRms >= AGC_SPEECH_THRESHOLD)
            {
                // Speech detected: compute target gain and smooth toward it
                float targetGain = Math.Min((float)(TARGET_RMS / cleanRms), MAX_GAIN);
                float coeff = targetGain < smoothedAgcGain ? AGC_ATTACK : AGC_RELEASE;
                smoothedAgcGain = smoothedAgcGain + (targetGain - smoothedAgcGain) * coeff;
            }
            // else: silence — keep current smoothedAgcGain (it will stay stable, no noise pumping)

            for (int i = 0; i < frame.Length; i++)
            {
                float boosted = frame[i] * smoothedAgcGain;
                frame[i] = (short)Math.Max(-32767, Math.Min(32767, boosted));
            }

            // 4. Measure post-processed level for VAD/visualizer
            short maxSample = 0;
            foreach (short sample in frame)
            {
                short abs = Math.Abs(sample);
                if (abs > maxSample) maxSample = abs;
            }

            // Convert to bytes for Opus encoding
            byte[] frameBytes = new byte[OPUS_FRAME_SIZE * 2];
            Buffer.BlockCopy(frame, 0, frameBytes, 0, frameBytes.Length);

            float level = maxSample / 32768f;

            // --- 👇 ADDED VISUALIZER LOGIC 👇 ---
            
            // Use a smoothing factor for a more visually stable bar
            const float SMOOTHING_FACTOR = 0.4f; 
            const int VISUALIZER_UPDATE_SKIP = 15; // Send update every 15 data events (~3 updates/sec)

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

            if (allowAudioTransmission && isConnectedToServer)
            {
                // Detect speech: level pre-filter + WebRTC VAD
                // Level pre-filter rejects quiet speaker bleed before VAD even runs
                const float VAD_MIN_LEVEL = 0.05f; // Below this = definitely not speech (post-denoise level)
                bool isSpeech = false;
                if (level >= VAD_MIN_LEVEL)
                {
                    if (vadAvailable && voiceActivityDetector != null)
                    {
                        try
                        {
                            isSpeech = voiceActivityDetector.HasSpeech(frameBytes);
                        }
                        catch
                        {
                            isSpeech = level > NoiseGate;
                        }
                    }
                    else
                    {
                        isSpeech = level > NoiseGate;
                    }
                }

                // Smooth noise gate: soft attack/release prevents chopped words
                float gateTarget = isSpeech ? 1.0f : 0.0f;
                float gateCoeff = isSpeech ? GATE_ATTACK : GATE_RELEASE;
                gateEnvelope = gateEnvelope * gateCoeff + gateTarget * (1f - gateCoeff);

                if (isSpeech)
                {
                    vadSpeechFrames++;
                    lastVadResult = true;
                    silenceFrameCount = 0;

                    // Apply gate envelope to smooth transitions
                    if (gateEnvelope < 0.99f)
                    {
                        for (int i = 0; i < OPUS_FRAME_SIZE; i++)
                            frame[i] = (short)(frame[i] * gateEnvelope);
                        Buffer.BlockCopy(frame, 0, frameBytes, 0, frameBytes.Length);
                    }

                    byte[] opusData = opusProcessor.EncodeToOpus(frameBytes);
                    outgoingOpusData.Enqueue(opusData);
                    vadPacketsSent++;
                }
                else
                {
                    vadSilenceFrames++;
                    silenceFrameCount++;

                    if (lastVadResult && silenceFrameCount < SILENCE_FRAMES_BEFORE_MUTE)
                    {
                        byte[] opusData = opusProcessor.EncodeToOpus(frameBytes);
                        outgoingOpusData.Enqueue(opusData);
                        vadPacketsSent++;
                    }
                    else
                    {
                        lastVadResult = false;
                    }
                }

                // VAD diagnostic every 3 seconds
                if ((DateTime.Now - lastVadDiagTime).TotalSeconds >= 3)
                {
                    Console.Error.WriteLine($"[VAD_DIAG] speech={vadSpeechFrames} silence={vadSilenceFrames} sent={vadPacketsSent} level={level:F3}");
                    vadSpeechFrames = 0;
                    vadSilenceFrames = 0;
                    vadPacketsSent = 0;
                    lastVadDiagTime = DateTime.Now;
                }

                // Self-speaking icon: show your own icon when transmitting
                // Require minimum audio level to avoid showing icon for keyboard clicks
                // that pass VAD but are too quiet for others to hear
                if (serverId != null && speakerIconMode == "all")
                {
                    bool isSending = lastVadResult && allowAudioTransmission && level >= 0.15f;
                    if (isSending)
                    {
                        selfSilentFrames = 0;
                        if (!selfSpeakingIconShown)
                        {
                            Console.Error.WriteLine($"CMD:SPEAKING:{serverId}");
                            selfSpeakingIconShown = true;
                        }
                    }
                    else if (selfSpeakingIconShown)
                    {
                        selfSilentFrames++;
                        if (selfSilentFrames >= SELF_SILENT_DEBOUNCE)
                        {
                            Console.Error.WriteLine($"CMD:SILENT:{serverId}");
                            selfSpeakingIconShown = false;
                            selfSilentFrames = 0;
                        }
                    }
                }
            }
        }
    }

     private void ForceCleanupCurrentMicrophone()
     {
         try
         {
             if (wasapiCapture != null)
             {
                 wasapiCapture.DataAvailable -= OnWasapiDataAvailable;
                 wasapiCapture.RecordingStopped -= OnRecordingStopped;

                 try { wasapiCapture.StopRecording(); } catch { }
                 try { wasapiCapture.Dispose(); } catch { }
                 wasapiCapture = null;
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

     private int micReconnectAttempts = 0;
     private const int MAX_MIC_RECONNECT_ATTEMPTS = 5;
     private volatile bool isReconnectingMic = false;

     private void OnRecordingStopped(object sender, StoppedEventArgs e)
     {
         if (e.Exception != null)
         {
             Console.Error.WriteLine($"[MIC] Recording stopped with error: {e.Exception.Message}");
         }
         else
         {
             Console.Error.WriteLine("[MIC] Recording stopped");
         }

         // Only auto-reconnect if mic was supposed to be active (not a deliberate stop)
         if (!IsMicrophoneEnabled) return;

         // Device was lost (OBS/IDE grabbed it exclusively, USB unplug, etc.)
         Console.Error.WriteLine("[MIC] Unexpected recording stop — attempting auto-reconnect...");
         _ = Task.Run(() => AttemptMicReconnect());
     }

     private async Task AttemptMicReconnect()
     {
         if (isReconnectingMic) return;
         isReconnectingMic = true;
         micReconnectAttempts = 0;

         try
         {
             while (micReconnectAttempts < MAX_MIC_RECONNECT_ATTEMPTS && IsMicrophoneEnabled)
             {
                 micReconnectAttempts++;
                 int delayMs = Math.Min(1000 * micReconnectAttempts, 5000); // 1s, 2s, 3s, 4s, 5s
                 Console.Error.WriteLine($"[MIC] Reconnect attempt {micReconnectAttempts}/{MAX_MIC_RECONNECT_ATTEMPTS} in {delayMs}ms...");
                 await Task.Delay(delayMs);

                 if (!IsMicrophoneEnabled) break; // User disabled mic while waiting

                 try
                 {
                     // Clean up old capture without clearing IsMicrophoneEnabled
                     if (wasapiCapture != null)
                     {
                         wasapiCapture.DataAvailable -= OnWasapiDataAvailable;
                         wasapiCapture.RecordingStopped -= OnRecordingStopped;
                         try { wasapiCapture.Dispose(); } catch { }
                         wasapiCapture = null;
                     }

                     // Try the same device first
                     MMDevice device = null;
                     if (!string.IsNullOrEmpty(SelectedMicrophoneId))
                     {
                         try
                         {
                             device = deviceEnumerator.GetDevice(SelectedMicrophoneId);
                             if (device?.State != DeviceState.Active) device = null;
                         }
                         catch { device = null; }
                     }

                     // Fallback to default device
                     if (device == null)
                     {
                         try
                         {
                             device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                             Console.Error.WriteLine($"[MIC] Original device unavailable, falling back to default: {device?.FriendlyName}");
                         }
                         catch { device = null; }
                     }

                     if (device == null || device.State != DeviceState.Active)
                     {
                         Console.Error.WriteLine("[MIC] No active capture device found, will retry...");
                         continue;
                     }

                     selectedDevice = device;
                     wasapiCapture = new WasapiCapture(selectedDevice, false, 20);
                     wasapiCapture.DataAvailable += OnWasapiDataAvailable;
                     wasapiCapture.RecordingStopped += OnRecordingStopped;
                     wasapiCapture.StartRecording();

                     Console.Error.WriteLine($"[MIC] Reconnected successfully to: {device.FriendlyName}");
                     micReconnectAttempts = 0;
                     isReconnectingMic = false;
                     return;
                 }
                 catch (Exception ex)
                 {
                     Console.Error.WriteLine($"[MIC] Reconnect attempt {micReconnectAttempts} failed: {ex.Message}");
                 }
             }

             Console.Error.WriteLine("[MIC] All reconnect attempts exhausted — microphone disabled");
             IsMicrophoneEnabled = false;
             actionScriptBridge.SendMicrophoneStatus(false);

             // Clear self-speaking icon
             if (selfSpeakingIconShown && serverId != null)
             {
                 Console.Error.WriteLine($"CMD:SILENT:{serverId}");
                 selfSpeakingIconShown = false;
                 selfSilentFrames = 0;
             }
         }
         finally
         {
             isReconnectingMic = false;
         }
     }

     // GetDeviceNumber removed — WASAPI captures directly from MMDevice, no WaveIn mapping needed

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
             // Clear self-speaking icon immediately
             if (selfSpeakingIconShown && serverId != null)
             {
                 Console.Error.WriteLine($"CMD:SILENT:{serverId}");
                 selfSpeakingIconShown = false;
                 selfSilentFrames = 0;
             }

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
         voiceActivityDetector?.Dispose();
         rnnDenoiser?.Dispose();

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
         VoiceLog.Init();
         VoiceLog.Write("ConsoleApp1 starting");

         // Catch any unhandled exceptions that bypass try/catch
         AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
         {
             var ex = e.ExceptionObject as Exception;
             VoiceLog.Write($"UNHANDLED EXCEPTION (isTerminating={e.IsTerminating}): {ex?.GetType().Name}: {ex?.Message}");
             VoiceLog.Write($"Stack: {ex?.StackTrace}");
         };

         TaskScheduler.UnobservedTaskException += (sender, e) =>
         {
             VoiceLog.Write($"UNOBSERVED TASK EXCEPTION: {e.Exception?.GetType().Name}: {e.Exception?.Message}");
             e.SetObserved(); // Prevent process crash from unobserved task exceptions
         };

         var chatManager = new ProximityChatManager();
         var actionScriptBridge = new ActionScriptBridge();
         var voiceManager = new VoiceManager(actionScriptBridge);

         // Link the managers
         chatManager.SetVoiceManager(voiceManager);
         voiceManager.SetChatManagerReference(chatManager);

         // DON'T start voice receiver here - wait for CONNECT_VOICE command
         // Remove the StartVoiceReceiver call from here

         var commandTask = Task.Run(async () => await ListenForCommands(chatManager, voiceManager, cancellationTokenSource.Token));

         // Background heartbeat — writes to voice_log.txt every 60s so we know process is alive
         _ = Task.Run(async () =>
         {
             try
             {
                 while (!cancellationTokenSource.Token.IsCancellationRequested)
                 {
                     await Task.Delay(60000, cancellationTokenSource.Token).ConfigureAwait(false);
                     VoiceLog.Write("HEARTBEAT — process alive");
                 }
             }
             catch (OperationCanceledException) { }
         });

         Console.Error.WriteLine("VERSION v0.0.38 BUILD LOADED");

         Console.CancelKeyPress += (sender, e) =>
         {
             e.Cancel = true;
             VoiceLog.Write("CTRL+C pressed — triggering shutdown");
             cancellationTokenSource.Cancel();
         };

         // Wait for command listener to exit (EXIT command or stdin closed)
         try
         {
             await commandTask;
         }
         catch (OperationCanceledException)
         {
             VoiceLog.Write("commandTask cancelled (OperationCanceledException)");
         }
         catch (Exception ex)
         {
             VoiceLog.Write($"commandTask exception: {ex.GetType().Name}: {ex.Message}");
             Console.Error.WriteLine($"[MAIN] Command listener error: {ex.Message}");
         }

         VoiceLog.Write("SHUTDOWN starting");
         Console.Error.WriteLine("[MAIN] Shutting down...");

         // Start force exit FIRST — if Dispose hangs, this guarantees process death
         _ = Task.Run(async () =>
         {
             await Task.Delay(3000);
             Console.Error.WriteLine("[MAIN] Force exit — Dispose hung");
             Environment.Exit(0);
         });

         // Ensure cancellation is triggered
         if (!cancellationTokenSource.IsCancellationRequested)
             cancellationTokenSource.Cancel();

         // Clean up resources
         try
         {
             chatManager.Dispose();
             voiceManager.Dispose();
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"[MAIN] Cleanup error: {ex.Message}");
         }

         Console.Error.WriteLine("[MAIN] Shutdown complete");
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

                    // Log stdin commands to file (skip noisy ones)
                    if (command != null && !command.StartsWith("KEEPALIVE"))
                        VoiceLog.Write($"STDIN: {command}");

                    // stdin closed = parent process died or disconnected
                    if (command == null)
                    {
                        // Check if parent process (WebMain) is still alive
                        string parentStatus = "unknown";
                        try
                        {
                            var parentId = System.Diagnostics.Process.GetCurrentProcess().Id;
                            var webMainProcs = System.Diagnostics.Process.GetProcessesByName("WebMain");
                            parentStatus = webMainProcs.Length > 0
                                ? $"WebMain ALIVE ({webMainProcs.Length} instances)"
                                : "WebMain NOT FOUND";
                        }
                        catch (Exception ex) { parentStatus = $"check_failed: {ex.Message}"; }

                        VoiceLog.Write($"STDIN CLOSED — {parentStatus} — triggering shutdown");
                        Console.Error.WriteLine("[COMMAND_LISTENER] stdin closed - shutting down");
                        cancellationTokenSource.Cancel();
                        return;
                    }

                    if (string.IsNullOrEmpty(command)) continue;

                    command = command.Trim();
                    var parts = command.Split(':');

                    switch (parts[0])
                    {
                        case "START_MIC":
                            // START_MIC
                            chatManager.StartMicrophone();
                            break;

                        case "UI_ON":
                            // UI_ON
                            chatManager.SetUIState(true);
                            Console.Error.WriteLine("🖥️ UI activated - audio level updates enabled");
                            break;

                        case "UI_OFF":
                            // UI_OFF
                            chatManager.SetUIState(false);
                            Console.Error.WriteLine("🖥️ UI deactivated - audio level updates disabled");
                            break;

                        case "CONNECT_VOICE":
                            // CONNECT_VOICE
                            if (parts.Length >= 4)
                            {
                                string serverIP = parts[1];
                                string playerID = parts[2];
                                string voiceID = parts[3];

                                // Guard against duplicate CONNECT_VOICE
                                if (voiceManager.IsConnectingOrConnected)
                                {
                                    VoiceLog.Write($"CONNECT_VOICE IGNORED (already connecting/connected) server={serverIP} player={playerID}");
                                    Console.Error.WriteLine("Already connected — ignoring duplicate CONNECT_VOICE");
                                    break;
                                }

                                voiceManager.IsConnectingOrConnected = true;
                                VoiceLog.Write($"CONNECT_VOICE server={serverIP} player={playerID}");
                                Console.Error.WriteLine($"Connecting UDP voice for player {playerID}...");

                                voiceManager.StoreConnectionDetails(serverIP, playerID, voiceID);

                                if (await voiceManager.StartVoiceReceiver())
                                {
                                    Console.Error.WriteLine("🎉 UDP Voice receiver started!");
                                    await voiceManager.SendAuthenticationToServer(serverIP, 2051, playerID, voiceID);
                                    VoiceLog.Write($"AUTH sent, confirmed={voiceManager.IsAuthConfirmed}");
                                    bool connected = await chatManager.ConnectToServer(serverIP, 2051, playerID, voiceID);

                                    if (connected)
                                    {
                                        VoiceLog.Write("VOICE CONNECTED OK");
                                        Console.Error.WriteLine("✅ UDP voice connection established!");
                                    }
                                    else
                                    {
                                        VoiceLog.Write("VOICE CONNECT FAILED");
                                    }
                                }
                                else
                                {
                                    VoiceLog.Write("StartVoiceReceiver FAILED");
                                    voiceManager.IsConnectingOrConnected = false;
                                }
                            }
                            break;

                        case "STOP_MIC":
                            // STOP_MIC
                            chatManager.StopMicrophone();
                            break;

                        case "ENABLE_AUDIO_TRANSMISSION":
                            // ENABLE_AUDIO_TRANSMISSION
                            chatManager.SetAudioTransmission(true);
                            break;

                        case "DISABLE_AUDIO_TRANSMISSION":
                            // DISABLE_AUDIO_TRANSMISSION
                            chatManager.SetAudioTransmission(false);
                            break;

                        case "SET_INCOMING_VOLUME":
                            // SET_INCOMING_VOLUME
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

                        case "SET_SPEAKER_ICON":
                            if (parts.Length > 1)
                            {
                                string mode = parts[1].ToLower().Trim();
                                if (mode == "all" || mode == "others" || mode == "off")
                                {
                                    chatManager.SetSpeakerIconMode(mode);
                                }
                                else
                                {
                                    Console.Error.WriteLine($"Invalid speaker icon mode: {mode} (use all/others/off)");
                                }
                            }
                            break;

                        // Priority system commands (Flash UI → Server)
                        case "SET_PRIORITY_ENABLED":
                            if (parts.Length > 1)
                                await chatManager.SendPrioritySettingUdp("ENABLED", parts[1].Trim());
                            break;

                        case "SET_PRIORITY_THRESHOLD":
                            if (parts.Length > 1)
                                await chatManager.SendPrioritySettingUdp("THRESHOLD", parts[1].Trim());
                            break;

                        case "SET_NON_PRIORITY_VOLUME":
                            if (parts.Length > 1)
                                await chatManager.SendPrioritySettingUdp("NON_PRIORITY_VOLUME", parts[1].Trim());
                            break;

                        case "SET_AUTO_PRIORITY_GUILD":
                            if (parts.Length > 1)
                                await chatManager.SendPrioritySettingUdp("GUILD_PRIORITY", parts[1].Trim());
                            break;

                        case "SET_AUTO_PRIORITY_LOCKED":
                            if (parts.Length > 1)
                                await chatManager.SendPrioritySettingUdp("LOCKED_PRIORITY", parts[1].Trim());
                            break;

                        case "SET_MAX_PRIORITY_SLOTS":
                            if (parts.Length > 1)
                                await chatManager.SendPrioritySettingUdp("MAX_PRIORITY_SLOTS", parts[1].Trim());
                            break;

                        case "ADD_MANUAL_PRIORITY":
                            if (parts.Length > 1)
                                await chatManager.SendPrioritySettingUdp("ADD_MANUAL", parts[1].Trim());
                            break;

                        case "REMOVE_MANUAL_PRIORITY":
                            if (parts.Length > 1)
                                await chatManager.SendPrioritySettingUdp("REMOVE_MANUAL", parts[1].Trim());
                            break;

                        case "SELECT_MIC":
                            // SELECT_MIC
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
                            // GET_MICS
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
                            Console.Error.WriteLine("  SET_INCOMING_VOLUME:0.5 - Set playback volume (0.0-2.0, above 1.0 = software boost)");
                            Console.Error.WriteLine("  SET_MIC_SENSITIVITY:1.0 - Set mic sensitivity");
                            Console.Error.WriteLine("  SET_NOISE_GATE:0.01 - Set noise gate threshold");
                            Console.Error.WriteLine("  PRIORITY_SETTING:type:value - Send priority command");
                            Console.Error.WriteLine("  STATUS - Show system status");
                            Console.Error.WriteLine("  TEST_OPUS - Test compression");
                            Console.Error.WriteLine("  EXIT / QUIT - Shutdown");
                            break;

                        case "KEEPALIVE":
                            break;

                        case "DISPOSE_NOTIFY":
                            // Flash is about to dispose PCBridge — log the reason/stack trace
                            string disposeReason = parts.Length > 1 ? string.Join(":", parts.Skip(1)) : "no_reason";
                            VoiceLog.Write($"DISPOSE_NOTIFY from Flash: {disposeReason}");
                            break;

                        case "EXIT":
                        case "QUIT":
                            VoiceLog.Write($"EXIT/QUIT command received");
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
                     VoiceLog.Write($"Command loop error: {ex.GetType().Name}: {ex.Message}");
                     Console.Error.WriteLine($"❌ Command error: {ex.Message}");
                     Thread.Sleep(100);
                 }
             }
             VoiceLog.Write("Command loop exited normally (while condition false)");
         }
         catch (OperationCanceledException)
         {
             VoiceLog.Write("Command listener cancelled (OperationCanceledException)");
             Console.Error.WriteLine("Command listener stopped");//22
         }
     }
 }
}