using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices;
using NAudio.Wave;
using System.Text.Json;
using NAudio.CoreAudioApi;
using System.IO;
using System.Text;
using System.Collections.Concurrent;
using System.Linq;
using Concentus.Structs;
using Concentus.Enums;

namespace ConsoleApp1
{
   public class VoiceManager : IDisposable
   {
       [DllImport("winmm.dll")]
       private static extern int timeBeginPeriod(int uPeriod);
       [DllImport("winmm.dll")]
       private static extern int timeEndPeriod(int uPeriod);

       #region Fields
       private UdpClient udpSendClient;
       private OpusAudioProcessor opusProcessor;
       private readonly ConcurrentDictionary<string, OpusDecoder> perSpeakerDecoders = new();
       private UdpClient udpReceiveClient;
       private IPEndPoint serverEndpoint;

        // Dynamic Jitter Buffer - Per-speaker for proper multi-player mixing
        private readonly ConcurrentDictionary<string, ConcurrentQueue<byte[]>> speakerBuffers = new();
        private readonly ConcurrentQueue<DateTime> packetArrivalTimes = new ConcurrentQueue<DateTime>();
        private int MIN_JITTER_PACKETS = 1;    // Will adjust for Bluetooth
        private int MAX_JITTER_PACKETS = 15;   // Will adjust for Bluetooth
        private int targetJitterPackets = 4;   // Will adjust for Bluetooth
        private readonly ConcurrentDictionary<string, bool> speakerInitialBufferFilled = new();
        private readonly ConcurrentDictionary<string, DateTime> speakerLastSeen = new();
        private DateTime lastSpeakerCleanup = DateTime.Now;
        private DateTime lastPacketArrival = DateTime.MinValue;
        private DateTime expectedNextPacket = DateTime.MinValue;
        private const int PACKET_INTERVAL_MS = 20;
        private int consecutiveLatePackets = 0;
        private int consecutiveOnTimePackets = 0;

       private bool isProcessingVoice = false;
       private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

       //777592 - Speaker icon tracking — sends CMD:SPEAKING/CMD:SILENT to Flash
       private HashSet<string> previousActiveSpeakers = new();
       private Dictionary<string, int> speakerSilentFrames = new();

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
        public float MasterGain { get; set; } = 1.0f;
        private float limiterGain = 1.0f; // Smoothed limiter gain (attack/release)
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
                   waveOut.DesiredLatency = 200; // 200ms for wired/USB
                   waveOut.NumberOfBuffers = 3;  // 3 buffers for smoother playback
               }

               // Use Opus native format (48kHz/16-bit/mono) - WaveOut handles device conversion
               detectedOutputFormat = new WaveFormat(48000, 16, 1);
               Console.Error.WriteLine($"[UDP_VOICE_INIT] Output Format: {detectedOutputFormat} (device: {detectedSampleRate}Hz/{detectedBits}-bit/{detectedChannels}ch)");

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

                // Start audio mixing loop (mixes all speaker buffers concurrently)
                _ = Task.Run(() => AudioMixingLoop(), cancellationTokenSource.Token);

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

               // Skip own voice — don't play back what we sent
               if (speakerId == storedPlayerID)
                   return;

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
                     return;

                 DateTime arrivalTime = DateTime.Now;

                 // Track packet timing
                 packetArrivalTimes.Enqueue(arrivalTime);
                 while (packetArrivalTimes.Count > 50)
                     packetArrivalTimes.TryDequeue(out _);

                 UpdateDynamicJitterBuffer(arrivalTime);

                 // Decode Opus → PCM (per-speaker decoder prevents state corruption)
                 byte[] rawPcmData = DecodeOpusToRawPcm(opusAudioData, speakerId);
                 if (rawPcmData.Length == 0) return;

                 // Resample to device format
                 byte[] resampledData = ResampleToDeviceFormat(rawPcmData);

                 // Apply server-side proximity/priority volume
                 byte[] finalAudio = ApplyVolumeToAudio(resampledData, serverVolume);

                 // Add to per-speaker jitter buffer
                 var speakerQueue = speakerBuffers.GetOrAdd(speakerId, _ => new ConcurrentQueue<byte[]>());
                 speakerQueue.Enqueue(finalAudio);
                 speakerLastSeen[speakerId] = DateTime.Now;

