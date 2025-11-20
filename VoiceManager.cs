using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using NAudio.Wave;
using System.Text.Json;
using NAudio.CoreAudioApi;
using System.IO;
using System.Text;
using System.Collections.Concurrent;
using System.Linq;

namespace ConsoleApp1
{
   public class VoiceManager : IDisposable
   {
       #region Fields
       private UdpClient udpSendClient;
       private OpusAudioProcessor opusProcessor;
       private UdpClient udpReceiveClient;
       private IPEndPoint serverEndpoint;

       // Dynamic Jitter Buffer - Adaptive based on device type
       private readonly ConcurrentQueue<byte[]> jitterBuffer = new ConcurrentQueue<byte[]>();
       private readonly ConcurrentQueue<DateTime> packetArrivalTimes = new ConcurrentQueue<DateTime>();
       private int MIN_JITTER_PACKETS = 2;    // Will adjust for Bluetooth
       private int MAX_JITTER_PACKETS = 15;   // Will adjust for Bluetooth
       private int targetJitterPackets = 3;   // Will adjust for Bluetooth
       private DateTime lastPacketArrival = DateTime.MinValue;
       private DateTime expectedNextPacket = DateTime.MinValue;
       private const int PACKET_INTERVAL_MS = 20;
       private int consecutiveLatePackets = 0;
       private int consecutiveOnTimePackets = 0;

       private bool isProcessingVoice = false;
       private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

       private string storedServerIP;
       private string storedPlayerID;
       private string storedVoiceID;
       private ProximityChatManager chatManagerRef;

       private WaveOutEvent waveOut;
       private BufferedWaveProvider waveProvider;
       private readonly ActionScriptBridge actionScriptBridge;

       public int VoiceReceivePort { get; private set; } = 2051;
       public bool IsVoiceReceiverActive { get; private set; } = false;

       private WaveFormat detectedOutputFormat;
       private string detectedDeviceName;
       private bool isBluetoothDevice = false;
       #endregion

       #region Constructor
       public VoiceManager(ActionScriptBridge bridge)
       {
           actionScriptBridge = bridge;
           opusProcessor = new OpusAudioProcessor();
       }
       #endregion

