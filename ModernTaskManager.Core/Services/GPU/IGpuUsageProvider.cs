// Ruta: ModernTaskManager.Core/Gpu/IGpuUsageProvider.cs
using System;
using ModernTaskManager.Core.Models; // Necesario para GpuDetailInfo

namespace ModernTaskManager.Core.Gpu
{
    public interface IGpuUsageProvider : IDisposable
    {
        string ProviderName { get; }
        bool IsSupported { get; }

        void Initialize();
        GpuDetailInfo GetStaticInfo();
        GpuAdapterDynamicInfo GetUsage();
    }
}