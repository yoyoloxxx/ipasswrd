using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IPasswrd.App;

// Fallback transport for the browser extension: a tiny HTTP server on 127.0.0.1.
//
// Why it exists: some antiviruses (Kaspersky in particular) block Chrome from LAUNCHING
// native-messaging host executables, which kills the normal pipe route entirely. Nothing
// stops the extension from calling localhost, though — so when the native host can't start,
// bg.js falls back to POSTing the same JSON commands here. Same handler, same rules.
//
// Security model (kept equivalent to the named pipe, which is CurrentUserOnly):
//  - bound to 127.0.0.1 only — never reachable from the network;
//  - the Host header must be our loopback address (blocks DNS-rebinding tricks);
//  - the Origin header must be EXACTLY our extension — ordinary web pages can't read or
//    even successfully send commands, so no site can pump passwords out of the vault;
//  - passwords still only leave the process while the vault is unlocked (same handler).
public partial class MainWindow
{
    private const int HttpBridgePort = 38799;
    private TcpListener? _httpBridge;

    private void StartHttpBridge()
    {
        try
        {
            _httpBridge = new TcpListener(IPAddress.Loopback, HttpBridgePort);
            _httpBridge.Start();
            _ = Task.Run(() => HttpAcceptLoop(_bridgeCts!.Token));
        }
        catch { _httpBridge = null; /* port busy — extension just uses the pipe host */ }
    }

    private async Task HttpAcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _httpBridge!.AcceptTcpClientAsync(ct);
                var c = client; client = null;
                _ = Task.Run(() => HttpServe(c, ct), ct);
            }
            catch when (ct.IsCancellationRequested) { client?.Dispose(); return; }
            catch { client?.Dispose(); try { await Task.Delay(300, ct); } catch { return; } }
        }
    }

    private async Task HttpServe(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var _ = client;
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;
            var stream = client.GetStream();

            // ---- read the request head (line + headers) ----
            var head = new MemoryStream();
            var one = new byte[1];
            while (head.Length < 32 * 1024)
            {
                int n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
                if (n == 0) return;
                head.WriteByte(one[0]);
                if (head.Length >= 4)
                {
                    var b = head.GetBuffer();
                    long L = head.Length;
                    if (b[L - 4] == '\r' && b[L - 3] == '\n' && b[L - 2] == '\r' && b[L - 1] == '\n') break;
                }
            }
            string headText = Encoding.UTF8.GetString(head.GetBuffer(), 0, (int)head.Length);
            var lines = headText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            var req = lines[0].Split(' ');
            if (req.Length < 2) return;
            string method = req[0].ToUpperInvariant();

            string Header(string name)
            {
                foreach (var l in lines.Skip(1))
                {
                    int i = l.IndexOf(':');
                    if (i > 0 && l[..i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                        return l[(i + 1)..].Trim();
                }
                return "";
            }

            string host = Header("Host");
            string origin = Header("Origin");
            bool hostOk = host == $"127.0.0.1:{HttpBridgePort}" || host == $"localhost:{HttpBridgePort}";
            // The Store build of the extension has a different identity from the unpacked one,
            // so this is a set rather than a single string. Anything not in it gets nothing:
            // no site may pump passwords out of the vault through this port.
            string? matched = TrustedExtensionIds()
                .Select(id => "chrome-extension://" + id)
                .FirstOrDefault(o => string.Equals(origin, o, StringComparison.OrdinalIgnoreCase));
            bool originOk = matched is not null;

            async Task Send(string status, string body, bool cors)
            {
                string extra = cors
                    ? $"Access-Control-Allow-Origin: {matched}\r\nAccess-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: content-type\r\nAccess-Control-Max-Age: 600\r\n"
                    : "";
                byte[] payload = Encoding.UTF8.GetBytes(body);
                string h = $"HTTP/1.1 {status}\r\n{extra}Content-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
                byte[] hb = Encoding.UTF8.GetBytes(h);
                await stream.WriteAsync(hb, ct);
                await stream.WriteAsync(payload, ct);
            }

            if (!hostOk || !originOk)                       // not our extension → nothing to see
            {
                await Send("403 Forbidden", "{\"ok\":false,\"error\":\"forbidden\"}", cors: false);
                return;
            }
            if (method == "OPTIONS")                        // CORS preflight
            {
                await Send("204 No Content", "", cors: true);
                return;
            }
            if (method != "POST")
            {
                await Send("405 Method Not Allowed", "{\"ok\":false,\"error\":\"method\"}", cors: true);
                return;
            }

            // ---- read the body (the JSON command line) ----
            int len = int.TryParse(Header("Content-Length"), out var cl) ? cl : 0;
            if (len <= 0 || len > 256 * 1024)
            {
                await Send("400 Bad Request", "{\"ok\":false,\"error\":\"bad_length\"}", cors: true);
                return;
            }
            var body = new byte[len];
            int got = 0;
            while (got < len)
            {
                int n = await stream.ReadAsync(body.AsMemory(got, len - got), ct);
                if (n == 0) break;
                got += n;
            }
            string json = Encoding.UTF8.GetString(body, 0, got);

            string resp = await HandleBridgeRequest(json, viaHttp: true);  // same dispatcher, but HTTP callers must be paired for secret commands
            await Send("200 OK", resp, cors: true);
        }
        catch { /* client went away / malformed — drop quietly */ }
    }
}
