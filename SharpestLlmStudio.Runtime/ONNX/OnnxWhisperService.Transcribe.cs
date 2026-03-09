using SharpesLlmStudio.Media;
using SharpestLlmStudio.Shared;
using System.Runtime.CompilerServices;

namespace SharpestLlmStudio.Runtime.ONNX
{
    public partial class OnnxWhisperService
    {
        public const int WhisperSampleRate = 16000;
        public const int WhisperChannels = 1;


        public async Task<AudioObj> PrepareForWhisperAsync(AudioObj source)
        {
            var prepared = new AudioObj(
                (float[])source.Data.Clone(),
                source.SampleRate,
                source.Channels,
                source.BitDepth,
                source.Name + "_whisper");

            if (prepared.SampleRate != WhisperSampleRate)
            {
                await prepared.ResampleAsync(WhisperSampleRate);
            }

            if (prepared.Channels != WhisperChannels)
            {
                await prepared.RechannelAsync(WhisperChannels);
            }

            return prepared;
        }


        public async Task TranscribeAsync(AudioObj audio, Action<string>? onSegment = null, string? language = null, bool timestamps = false, bool speakers = false, CancellationToken cancellationToken = default)
        {
            await foreach (var segment in this.TranscribeAsyncEnumerable(audio, language, timestamps, speakers, cancellationToken))
            {
                onSegment?.Invoke(segment);
            }
        }


        public async Task TranscribeFileAsync(string filePath, Action<string>? onSegment = null, string? language = null, bool timestamps = false, bool speakers = false, CancellationToken cancellationToken = default)
        {
            await foreach (var segment in this.TranscribeFileAsyncEnumerable(filePath, language, timestamps, speakers, cancellationToken))
            {
                onSegment?.Invoke(segment);
            }
        }


        public async IAsyncEnumerable<string> TranscribeAsyncEnumerable(AudioObj audio, string? language = null, bool timestamps = false, bool speakers = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_processor == null)
            {
                throw new InvalidOperationException("No Whisper model loaded.");
            }

            this.IsTranscribing = true;
            try
            {
                var prepared = await PrepareForWhisperAsync(audio);
                await foreach (var segment in this.TranscribeSamplesAsyncEnumerable(prepared.Data, prepared.SampleRate, language, timestamps, speakers, cancellationToken))
                {
                    yield return segment;
                }
            }
            finally
            {
                this.IsTranscribing = false;
            }
        }


        public async IAsyncEnumerable<string> TranscribeFileAsyncEnumerable(string filePath, string? language = null, bool timestamps = false, bool speakers = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_processor == null)
            {
                throw new InvalidOperationException("No Whisper model loaded.");
            }

            var audioObj = Audio.ImportAudio(filePath);
            if (audioObj == null)
            {
                throw new FileNotFoundException("Unable to load audio file.", filePath);
            }

