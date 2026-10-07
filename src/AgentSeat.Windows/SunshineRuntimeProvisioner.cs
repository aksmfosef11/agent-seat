using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSeat.Core.Models;
using AgentSeat.Core.Services;

namespace AgentSeat.Windows;

internal sealed record SunshineAdminSecret(string Username, string Password);

internal sealed record SunshineInstance(
    string RuntimeDirectory,
    string ExecutablePath,
    string ConfigurationFile,
    string ApplicationsFile,
    string StateFile,
    string LogFile,
    SunshineAdminSecret AdminSecret);

internal sealed class SunshineRuntimeProvisioner(
    string templateDirectory,
    string steamLauncherPath,
    string appCompatDirectory)
{
    private const string RuntimeRelativePath = @"AppData\Local\agent-seat\Sunshine";
    private readonly string _templateDirectory = Path.GetFullPath(templateDirectory);
    private readonly string _steamLauncherPath = Path.GetFullPath(steamLauncherPath);
    private readonly string _appCompatDirectory = Path.GetFullPath(appCompatDirectory);

    internal bool TemplateExists =>
        File.Exists(Path.Combine(_templateDirectory, "sunshine.exe")) &&
        File.Exists(Path.Combine(_templateDirectory, "assets", "apps.json"));

    internal bool CompatibilityExists =>
        SteamIsolationRuntime.IsComplete(_steamLauncherPath, _appCompatDirectory);

    internal SunshineInstance Provision(SeatDefinition seat, string userProfilePath)
    {
        if (!TemplateExists)
        {
            throw new DirectoryNotFoundException(
                $"A complete Sunshine portable template was not found at '{_templateDirectory}'.");
        }
        if (!CompatibilityExists)
        {
            throw new DirectoryNotFoundException(
                $"The Steam isolation launcher or native compatibility runtime is incomplete at '{_appCompatDirectory}'.");
        }

        var steamExecutable = SteamLocator.FindExecutable() ??
                              throw new FileNotFoundException(
                                  "Steam is not installed. Install Steam on the host before starting a seat stream.");
        if (!SteamLocator.SupportsMasterIpcOverride(steamExecutable))
        {
            throw new NotSupportedException(
                "This Steam build does not expose master_ipc_name_override, which AgentSeat needs for per-seat Steam IPC names.");
        }

        var defaultApplications = SunshineApplicationsBuilder.BuildDefaultCatalog(
            seat.Id,
            steamExecutable,
            _steamLauncherPath,
            _appCompatDirectory);

        var profileRoot = Path.GetFullPath(userProfilePath);
        var runtimeRoot = Path.GetFullPath(Path.Combine(profileRoot, RuntimeRelativePath));
        EnsureDescendant(profileRoot, runtimeRoot);
        var runtimeDirectory = Path.GetFullPath(Path.Combine(runtimeRoot, seat.Id));
        EnsureDescendant(runtimeRoot, runtimeDirectory);

        CopyTemplate(runtimeDirectory);
        var configDirectory = Path.Combine(runtimeDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        CopyApplicationAsset("steam.png", configDirectory);

        var appsFile = Path.Combine(configDirectory, "apps.json");
        if (!File.Exists(appsFile))
        {
            AtomicWriteText(appsFile, defaultApplications);
        }
        else
        {
            MigrateUnmodifiedApplications(appsFile, seat.Id, defaultApplications);
        }

        var stateFile = Path.Combine(configDirectory, "sunshine_state.json");
        var secretFile = Path.Combine(configDirectory, "agent-seat-admin.dat");
        var secret = GetOrCreateSecret(secretFile, seat.Id);
        EnsureSunshineCredentials(stateFile, secret);

        var logFile = Path.Combine(configDirectory, "sunshine.log");
        var configurationFile = Path.Combine(configDirectory, "agent-seat.conf");
        var configuration = SunshineConfigurationBuilder.Build(
            seat,
            new SunshineInstancePaths(
                appsFile,
                stateFile,
                logFile,
                Path.Combine(configDirectory, "cakey.pem"),
                Path.Combine(configDirectory, "cacert.pem")));
        AtomicWriteText(configurationFile, configuration);

        return new SunshineInstance(
            runtimeDirectory,
            Path.Combine(runtimeDirectory, "sunshine.exe"),
            configurationFile,
            appsFile,
            stateFile,
            logFile,
            secret);
    }

    internal SunshineAdminSecret ReadSecret(string runtimeDirectory, string seatId)
    {
        var secretFile = Path.Combine(runtimeDirectory, "config", "agent-seat-admin.dat");
        if (!File.Exists(secretFile))
        {
            throw new FileNotFoundException("The protected Sunshine management credential is missing.", secretFile);
        }

        return UnprotectSecret(File.ReadAllBytes(secretFile), seatId);
    }

    private void CopyTemplate(string runtimeDirectory)
    {
        Directory.CreateDirectory(runtimeDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(
                     _templateDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_templateDirectory, sourceFile);
            if (relative.Equals("config", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith($"config{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationFile = Path.GetFullPath(Path.Combine(runtimeDirectory, relative));
            EnsureDescendant(runtimeDirectory, destinationFile);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
    }

    private void CopyApplicationAsset(string fileName, string configDirectory)
    {
        var source = Path.Combine(_templateDirectory, "assets", fileName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                $"Sunshine application artwork '{fileName}' is missing from the portable template.",
                source);
        }

        File.Copy(source, Path.Combine(configDirectory, fileName), overwrite: true);
    }

    private static SunshineAdminSecret GetOrCreateSecret(string secretFile, string seatId)
    {
        if (File.Exists(secretFile))
        {
            return UnprotectSecret(File.ReadAllBytes(secretFile), seatId);
        }

        var secret = new SunshineAdminSecret("agent-seat", CreateRandomPassword());
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(secret);
        try
        {
            AtomicWriteBytes(secretFile, Dpapi.Protect(plaintext, Entropy(seatId)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return secret;
    }

    private static void MigrateUnmodifiedApplications(
        string appsFile,
        string seatId,
        string defaultApplications)
    {
        var existing = File.ReadAllText(appsFile, Encoding.UTF8);
        if (SunshineApplicationsBuilder.IsUnmodifiedUpstreamDefault(existing) ||
            SunshineApplicationsBuilder.IsUnmodifiedAgentSeatDefault(existing, seatId))
        {
            AtomicWriteText(appsFile, defaultApplications);
        }
    }

    private static SunshineAdminSecret UnprotectSecret(byte[] protectedBytes, string seatId)
    {
        var plaintext = Dpapi.Unprotect(protectedBytes, Entropy(seatId));
        try
        {
            return JsonSerializer.Deserialize<SunshineAdminSecret>(plaintext) ??
                   throw new CryptographicException("The Sunshine credential payload was empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void EnsureSunshineCredentials(string stateFile, SunshineAdminSecret secret)
    {
        JsonObject state;
        if (File.Exists(stateFile))
        {
            state = JsonNode.Parse(File.ReadAllText(stateFile, Encoding.UTF8)) as JsonObject ??
                    throw new InvalidDataException($"Sunshine state '{stateFile}' is not a JSON object.");
        }
        else
        {
            state = new JsonObject();
        }

        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
        var passwordMaterial = Encoding.UTF8.GetBytes(secret.Password + salt);
        try
        {
            var digest = SHA256.HashData(passwordMaterial);
            Array.Reverse(digest); // Sunshine's util::hex(std::array) serializes the digest in reverse byte order.
            state["username"] = secret.Username;
            state["salt"] = salt;
            state["password"] = Convert.ToHexString(digest);
            CryptographicOperations.ZeroMemory(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordMaterial);
        }

        AtomicWriteText(
            stateFile,
            state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static string CreateRandomPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static byte[] Entropy(string seatId) =>
        Encoding.UTF8.GetBytes($"AgentSeat/Sunshine/{seatId}/v1");

    private static void AtomicWriteText(string path, string content) =>
        AtomicWriteBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));

    private static void AtomicWriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void EnsureDescendant(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Runtime path '{normalizedCandidate}' escaped the expected root '{normalizedRoot}'.");
        }
    }

    private static class Dpapi
    {
        private const int CryptProtectUiForbidden = 0x1;
        private const int CryptProtectLocalMachine = 0x4;

        internal static byte[] Protect(byte[] plaintext, byte[] entropy) => Transform(
            plaintext,
            entropy,
            protect: true);

        internal static byte[] Unprotect(byte[] ciphertext, byte[] entropy) => Transform(
            ciphertext,
            entropy,
            protect: false);

        private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
        {
            var inputPointer = Marshal.AllocHGlobal(input.Length);
            var entropyPointer = Marshal.AllocHGlobal(entropy.Length);
            try
            {
                Marshal.Copy(input, 0, inputPointer, input.Length);
                Marshal.Copy(entropy, 0, entropyPointer, entropy.Length);
                var inputBlob = new DataBlob(input.Length, inputPointer);
                var entropyBlob = new DataBlob(entropy.Length, entropyPointer);
                IntPtr description = IntPtr.Zero;
                DataBlob outputBlob;
                bool succeeded;
                if (protect)
                {
                    succeeded = NativeMethods.CryptProtectData(
                        ref inputBlob,
                        "AgentSeat Sunshine credential",
                        ref entropyBlob,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        CryptProtectUiForbidden | CryptProtectLocalMachine,
                        out outputBlob);
                }
                else
                {
                    succeeded = NativeMethods.CryptUnprotectData(
                        ref inputBlob,
                        out description,
                        ref entropyBlob,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        CryptProtectUiForbidden,
                        out outputBlob);
                }

                if (description != IntPtr.Zero)
                {
                    _ = NativeMethods.LocalFree(description);
                }

                if (!succeeded)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        protect ? "Windows could not protect the Sunshine credential." :
                        "Windows could not decrypt the Sunshine credential.");
                }

                try
                {
                    var output = new byte[outputBlob.Length];
                    Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                    return output;
                }
                finally
                {
                    if (outputBlob.Data != IntPtr.Zero)
                    {
                        _ = NativeMethods.LocalFree(outputBlob.Data);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inputPointer);
                Marshal.FreeHGlobal(entropyPointer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct DataBlob(int length, IntPtr data)
        {
            internal readonly int Length = length;
            internal readonly IntPtr Data = data;
        }

        private static class NativeMethods
        {
            [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptProtectData(
                ref DataBlob dataIn,
                string description,
                ref DataBlob optionalEntropy,
                IntPtr reserved,
                IntPtr promptStruct,
                int flags,
                out DataBlob dataOut);

            [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptUnprotectData(
                ref DataBlob dataIn,
                out IntPtr description,
                ref DataBlob optionalEntropy,
                IntPtr reserved,
                IntPtr promptStruct,
                int flags,
                out DataBlob dataOut);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr LocalFree(IntPtr memory);
        }
    }
}
