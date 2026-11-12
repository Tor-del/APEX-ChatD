using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

using System.IO;
using System.Net.Http;

namespace ApexChatd.GitHubUpdates;

internal sealed class GitHubUpdater(HttpClient? client = null)
{
    private const string Repository = "https://github.com/Tor-del/APEX-ChatD";
    private const string ReleasesApi = "https://api.github.com/repos/Tor-del/APEX-ChatD/releases";
    private static readonly Version FirstAutomaticInstaller = new(1, 18, 0, 0);
    private const long MaxInstallerSize = 512L * 1024 * 1024;
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient http = client ?? SharedClient;

    internal sealed record Release(Version Version, Uri Page, Uri Download, long Size, string Digest);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("APEX-Chatd-Updater");
        return client;
    }

    public Task<Release?> CheckAsync(Version current, CancellationToken ct) =>
        ReadReleaseAsync("latest", version => version > current, allowLegacyInstaller: false, ct);

    public Task<Release?> FindRollbackAsync(Version target, CancellationToken ct) =>
        ReadReleaseAsync($"tags/v{target.ToString(3)}", version => version == target, allowLegacyInstaller: true, ct);

    private async Task<Release?> ReadReleaseAsync(string endpoint, Func<Version, bool> accept,
        bool allowLegacyInstaller, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ReleasesApi}/{endpoint}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || tag != $"v{version}" || root.GetProperty("draft").GetBoolean() ||
            root.GetProperty("prerelease").GetBoolean())
            return null;
        var build = new Version(version.Major, version.Minor, version.Build, 0);
        if (!accept(build)) return null;

        var baseName = $"APEX-Chatd-Setup-{build}";
        var page = new Uri($"{Repository}/releases/tag/{tag}");
        var variantFile = Path.Combine(AppContext.BaseDirectory, "installer-variant.txt");
        var withEdge = File.Exists(variantFile) && File.ReadAllText(variantFile).Trim() == "edge-tts";
        var assets = root.GetProperty("assets");
        string[] variants = withEdge
            ? allowLegacyInstaller ? ["-edge-tts", ""] : ["-edge-tts"]
            : [""];
        foreach (var variant in variants)
        {
            var name = $"{baseName}{variant}-x64.exe";
            var download = new Uri($"{Repository}/releases/download/{tag}/{name}");
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != name)
                    continue;
                var size = asset.GetProperty("size").GetInt64();
                var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
                if (asset.GetProperty("browser_download_url").GetString() == download.AbsoluteUri &&
                    size > 0 && size <= MaxInstallerSize && digest is { Length: 71 } &&
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.AsSpan(7).ToString().All(Uri.IsHexDigit))
                    return new(build, page, download, size, digest[7..]);
                throw new InvalidDataException(Ui.T("В релизе нет установщика с проверяемой контрольной суммой."));
            }
        }
        throw new InvalidDataException(Ui.T("В релизе нет установщика с проверяемой контрольной суммой."));
    }

    public static Version? FindPreviousVersion(string? directory, Version currentVersion)
    {
        if (directory is null) return null;
        try
        {
            var file = Path.Combine(directory, "previous-version.txt");
            return File.Exists(file) && Version.TryParse(File.ReadAllText(file).Trim(), out var previous) &&
                previous.Revision == 0 && previous >= FirstAutomaticInstaller && previous < currentVersion ? previous : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? FindInstallation()
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\APEXChatd.Desktop_is1";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var key = root.OpenSubKey(keyPath);
            if (key?.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location) &&
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),
                    Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                return location;
        }
        return null;
    }

    public async Task<string> DownloadAsync(Release release, IProgress<int> progress, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "APEX-Chatd-Updates");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, Path.GetFileName(release.Download.AbsolutePath));
        var partial = file + ".partial";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        try
        {
            using var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long written = 0;
                var lastProgress = -1;
                for (int read; (read = await source.ReadAsync(buffer, token)) > 0;)
                {
                    written += read;
                    if (written > release.Size) throw InvalidInstaller();
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    var percent = (int)(written * 100 / release.Size);
                    if (percent != lastProgress) progress.Report(lastProgress = percent);
                }
                if (written != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
                    throw InvalidInstaller();
            }
            var version = FileVersionInfo.GetVersionInfo(partial);
            if (new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart) != release.Version)
                throw InvalidInstaller();
            File.Move(partial, file, overwrite: true);
            return file;
        }
        finally
        {
            try { File.Delete(partial); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void LaunchInstaller(string file, string installDirectory)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var argument in new[] { "/VERYSILENT", "/SP-", "/NORESTART", "/NOCLOSEAPPLICATIONS", "/APEXUPDATE=1",
                     $"/APEXWAITPID={Environment.ProcessId}", $"/DIR={Path.TrimEndingDirectorySeparator(installDirectory)}" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException(Ui.T("Не удалось запустить установщик."));
    }

    private static InvalidDataException InvalidInstaller() => new(Ui.T("Не удалось проверить загруженный установщик."));
}
