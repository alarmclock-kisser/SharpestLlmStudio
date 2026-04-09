using Microsoft.ML.OnnxRuntime;
using SharpestLlmStudio.Shared;
using SharpesLlmStudio.Media;
using Whisper.net;
using System;
using System.Collections.Generic;
using System.Text;

namespace SharpestLlmStudio.Runtime.ONNX
{
    public partial class OnnxWhisperService : IDisposable
    {
        private readonly WebAppSettings Settings;
        private readonly AudioHandling _audioHandling;

        // Whisper.net (GGML .bin)
        private WhisperFactory? _factory;
        private WhisperProcessor? _processor;

        // ONNX Runtime
        private readonly DxgiHelper _dxgiHelper = new();
        private InferenceSession? _session;

        public List<string> WhisperModelDirectories { get; set; } = [];
        public List<OnnxWhisperModel> WhisperModels { get; set; } = [];
        public OnnxWhisperModel? LoadedModel { get; set; }
        public bool IsLoaded => _processor != null || _session != null;
        public bool IsTranscribing { get; internal set; }
        public bool IsLiveMode { get; internal set; }

        public List<string> DirectMlDevices { get; private set; } = [];
        public AudioHandling Audio => _audioHandling;


        // Ctor
        public OnnxWhisperService(WebAppSettings settings)
        {
            this.Settings = settings;
            this._audioHandling = new AudioHandling();
        }

        public List<string> EnsureDirectMlDevicesLoaded()
        {
            if (this.DirectMlDevices.Count == 0)
            {
                this.DirectMlDevices = this._dxgiHelper.GetDirectMlDevices();
            }

            return this.DirectMlDevices;
        }



        public string[] GetWhisperModels()
        {
            this.WhisperModels.Clear();
            this.WhisperModelDirectories = (this.Settings.WhisperModelDirectories ?? [])
                .Select(Environment.ExpandEnvironmentVariables)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToList();

            // Get every subdir as a model root that contains *.bin or *.onnx files (with might have *.json config files)
            foreach (var modelDir in WhisperModelDirectories)
            {
                var subdirs = Directory.GetDirectories(modelDir);
                foreach (var subdir in subdirs)
                {
                    var modelFiles = Directory.GetFiles(subdir, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (modelFiles.Length > 0)
                    {
                        foreach (var modelFile in modelFiles)
                        {
                            var modelName = Path.GetFileNameWithoutExtension(modelFile);
                            var configFiles = Directory.GetFiles(subdir, "*.json", SearchOption.TopDirectoryOnly);
                            if (!this.WhisperModels.Any(m => m.ModelFilePath.Equals(modelFile, StringComparison.OrdinalIgnoreCase)))
                            {
                                this.WhisperModels.Add(new OnnxWhisperModel
                                {
                                    ModelRootDirectory = subdir,
                                    ModelFilePath = modelFile,
                                    ModelName = modelName,
                                    ConfigurationFiles = Path.GetExtension(modelFile).ToLowerInvariant().Equals(".onnx") ? configFiles : null
                                });
                            }
                        }
                    }
                }
            }

            return this.WhisperModels.Select(m => m.ModelFilePath).ToArray();
        }


        public bool LoadModel(string modelPath, int dmlDeviceId = 0)
        {
            this.StopLiveMode();
            this.DisposeSession();
            try { this._processor?.Dispose(); } catch { }
            try { this._factory?.Dispose(); } catch { }
            this._processor = null;
            this._factory = null;

            if (modelPath.StartsWith("/res", StringComparison.OrdinalIgnoreCase))
            {
                return this.LoadModelFromRessource(dmlDeviceId);
            }

            if (modelPath.StartsWith("/default", StringComparison.OrdinalIgnoreCase))
            {
                modelPath = this.WhisperModels.Count > 0 ? this.WhisperModels[0].ModelFilePath : string.Empty;
            }

            if (!File.Exists(modelPath))
            {
                StaticLogger.Log($"Model file not found: {modelPath}");
                return false;
            }

            try
            {
                var options = new SessionOptions();
                options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;

                try
                {
                    // First if available use DirectML on dmlDeviceId
                    options.AppendExecutionProvider_DML(dmlDeviceId);
                    options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    this._session = new InferenceSession(modelPath, options);

                    StaticLogger.Log($"Using DirectML execution provider: [{dmlDeviceId}]");
                }
                catch (Exception ex)
                {
                    try
                    {
                        // First if available use DirectML on Desktop GPU
                        options.AppendExecutionProvider_DML((dmlDeviceId > 0 ? 0 : 1));
                        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                        this._session = new InferenceSession(modelPath, options);

                        StaticLogger.Log($"Using DirectML execution provider: [{(dmlDeviceId > 0 ? 0 : 1)}] GPU");
                    }
                    catch (Exception ex2)
                    {
                        StaticLogger.Log($"DirectML not available: {ex.Message}, {ex2.Message}");
                        StaticLogger.Log("Falling back to CPU execution provider.");
                        options.AppendExecutionProvider_CPU();
                        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                        this._session = new InferenceSession(modelPath, options);
                    }
                }

            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error loading model: {ex.Message}");
                return false;
            }

            this.LoadedModel = this.WhisperModels.FirstOrDefault(m => m.ModelFilePath.Equals(modelPath, StringComparison.OrdinalIgnoreCase))
                ?? new OnnxWhisperModel { ModelFilePath = modelPath, ModelName = Path.GetFileNameWithoutExtension(modelPath) };
            StaticLogger.Log($"Model loaded successfully: {modelPath}");
            return true;
        }

        public bool LoadModelFromRessource(int dmlDeviceId = 0)
        {
            try
            {
                var assembly = typeof(OnnxWhisperService).Assembly;
                // Der Name muss exakt sein: [ProjektNamespace].[Ordner].[Dateiname]
                string resourceName = "OnnxBpmScanner.Runtime.Ressources.beat_this.onnx";

                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    return false;
                }

                using MemoryStream ms = new MemoryStream();
                stream.CopyTo(ms);
                byte[] modelBytes = ms.ToArray();

                this.LoadedModel = new OnnxWhisperModel { ModelName = resourceName };

                try
                {
                    var options = new SessionOptions();
                    options.AppendExecutionProvider_DML(dmlDeviceId);

                    this._session = new InferenceSession(modelBytes, options);
                    return true;
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"DirectML not available: {ex.Message}");
                    StaticLogger.Log("Falling back to CPU execution provider.");
                    var options = new SessionOptions();
                    options.AppendExecutionProvider_CPU();
                    this._session = new InferenceSession(modelBytes, options);
                    return true;
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Resource Load Error: {ex.Message}");
                return false;
            }
        }