                 // Drop oldest if speaker buffer too large (prevent memory growth)
                 while (speakerQueue.Count > MAX_JITTER_PACKETS)
                     speakerQueue.TryDequeue(out _);

                 // NOTE: We do NOT drain/play here anymore.
                 // The AudioMixingLoop handles mixing all speakers concurrently.

                 lastPacketArrival = arrivalTime;
             }
             catch (Exception ex)
             {
                 Console.Error.WriteLine($"🔊 ERROR: {ex.Message}");
             }
         }

        /// <summary>
        /// Runs on a background thread. Every ~15ms, takes ONE packet from each active speaker,
        /// mixes them together (sum with clipping), applies MasterGain, and pushes to WaveOut.
        /// This ensures 20ms real-time = 20ms audio regardless of speaker count.
        /// </summary>
        private void AudioMixingLoop()
        {
            // Force 1ms timer resolution for accurate Thread.Sleep
            timeBeginPeriod(1);
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            Console.Error.WriteLine("[MIXER] Audio mixing loop started (1ms timer, AboveNormal priority)");
            int packetSize = 0;

            // Wait for first packet to learn the PCM packet size
            while (packetSize == 0 && isProcessingVoice && !cancellationTokenSource.Token.IsCancellationRequested)
            {
                foreach (var queue in speakerBuffers.Values)
                {
                    if (queue.TryPeek(out var peeked))
                    {
                        packetSize = peeked.Length;
                        break;
                    }
                }
                Thread.Sleep(10);
            }

            if (packetSize == 0) return;
            Console.Error.WriteLine($"[MIXER] Detected packet size: {packetSize} bytes ({packetSize / 2} samples)");

            int sampleCount = packetSize / 2; // 16-bit audio
            int mixCycleCount = 0;
            int audioProducedCount = 0;
            int skippedCount = 0;
            long mixedSampleSum = 0; // For RMS calculation
            int mixedPeakSample = 0;
            int silentFrameCount = 0; // frames where peak < 100
            var diagTimer = System.Diagnostics.Stopwatch.StartNew();

            while (isProcessingVoice && !cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    mixCycleCount++;

                    // Prevent buffer from growing indefinitely: if WaveOut buffer is
                    // already well-stocked (>400ms), skip this cycle to let it drain.
                    if (waveProvider != null && waveProvider.BufferedDuration.TotalMilliseconds > 400)
                    {
                        Thread.Sleep(18);
                        continue;
                    }

                    // Cleanup stale speakers every 10 seconds
                    if ((DateTime.Now - lastSpeakerCleanup).TotalSeconds >= 10)
                    {
                        foreach (var kvp in speakerLastSeen)
                        {
                            if ((DateTime.Now - kvp.Value).TotalSeconds > 10)
                            {
                                speakerBuffers.TryRemove(kvp.Key, out _);
                                speakerInitialBufferFilled.TryRemove(kvp.Key, out _);
                                perSpeakerDecoders.TryRemove(kvp.Key, out _);
                                speakerLastSeen.TryRemove(kvp.Key, out _);
                            }
                        }
                        lastSpeakerCleanup = DateTime.Now;
                    }

                    int[] mixBuffer = new int[sampleCount];
                    bool hasAudio = false;
                    int activeSpeakers = 0;
                    var currentActiveSpeakerIds = new HashSet<string>(); //777592

                    foreach (var kvp in speakerBuffers)
                    {
                        var queue = kvp.Value;
                        string speaker = kvp.Key;

                        // Initial buffering: wait for targetJitterPackets before first play
                        if (!speakerInitialBufferFilled.ContainsKey(speaker))
                        {
                            if (queue.Count < targetJitterPackets)
                            {
                                skippedCount++;
                                continue;
                            }
                            speakerInitialBufferFilled[speaker] = true;
                            Console.Error.WriteLine($"[MIXER] Speaker {speaker} initial buffer filled ({queue.Count} packets)");
                        }

                        if (queue.Count < MIN_JITTER_PACKETS)
                        {
                            skippedCount++;
                            continue;
                        }

                        if (queue.TryDequeue(out byte[] audioPacket))
                        {
                            hasAudio = true;
                            activeSpeakers++;
                            currentActiveSpeakerIds.Add(speaker); //777592

                            int samples = Math.Min(sampleCount, audioPacket.Length / 2);

                            for (int i = 0; i < samples; i++)
                            {
                                short sample = (short)(audioPacket[i * 2] | (audioPacket[i * 2 + 1] << 8));
                                mixBuffer[i] += sample;
                            }
                        }
                    }

                    //777592 - Speaker icon events: notify Flash when speakers start/stop
                    foreach (var id in currentActiveSpeakerIds)
                    {
                        speakerSilentFrames.Remove(id);
                        if (!previousActiveSpeakers.Contains(id))
                            Console.Error.WriteLine($"CMD:SPEAKING:{id}");
                    }
                    foreach (var id in previousActiveSpeakers)
                    {
                        if (!currentActiveSpeakerIds.Contains(id))
                        {
                            speakerSilentFrames.TryGetValue(id, out int frames);
                            frames++;
                            if (frames >= 15) // ~300ms debounce
                            {
                                Console.Error.WriteLine($"CMD:SILENT:{id}");
                                speakerSilentFrames.Remove(id);
                            }
                            else
                            {
                                speakerSilentFrames[id] = frames;
                            }
                        }
                    }
                    // Keep speakers with pending silent frames as "previous" so debounce continues
                    previousActiveSpeakers = new HashSet<string>(currentActiveSpeakerIds);
                    foreach (var id in speakerSilentFrames.Keys)
                        previousActiveSpeakers.Add(id);

                    if (hasAudio && waveProvider != null)
                    {
                        audioProducedCount++;
                        byte[] finalBytes = new byte[packetSize];

                        // Peak limiter (WebRTC-style): only reduce gain when mix actually clips
                        // Find peak of the raw mix
                        int framePeakRaw = 0;
                        for (int i = 0; i < sampleCount; i++)
                        {
                            int abs = Math.Abs(mixBuffer[i]);
                            if (abs > framePeakRaw) framePeakRaw = abs;
                        }

                        // Calculate needed gain for this frame (1.0 if no clipping)
                        float neededGain = framePeakRaw > 32767 ? 32767f / framePeakRaw : 1.0f;

                        // Smooth limiter: fast attack (~1 frame), slow release (~50ms = ~2.5 frames)
                        if (neededGain < limiterGain)
                            limiterGain = neededGain; // Instant attack — prevent clipping immediately
                        else
                            limiterGain = limiterGain + (neededGain - limiterGain) * 0.4f; // ~50ms release

                        float gain = MasterGain * limiterGain;

                        for (int i = 0; i < sampleCount; i++)
                        {
                            int mixed = (int)(mixBuffer[i] * gain);

                            if (mixed > 32767) mixed = 32767;
                            else if (mixed < -32768) mixed = -32768;

                            finalBytes[i * 2] = (byte)(mixed & 0xFF);
                            finalBytes[i * 2 + 1] = (byte)((mixed >> 8) & 0xFF);
                        }

                        // Track audio levels (reuse framePeakRaw from limiter)
                        for (int i = 0; i < sampleCount; i++)
                            mixedSampleSum += (long)mixBuffer[i] * mixBuffer[i];
                        if (framePeakRaw > mixedPeakSample) mixedPeakSample = framePeakRaw;
                        if (framePeakRaw < 100) silentFrameCount++;

                        waveProvider.AddSamples(finalBytes, 0, finalBytes.Length);
                    }

                    // Diagnostic log every 2 seconds
                    if (diagTimer.ElapsedMilliseconds >= 2000)
                    {
                        int speakerCount = speakerBuffers.Count;
                        string bufferInfo = "";
                        foreach (var kvp in speakerBuffers)
                            bufferInfo += $" [{kvp.Key}:{kvp.Value.Count}]";

                        int bufferedMs = waveProvider != null ? (int)(waveProvider.BufferedDuration.TotalMilliseconds) : -1;
                        double rms = audioProducedCount > 0 ? Math.Sqrt(mixedSampleSum / (double)(audioProducedCount * sampleCount)) : 0;

                        Console.Error.WriteLine($"[MIXER_DIAG] cycles={mixCycleCount} produced={audioProducedCount} skipped={skippedCount} silent={silentFrameCount} peak={mixedPeakSample} rms={rms:F0} waveOutBuf={bufferedMs}ms queues:{bufferInfo}");

                        mixCycleCount = 0;
                        audioProducedCount = 0;
                        skippedCount = 0;
                        mixedSampleSum = 0;
                        mixedPeakSample = 0;
                        silentFrameCount = 0;
                        diagTimer.Restart();
                    }

                    Thread.Sleep(18);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[MIXER] Error: {ex.Message}");
                    Thread.Sleep(50);
                }
            }

            timeEndPeriod(1);
            Console.Error.WriteLine("[MIXER] Audio mixing loop stopped");
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

       private byte[] DecodeOpusToRawPcm(byte[] opusData, string speakerId)
       {
           try
           {
               // Each speaker gets their own decoder to prevent state corruption
               var decoder = perSpeakerDecoders.GetOrAdd(speakerId, _ => new OpusDecoder(48000, 1));

               short[] pcmOutput = new short[960]; // 20ms at 48kHz mono
               int decodedSamples = decoder.Decode(opusData, 0, opusData.Length, pcmOutput, 0, pcmOutput.Length, false);

               if (decodedSamples <= 0) return new byte[0];

               byte[] result = new byte[decodedSamples * 2];
               Buffer.BlockCopy(pcmOutput, 0, result, 0, result.Length);
               return result;
           }
           catch (Exception ex)
           {
               Console.Error.WriteLine($"DECODE ERROR [{speakerId}]: {ex.Message}");
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

                var localEndpoint = (IPEndPoint)udpSendClient.Client.LocalEndPoint;
                Console.Error.WriteLine($"[UDP_AUTH] Target server: {serverEndpoint}");
                Console.Error.WriteLine($"[UDP_AUTH] Local port: {localEndpoint.Port}");

                var authRequest = new ProximityChatManager.UdpAuthRequest
                {
                    PlayerId = playerId,
                    VoiceId = voiceId,
                    Command = "AUTH"
                };

                string jsonData = JsonSerializer.Serialize(authRequest);
                byte[] authPacket = Encoding.UTF8.GetBytes("AUTH" + jsonData);

                await udpSendClient.SendAsync(authPacket, authPacket.Length, serverEndpoint);

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

               // udpSendClient and udpReceiveClient are the same object - only dispose once
               udpSendClient = null;
               udpReceiveClient?.Close();
               udpReceiveClient?.Dispose();
               udpReceiveClient = null;

               // Clean up per-speaker decoders
               perSpeakerDecoders.Clear();
               speakerBuffers.Clear();

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
                // Volume > 1.0 uses software gain (MasterGain) for amplification
                // Volume 0-1.0 uses hardware volume (waveOut.Volume)
                if (volume > 1.0f)
                {
                    MasterGain = volume; // e.g. 1.5 = 150% via DSP
                    if (waveOut != null) waveOut.Volume = 1.0f;
                }
                else
                {
                    MasterGain = 1.0f;
                    float clampedVolume = Math.Max(0f, Math.Min(1f, volume));
                    if (waveOut != null)
                    {
                        waveOut.Volume = clampedVolume;
                    }
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
                if (MasterGain > 1.0f) return MasterGain;
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
            if (MasterGain > 1.0f) return MasterGain;
            return waveOut?.Volume ?? 0f;
        }

        public void SetVolume(float volume)
        {
            SetIncomingVolume(volume); // Unified: uses MasterGain for > 1.0
        }

       public bool IsVoiceSystemReady()
       {
           return IsVoiceReceiverActive && waveOut != null && waveProvider != null && udpReceiveClient != null;
       }
       #endregion

       #region IDisposable
       public void Dispose()
       {
           // Cancel first so background tasks (UDP listener, audio mixer) can exit their loops
           cancellationTokenSource.Cancel();

           // Give background tasks a moment to notice cancellation before we yank resources
           Thread.Sleep(200);

           StopVoiceReceiver();

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

           try { cancellationTokenSource.Dispose(); } catch { }

           Console.Error.WriteLine("[VOICE_MANAGER] Dispose complete");
       }
       #endregion
   }
}