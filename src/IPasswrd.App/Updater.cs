using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace IPasswrd.App;

/// <summary>
/// Updates from GitHub Releases.
///
/// Nothing is ever applied while the app is running. A new build is downloaded quietly in the
/// background and swapped in on exit — a password manager must not restart itself out from
/// under someone who is halfway through editing an entry with the vault open.
///
/// A portable copy (run straight from <c>dist</c>) is not a Velopack install; every method
/// here degrades to "no updates" rather than pretending otherwise.
/// </summary>
internal static class Updater
{
    private const string RepoUrl = "https://github.com/yoyololka/ipasswrd";

    private static UpdateManager? _mgr;
    private static UpdateInfo? _staged;

    private static UpdateManager Manager =>
        _mgr ??= new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    /// <summary>True when running from an installed copy that can update itself.</summary>
    public static bool IsManagedInstall
    {
        get { try { return Manager.IsInstalled; } catch { return false; } }
    }

    /// <summary>Version to show in Settings — the installed one, or the assembly version for a portable run.</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                if (Manager.IsInstalled && Manager.CurrentVersion is { } v) return v.ToString();
            }
            catch { /* fall through to the assembly version */ }

            Version? asm = Assembly.GetExecutingAssembly().GetName().Version;
            return asm is null ? "1.0.0" : $"{asm.Major}.{asm.Minor}.{asm.Build}";
        }
    }

    /// <summary>Version already downloaded and waiting for the next launch, if any.</summary>
    public static string? StagedVersion { get; private set; }

    /// <summary>
    /// Look for a newer release and download it. Returns the version now waiting, or null when
    /// there is nothing new. Never throws: no network, a rate limit or a moved repo are all
    /// reasons to stay quiet rather than to interrupt someone.
    /// </summary>
    public static async Task<string?> CheckAndStageAsync()
    {
        if (!IsManagedInstall) return null;
        try
        {
            UpdateInfo? info = await Manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null) return null;

            await Manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
            _staged = info;
            StagedVersion = info.TargetFullRelease.Version.ToString();
            return StagedVersion;
        }
        catch { return null; }
    }

    /// <summary>Hand the staged build over to the updater, to be applied once this process exits.</summary>
    public static void ApplyOnExit()
    {
        if (_staged is null) return;
        try { Manager.WaitExitThenApplyUpdates(_staged, silent: true, restart: false); }
        catch { /* the update simply stays staged for next time */ }
    }
}
