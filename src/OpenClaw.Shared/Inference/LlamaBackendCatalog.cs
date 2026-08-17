using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// A llama.cpp compute backend variant we ship. The variant is chosen at
/// runtime from detected hardware; the release it comes from is pinned.
/// </summary>
public enum LlamaBackend
{
    /// <summary>Portable CPU build. Always usable, always the last resort.</summary>
    Cpu = 0,
    /// <summary>NVIDIA CUDA 12.x build. Widest driver compatibility.</summary>
    Cuda12 = 1,
    /// <summary>NVIDIA CUDA 13.x build.</summary>
    Cuda13 = 2,
    /// <summary>Vendor-neutral Vulkan build, for AMD/Intel adapters.</summary>
    Vulkan = 3,
}

/// <summary>
/// One downloadable archive belonging to a backend variant.
/// </summary>
/// <param name="FileName">Release asset file name.</param>
/// <param name="Sha256">
/// Pinned lowercase hex SHA-256 of the archive. <b>Null means the runtime must
/// refuse to download it</b>, exactly as the audio catalogs behave. See
/// <c>docs/LOCAL_INFERENCE_ASSETS.md</c>.
/// </param>
/// <param name="ApproximateSizeBytes">Size hint for the UI's progress display. 0 when unknown.</param>
public sealed record LlamaBackendAsset(
    string FileName,
    string? Sha256,
    long ApproximateSizeBytes = 0)
{
    /// <summary>Full HTTPS download URL, derived from the pinned release tag.</summary>
    public string DownloadUrl => $"{LlamaBackendCatalog.ReleaseDownloadBase}/{FileName}";
}

/// <summary>
/// A backend variant plus every archive that has to be extracted for it to run.
/// </summary>
/// <param name="Backend">Which compute backend this entry provides.</param>
/// <param name="Architecture">CPU architecture the archive targets.</param>
/// <param name="Assets">
/// All archives to extract into the same runtime directory, in extraction order.
/// The CUDA variants need two: the llama.cpp binaries plus the matching CUDA
/// runtime redistributable, without which <c>llama-server.exe</c> fails to start
/// with a missing-DLL error rather than anything diagnostic.
/// </param>
/// <param name="DisplayName">Label for the backend override dropdown.</param>
public sealed record LlamaBackendVariant(
    LlamaBackend Backend,
    System.Runtime.InteropServices.Architecture Architecture,
    IReadOnlyList<LlamaBackendAsset> Assets,
    string DisplayName)
{
    /// <summary>
    /// Stable directory-safe key for this variant's extracted runtime, e.g.
    /// <c>b10472-cuda12-X64</c>. Includes the release tag so a catalog bump
    /// installs alongside the old runtime instead of half-overwriting it.
    /// </summary>
    public string RuntimeKey =>
        $"{LlamaBackendCatalog.ReleaseTag}-{Backend.ToString().ToLowerInvariant()}-{Architecture.ToString().ToLowerInvariant()}";

    /// <summary>
    /// True when every asset carries a pinned hash, so the download manager is
    /// allowed to fetch it. False entries are visible in the UI but not installable.
    /// </summary>
    public bool IsDownloadable => Assets.Count > 0 && Assets.All(a => !string.IsNullOrWhiteSpace(a.Sha256));

    /// <summary>Combined size hint across all archives.</summary>
    public long ApproximateSizeBytes => Assets.Sum(a => a.ApproximateSizeBytes);
}

/// <summary>
/// The pinned llama.cpp release and the Windows backend variants we ship from it.
///
/// <para><b>Pinning policy.</b> The release tag and every asset hash are compiled
/// into the signed application; only the <em>variant</em> is decided at runtime,
/// from <see cref="HostHardwareInfo"/>. We deliberately do not resolve "latest" at
/// runtime: an unpinned binary cannot be integrity-checked, and upstream flag
/// changes would silently break the per-model run recipes.</para>
///
/// <para><b>Bumping the release.</b> Download each asset, compute
/// <c>Get-FileHash -Algorithm SHA256</c>, update <see cref="ReleaseTag"/> and every
/// hash below in the same commit, and re-verify the run recipes still parse against
/// the new build. See <c>docs/LOCAL_INFERENCE_ASSETS.md</c>.</para>
/// </summary>
public static class LlamaBackendCatalog
{
    /// <summary>Pinned upstream release tag.</summary>
    public const string ReleaseTag = "b10472";