            await foreach (var segment in this.TranscribeAsyncEnumerable(audioObj, language, timestamps, speakers, cancellationToken))
            {
                yield return segment;
            }
        }


        public async Task TranscribeSamplesAsync(float[] samples, int sampleRate, Action<string>? onSegment = null, string? language = null, bool timestamps = false, bool speakers = false, CancellationToken cancellationToken = default)
        {
            await foreach (var segment in this.TranscribeSamplesAsyncEnumerable(samples, sampleRate, language, timestamps, speakers, cancellationToken))
            {
                onSegment?.Invoke(segment);
            }
        }


        public async IAsyncEnumerable<string> TranscribeSamplesAsyncEnumerable(float[] samples, int sampleRate, string? language = null, bool timestamps = false, bool speakers = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_processor == null)
            {
                throw new InvalidOperationException("No Whisper model loaded.");
            }
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                int bitsPerSample = 16;
                int byteRate = sampleRate * WhisperChannels * (bitsPerSample / 8);
                int blockAlign = WhisperChannels * (bitsPerSample / 8);
                int dataSize = samples.Length * (bitsPerSample / 8);

                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataSize);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

                writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)WhisperChannels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write((short)blockAlign);
                writer.Write((short)bitsPerSample);

                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(dataSize);

                foreach (float sample in samples)
                {
                    short s16 = (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);
                    writer.Write(s16);
                }
            }

            ms.Position = 0;

            // If a factory is available and a specific language is requested, create a transient processor with that language.
            object? procObj = null;
            bool disposeProc = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(language) && _factory != null)
                {
                    try
                    {
                        procObj = _factory.CreateBuilder().WithLanguage(language ?? "auto").Build();
                        disposeProc = true;
                    }
                    catch
                    {
                        procObj = _processor; // fallback
                        disposeProc = false;
                    }
                }
                else
                {
                    procObj = _processor;
                }

                // Pick ProcessAsync overload explicitly to avoid ambiguous matches.
                var processAsyncMethod = procObj.GetType()
                    .GetMethods()
                    .Where(m => string.Equals(m.Name, "ProcessAsync", StringComparison.Ordinal))
                    .FirstOrDefault(m =>
                    {
                        var parameters = m.GetParameters();
                        return parameters.Length == 2
                            && typeof(Stream).IsAssignableFrom(parameters[0].ParameterType)
                            && parameters[1].ParameterType == typeof(CancellationToken);
                    });

                processAsyncMethod ??= procObj.GetType()
                    .GetMethods()
                    .Where(m => string.Equals(m.Name, "ProcessAsync", StringComparison.Ordinal))
                    .FirstOrDefault(m =>
                    {
                        var parameters = m.GetParameters();
                        return parameters.Length == 1
                            && typeof(Stream).IsAssignableFrom(parameters[0].ParameterType);
                    });

                if (processAsyncMethod == null)
                {
                    yield break;
                }

                object?[] processArgs = processAsyncMethod.GetParameters().Length == 2
                    ? [ms, cancellationToken]
                    : [ms];

                var enumerable = processAsyncMethod.Invoke(procObj, processArgs);
                if (enumerable == null)
                {
                    yield break;
                }

                await foreach (var segment in ((IAsyncEnumerable<object>)enumerable))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string text = ExtractSegmentText(segment);
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    string prefix = string.Empty;
                    if (timestamps)
                    {
                        var ts = ExtractSegmentTimestamps(segment);
                        if (ts != null)
                        {
                            prefix += $"[{FormatTime(ts.Value.start)} - {FormatTime(ts.Value.end)}] ";
                        }
                    }

                    if (speakers)
                    {
                        var sp = ExtractSegmentSpeaker(segment);
                        if (!string.IsNullOrEmpty(sp))
                        {
                            prefix += $"{sp}: ";
                        }
                    }

                    yield return prefix + text;
                }
            }
            finally
            {
                if (disposeProc && procObj != null)
                {
                    try { (procObj as IDisposable)?.Dispose(); } catch { }
                }
            }
        }

        private static string ExtractSegmentText(object segment)
        {
            if (segment == null) return string.Empty;
            var t = segment.GetType();
            var prop = t.GetProperty("Text") ?? t.GetProperty("text") ?? t.GetProperty("Transcript");
            if (prop != null)
            {
                var v = prop.GetValue(segment);
                return v?.ToString()?.Trim() ?? string.Empty;
            }
            return segment.ToString() ?? string.Empty;
        }

        private static (double start, double end)? ExtractSegmentTimestamps(object segment)
        {
            if (segment == null) return null;
            var t = segment.GetType();
            // try common property names
            var startProp = t.GetProperty("Start") ?? t.GetProperty("StartTime") ?? t.GetProperty("start");
            var endProp = t.GetProperty("End") ?? t.GetProperty("EndTime") ?? t.GetProperty("end");
            if (startProp != null && endProp != null)
            {
                try
                {
                    var sVal = startProp.GetValue(segment);
                    var eVal = endProp.GetValue(segment);
                    double s = Convert.ToDouble(sVal);
                    double e = Convert.ToDouble(eVal);
                    return (s, e);
                }
                catch
                {
                    // ignore
                }
            }

            // try Timestamp or Duration-based
            var tsProp = t.GetProperty("Timestamp") ?? t.GetProperty("Time");
            if (tsProp != null)
            {
                try
                {
                    var v = tsProp.GetValue(segment);
                    double val = Convert.ToDouble(v);
                    return (val, val);
                }
                catch { }
            }

            return null;
        }

        private static string ExtractSegmentSpeaker(object segment)
        {
            if (segment == null) return string.Empty;
            var t = segment.GetType();
            var prop = t.GetProperty("Speaker") ?? t.GetProperty("speaker") ?? t.GetProperty("SpeakerLabel") ?? t.GetProperty("SpeakerId");
            if (prop != null)
            {
                var v = prop.GetValue(segment);
                return v?.ToString() ?? string.Empty;
            }
            return string.Empty;
        }

        private static string FormatTime(double seconds)
        {
            try
            {
                var ts = TimeSpan.FromSeconds(seconds);
                if (ts.TotalHours >= 1)
                    return ts.ToString(@"hh\:mm\:ss\.fff");
                return ts.ToString(@"mm\:ss\.fff");
            }
            catch
            {
                return seconds.ToString("F2") + "s";
            }
        }
    }
}
