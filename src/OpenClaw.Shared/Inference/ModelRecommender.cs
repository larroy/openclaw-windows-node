using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClaw.Shared.Inference;

/// <summary>How well a model fits the host it would run on.</summary>
public enum ModelFit
{
    /// <summary>Will not run usefully here.</summary>
    WontFit = 0,
    /// <summary>Runs, but slowly: the weights spill out of VRAM into system RAM.</summary>
    Tight = 1,
    /// <summary>Fits in VRAM with headroom.</summary>
    Fits = 2,
}

/// <summary>
/// One model evaluated against a host.
/// </summary>
/// <param name="Model">The catalog entry.</param>
/// <param name="Fit">Verdict for this host.</param>
/// <param name="Reason">Short PII-free explanation, shown next to the entry.</param>
/// <param name="IsEligibleForAutoSelection">
/// False for entries the recommender must never pick on the user's behalf:
/// unpublished checkpoints, missing hashes, and confirmation-gated giants.
/// </param>
public sealed record ModelFitAssessment(
    LocalModelInfo Model,
    ModelFit Fit,
    string Reason,
    bool IsEligibleForAutoSelection);

/// <summary>
/// The recommender's answer for a host.
/// </summary>
/// <param name="Recommended">Best auto-selectable model, or null when none fits.</param>
/// <param name="Assessments">Every catalog entry with its verdict, in catalog order, for the UI.</param>
/// <param name="Summary">One-line PII-free explanation of the outcome.</param>
public sealed record LocalModelRecommendation(
    LocalModelInfo? Recommended,
    IReadOnlyList<ModelFitAssessment> Assessments,
    string Summary);

/// <summary>
/// Picks the local model a host can actually run.
///
/// <para>Pure and total: no I/O, no throwing. The rules are deliberately
/// conservative. Recommending a model that does not fit produces a download of
/// tens of gigabytes followed by a server that either refuses to start or runs
/// at unusable speed, so "no recommendation" is a better answer than a guess.</para>
/// </summary>
public static class ModelRecommender
{
    /// <summary>
    /// Fraction of VRAM we are willing to fill. The display driver, the desktop
    /// compositor, and any other GPU client need the remainder; sizing to 100%
    /// of reported VRAM reliably produces an out-of-memory failure at load.
    /// </summary>
    private const double VramHeadroomFactor = 0.90;

    /// <summary>
    /// Fraction of installed RAM we are willing to fill for a CPU/spill run. The
    /// OS and the rest of the app still have to fit.
    /// </summary>
    private const double SystemRamHeadroomFactor = 0.75;

    /// <summary>
    /// Evaluate every catalog entry against <paramref name="hardware"/> and pick
    /// the largest model that fits.
    /// </summary>
    /// <param name="hardware">Probe result.</param>
    /// <param name="models">Catalog to evaluate. Defaults to <see cref="LocalModelCatalog.Models"/>.</param>
    public static LocalModelRecommendation Recommend(
        HostHardwareInfo hardware,
        IReadOnlyList<LocalModelInfo>? models = null)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        models ??= LocalModelCatalog.Models;

        var vramBudget = Budget(hardware.TotalNvidiaVramBytes, VramHeadroomFactor);
        var ramBudget = Budget(hardware.TotalPhysicalMemoryBytes, SystemRamHeadroomFactor);

        var assessments = models.Select(m => Assess(m, vramBudget, ramBudget)).ToArray();

        // Prefer a true VRAM fit; only then accept a slow RAM-backed run. Within a
        // tier, take the largest model, which is the most capable one that fits.
        var recommended = assessments
            .Where(a => a.IsEligibleForAutoSelection && a.Fit != ModelFit.WontFit)
            .OrderByDescending(a => a.Fit)
            .ThenByDescending(a => a.Model.TotalSizeBytes)
            .Select(a => a.Model)
            .FirstOrDefault();

        var summary = BuildSummary(recommended, assessments, vramBudget, ramBudget);
        return new LocalModelRecommendation(recommended, assessments, summary);
    }

    private static ModelFitAssessment Assess(LocalModelInfo model, long? vramBudget, long? ramBudget)
    {
        if (!model.IsDownloadable)
        {
            return new ModelFitAssessment(
                model,
                ModelFit.WontFit,
                model.Shards.Count == 0
                    ? "Checkpoint is not published yet."
                    : "Checkpoint has no pinned hash, so it cannot be downloaded.",
                IsEligibleForAutoSelection: false);
        }

        var needed = model.MinimumRecommendedMemoryBytes;
        var eligible = !model.RequiresConfirmation;

        if (vramBudget is { } vram && needed <= vram)
        {
            return new ModelFitAssessment(
                model,
                ModelFit.Fits,
                $"Fits in {FormatGib(vram)} of usable VRAM.",
                eligible);
        }

        if (ramBudget is { } ram && needed <= ram)
        {
            var why = vramBudget is null
                ? "No NVIDIA VRAM detected"
                : $"Larger than the {FormatGib(vramBudget.Value)} of usable VRAM";
            return new ModelFitAssessment(
                model,
                ModelFit.Tight,
                $"{why}, so it runs from system RAM. Expect slow generation.",
                eligible);
        }

        var largest = Math.Max(vramBudget ?? 0, ramBudget ?? 0);
        return new ModelFitAssessment(
            model,
            ModelFit.WontFit,
            largest > 0
                ? $"Needs about {FormatGib(needed)} but only {FormatGib(largest)} is usable on this host."
                : $"Needs about {FormatGib(needed)}; available memory could not be determined.",
            eligible);
    }

    private static string BuildSummary(
        LocalModelInfo? recommended,
        IReadOnlyList<ModelFitAssessment> assessments,
        long? vramBudget,
        long? ramBudget)
    {
        if (recommended is not null)
        {
            var fit = assessments.First(a => ReferenceEquals(a.Model, recommended)).Fit;
            return fit == ModelFit.Fits
                ? $"Recommended: {recommended.DisplayName}."
                : $"Recommended: {recommended.DisplayName}. It will run from system RAM and be slow.";
        }

        if (vramBudget is null && ramBudget is null)
            return "Hardware could not be detected, so no model can be recommended.";

        var gated = assessments.Any(a => a.Model.RequiresConfirmation && a.Fit != ModelFit.WontFit);
        return gated
            ? "No model is recommended automatically. A large gated model would fit but must be chosen explicitly."
            : "No catalog model fits this host's memory.";
    }

    private static long? Budget(long? total, double factor) =>
        total is { } value && value > 0 ? (long)(value * factor) : null;

    private static string FormatGib(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
}
