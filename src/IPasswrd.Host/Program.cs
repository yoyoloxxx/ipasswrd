using System.IO.Pipes;
using System.Text;

// IPasswrd native-messaging host: bridges the Chrome extension's stdio to the app's
// named pipe ("ipasswrd.browser"). Chrome framing: 4-byte little-endian length + UTF-8
// JSON in both directions. One request → one response; Chrome spawns this process per
// sendNativeMessage call. If the app is not running, it is started once and awaited.

Log("started args=[" + string.Join(' ', args) + "]");

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

while (true)
{
    byte[]? msg = ReadMessage(stdin);
    if (msg is null) { Log("stdin closed"); break; }          // browser closed the port
    string req = Encoding.UTF8.GetString(msg);
    Log("req cmd=" + CmdOf(req));
    string resp = Forward(req);
    Log("resp=" + Head(resp));
    WriteMessage(stdout, Encoding.UTF8.GetBytes(resp));
}

// Diagnostic breadcrumbs only — command names and response heads, never payloads/secrets.
static void Log(string s)
{
    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ipasswrd-host.log"),
        DateTime.Now.ToString("HH:mm:ss.fff") + " [" + Environment.ProcessId + "] " + s + Environment.NewLine); }
    catch { /* logging must never break the bridge */ }
}

static string CmdOf(string json)
{
    try { using var d = System.Text.Json.JsonDocument.Parse(json);
          return d.RootElement.TryGetProperty("cmd", out var c) ? (c.GetString() ?? "?") : "?"; }
    catch { return "unparsed"; }
}

static string Head(string s) => s.Length <= 60 ? s : s[..60];

static byte[]? ReadMessage(Stream s)
{
    byte[] len = new byte[4];
    if (!FillExactly(s, len)) return null;
    int n = BitConverter.ToInt32(len, 0);
    if (n <= 0 || n > (1 << 20)) return null;                 // sanity: 1 MB cap
    byte[] buf = new byte[n];
    return FillExactly(s, buf) ? buf : null;
}

static bool FillExactly(Stream s, byte[] buf)
{
    int off = 0;
    while (off < buf.Length)
    {
        int r = s.Read(buf, off, buf.Length - off);
        if (r <= 0) return false;
        off += r;
    }
    return true;
}

static void WriteMessage(Stream s, byte[] payload)
{
    s.Write(BitConverter.GetBytes(payload.Length), 0, 4);
    s.Write(payload, 0, payload.Length);
    s.Flush();
}

static string Forward(string json)
{
    for (int attempt = 0; attempt < 2; attempt++)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "ipasswrd.browser",
                PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);
            using var w = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var r = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            w.WriteLine(json);
            return r.ReadLine() ?? "{\"ok\":false,\"error\":\"no_response\"}";
        }
        catch
        {
            if (attempt == 0 && TryStartApp()) continue;      // app was down — started it, retry once
            return "{\"ok\":false,\"error\":\"app_not_running\"}";
        }
    }
    return "{\"ok\":false,\"error\":\"app_not_running\"}";
}

static bool TryStartApp()
{
    try
    {
        // dist-host\IPasswrd.Host.exe → ..\dist\IPasswrd.App.exe (repo layout), else the known path.
        string exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "dist", "IPasswrd.App.exe"));
        if (!File.Exists(exe)) exe = @"D:\MyProjects\IPasswrd\dist\IPasswrd.App.exe";
        if (!File.Exists(exe)) return false;
        // Start HIDDEN in the tray: the browser talks over the pipe; no window pops up.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--tray") { UseShellExecute = true });

        for (int i = 0; i < 25; i++)                          // wait up to ~10 s for the pipe server
        {
            Thread.Sleep(400);
            try
            {
                using var probe = new NamedPipeClientStream(".", "ipasswrd.browser",
                    PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                probe.Connect(200);
                return true;
            }
            catch { /* not up yet */ }
        }
        return false;
    }
    catch { return false; }
}
