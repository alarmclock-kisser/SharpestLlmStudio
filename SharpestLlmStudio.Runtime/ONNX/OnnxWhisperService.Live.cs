using NAudio.Wave;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime.ONNX
{
    public partial class OnnxWhisperService
    {
        private CancellationTokenSource? _liveCts;
        private readonly SemaphoreSlim _liveLock = new(1, 1);

        private const int LiveChunkSeconds = 3;


        public async Task StartLiveModeAsync(Action<string>? onText = null, string? language = null, bool timestamps = false, bool speakers = false, Action<float>? onLevel = null)
        {
            if (_processor == null)
            {
                StaticLogger.Log("Cannot start live mode: no Whisper model loaded.");
                return;
            }

            if (this.IsLiveMode)
            {
                return;
            }

            this.IsLiveMode = true;
            _liveCts = new CancellationTokenSource();
            var ct = _liveCts.Token;

            int micIndex = Audio.FindActiveMicrophoneIndex();
            if (micIndex < 0)
            {
                StaticLogger.Log("No microphone found for live mode.");
                this.IsLiveMode = false;
                return;
            }

            _ = Task.Run(async () =>
            {
                var buffer = new List<float>();
                var waveIn = new WaveInEvent
                {
                    DeviceNumber = micIndex,
                    WaveFormat = new WaveFormat(WhisperSampleRate, 16, WhisperChannels)
                };

                waveIn.DataAvailable += (s, e) =>
                {
                    float peak = 0f;
                    for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                    {
                        short sample = BitConverter.ToInt16(e.Buffer, i);
                        float normalized = sample / 32768f;
                        buffer.Add(normalized);
                        peak = Math.Max(peak, Math.Abs(normalized));
                    }

                    try { onLevel?.Invoke(Math.Clamp(peak, 0f, 1f)); } catch { }
                };

                waveIn.StartRecording();
                StaticLogger.Log("Live transcription started.");

                int chunkSize = WhisperSampleRate * LiveChunkSeconds;

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(LiveChunkSeconds * 1000, ct).ConfigureAwait(false);

                        float[] chunk;
                        lock (buffer)
                        {
                            if (buffer.Count < chunkSize / 2)
                            {
                                continue;
                            }

                            chunk = [.. buffer];
                            buffer.Clear();
                        }

                        await _liveLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            await TranscribeSamplesAsync(chunk, WhisperSampleRate, onText, language, timestamps, speakers, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            StaticLogger.Log($"Live transcription chunk error: {ex.Message}");
                        }
                        finally
                        {
                            _liveLock.Release();
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    try { waveIn.StopRecording(); } catch { }
                    waveIn.Dispose();
                    try { onLevel?.Invoke(0f); } catch { }
                    this.IsLiveMode = false;
                    StaticLogger.Log("Live transcription stopped.");
                }
            }, ct);
        }


        public void StopLiveMode()
        {
            if (_liveCts != null)
            {
                try { _liveCts.Cancel(); } catch { }
                try { _liveCts.Dispose(); } catch { }
                _liveCts = null;
            }

            this.IsLiveMode = false;
        }
    }
}
