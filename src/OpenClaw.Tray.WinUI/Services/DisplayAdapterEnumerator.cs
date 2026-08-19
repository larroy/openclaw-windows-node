using Microsoft.Win32;
using OpenClaw.Shared.Inference;
using System;
using System.Collections.Generic;

namespace OpenClawTray.Services;

/// <summary>
/// Enumerates installed display adapters from the driver class registry key.
///
/// <para>This is the fallback the hardware probe uses when <c>nvidia-smi</c> is
/// absent, which is exactly the AMD/Intel case where the choice is between the
/// Vulkan build and the CPU build. It lives here rather than in
/// <c>OpenClaw.Shared</c> because registry access needs a Windows-targeted
/// framework, and the probe takes it as an injected delegate for that reason.</para>
///
/// <para>Vendor and name only. Adapter memory is deliberately not reported: the
/// value that is easy to read here is the same one WMI exposes as a 32-bit field
/// that wraps above 4 GB, and a wrong VRAM number is worse than no number, since
/// the model recommender would size a download against it.</para>
/// </summary>
internal static class DisplayAdapterEnumerator
{
    /// <summary>Device class GUID for display adapters.</summary>
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// Read the installed adapters. Never throws: an unreadable registry yields
    /// an empty list, which the probe treats as "no GPU detected".
    /// </summary>
    public static IReadOnlyList<GpuInfo> Enumerate()
    {
        var adapters = new List<GpuInfo>();

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (classKey is null) return adapters;

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                // Instance subkeys are four digits ("0000"). Anything else is
                // configuration state, not an adapter.
                if (subKeyName.Length != 4 || !uint.TryParse(subKeyName, out _)) continue;

                try
                {
                    using var instance = classKey.OpenSubKey(subKeyName);
                    if (instance?.GetValue("DriverDesc") is not string name || name.Length == 0) continue;

                    adapters.Add(new GpuInfo(NvidiaSmiParser.ClassifyVendor(name), name));
                }
                catch (Exception)
                {
                    // A single unreadable instance must not hide the others.
                }
            }
        }
        catch (Exception)
        {
            // Restricted registry access degrades to "no adapters", matching the
            // probe's never-throw contract.
            return adapters;
        }

        return adapters;
    }
}
