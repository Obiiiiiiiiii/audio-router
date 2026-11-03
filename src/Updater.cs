using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace AudioRouter;

/// <summary>
/// Self-update from the repo's GitHub Releases. Swaps the exe in place: Windows lets a running exe be
/// renamed, so the current one becomes AudioRouter.old.exe, the new one takes its name, and launching it
/// replaces this process (see Program.TakeOverFromRunningCopy). The .old file is cleaned up next start.
/// </summary>
static class Updater
{
    const string LatestReleaseUrl = "https://api.github.com/repos/Obiiiiiiiiii/audio-router/releases/latest";
    const string AssetName = "AudioRouter.exe";

    public sealed record Release(Version Version, string Tag, string DownloadUrl);

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AudioRouter-updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentVersionText => $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    /// <summary>The latest release if it's newer than this build, otherwise null.</summary>
    public static async Task<Release?> CheckAsync()
    {
        using var resp = await Http.GetAsync(LatestReleaseUrl);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("No releases found (is the repo public and has a release been published?)");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        string? url = null;
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                url = asset.GetProperty("browser_download_url").GetString();
        }
        if (url == null) return null;

        var current = CurrentVersion;
        var cmp = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        var mine = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        return cmp > mine ? new Release(version, tag, url) : null;
    }

    /// <summary>Download, swap the exe, and start the new one (which then closes this one).</summary>
    public static async Task InstallAsync(Release release)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find my own exe");
        var dir = Path.GetDirectoryName(exe)!;
        var newPath = Path.Combine(dir, "AudioRouter.new.exe");
        var oldPath = Path.Combine(dir, "AudioRouter.old.exe");

        Log.Info($"Downloading update {release.Tag}");
        using (var resp = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var file = File.Create(newPath);
            await resp.Content.CopyToAsync(file);
        }
        if (new FileInfo(newPath).Length < 1_000_000)
            throw new InvalidOperationException("Downloaded update looks incomplete");

        TryDelete(oldPath);
        File.Move(exe, oldPath);
        try
        {
            File.Move(newPath, exe);
        }
        catch
        {
            File.Move(oldPath, exe); // put things back
            throw;
        }

        Log.Info($"Installed {release.Tag}; restarting");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
    }

    /// <summary>Called at startup: remove leftovers from a previous update.</summary>
    public static void CleanUp()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        var dir = Path.GetDirectoryName(exe)!;
        TryDelete(Path.Combine(dir, "AudioRouter.old.exe"));
        TryDelete(Path.Combine(dir, "AudioRouter.new.exe"));
    }

    static void TryDelete(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch { Thread.Sleep(300); }
        }
    }
}
