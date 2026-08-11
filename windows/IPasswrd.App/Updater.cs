using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
/// The waiter is armed the moment the download finishes, not on the way out of a clean
/// shutdown. Otherwise an update would sit staged forever for anyone who ends the app by
/// killing it, logging off or rebooting — which, for a tray app, is most people.
///
/// A portable copy (run straight from <c>dist</c>) is not a Velopack install; every method
/// here degrades to "no updates" rather than pretending otherwise.
/// </summary>
internal static class Updater
{
    private const string RepoUrl = "https://github.com/yoyoloxxx/ipasswrd";

    private static UpdateManager? _mgr;
    private static bool _armed;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? fullName);

    /// <summary>
    /// True when this copy came from the Microsoft Store. Updates there are the Store's job,
    /// so offering a "check for updates" button would be a button that does nothing.
    /// </summary>
    public static bool IsStoreBuild { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        const int NoPackage = 15700;   // APPMODEL_ERROR_NO_PACKAGE
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != NoPackage;
        }
        catch { return false; }   // older Windows without the app-model API
    }

    private static UpdateManager Manager =>
        _mgr ??= new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    /// <summary>True when running from an installed copy that can update itself.</summary>
    public static bool IsManagedInstall
    {
        get
        {
            if (IsStoreBuild) return false;   // the Store owns updates for its own copies
            try { return Manager.IsInstalled; } catch { return false; }
        }
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
            StagedVersion = info.TargetFullRelease.Version.ToString();

            // Arm the swap now: Update.exe sits waiting for this process to end, however it
            // ends. Nothing restarts on its own — the new build is simply what starts next time.
            //
            // The waiter is armed with null deliberately: null means "apply whatever is newest
            // in the packages folder when the app exits". Arming it with THIS release pinned
            // the first version the app happened to see, so a second release published while
            // the app was still running downloaded, sat there and was never applied — the app
            // came back on the older build. Over an evening of quick releases that looked for
            // all the world like broken delta updates, and cost them: they were turned off.
            if (!_armed)
            {
                try
                {
                    Manager.WaitExitThenApplyUpdates(null, silent: true, restart: false);
                    _armed = true;
                }
                catch { /* stays staged; the next launch will try again */ }
            }
            return StagedVersion;
        }
        catch { return null; }
    }
}
