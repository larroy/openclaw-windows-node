using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Security.Extensions;

namespace OpenClaw.Connection;

internal readonly record struct AuthenticodeTrustResult(bool IsTrusted, string? Detail)
{
    public static AuthenticodeTrustResult Trusted() => new(true, null);

    public static AuthenticodeTrustResult Rejected(string detail) => new(false, detail);
}

/// <summary>
/// Minimal projection of a Windows AppX/MSIX package as reported by
/// <c>Get-AppxPackage</c>, used to corroborate genuine Microsoft WSL binaries that
/// ship without a per-file Authenticode/catalog signature (MSIX packages are
/// trusted at the package level, not per-file).
/// </summary>
internal readonly record struct AppxPackageInfo(
    string? PackageFamilyName,
    string? Publisher,
    string? SignatureKind);

internal static class WindowsAuthenticodeVerifier
{
    // Modern WSL ships as an MSIX package. The trailing suffix is a hash derived
    // from the publisher's signing certificate/public key, so an impostor package
    // re-signed with a different certificate would get a different family name.
    private const string WslPackageFamilyName =
        "MicrosoftCorporationII.WindowsSubsystemForLinux_8wekyb3d8bbwe";

    public static AuthenticodeTrustResult VerifyMicrosoftSignedFile(string path) =>
        VerifyMicrosoftSignedFile(path, LookupWslAppxPackageViaPowerShell);

    /// <summary>
    /// Test seam: allows callers to substitute a fake WSL AppX package lookup
    /// instead of shelling out to PowerShell.
    /// </summary>
    internal static AuthenticodeTrustResult VerifyMicrosoftSignedFile(
        string path,
        Func<AppxPackageInfo?> lookupWslPackage)
    {
        var primary = VerifyAuthenticodeSignature(path);
        if (primary.IsTrusted)
            return primary;

        // MSIX packages (modern WSL) don't carry a per-file Authenticode/catalog
        // signature, so a failed classic check isn't conclusive for wslrelay.exe.
        // Only consult the package-level fallback for the WSL binaries this
        // codebase actually cares about; never widen trust for arbitrary files.
        if (!string.Equals(Path.GetFileName(path), "wslrelay.exe", StringComparison.OrdinalIgnoreCase))
            return primary;

        return VerifyWslPackageFallback(primary, lookupWslPackage);
    }

    private static AuthenticodeTrustResult VerifyAuthenticodeSignature(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var signature = FileSignatureInfo.GetFromFileStream(stream);
            using var signingCertificate = signature.SigningCertificate;
            using var timestampCertificate = signature.TimestampCertificate;

            if (signature.State != SignatureState.SignedAndTrusted)
            {
                return AuthenticodeTrustResult.Rejected(
                    $"WSL relay Authenticode verification failed ({signature.State}).");
            }
            if (signingCertificate is null)
            {
                return AuthenticodeTrustResult.Rejected(
                    "WSL relay Authenticode signer could not be read.");
            }

            return HasMicrosoftPublisherIdentity(signingCertificate.Subject)
                ? AuthenticodeTrustResult.Trusted()
                : AuthenticodeTrustResult.Rejected(
                    "WSL relay Authenticode signer is not Microsoft Corporation.");
        }
        catch
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL relay Authenticode verification could not complete.");
        }
    }

    private static AuthenticodeTrustResult VerifyWslPackageFallback(
        AuthenticodeTrustResult primaryFailure,
        Func<AppxPackageInfo?> lookupWslPackage)
    {
        AppxPackageInfo? package;
        try
        {
            package = lookupWslPackage();
        }
        catch
        {
            package = null;
        }

        if (package is not { } info)
        {
            // No genuine WSL AppX package installed to corroborate; surface the
            // original Authenticode diagnostic rather than a new, less useful one.
            return primaryFailure;
        }

        if (!string.Equals(info.PackageFamilyName, WslPackageFamilyName, StringComparison.Ordinal))
        {
            // Doesn't match the well-known family name (a different/impostor
            // package can't corroborate this binary); surface the original detail.
            return primaryFailure;
        }

        if (string.IsNullOrEmpty(info.SignatureKind) ||
            string.Equals(info.SignatureKind, "None", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL package signature verification failed: the installed WSL package is unsigned.");
        }

        if (info.Publisher is null || !HasMicrosoftPublisherIdentity(info.Publisher))
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL package signature verification failed: the installed WSL package's publisher is not Microsoft Corporation.");
        }

        return AuthenticodeTrustResult.Trusted();
    }

    internal static bool HasMicrosoftPublisherIdentity(string subject) =>
        subject.Split(',')
            .Select(part => part.Trim())
            .Any(part =>
                string.Equals(
                    part,
                    "O=Microsoft Corporation",
                    StringComparison.OrdinalIgnoreCase));

    private static AppxPackageInfo? LookupWslAppxPackageViaPowerShell()
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolvePowerShellPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                "Get-AppxPackage -Name 'MicrosoftCorporationII.WindowsSubsystemForLinux' | " +
                "Select-Object -First 1 PackageFamilyName, Publisher, SignatureKind | " +
                "ConvertTo-Json -Compress");

            process = Process.Start(psi);
            if (process is null)
                return null;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            if (!process.WaitForExit(8_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string output;
            try
            {
                output = stdoutTask.GetAwaiter().GetResult();
                _ = stderrTask.GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            return ParseAppxPackageJson(output);
        }
        catch
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ResolvePowerShellPath()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(systemRoot))
            systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot))
            systemRoot = @"C:\Windows";
        return Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static AppxPackageInfo? ParseAppxPackageJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    return null;
                root = root[0];
            }
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var familyName = GetStringProperty(root, "PackageFamilyName");
            if (familyName is null)
                return null;

            return new AppxPackageInfo(
                familyName,
                GetStringProperty(root, "Publisher"),
                GetStringProperty(root, "SignatureKind"));
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
