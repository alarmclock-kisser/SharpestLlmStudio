using SharpestLlmStudio.Shared;
using Vortice.DXGI;

namespace SharpestLlmStudio.Runtime.ONNX
{
    internal class DxgiHelper
    {
        internal List<string> GetDirectMlDevices()
        {
            var deviceNames = new List<string>();
            try
            {
                // Erstellt eine DXGI-Factory, um die Hardware-Adapter aufzulisten
                if (DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).Success && factory != null)
                {
                    for (int i = 0; factory.EnumAdapters1((uint)i, out IDXGIAdapter1 adapter).Success; i++)
                    {
                        AdapterDescription1 desc = adapter.Description1;

                        // Wir filtern nach echten GPUs (Hardware-Adapter)
                        // Software-Renderer (wie Microsoft Basic Render Driver) ignorieren wir meist
                        if ((desc.Flags & AdapterFlags.Software) == AdapterFlags.None)
                        {
                            string name = desc.Description; // Z.B. "NVIDIA GeForce RTX 3060"
                            deviceNames.Add($"{i}: {name}");
                        }

                        adapter.Dispose();
                    }
                    factory.Dispose();
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[DXGI] Fehler beim Auslesen der GPUs: {ex.Message}");
            }

            // Falls gar nichts gefunden wurde (Fallback auf CPU)
            if (deviceNames.Count == 0)
            {
                deviceNames.Add("CPU");
            }

            return deviceNames;
        }




    }
}