        // ── Whisper.net model loading (.bin) ──

        public async Task<bool> LoadModelAsync(string modelPath)
        {
            if (Path.GetExtension(modelPath).Equals(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                return await Task.Run(() => this.LoadModel(modelPath));
            }

            UnloadModel();

            if (!File.Exists(modelPath))
            {
                StaticLogger.Log($"Whisper model file not found: {modelPath}");
                return false;
            }

            try
            {
                await Task.Run(() =>
                {
                    _factory = WhisperFactory.FromPath(modelPath);
                    _processor = _factory.CreateBuilder()
                        .WithLanguage("auto")
                        .Build();
                });

                this.LoadedModel = this.WhisperModels.FirstOrDefault(m =>
                    m.ModelFilePath.Equals(modelPath, StringComparison.OrdinalIgnoreCase))
                    ?? new OnnxWhisperModel { ModelFilePath = modelPath, ModelName = Path.GetFileNameWithoutExtension(modelPath) };

                StaticLogger.Log($"Whisper.net model loaded: {Path.GetFileName(modelPath)}");
                return true;
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error loading Whisper.net model: {ex.Message}");
                UnloadModel();
                return false;
            }
        }

        public void UnloadModel()
        {
            this.StopLiveMode();
            this.DisposeSession();
            try { _processor?.Dispose(); } catch { }
            try { _factory?.Dispose(); } catch { }
            _processor = null;
            _factory = null;
            this.LoadedModel = null;
            this.IsTranscribing = false;
            this.IsLiveMode = false;
        }


        // ── ONNX session management ──

        public void DisposeSession()
        {
            if (this._session != null)
            {
                this._session.Dispose();
                this._session = null;
                this.LoadedModel = null;
                StaticLogger.Log("ONNX session disposed.");
            }
        }


        public void Dispose()
        {
            StopLiveMode();
            UnloadModel();
            this.DisposeSession();
            _audioHandling.DisposeAsync().AsTask().GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }

    }
}
