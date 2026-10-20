using Windows.Networking.Connectivity;

namespace OpenClawTray.Services;

/// <summary>
/// Windows connection-cost check for <see cref="KokoroVoicePackPrefetcher"/>. Kept apart
/// from the prefetcher so the prefetcher has no WinRT dependency.
/// </summary>
internal static class KokoroPrefetchNetwork
{
    /// <summary>Metered, roaming, over-limit, or offline connections skip the ~380 MB download.</summary>
    public static bool IsMeteredConnection()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null) return true;
        var cost = profile.GetConnectionCost();
        return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
            || cost.Roaming
            || cost.OverDataLimit;
    }
}