    /// <summary>Base URL for this release's assets.</summary>
    public const string ReleaseDownloadBase =
        "https://github.com/ggml-org/llama.cpp/releases/download/" + ReleaseTag;

    private const System.Runtime.InteropServices.Architecture X64 =
        System.Runtime.InteropServices.Architecture.X64;
    private const System.Runtime.InteropServices.Architecture Arm64 =
        System.Runtime.InteropServices.Architecture.Arm64;

    // SECURITY - pinned SHA-256 hashes (lowercase hex), computed from the
    // archives published at the b10472 release and cross-checked against the
    // sizes the GitHub releases API reports for each asset. Verified 2026-08-17.
    // Downloads with a different hash are rejected and the partial file is
    // deleted. Re-verify before every public release and record the provenance.
    // See docs/LOCAL_INFERENCE_ASSETS.md.
    public static readonly IReadOnlyList<LlamaBackendVariant> Variants =
    [
        new(LlamaBackend.Cuda13, X64,
            [
                new($"llama-{ReleaseTag}-bin-win-cuda-13.3-x64.zip",
                    "ce7ca842c1400a85457e6c7ce844f21e52f187e6f0364b7daf3d2fd1ccf6db3b",
                    146_707_438),
                new("cudart-llama-bin-win-cuda-13.3-x64.zip",
                    "1462a050eb4c684921ba51dcc4cc488a036674c3e73e9945ee705b854808d03e",
                    390_970_417),
            ],
            "NVIDIA CUDA 13.3 (x64)"),

        new(LlamaBackend.Cuda12, X64,
            [
                new($"llama-{ReleaseTag}-bin-win-cuda-12.4-x64.zip",
                    "aadc171ddb4ed1822bc1730bff447068b529718cef886437866e3bd536eda143",
                    250_798_945),
                new("cudart-llama-bin-win-cuda-12.4-x64.zip",
                    "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6",
                    391_443_627),
            ],
            "NVIDIA CUDA 12.4 (x64)"),

        new(LlamaBackend.Cuda13, Arm64,
            [
                new($"llama-{ReleaseTag}-bin-win-cuda-13.4-arm64.zip",
                    "1ce04088513dcbea5c172d529032cf0eb405c2bc74df761921bfb7bef3fa28b4",
                    140_341_800),
                new("cudart-llama-bin-win-cuda-13.4-arm64.zip",
                    "5a40dc7c5fa3d0a80ceeba4f16f9e8d25d87bcf1399c9233588953c43436c33c",
                    153_318_797),
            ],
            "NVIDIA CUDA 13.4 (ARM64)"),

        new(LlamaBackend.Vulkan, X64,
            [
                new($"llama-{ReleaseTag}-bin-win-vulkan-x64.zip",
                    "2104e62c7e5237f2190240cdc987d8c3946a77051f696771d03b8d762a9d2fae",
                    34_813_404),
            ],
            "Vulkan (x64)"),

        new(LlamaBackend.Cpu, X64,
            [
                new($"llama-{ReleaseTag}-bin-win-cpu-x64.zip",
                    "ef495329c85c171991972fd3226a179c1900368cab66e2ebba8b21a7471a74e5",
                    18_470_168),
            ],
            "CPU (x64)"),

        new(LlamaBackend.Cpu, Arm64,
            [
                new($"llama-{ReleaseTag}-bin-win-cpu-arm64.zip",
                    "6de7a00ad19fa3c5a772575d8a4fc75b265fcc2b875a2206b437af7d925b29b1",
                    12_229_653),
            ],
            "CPU (ARM64)"),
    ];

    /// <summary>The server executable inside every extracted archive.</summary>
    public const string ServerExecutableName = "llama-server.exe";

    /// <summary>
    /// Look up a variant by backend and architecture, or null when this release
    /// has no such build (e.g. Vulkan on ARM64).
    /// </summary>
    public static LlamaBackendVariant? Find(
        LlamaBackend backend,
        System.Runtime.InteropServices.Architecture architecture) =>
        Variants.FirstOrDefault(v => v.Backend == backend && v.Architecture == architecture);
}
