using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// Builds the <c>llama-server</c> command line.
///
/// <para>Split from the process host so the argument contract is unit testable
/// without launching anything. The division of ownership matters: the launcher
/// owns <c>-m</c>, <c>--host</c>, and <c>--port</c> because they come from
/// runtime state, while everything else is the checkpoint's tuned recipe from
/// <see cref="LocalModelCatalog"/>. A recipe that also set a launcher-owned flag
/// would either duplicate it or silently win, so that separation is enforced
/// here and asserted in tests.</para>
/// </summary>
public static class LlamaServerArguments
{
    /// <summary>Loopback bind address. The default and the safe choice.</summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>
    /// Bind address that accepts connections from outside the machine. Required
    /// when a NAT-mode WSL gateway has to reach the server, and never selected
    /// implicitly: it exposes an unauthenticated inference endpoint to the LAN.
    /// </summary>
    public const string AllInterfacesHost = "0.0.0.0";

    /// <summary>
    /// Flags the launcher owns. A catalog recipe must not contain any of these.
    /// </summary>
    public static readonly string[] LauncherOwnedFlags = ["-m", "--model", "--host", "--port"];

    /// <summary>
    /// Build the full argument list for a model run.
    /// </summary>
    /// <param name="modelPath">Path to the checkpoint, or its first shard.</param>
    /// <param name="port">TCP port to listen on.</param>
    /// <param name="recipeArgs">The checkpoint's tuned arguments.</param>
    /// <param name="bindBeyondLoopback">
    /// When true, bind <see cref="AllInterfacesHost"/> instead of loopback. The
    /// caller is responsible for having obtained explicit consent.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The recipe contains a flag the launcher owns.
    /// </exception>
    public static IReadOnlyList<string> Build(
        string modelPath,
        int port,
        IReadOnlyList<string> recipeArgs,
        bool bindBeyondLoopback = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(recipeArgs);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        var conflict = recipeArgs.FirstOrDefault(
            a => LauncherOwnedFlags.Contains(a, StringComparer.Ordinal));
        if (conflict is not null)
        {
            throw new ArgumentException(
                $"Run recipe sets '{conflict}', which the launcher owns. Remove it from the catalog entry.",
                nameof(recipeArgs));
        }

        var args = new List<string>(recipeArgs.Count + 6)
        {
            "-m", modelPath,
            "--host", bindBeyondLoopback ? AllInterfacesHost : LoopbackHost,
            "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        args.AddRange(recipeArgs);
        return args;
    }

    /// <summary>Health endpoint for a server on <paramref name="port"/>.</summary>
    public static string BuildHealthUrl(int port) =>
        $"http://{LoopbackHost}:{port}/health";

    /// <summary>
    /// OpenAI-compatible base URL a client should use.
    /// </summary>
    /// <param name="host">
    /// Host a client can actually reach the server on. Loopback for a local
    /// client; the Windows host's name or address for a NAT-mode WSL gateway.
    /// </param>
    public static string BuildBaseUrl(string host, int port) =>
        $"http://{host}:{port}/v1";
}
