using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// One GGUF file belonging to a model. Large models are published as numbered
/// shards; llama.cpp is pointed at shard 1 and loads the rest itself, but every
/// shard has to be present in the same directory first.
/// </summary>
/// <param name="FileName">On-disk name. Must match the upstream name for shard discovery to work.</param>
/// <param name="DownloadUrl">HTTPS URL of the file.</param>
/// <param name="SizeBytes">Exact size in bytes, from the HuggingFace file listing.</param>
/// <param name="Sha256">
/// Pinned lowercase hex SHA-256. HuggingFace reports this as the LFS object id,
/// so the values here are the upstream-published hashes rather than ones we
/// computed locally. Null means the download manager must refuse the file.
/// </param>
public sealed record GgufShard(
    string FileName,
    string DownloadUrl,
    long SizeBytes,
    string? Sha256);

/// <summary>
/// A model we offer for local inference, with the run recipe it needs.
/// </summary>
/// <param name="Id">Stable catalog id used in settings and on disk.</param>
/// <param name="DisplayName">Human-facing name.</param>
/// <param name="Shards">GGUF files, shard 1 first. Empty for entries that are not published yet.</param>
/// <param name="RecipeArgs">
/// Model-specific <c>llama-server</c> arguments. Excludes <c>-m</c>, <c>--host</c>,
/// and <c>--port</c>, which the process launcher owns. These are sampler and
/// speculative-decoding settings tuned per checkpoint; changing them changes
/// output quality, so they live with the checkpoint rather than in the launcher.
/// </param>
/// <param name="MinimumRecommendedMemoryBytes">
/// Memory needed for a comfortable run: model weights plus KV cache and runtime
/// overhead. Compared against VRAM first, then system RAM.
/// </param>
/// <param name="RequiresConfirmation">
/// True for models whose download is large enough that starting it by accident is
/// a real harm. Never auto-recommended; the UI must confirm the size explicitly.
/// </param>
/// <param name="Notes">Short PII-free note shown under the entry in the picker.</param>
public sealed record LocalModelInfo(
    string Id,
    string DisplayName,
    IReadOnlyList<GgufShard> Shards,
    IReadOnlyList<string> RecipeArgs,
    long MinimumRecommendedMemoryBytes,
    bool RequiresConfirmation = false,
    string? Notes = null)
{
    /// <summary>Total download and on-disk size across all shards.</summary>
    public long TotalSizeBytes => Shards.Sum(s => s.SizeBytes);

    /// <summary>
    /// False when the checkpoint is not published yet (no shards) or any shard
    /// lacks a pinned hash. Such entries render as unavailable and the download
    /// manager refuses them.
    /// </summary>
    public bool IsDownloadable =>
        Shards.Count > 0 && Shards.All(s => !string.IsNullOrWhiteSpace(s.Sha256) && !string.IsNullOrWhiteSpace(s.DownloadUrl));

    /// <summary>The shard llama-server is launched against.</summary>
    public GgufShard? PrimaryShard => Shards.Count > 0 ? Shards[0] : null;
}

/// <summary>
/// The models we offer for local inference, with their tuned run recipes.
///
/// <para><b>Integrity.</b> Every shard hash is the HuggingFace LFS object id,
/// which is the file's SHA-256. Downloads verify against it and fail closed on a
/// mismatch, matching the audio-asset policy in
/// <c>docs/AUDIO_MODEL_ASSETS.md</c>.</para>
/// </summary>
public static class LocalModelCatalog
{
    /// <summary>Rough multiplier for KV cache and runtime overhead on top of the weights.</summary>
    private const double OverheadFactor = 1.15;

    private const long Gib = 1024L * 1024 * 1024;

    public const string Qwen35BId = "qwen3.6-35b-a3b";
    public const string Qwen27BId = "qwen3.8-27b";
    public const string DeepSeekV4FlashId = "deepseek-v4-flash-0731";

    private const string QwenRepoBase =
        "https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF/resolve/main";
    private const string DeepSeekRepoBase =
        "https://huggingface.co/unsloth/DeepSeek-V4-Flash-0731-GGUF/resolve/main/UD-Q4_K_XL";