       #region UDP Voice Receiver
       public async Task<bool> StartVoiceReceiver(int localPort = 2051)
       {
           try
           {
               Console.Error.WriteLine("[UDP_VOICE_INIT] Starting UDP voice receiver...");

               if (IsVoiceReceiverActive)
               {
                   Console.Error.WriteLine("[UDP_VOICE_INIT] Voice receiver already active");
                   return true;
               }

               VoiceReceivePort = localPort;

               // Probe audio device
               var deviceEnumerator = new MMDeviceEnumerator();
               var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);

               detectedDeviceName = defaultDevice.FriendlyName;
               int detectedSampleRate = defaultDevice.AudioClient.MixFormat.SampleRate;
               int detectedBits = defaultDevice.AudioClient.MixFormat.BitsPerSample;
               int detectedChannels = defaultDevice.AudioClient.MixFormat.Channels;

               Console.Error.WriteLine($"[UDP_VOICE_INIT] Device: {detectedDeviceName}");
               Console.Error.WriteLine($"[UDP_VOICE_INIT] Native Format: {detectedSampleRate}Hz, {detectedBits}-bit, {detectedChannels}ch");

               // DETECT BLUETOOTH
               isBluetoothDevice = detectedDeviceName.ToLower().Contains("bluetooth") ||
                                  detectedDeviceName.ToLower().Contains("bt") ||
                                  detectedDeviceName.ToLower().Contains("wireless") ||
                                  detectedDeviceName.ToLower().Contains("headset") ||
                                  detectedSampleRate == 8000 ||  // Common BT rate
                                  detectedSampleRate == 16000;   // Common BT rate

               if (isBluetoothDevice)
               {
                   Console.Error.WriteLine("[UDP_VOICE_INIT] 🎧 BLUETOOTH DEVICE DETECTED - Applying optimizations");
                  
                   // Adjust jitter buffer for Bluetooth
                   MIN_JITTER_PACKETS = 5;
                   MAX_JITTER_PACKETS = 25;
                   targetJitterPackets = 8;
                  
                   Console.Error.WriteLine($"[UDP_VOICE_INIT] Bluetooth jitter buffer: {MIN_JITTER_PACKETS}-{MAX_JITTER_PACKETS} packets (initial: {targetJitterPackets})");
               }

               // Initialize audio output
               waveOut = new WaveOutEvent();

               // Set latency based on device type
               if (isBluetoothDevice)
               {
                   waveOut.DesiredLatency = 300; // 300ms for Bluetooth
                   Console.Error.WriteLine("[UDP_VOICE_INIT] Set 300ms output latency for Bluetooth");
               }
               else
               {
                   waveOut.DesiredLatency = 150; // 150ms for wired/USB
               }

               // Use device's native format
               detectedOutputFormat = new WaveFormat(detectedSampleRate, detectedBits, detectedChannels);
               Console.Error.WriteLine($"[UDP_VOICE_INIT] Output Format: {detectedOutputFormat}");

               waveProvider = new BufferedWaveProvider(detectedOutputFormat);

               // BUFFER DURATION - Bluetooth needs more
               if (isBluetoothDevice)
               {
                   waveProvider.BufferDuration = TimeSpan.FromSeconds(3);
                   Console.Error.WriteLine("[UDP_VOICE_INIT] Using 3-second buffer for Bluetooth");
               }
               else
               {
                   waveProvider.BufferDuration = TimeSpan.FromSeconds(2);
                   Console.Error.WriteLine("[UDP_VOICE_INIT] Using 2-second buffer");
               }

               waveProvider.DiscardOnBufferOverflow = true;

               waveOut.Init(waveProvider);
               waveOut.Play();

               Console.Error.WriteLine($"[UDP_VOICE_INIT] WaveOut State: {waveOut.PlaybackState}");
               Console.Error.WriteLine($"[UDP_VOICE_INIT] Buffer Length: {waveProvider.BufferLength} bytes");

               // Initialize UDP
               udpReceiveClient = new UdpClient();
               udpSendClient = udpReceiveClient;
               udpReceiveClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

               int actualPort = ((IPEndPoint)udpReceiveClient.Client.LocalEndPoint).Port;
               Console.Error.WriteLine($"[UDP_INIT] Bound to port: {actualPort}");
               Console.WriteLine($"CMD:CLIENT_PORT:{actualPort}");

               isProcessingVoice = true;
               IsVoiceReceiverActive = true;

               // Start listener
               var listenerStarted = new TaskCompletionSource<bool>();
               _ = Task.Run(async () => await UdpVoiceListener(listenerStarted), cancellationTokenSource.Token);

               var timeoutTask = Task.Delay(2000);
               if (await Task.WhenAny(listenerStarted.Task, timeoutTask) == listenerStarted.Task)
               {
                   await Task.Delay(100);
                   Console.Error.WriteLine("[UDP_VOICE_INIT] ✅ UDP voice receiver ready!");
                   return true;
               }
               else
               {
                   Console.Error.WriteLine("[UDP_VOICE_INIT] ERROR: Listener timeout");
                   IsVoiceReceiverActive = false;
                   return false;
               }
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"[UDP_VOICE_INIT] ERROR: {ex.Message}");
               IsVoiceReceiverActive = false;
               return false;
           }
       }

       private async Task UdpVoiceListener(TaskCompletionSource<bool> readySignal = null)
       {
           Console.Error.WriteLine("[UDP_LISTENER] Starting UDP voice packet listener");
           bool signalSent = false;

           while (isProcessingVoice && !cancellationTokenSource.Token.IsCancellationRequested)
           {
               try
               {
                   if (!signalSent && readySignal != null)
                   {
                       readySignal.SetResult(true);
                       signalSent = true;
                       Console.Error.WriteLine("[UDP_LISTENER] Ready signal sent");
                   }

                   var result = await udpReceiveClient.ReceiveAsync();
                   var packet = result.Buffer;
                   var senderEndpoint = result.RemoteEndPoint;

                   // Check for command packets (4-byte ASCII header)
                   if (packet.Length >= 4)
                   {
                       string possibleCommand = Encoding.UTF8.GetString(packet, 0, 4);

                       switch (possibleCommand)
                       {
                           case "ARSP": // Auth response
                               await ProcessAuthResponse(packet);
                               continue;

                           case "PRSP": // Priority response
                               await ProcessPriorityResponse(packet);
                               continue;

                           case "PONG": // Ping response
                               continue;
                       }
                   }

                   // Voice packet: [2 bytes speakerId][4 bytes volume][2 bytes length][Opus audio]
                   if (packet.Length >= 8)
                   {
                       await ProcessUdpVoicePacket(packet, senderEndpoint);
                   }
               }
               catch (Exception ex)
               {
                   if (!cancellationTokenSource.Token.IsCancellationRequested)
                   {
                       Console.Error.WriteLine($"[UDP_LISTENER] Error: {ex.Message}");
                       await Task.Delay(100, cancellationTokenSource.Token);
                   }
               }
           }

           Console.Error.WriteLine("[UDP_LISTENER] UDP voice listener stopped");
       }

       private async Task ProcessUdpVoicePacket(byte[] packet, IPEndPoint senderEndpoint)
       {
           try
           {
               // Packet format: [2 bytes speakerId][4 bytes volume][2 bytes length][Opus audio]
               if (packet.Length < 8)
               {
                   return;
               }

               // Parse speaker ID (2 bytes at offset 0)
               ushort speakerIdShort = BitConverter.ToUInt16(packet, 0);
               string speakerId = speakerIdShort.ToString();

               // Parse volume (4 bytes at offset 2)
               float serverVolume = BitConverter.ToSingle(packet, 2);

               // Parse Opus length (2 bytes at offset 6)
               ushort opusLength = BitConverter.ToUInt16(packet, 6);

               // Extract Opus data (starts at offset 8)
               if (packet.Length < 8 + opusLength)
               {
                   return;
               }

               byte[] opusAudioData = new byte[opusLength];
               Array.Copy(packet, 8, opusAudioData, 0, opusLength);

               // Minimal logging for performance
               // Console.Error.WriteLine($"[UDP_VOICE] Received {opusLength} Opus bytes from {speakerId}");

               await ProcessIncomingUdpVoice(opusAudioData, serverVolume, speakerId);
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"[UDP_VOICE] Error: {ex.Message}");
           }
       }

       private async Task ProcessIncomingUdpVoice(byte[] opusAudioData, float serverVolume, string speakerId)
       {
           try
           {
               if (waveOut?.Volume <= 0f || waveProvider == null)
               {
                   return;
               }

               DateTime arrivalTime = DateTime.Now;

               // Track packet timing
               packetArrivalTimes.Enqueue(arrivalTime);
               while (packetArrivalTimes.Count > 50)
               {
                   packetArrivalTimes.TryDequeue(out _);
               }

               UpdateDynamicJitterBuffer(arrivalTime);

               // Decode Opus → PCM
               byte[] rawPcmData = DecodeOpusToRawPcm(opusAudioData);
               if (rawPcmData.Length == 0) return;

               // Resample to device format
               byte[] resampledData = ResampleToDeviceFormat(rawPcmData);

               // Apply volume
               byte[] finalAudio = ApplyVolumeToAudio(resampledData, serverVolume);

               // Add to jitter buffer
               jitterBuffer.Enqueue(finalAudio);

               // Drop oldest if buffer too large
               while (jitterBuffer.Count > MAX_JITTER_PACKETS)
               {
                   jitterBuffer.TryDequeue(out _);
               }

               // Play when buffer reaches target
               if (jitterBuffer.Count >= targetJitterPackets)
               {
                   while (jitterBuffer.TryDequeue(out var audioPacket))
                   {
                       int availableSpace = waveProvider.BufferLength - waveProvider.BufferedBytes;

                       if (availableSpace < audioPacket.Length)
                       {
                           waveProvider.ClearBuffer();
                       }

                       waveProvider.AddSamples(audioPacket, 0, audioPacket.Length);
                   }
               }

               lastPacketArrival = arrivalTime;
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"🔊 ERROR: {ex.Message}");
           }
       }

       private void UpdateDynamicJitterBuffer(DateTime arrivalTime)
       {
           if (expectedNextPacket == DateTime.MinValue)
           {
               expectedNextPacket = arrivalTime.AddMilliseconds(PACKET_INTERVAL_MS);
               return;
           }

           double jitterMs = (arrivalTime - expectedNextPacket).TotalMilliseconds;
           expectedNextPacket = arrivalTime.AddMilliseconds(PACKET_INTERVAL_MS);

           if (jitterMs > 10) // Late packet
           {
               consecutiveLatePackets++;
               consecutiveOnTimePackets = 0;

               if (consecutiveLatePackets >= 5 && targetJitterPackets < MAX_JITTER_PACKETS)
               {
                   targetJitterPackets++;
                   Console.Error.WriteLine($"[JITTER] Buffer increased to {targetJitterPackets * 20}ms");
                   consecutiveLatePackets = 0;
               }
           }
           else if (jitterMs < 5) // On-time packet
           {
               consecutiveOnTimePackets++;
               consecutiveLatePackets = 0;

               if (consecutiveOnTimePackets >= 20 && targetJitterPackets > MIN_JITTER_PACKETS)
               {
                   targetJitterPackets--;
                   Console.Error.WriteLine($"[JITTER] Buffer decreased to {targetJitterPackets * 20}ms");
                   consecutiveOnTimePackets = 0;
               }
           }
       }

       public UdpClient GetUdpClient()
       {
           return udpSendClient;
       }

       private byte[] DecodeOpusToRawPcm(byte[] opusData)
       {
           try
           {
               if (opusProcessor == null) return new byte[0];
               return opusProcessor.DecodeFromOpus(opusData);
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"🔊 DECODE ERROR: {ex.Message}");
               return new byte[0];
           }
       }

       private byte[] ResampleToDeviceFormat(byte[] pcm48khzData)
       {
           try
           {
               WaveFormat inputFormat = new WaveFormat(48000, 16, 1);
               WaveFormat outputFormat = detectedOutputFormat;

               // Fast path - no resampling needed
               if (inputFormat.SampleRate == outputFormat.SampleRate &&
                   inputFormat.BitsPerSample == outputFormat.BitsPerSample &&
                   inputFormat.Channels == outputFormat.Channels)
               {
                   return pcm48khzData;
               }

               using (var inputStream = new MemoryStream(pcm48khzData))
               using (var rawSource = new RawSourceWaveStream(inputStream, inputFormat))
               {
                   // Try MediaFoundationResampler (3-5x faster)
                   try
                   {
                       using (var resampler = new MediaFoundationResampler(rawSource, outputFormat))
                       {
                           resampler.ResamplerQuality = 60;

                           int expectedSize = (int)((long)pcm48khzData.Length *
                               outputFormat.SampleRate / inputFormat.SampleRate *
                               outputFormat.BitsPerSample / inputFormat.BitsPerSample *
                               outputFormat.Channels / inputFormat.Channels) + 4096;

                           byte[] outputBuffer = new byte[expectedSize];
                           int bytesRead = resampler.Read(outputBuffer, 0, outputBuffer.Length);

                           if (bytesRead == outputBuffer.Length)
                               return outputBuffer;

                           byte[] result = new byte[bytesRead];
                           Array.Copy(outputBuffer, result, bytesRead);
                           return result;
                       }
                   }
                   catch
                   {
                       // Fallback to WaveFormatConversionProvider
                       rawSource.Position = 0;
                       using (var resampler = new WaveFormatConversionProvider(outputFormat, rawSource))
                       {
                           int expectedSize = (int)((long)pcm48khzData.Length *
                               outputFormat.SampleRate / inputFormat.SampleRate) + 4096;

                           byte[] outputBuffer = new byte[expectedSize];
                           int bytesRead = resampler.Read(outputBuffer, 0, outputBuffer.Length);

                           byte[] result = new byte[bytesRead];
                           Array.Copy(outputBuffer, result, bytesRead);
                           return result;
                       }
                   }
               }
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"🔊 RESAMPLE ERROR: {ex.Message}");
               return pcm48khzData;
           }
       }

       private byte[] ApplyVolumeToAudio(byte[] audioData, float volumeMultiplier)
       {
           volumeMultiplier = Math.Max(0.0f, Math.Min(2.0f, volumeMultiplier));

           // Fast path - no volume adjustment needed
           if (Math.Abs(volumeMultiplier - 1.0f) < 0.001f)
           {
               return audioData;
           }

           byte[] adjustedAudio = new byte[audioData.Length];

           for (int i = 0; i < audioData.Length - 1; i += 2)
           {
               short sample = (short)(audioData[i] | (audioData[i + 1] << 8));
               int adjusted = (int)(sample * volumeMultiplier);
               adjusted = Math.Max(-32768, Math.Min(32767, adjusted));

               adjustedAudio[i] = (byte)(adjusted & 0xFF);
               adjustedAudio[i + 1] = (byte)((adjusted >> 8) & 0xFF);
           }

           return adjustedAudio;
       }

       private async Task ProcessAuthResponse(byte[] packet)
       {
           try
           {
               string jsonData = Encoding.UTF8.GetString(packet, 4, packet.Length - 4);
               Console.Error.WriteLine($"[UDP_AUTH] Server response: {jsonData}");
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"[UDP_AUTH] Error: {ex.Message}");
           }
       }

       private async Task ProcessPriorityResponse(byte[] packet)
       {
           try
           {
               string jsonData = Encoding.UTF8.GetString(packet, 4, packet.Length - 4);
               Console.Error.WriteLine($"[UDP_PRIORITY] Server response: {jsonData}");
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"[UDP_PRIORITY] Error: {ex.Message}");
           }
       }

       public async Task SendAuthenticationToServer(string serverIP, int port, string playerId, string voiceId)
       {
           try
           {
               Console.Error.WriteLine("⚡ SendAuthenticationToServer CALLED");

               serverEndpoint = new IPEndPoint(IPAddress.Parse(serverIP), port);
              
               udpSendClient.Connect(serverEndpoint);

               var localEndpoint = (IPEndPoint)udpSendClient.Client.LocalEndPoint;
               Console.Error.WriteLine($"[UDP_AUTH] Connected to {serverEndpoint}");
               Console.Error.WriteLine($"[UDP_AUTH] Local port: {localEndpoint.Port}");

               var authRequest = new ProximityChatManager.UdpAuthRequest
               {
                   PlayerId = playerId,
                   VoiceId = voiceId,
                   Command = "AUTH"
               };

               string jsonData = JsonSerializer.Serialize(authRequest);
               byte[] authPacket = Encoding.UTF8.GetBytes("AUTH" + jsonData);

               await udpSendClient.SendAsync(authPacket, authPacket.Length);

               Console.Error.WriteLine($"[UDP_AUTH] ✅ Authentication sent for player {playerId}");
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"❌ [UDP_AUTH] EXCEPTION: {ex.Message}");
           }
       }

       public void StopVoiceReceiver()
       {
           try
           {
               isProcessingVoice = false;
               IsVoiceReceiverActive = false;

               waveOut?.Stop();
               waveOut?.Dispose();
               waveOut = null;
               waveProvider = null;

               udpReceiveClient?.Close();
               udpReceiveClient?.Dispose();
               udpReceiveClient = null;

               udpSendClient?.Close();
               udpSendClient?.Dispose();
               udpSendClient = null;

               Console.Error.WriteLine("[UDP_VOICE_CLEANUP] Voice receiver stopped");
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"[UDP_VOICE_CLEANUP] Error: {ex.Message}");
           }
       }
       #endregion

       #region Existing Methods
       public void SetChatManagerReference(ProximityChatManager chatManager)
       {
           chatManagerRef = chatManager;
       }

       public void StoreConnectionDetails(string serverIP, string playerID, string voiceID)
       {
           storedServerIP = serverIP;
           storedPlayerID = playerID;
           storedVoiceID = voiceID;
       }

       public string GetLocalEndpoint()
       {
           try
           {
               using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
               {
                   socket.Connect("8.8.8.8", 65530);
                   var endPoint = socket.LocalEndPoint as IPEndPoint;
                   return $"{endPoint.Address}:{VoiceReceivePort}";
               }
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"Error getting local endpoint: {ex.Message}");
               return $"127.0.0.1:{VoiceReceivePort}";
           }
       }

       public void SetIncomingVolume(float volume)
       {
           try
           {
               float clampedVolume = Math.Max(0f, Math.Min(1f, volume));
               if (waveOut != null)
               {
                   waveOut.Volume = clampedVolume;
               }
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"Error setting incoming volume: {ex.Message}");
           }
       }

       public float GetIncomingVolume()
       {
           try
           {
               return waveOut?.Volume ?? 0f;
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"Error getting incoming volume: {ex.Message}");
               return 0f;
           }
       }

       public float GetCurrentVolume()
       {
           return waveOut?.Volume ?? 0f;
       }

       public void SetVolume(float volume)
       {
           if (waveOut != null)
           {
               waveOut.Volume = Math.Max(0f, Math.Min(1f, volume));
           }
       }

       public bool IsVoiceSystemReady()
       {
           return IsVoiceReceiverActive && waveOut != null && waveProvider != null && udpReceiveClient != null;
       }
       #endregion

       #region IDisposable
       public void Dispose()
       {
           StopVoiceReceiver();
           cancellationTokenSource.Cancel();
           cancellationTokenSource.Dispose();

           try
           {
               if (opusProcessor is IDisposable disposableOpus)
               {
                   disposableOpus.Dispose();
               }
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"Error disposing Opus processor: {ex.Message}");
           }
       }
       #endregion
   }
}