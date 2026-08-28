using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Shared.Inference;

public interface IHostHardwareProbe
{
    HostHardwareInfo Probe();
}

/// <summary>
/// Reads the CUDA driver's own device and allocatable-memory view. This is the
/// sole GPU-memory source for Local AI qualification, including UMA devices.
/// </summary>
public sealed class CudaHostHardwareProbe : IHostHardwareProbe
{
    public HostHardwareInfo Probe()
    {
        PhysicalMemorySnapshot? physicalMemory = null;
        try { physicalMemory = PhysicalMemoryProbe.TryRead(); } catch { }

        IReadOnlyList<GpuInfo> gpus;
        try { gpus = CaptureCudaGpus(); } catch { gpus = []; }

        return new HostHardwareInfo(
            RuntimeInformation.OSArchitecture,
            physicalMemory?.TotalBytes,
            physicalMemory?.AvailableBytes,
            gpus,
            VulkanAvailable: false);
    }

    private static IReadOnlyList<GpuInfo> CaptureCudaGpus()
    {
        if (!OperatingSystem.IsWindows() || CuInit(0) != CudaSuccess ||
            CuDeviceGetCount(out int count) != CudaSuccess)
        {
            return [];
        }

        int? cudaMajorVersion = CuDriverGetVersion(out int driverVersion) == CudaSuccess && driverVersion > 0
            ? driverVersion / 1000
            : null;
        var gpus = new List<GpuInfo>();
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            if (CuDeviceGet(out int device, ordinal) != CudaSuccess)
                continue;

            string? name = ReadDeviceName(device);
            string? pciBusId = ReadPciBusId(device);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pciBusId))
                continue;

            IntPtr context = IntPtr.Zero;
            try
            {
                if (CuCtxCreate(out context, 0, device) != CudaSuccess ||
                    CuMemGetInfo(out nuint freeBytes, out nuint totalBytes) != CudaSuccess ||
                    totalBytes == 0 || totalBytes > long.MaxValue || freeBytes > totalBytes)
                {
                    continue;
                }

                gpus.Add(new GpuInfo(
                    GpuVendor.Nvidia,
                    name,
                    GpuVisibleMemoryBytes: (long)totalBytes,
                    FreeGpuVisibleMemoryBytes: (long)freeBytes,
                    CudaMajorVersion: cudaMajorVersion,
                    StableId: ToCudaVisibleDevicesSelector(pciBusId)));
            }
            finally
            {
                if (context != IntPtr.Zero)
                    _ = CuCtxDestroy(context);
            }
        }

        return gpus;
    }

    internal static string ToCudaVisibleDevicesSelector(string pciBusId) => pciBusId;

    private static string? ReadDeviceName(int device)
    {
        var buffer = new byte[DeviceNameCapacity];
        return CuDeviceGetName(buffer, buffer.Length, device) == CudaSuccess ? DecodeUtf8(buffer) : null;
    }

    private static string? ReadPciBusId(int device)
    {
        var buffer = new byte[PciBusIdCapacity];
        return CuDeviceGetPciBusId(buffer, buffer.Length, device) == CudaSuccess ? DecodeUtf8(buffer) : null;
    }

    private static string? DecodeUtf8(byte[] buffer)
    {
        int terminator = Array.IndexOf(buffer, (byte)0);
        string value = Encoding.UTF8.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private const int CudaSuccess = 0;
    private const int DeviceNameCapacity = 256;
    private const int PciBusIdCapacity = 32;

    [DllImport("nvcuda.dll", EntryPoint = "cuInit", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuInit(uint flags);

    [DllImport("nvcuda.dll", EntryPoint = "cuDriverGetVersion", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDriverGetVersion(out int driverVersion);

    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetCount", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGetCount(out int count);

    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGet", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGet(out int device, int ordinal);

    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetName", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGetName([Out] byte[] name, int length, int device);

    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetPCIBusId", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGetPciBusId([Out] byte[] pciBusId, int length, int device);

    [DllImport("nvcuda.dll", EntryPoint = "cuCtxCreate_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuCtxCreate(out IntPtr context, uint flags, int device);

    [DllImport("nvcuda.dll", EntryPoint = "cuCtxDestroy_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuCtxDestroy(IntPtr context);

    [DllImport("nvcuda.dll", EntryPoint = "cuMemGetInfo_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuMemGetInfo(out nuint freeBytes, out nuint totalBytes);
}