    public static readonly IReadOnlyList<LocalModelInfo> Models =
    [
        new(
            Qwen35BId,
            "Qwen3.6 35B A3B (UD-Q4_K_M)",
            [
                new("Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
                    $"{QwenRepoBase}/Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
                    22_663_387_424,
                    "0b21525e972670ed59e1812e170b27c26355381f0656ecc4e25617ece7dac58b"),
            ],
            // Multi-token-prediction speculative decoding plus the sampler settings
            // Unsloth publishes for this checkpoint.
            [
                "-b", "4096",
                "-ub", "4096",
                "--spec-type", "draft-mtp",
                "--temp", "1.0",
                "--top-k", "20",
                "--top-p", "0.95",
                "--min-p", "0.0",
                "--repeat-penalty", "1",
                "--presence-penalty", "1.5",
                "-np", "1",
                "-dio",
            ],
            MinimumRecommendedMemoryBytes: (long)(22_663_387_424 * OverheadFactor),
            Notes: "A3B mixture-of-experts. Fits a single 32 GB or larger GPU."),

        new(
            Qwen27BId,
            "Qwen3.8 27B",
            // Checkpoint not published upstream yet. The entry exists so the UI can
            // show it as pending and so the recipe is reviewed alongside its sibling;
            // with no shards it is not downloadable and cannot be selected.
            [],
            [
                "-b", "4096",
                "-ub", "4096",
                "--spec-type", "draft-mtp",
                "--temp", "1.0",
                "--top-k", "20",
                "--top-p", "0.95",
                "--min-p", "0",
                "--repeat-penalty", "1",
                "--presence-penalty", "0",
                "-np", "1",
                "-dio",
            ],
            MinimumRecommendedMemoryBytes: 20 * Gib,
            Notes: "Checkpoint not released yet. The recipe is expected to match Qwen3.6 and will be confirmed on release."),

        new(
            DeepSeekV4FlashId,
            "DeepSeek V4 Flash 0731 (UD-Q4_K_XL)",
            [
                new("DeepSeek-V4-Flash-0731-UD-Q4_K_XL-00001-of-00005.gguf",
                    $"{DeepSeekRepoBase}/DeepSeek-V4-Flash-0731-UD-Q4_K_XL-00001-of-00005.gguf",
                    5_257_408,
                    "d13ce8f90855547bdaebe7312f531a1f2c4f822178d3103951f27fe884395cfa"),
                new("DeepSeek-V4-Flash-0731-UD-Q4_K_XL-00002-of-00005.gguf",
                    $"{DeepSeekRepoBase}/DeepSeek-V4-Flash-0731-UD-Q4_K_XL-00002-of-00005.gguf",
                    48_935_523_072,
                    "d5b61668950f4743aacd677675d7fcf7507dbe1db6d304e8ff97ed1f00827bee"),
                new("DeepSeek-V4-Flash-0731-UD-Q4_K_XL-00003-of-00005.gguf",
                    $"{DeepSeekRepoBase}/DeepSeek-V4-Flash-0731-UD-Q4_K_XL-00003-of-00005.gguf",
                    48_980_787_136,
                    "9705db7e589f360685ca7bd48100b270d78d228d4f5aa980508f3b2778af5494"),
                new("DeepSeek-V4-Flash-0731-UD-Q4_K_XL-00004-of-00005.gguf",
                    $"{DeepSeekRepoBase}/DeepSeek-V4-Flash-0731-UD-Q4_K_XL-00004-of-00005.gguf",
                    49_999_168_416,
                    "7f13a68e3ca64208454c4ba32cc2757c0cbe78e3e5576c3142bf7007ca97da42"),
                new("DeepSeek-V4-Flash-0731-UD-Q4_K_XL-00005-of-00005.gguf",
                    $"{DeepSeekRepoBase}/DeepSeek-V4-Flash-0731-UD-Q4_K_XL-00005-of-00005.gguf",
                    7_174_505_088,
                    "ed0d93164d3784968d6ce40d6d201ba98337f16e7db1b31fe495b2b0f334cc09"),
            ],
            [
                "--temp", "1.0",
                "--top-p", "1.0",
                "--min-p", "0.01",
                "--spec-type", "draft-dspark",
                "--spec-draft-n-max", "3",
                "-ngl", "99",
                "-ngld", "99",
            ],
            // ~155 GiB of weights. Only a very-large-memory host runs this usefully.
            MinimumRecommendedMemoryBytes: 170 * Gib,
            RequiresConfirmation: true,
            Notes: "About 155 GB across 5 files. Requires a very large memory host and a long download."),
    ];

    /// <summary>Look up a model by catalog id, or null when unknown.</summary>
    public static LocalModelInfo? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
