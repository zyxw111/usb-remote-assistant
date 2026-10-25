using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UsbRemoteAssistant {
    public static partial class Tests {
        public static int RunBoard(string reportPath) {
            var results = new List<string>();
            string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)), "board-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);
            try { BoardTests(results, root); results.Add("ALL BOARD AND STATUS TESTS PASSED"); File.WriteAllLines(reportPath, results, new UTF8Encoding(false)); return 0; }
            catch (Exception e) { results.Add("FAILED: " + e); File.WriteAllLines(reportPath, results, new UTF8Encoding(false)); return 1; }
        }
        static void BoardTests(List<string> results, string root) {
            string key = new String('a', 64);
            var legacy = JsonData.Read<Settings>("{\"ManageEasyTier\":false,\"PrimaryIp\":\"192.0.2.10\",\"SecondaryIp\":\"192.0.2.20\",\"UsbPort\":7575,\"TransferPort\":17875,\"PairKey\":\"" + key + "\"}");
            legacy.Validate(); Assert(!legacy.BoardEnabled && legacy.BoardSshPort == 22 && legacy.BoardListenPort == 2222 && legacy.BoardIp == "", "old settings migrate without enabling an unknown board");
            var options = new BoardOptions { Enabled = true, Ip = "198.51.100.2", User = "board_user", ListenPort = 2222, SshPort = 22 };
            options.Validate(legacy); Assert(options.Command(legacy.SecondaryIp) == "ssh -p 2222 -l board_user 192.0.2.20", "SSH command uses board identity on secondary endpoint");
            foreach (var bad in new[] {
                new BoardOptions { Enabled = true, Ip = "" }, new BoardOptions { Enabled = true, Ip = "127.0.0.1" },
                new BoardOptions { Ip = "198.51.100.2", ListenPort = 7575 }, new BoardOptions { User = "root & echo bad" },
                new BoardOptions { User = "-ProxyCommand=bad" }, new BoardOptions { ListenPort = 22 }
            }) { bool rejected = false; try { bad.Validate(legacy); } catch (IOException) { rejected = true; } Assert(rejected, "unsafe/invalid board settings rejected"); }
            bool usbStarted = false, fileStarted = false;
            var started = Startup.Independent(() => { usbStarted = true; return new List<UsbDevice>(); }, () => { fileStarted = true; }, () => { throw new IOException("board relay occupied"); });
            Assert(usbStarted && fileStarted && started.BoardError != null && started.UsbError == null && started.FileError == null, "SSH relay failure must not block USB or files");
            results.Add("PASS old settings migration, configurable board options, command validation, and independent SSH/USB/file startup");

            var tracker = new PeerTracker(); var now = DateTime.UtcNow;
            var primary = new AssistantStatus { Role = "主机端", UsbConnected = true, UsbOwned = true };
            tracker.Record(primary, "主机端", now);
            Assert(PeerTracker.SecondaryUsbText(true, true, tracker.Fresh(now)).Contains("已接入主机"), "secondary reflects actual imported USB");
            Assert(PeerTracker.SecondaryUsbText(true, false, tracker.Fresh(now)).Contains("等待主机"), "disconnected TCP cannot be overridden by cached ownership");
            Assert(PeerTracker.SecondaryUsbText(true, true, null).Contains("主机已连接"), "old clients with no status still show actual TCP connection");
            Assert(tracker.Fresh(now.AddSeconds(26)) == null, "expired status is not shown as connected ownership");
            bool roleRejected = false; try { tracker.Record(primary, "副机端", now); } catch (IOException) { roleRejected = true; } Assert(roleRejected, "opposite role enforced");
            primary.UsbOwned = false; Assert(PeerTracker.SecondaryUsbText(true, true, primary).Contains("等待选择"), "connection without device ownership is distinguished");
            Assert(Sys.UsbConnectionMatches(7575, IPAddress.Parse("::ffff:192.0.2.10"), System.Net.NetworkInformation.TcpState.Established, legacy), "IPv4-mapped IPv6 peer correctly recognized");
            Assert(!Sys.UsbConnectionMatches(17875, IPAddress.Parse("192.0.2.10"), System.Net.NetworkInformation.TcpState.Established, legacy), "file/control socket is not confused with USB session");
            tracker.Clear(); Assert(tracker.Fresh(now) == null, "stop clears peer state");
            results.Add("PASS secondary USB states: awaiting main, TCP connected, device imported, old peer fallback, stale heartbeat expiry, and disconnect");

            var backend = new TcpListener(IPAddress.Loopback, 0); backend.Start(); int backendPort = ((IPEndPoint)backend.LocalEndpoint).Port;
            using (var relay = new BoardRelay("127.0.0.1", "127.0.0.1", 0, "127.0.0.1", backendPort, "test", x => {})) {
                relay.Start(); int relayPort = relay.ActualPort;
                byte[] request = new byte[3 * 1024 * 1024 + 37], response = new byte[2 * 1024 * 1024 + 19]; new Random(46).NextBytes(request); new Random(71).NextBytes(response);
                var board = Task.Run(() => {
                    using (var accepted = backend.AcceptTcpClient()) using (var stream = accepted.GetStream()) using (var received = new MemoryStream()) {
                        accepted.ReceiveTimeout = 10000; byte[] buffer = new byte[65536]; int n;
                        while ((n = stream.Read(buffer, 0, buffer.Length)) != 0) received.Write(buffer, 0, n);
                        Assert(Wire.Equal(Wire.Digest(request), Wire.Digest(received.ToArray())), "relay request bytes verified");
                        stream.Write(response, 0, response.Length);
                    }
                });
                using (var client = new TcpClient()) {
                    client.Connect(IPAddress.Loopback, relayPort); client.ReceiveTimeout = 10000;
                    var stream = client.GetStream(); stream.Write(request, 0, request.Length); client.Client.Shutdown(SocketShutdown.Send);
                    using (var result = new MemoryStream()) { byte[] buffer = new byte[65536]; int n; while ((n = stream.Read(buffer, 0, buffer.Length)) != 0) result.Write(buffer, 0, n); Assert(Wire.Equal(Wire.Digest(response), Wire.Digest(result.ToArray())), "relay response after half-close verified"); }
                }
                Assert(board.Wait(10000), "board completes after EOF");
                results.Add("PASS real TCP relay: 3 MiB request and 2 MiB response independently SHA-256 checked, including TCP half-close");
                backend.Stop();
                using (var offline = new TcpClient()) {
                    offline.Connect(IPAddress.Loopback, relayPort); offline.ReceiveTimeout = 7000; bool ended = false;
                    try { ended = offline.GetStream().ReadByte() == -1; } catch (IOException) { ended = true; }
                    Assert(ended && relay.Running, "offline board terminates only this session; relay stays up");
                }
                backend = new TcpListener(IPAddress.Loopback, backendPort); backend.Start();
                using (var denied = new TcpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0))) {
                    denied.Connect(IPAddress.Loopback, relayPort); denied.ReceiveTimeout = 3000; bool refused = false;
                    try { refused = denied.GetStream().ReadByte() == -1; } catch (IOException) { refused = true; } Assert(refused, "non-paired source IP rejected");
                }
                bool occupied = false;
                using (var duplicate = new BoardRelay("127.0.0.1", "127.0.0.1", relayPort, "127.0.0.1", backendPort, "duplicate", x => {})) { try { duplicate.Start(); } catch (SocketException) { occupied = true; } }
                Assert(occupied && relay.Running, "occupied port is not taken over");
                var bannerTask = Task.Run(() => { using (var b = backend.AcceptTcpClient()) { byte[] banner = Encoding.ASCII.GetBytes("SSH-2.0-BoardTest\r\n"); b.GetStream().Write(banner, 0, banner.Length); b.ReceiveTimeout = 5000; try { b.GetStream().ReadByte(); } catch {} } });
                Assert(MainForm.ReadSshBanner("127.0.0.1", relayPort) == "SSH-2.0-BoardTest", "SSH identity travels through relay"); Assert(bannerTask.Wait(6000), "banner probe closes session");
                results.Add("PASS board offline then online: same relay accepts a new SSH connection without restart");
                using (var active = new TcpClient()) {
                    active.Connect(IPAddress.Loopback, relayPort); using (var b = backend.AcceptTcpClient()) {
                        Assert(SpinWait.SpinUntil(() => relay.Active > 0, 3000), "active relay session is counted"); relay.Dispose(); active.ReceiveTimeout = 3000;
                        bool closed = false; try { closed = active.GetStream().ReadByte() == -1; } catch (IOException) { closed = true; } Assert(closed && !relay.Running && !Sys.CanConnect("127.0.0.1", relayPort, 250), "Stop closes active sessions and listener");
                    }
                }
                results.Add("PASS peer-IP restriction, occupied-port rejection, actual SSH banner, active-session counting, and Stop teardown");
            }
            backend.Stop();

            using (var server = new TransferServer("127.0.0.1", "127.0.0.1", 0, Path.Combine(root, "control-inbox"), key, x => {})) {
                int controlCalls = 0; var busy = new ManualResetEventSlim(false);
                server.ControlHandler = (command, data) => { Interlocked.Increment(ref controlCalls); if (command == "assistant-board-v1") { var incoming = JsonData.Read<BoardOptions>(data); incoming.Validate(legacy); return JsonData.Write(incoming); } return JsonData.Write(primary); };
                server.Start(); int port = server.ActualPort;
                var report = JsonData.Read<AssistantStatus>(AssistantClient.Request("127.0.0.1", port, key, "assistant-status-v1", JsonData.Write(new AssistantStatus { Role = "副机端" })));
                Assert(report.Role == "主机端", "authenticated control status request");
                var saved = JsonData.Read<BoardOptions>(AssistantClient.Request("127.0.0.1", port, key, "assistant-board-v1", JsonData.Write(options)));
                Assert(saved.Ip == options.Ip && saved.ListenPort == 2222, "authenticated settings round-trip");
                int beforeBad = controlCalls; bool badKey = false;
                try { AssistantClient.Request("127.0.0.1", port, new String('b', 64), "assistant-board-v1", JsonData.Write(options)); } catch { badKey = true; }
                Assert(badKey && controlCalls == beforeBad, "unauthenticated control cannot change settings");
                using (var slow = new TcpClient()) {
                    slow.Connect(IPAddress.Loopback, port); slow.ReceiveTimeout = 5000;
                    using (var stream = slow.GetStream()) using (var r = new BinaryReader(stream, Encoding.UTF8, true)) using (var w = new BinaryWriter(stream, Encoding.UTF8, true)) {
                        Wire.ClientAuth(r, w, key); Wire.Text(w, "file"); Wire.Text(w, "slow.bin"); w.Write(1L); w.Write(0L); Wire.Text(w, new String('0', 64)); w.Flush();
                        Assert(Wire.Text(r) == "checking", "file transfer holds serialization gate while checking");
                        var duringFile = JsonData.Read<AssistantStatus>(AssistantClient.Request("127.0.0.1", port, key, "assistant-status-v1", JsonData.Write(new AssistantStatus { Role = "副机端" })));
                        Assert(duringFile.Role == "主机端", "heartbeat cannot be blocked behind a file transfer");
                    }
                }
                results.Add("PASS mutually authenticated control, board settings synchronization, wrong-key rejection, and heartbeat while file gate is occupied");
            }
        }
    }
}
