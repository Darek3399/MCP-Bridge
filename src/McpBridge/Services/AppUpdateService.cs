using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace McpBridge.Services;

public sealed class AppUpdateInfo
{
    public Version Version { get; set; } = new Version(0, 0, 0);
    public string VersionLabel { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string LocalInstallerPath { get; set; } = string.Empty;
}

public sealed class UpdateDownloadProgress
{
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public double? Percentage => TotalBytes > 0
        ? Math.Min(100.0, BytesReceived * 100.0 / TotalBytes)
        : null;
}

public sealed class AppUpdateService
{
    private const string UpdateMutexName = @"Local\MCPBridge.Update";
    private const string ReleaseApiUrl = "https://api.github.com/repos/Darek3399/MCP-Bridge/releases/latest";
    private const string InstallerPrefix = "MCPBridge-Setup-";
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCPBridge-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using (var response = await Http.GetAsync(
                   ReleaseApiUrl,
                   HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                var serializer = new DataContractJsonSerializer(typeof(GitHubRelease));
                var release = serializer.ReadObject(stream) as GitHubRelease;
                if (release == null || release.Draft || release.Prerelease)
                    return null;

                var latestVersion = ParseReleaseVersion(release.TagName);
                if (latestVersion == null)
                    throw new InvalidDataException("GitHub returned an invalid stable release tag.");

                var currentVersion = GetCurrentVersion();
                if (latestVersion.CompareTo(currentVersion) <= 0)
                    return null;

                var expectedAssetName = InstallerPrefix + latestVersion.ToString(3) + ".exe";
                var asset = (release.Assets ?? new List<GitHubReleaseAsset>())
                    .FirstOrDefault(item => string.Equals(item.Name, expectedAssetName, StringComparison.OrdinalIgnoreCase));

                if (asset == null)
                    throw new InvalidDataException("The latest GitHub release does not contain " + expectedAssetName + ".");

                if (string.IsNullOrWhiteSpace(asset.Digest) ||
                    !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The latest installer does not publish a SHA-256 digest.");

                var hash = asset.Digest.Substring("sha256:".Length).Trim();
                if (!Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$"))
                    throw new InvalidDataException("The latest installer has an invalid SHA-256 digest.");

                if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
                    !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The latest installer download URL is not a trusted GitHub HTTPS URL.");

                return new AppUpdateInfo
                {
                    Version = latestVersion,
                    VersionLabel = latestVersion.ToString(3),
                    AssetName = asset.Name!,
                    DownloadUrl = downloadUri.AbsoluteUri,
                    Sha256 = hash.ToUpperInvariant(),
                    Size = asset.Size
                };
            }
        }
    }

    public async Task<string> DownloadAndVerifyAsync(
        AppUpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (update == null) throw new ArgumentNullException(nameof(update));

        Directory.CreateDirectory(AppPaths.UpdatesDirectory);
        var targetPath = Path.Combine(AppPaths.UpdatesDirectory, update.AssetName);

        if (File.Exists(targetPath) && string.Equals(
                await ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false),
                update.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            RemoveObsoleteInstallers(targetPath);
            update.LocalInstallerPath = targetPath;
            return targetPath;
        }

        // Incomplete transfers are disposable; already verified installers are preserved
        // until the replacement has fully downloaded and its digest has been checked.
        RemoveIncompleteDownloads();
        var temporaryPath = Path.Combine(
            AppPaths.UpdatesDirectory,
            ".MCPBridge-Download-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (var response = await Http.GetAsync(
                       update.DownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var contentLength = response.Content.Headers.ContentLength ?? update.Size;
                if (update.Size > 0 && contentLength > 0 && contentLength != update.Size)
                    throw new InvalidDataException("The installer size reported by GitHub does not match the download response.");

                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           81920,
                           FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        received += read;
                        progress?.Report(new UpdateDownloadProgress
                        {
                            BytesReceived = received,
                            TotalBytes = update.Size > 0 ? update.Size : contentLength
                        });
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(true);

                    if (update.Size > 0 && received != update.Size)
                        throw new InvalidDataException("The downloaded installer size does not match the GitHub release asset.");
                }
            }

            var actualHash = await ComputeSha256Async(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer failed SHA-256 verification.");

            // Only after the new EXE is complete and verified do we replace same-version
            // content and remove older versioned installers.
            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(temporaryPath, targetPath);
            RemoveObsoleteInstallers(targetPath);

            update.LocalInstallerPath = targetPath;
            return targetPath;
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    public static bool IsUpdaterRunning()
    {
        try
        {
            using (var updateMutex = Mutex.OpenExisting(UpdateMutexName))
            {
                try
                {
                    if (!updateMutex.WaitOne(0))
                        return true;

                    updateMutex.ReleaseMutex();
                    return IsUpdaterProcessRunning();
                }
                catch (AbandonedMutexException)
                {
                    // The updater crashed. The OS released its mutex; the next Bridge launch may proceed.
                    try { updateMutex.ReleaseMutex(); } catch { }
                    return IsUpdaterProcessRunning();
                }
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return IsUpdaterProcessRunning();
        }
        catch (UnauthorizedAccessException)
        {
            // Fail closed rather than launching Bridge while an updater may be replacing files.
            return true;
        }
    }

    private static bool IsUpdaterProcessRunning()
    {
        var updatesDirectory = Path.GetFullPath(AppPaths.UpdatesDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var updaterExecutable = Path.GetFullPath(AppPaths.UpdaterExecutable);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string processName;
                try { processName = process.ProcessName; }
                catch { continue; }

                if (!processName.StartsWith("MCPBridge.Updater", StringComparison.OrdinalIgnoreCase) &&
                    !processName.StartsWith("MCPBridge-Setup-", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath))
                        continue;

                    var fullPath = Path.GetFullPath(executablePath);
                    if (string.Equals(fullPath, updaterExecutable, StringComparison.OrdinalIgnoreCase) ||
                        fullPath.StartsWith(updatesDirectory, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // Fail closed when a matching updater process exists but its image path cannot be read.
                    return true;
                }
            }
        }

        return false;
    }

    public static void CleanupStaleUpdaterWorkers()
    {
        try
        {
            if (!Directory.Exists(AppPaths.UpdatesDirectory) || IsUpdaterRunning())
                return;

            foreach (var worker in Directory.GetFiles(
                         AppPaths.UpdatesDirectory,
                         "MCPBridge.Updater.Worker.*.exe",
                         SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(worker);
            }

            RemoveCompletedInstallers();
            RemoveIncompleteDownloads();
        }
        catch (Exception exception)
        {
            ErrorLog.Write("AppUpdateService.CleanupStaleUpdaterWorkers", exception);
        }
    }

    public static void CompleteUpdaterHandoff(string[] args)
    {
        if (args == null || args.Length < 4)
            return;

        var handoffIndex = Array.FindIndex(args, value =>
            string.Equals(value, "--update-handoff", StringComparison.OrdinalIgnoreCase));
        if (handoffIndex < 0 || args.Length <= handoffIndex + 3)
            return;

        if (!int.TryParse(args[handoffIndex + 1], out var workerPid))
            return;

        var workerPath = args[handoffIndex + 2];
        var removeInstallers = string.Equals(args[handoffIndex + 3], "1", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(workerPath))
            return;

        // Never terminate an updater that may still be writing application files.
        // The worker exits immediately after launching Bridge; wait for its natural exit.
        try
        {
            using (var worker = Process.GetProcessById(workerPid))
            {
                if (!worker.WaitForExit(15_000))
                    return;
            }
        }
        catch (ArgumentException)
        {
            // The worker already exited.
        }
        catch (InvalidOperationException)
        {
            // The worker already exited.
        }

        TryDeleteFile(workerPath);

        if (removeInstallers)
        {
            RemoveObsoleteInstallers(null);
            RemoveIncompleteDownloads();
        }
    }

    private static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .Select(attribute => attribute.InformationalVersion)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var match = Regex.Match(informational, @"\d+\.\d+\.\d+");
            if (match.Success && Version.TryParse(match.Value, out var parsed))
                return parsed;
        }

        return assembly.GetName().Version ?? new Version(0, 0, 0);
    }

    private static Version? ParseReleaseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var match = Regex.Match(tag.Trim(), @"^v?(?<version>\d+\.\d+\.\d+)$", RegexOptions.IgnoreCase);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
            ? version
            : null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        using (var sha256 = SHA256.Create())
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
        {
            // The file is local and bounded by the GitHub asset size; hashing is done off the UI thread.
            return await Task.Run(
                () => BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RemoveCompletedInstallers()
    {
        try
        {
            if (!Directory.Exists(AppPaths.UpdatesDirectory))
                return;

            var installedVersion = GetCurrentVersion();
            foreach (var file in Directory.GetFiles(
                         AppPaths.UpdatesDirectory,
                         InstallerPrefix + "*.exe",
                         SearchOption.TopDirectoryOnly))
            {
                var fileVersion = ParseReleaseVersion(Path.GetFileNameWithoutExtension(file).Substring(InstallerPrefix.Length));
                if (fileVersion != null && fileVersion.CompareTo(installedVersion) <= 0)
                    TryDeleteFile(file);
            }
        }
        catch (Exception exception)
        {
            ErrorLog.Write("AppUpdateService.RemoveCompletedInstallers", exception);
        }
    }

    private static void RemoveObsoleteInstallers(string? keepPath)
    {
        try
        {
            if (!Directory.Exists(AppPaths.UpdatesDirectory))
                return;

            var normalizedKeepPath = string.IsNullOrWhiteSpace(keepPath) ? null : Path.GetFullPath(keepPath);
            foreach (var file in Directory.GetFiles(AppPaths.UpdatesDirectory, InstallerPrefix + "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (normalizedKeepPath != null &&
                    string.Equals(Path.GetFullPath(file), normalizedKeepPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                TryDeleteFile(file);
            }
        }
        catch (Exception exception)
        {
            ErrorLog.Write("AppUpdateService.RemoveObsoleteInstallers", exception);
        }
    }

    private static void RemoveIncompleteDownloads()
    {
        try
        {
            if (!Directory.Exists(AppPaths.UpdatesDirectory))
                return;

            foreach (var file in Directory.GetFiles(AppPaths.UpdatesDirectory, ".MCPBridge-Download-*.tmp", SearchOption.TopDirectoryOnly))
                TryDeleteFile(file);
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    [DataContract]
    private sealed class GitHubRelease
    {
        [DataMember(Name = "tag_name")]
        public string? TagName { get; set; }

        [DataMember(Name = "draft")]
        public bool Draft { get; set; }

        [DataMember(Name = "prerelease")]
        public bool Prerelease { get; set; }

        [DataMember(Name = "assets")]
        public List<GitHubReleaseAsset>? Assets { get; set; }
    }

    [DataContract]
    private sealed class GitHubReleaseAsset
    {
        [DataMember(Name = "name")]
        public string? Name { get; set; }

        [DataMember(Name = "browser_download_url")]
        public string? DownloadUrl { get; set; }

        [DataMember(Name = "digest")]
        public string? Digest { get; set; }

        [DataMember(Name = "size")]
        public long Size { get; set; }
    }
}
