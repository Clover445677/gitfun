using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatGptBridge {
    // A loopback HTTP CONNECT relay. TLS stays end-to-end; no certificates or MITM.
    public sealed class ProxyGate : IDisposable {
        readonly int port;
        readonly Func<bool> safe;
        readonly Func<int> upstreamPort;
        readonly Func<string, bool> allowedHost;
        readonly TcpListener listener;
        readonly ConcurrentDictionary<int, TcpClient> clients = new ConcurrentDictionary<int, TcpClient>();
        volatile bool stopping;
        int nextId;
        public ProxyGate(int port, Func<bool> safe, Func<int> upstreamPort, Func<string, bool> allowedHost) {
            this.port = port; this.safe = safe; this.upstreamPort = upstreamPort; this.allowedHost = allowedHost; listener = new TcpListener(IPAddress.Loopback, port);
        }
        public void Start() { listener.Start(); Task.Run((Func<Task>)Accept); }
        async Task Accept() {
            while (!stopping) {
                try { TcpClient c = await listener.AcceptTcpClientAsync(); int id = Interlocked.Increment(ref nextId); clients[id] = c; Task.Run(() => Serve(id, c)); }
                catch (ObjectDisposedException) { break; } catch (SocketException) { if (stopping) break; }
            }
        }
        internal static byte[] ReadHeader(NetworkStream stream) {
            using (var memory = new MemoryStream()) {
                int matched = 0;
                while (memory.Length < 32768) {
                    int b = stream.ReadByte(); if (b < 0) throw new IOException("Connection closed."); memory.WriteByte((byte)b);
                    int expected = matched == 0 || matched == 2 ? 13 : 10;
                    matched = b == expected ? matched + 1 : (b == 13 ? 1 : 0);
                    if (matched == 4) return memory.ToArray();
                }
                throw new IOException("Header too large.");
            }
        }
        async Task Serve(int id, TcpClient client) {
            TcpClient upstream = null;
            try {
                client.NoDelay = true; var incoming = client.GetStream(); incoming.ReadTimeout = 7000;
                byte[] header = ReadHeader(incoming); string first = Encoding.ASCII.GetString(header).Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                string[] line = first.Split(' ');
                if (line.Length != 3 || line[0] != "CONNECT" || !ValidAuthority(line[1])) { await Error(incoming, 400, "Only HTTPS CONNECT is supported"); return; }
                if (!allowedHost(new Uri("https://" + line[1]).Host)) { await Error(incoming, 403, "This local bridge accepts OpenAI destinations only"); return; }
                // The guard must be healthy and the core rules must still pin OpenAI to US.
                // Never resolve the destination here, and never connect to it directly.
                if (!safe()) { await Error(incoming, 503, "US route is unavailable; no direct connection allowed"); return; }
                upstream = new TcpClient(); upstream.NoDelay = true;
                Task connect = upstream.ConnectAsync(IPAddress.Loopback, upstreamPort());
                if (await Task.WhenAny(connect, Task.Delay(5000)) != connect) throw new IOException("Proxy timeout.");
                await connect;
                var outgoing = upstream.GetStream(); outgoing.ReadTimeout = 7000;
                byte[] request = Encoding.ASCII.GetBytes("CONNECT " + line[1] + " HTTP/1.1\r\nHost: " + line[1] + "\r\n\r\n");
                await outgoing.WriteAsync(request, 0, request.Length);
                byte[] response = ReadHeader(outgoing); string status = Encoding.ASCII.GetString(response).Split('\n')[0];
                if (!status.StartsWith("HTTP/1.1 200") && !status.StartsWith("HTTP/1.0 200")) { await Error(incoming, 503, "US server unavailable; waiting for recovery"); return; }
                await incoming.WriteAsync(response, 0, response.Length);
                incoming.ReadTimeout = Timeout.Infinite; outgoing.ReadTimeout = Timeout.Infinite;
                Task a = incoming.CopyToAsync(outgoing), b = outgoing.CopyToAsync(incoming);
                await Task.WhenAny(a, b);
            } catch { try { Error(client.GetStream(), 503, "US route unavailable; no direct fallback").GetAwaiter().GetResult(); } catch { } }
            finally { TcpClient removed; clients.TryRemove(id, out removed); client.Close(); if (upstream != null) upstream.Close(); }
        }
        internal static bool ValidAuthority(string authority) {
            if (authority.IndexOfAny(new[] { '\r', '\n', '/', '\\', '@', ' ', '?' }) >= 0) return false;
            Uri parsed; return Uri.TryCreate("https://" + authority, UriKind.Absolute, out parsed) && parsed.Port > 0 && parsed.Port <= 65535 && parsed.Host.Length > 0;
        }
        static Task Error(NetworkStream stream, int code, string text) {
            byte[] body = Encoding.UTF8.GetBytes(text); byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " Service Unavailable\r\nConnection: close\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + body.Length + "\r\n\r\n");
            byte[] all = new byte[header.Length + body.Length]; Buffer.BlockCopy(header, 0, all, 0, header.Length); Buffer.BlockCopy(body, 0, all, header.Length, body.Length); return stream.WriteAsync(all, 0, all.Length);
        }
        public void BlockExisting() { foreach (var c in clients.Values) try { c.Close(); } catch { } }
        public void Dispose() { stopping = true; listener.Stop(); BlockExisting(); }
    }
}
