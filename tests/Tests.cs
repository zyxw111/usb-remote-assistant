using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace UsbRemoteAssistant {
    public static partial class Tests {
        [DllImport("kernel32.dll", SetLastError=true)]
        static extern bool DeviceIoControl(SafeFileHandle handle, uint control, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
        static void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
        static string Hash(string path) { using (var f = File.OpenRead(path)) return Wire.Hash(f, f.Length, CancellationToken.None, null); }
        static TransferItem Item(string path, string relative) { return new TransferItem { Source = path, Relative = relative, Size = new FileInfo(path).Length }; }
        static void WritePattern(string path, int size, int seed) {
            var random = new Random(seed); byte[] buffer = new byte[1024 * 1024]; random.NextBytes(buffer);
            using (var file = File.Create(path)) { int left = size; while (left > 0) { int n = Math.Min(left, buffer.Length); file.Write(buffer, 0, n); left -= n; } }
        }
        static void Sparse(string path, long length) {
            using (var f = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None)) {
                uint returned;
                if (!DeviceIoControl(f.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero)) throw new Exception("Cannot create sparse test file: " + Marshal.GetLastWin32Error());
                f.SetLength(length); f.Flush(true);
            }
        }
        public static int RunRecovery(string reportPath) {
            var results = new List<string>();
            try { RecoveryTests(results); results.Add("ALL RECOVERY TESTS PASSED"); File.WriteAllLines(reportPath, results, new UTF8Encoding(false)); return 0; }
            catch (Exception e) { results.Add("FAILED: " + e); File.WriteAllLines(reportPath, results, new UTF8Encoding(false)); return 1; }
        }
        static void RecoveryTests(List<string> results) {
            var session = new RecoverySession(); int networkCalls = 0, usbCalls = 0, fileCalls = 0;
            session.Start();
            Action network = () => { if (++networkCalls < 3) throw new IOException("network not assigned yet"); };
            Func<List<UsbDevice>> usb = () => { usbCalls++; return new List<UsbDevice>(); };
            Action files = () => { fileCalls++; };
            Assert(session.Tick(network, usb, files).NetworkError != null && session.Enabled, "first network failure keeps recovery enabled");
            Assert(session.Tick(network, usb, files).NetworkError != null && usbCalls == 0 && fileCalls == 0, "unready network defers service startup");
            Assert(session.Tick(network, usb, files).NetworkError == null && usbCalls == 1 && fileCalls == 1, "network recovers without another Start click");
            session.Stop(); Assert(session.Tick(network, usb, files) == null && networkCalls == 3 && usbCalls == 1 && fileCalls == 1, "Stop prevents automatic restarts");
            results.Add("PASS network unavailable for two cycles, later ready: retries stay enabled; explicit Stop suppresses restart");

            var peer = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            peer.Start(); int port = ((System.Net.IPEndPoint)peer.LocalEndpoint).Port; peer.Stop();
            peer = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            bool configured = false, imported = false, board = false, owned = false;
            int addCalls = 0, useCalls = 0, ensureCalls = 0;
            var cfg = new Settings { SecondaryIp = "127.0.0.1", UsbPort = port, DeviceVendor = 1027, DeviceProduct = 24592, DeviceSerial = "TEST-CABLE-001" };
            Func<bool> listening = () => Sys.CanConnect("127.0.0.1", port, 250);
            Func<string, string> transport = command => {
                if (command == "MANUAL HUB LIST") return configured ? "127.0.0.1:" + port : "";
                if (command == "MANUAL HUB ADD,127.0.0.1:" + port) { configured = true; imported = listening(); addCalls++; return "OK"; }
                if (command.StartsWith("USE,")) { Assert(command == "USE,SECONDARY.9", "must never auto-use the fingerprint reader"); owned = true; useCalls++; return "OK"; }
                if (command != "GET CLIENT STATE") throw new Exception("Unexpected IPC command: " + command);
                bool connected = imported;
                string device = board ? "<device product='TUL' vendor='Xilinx' idVendor='1027' idProduct='24592' deviceSerial='TEST-CABLE-001' address='9' state='" + (owned ? "3" : "1") + "' boundClientHostname='" + (owned ? Environment.MachineName : "") + "'/>" : "<device product='Fingerprint' idVendor='10637' idProduct='8243' address='1' state='1'/>";
                // Include cached devices even when disconnected to verify that stale lists are rejected.
                return "<state><server><connection host='127.0.0.1' hostname='SECONDARY' port='" + port + "' state='" + (connected ? "3" : "1") + "'/>" + device + "</server></state>";
            };
            var controller = new UsbController(cfg, x => {}, transport, () => { ensureCalls++; }, listening, ms => {});
            try {
                session.Start();
                UsbSnapshot state = null;
                Func<List<UsbDevice>> poll = () => { state = controller.PollPrimary(); return state.Devices; };
                session.Tick(() => {}, poll, files);
                Assert(!state.PeerConnected && state.Devices.Count == 0 && addCalls == 1, "primary starts while secondary is offline");
                session.Tick(() => {}, poll, files);
                Assert(!state.PeerConnected && addCalls == 1 && fileCalls == 3, "USB offline does not block file service checks");
                peer.Start();
                session.Tick(() => {}, poll, files);
                Assert(state.PeerConnected && state.Devices.Count == 1 && state.Status.Contains("副机已连接") && useCalls == 0 && addCalls == 2, "late secondary reconnects with same running client; no downloader is reported separately");
                results.Add("PASS primary-first with real loopback TCP peer offline then online: same controller repairs connection; fingerprint is never claimed");
                board = true; session.Tick(() => {}, poll, files);
                Assert(state.Devices.Count == 1 && state.Devices[0].Owned && useCalls == 1, "later known downloader is automatically acquired");
                session.Tick(() => {}, poll, files); Assert(useCalls == 1, "owned downloader is not interrupted");
                peer.Stop(); owned = false; session.Tick(() => {}, poll, files);
                Assert(!state.PeerConnected && state.Devices.Count == 0, "disconnected cached device must not appear owned");
                imported = false; peer.Start(); session.Tick(() => {}, poll, files);
                Assert(state.PeerConnected && state.Devices[0].Owned && useCalls == 2, "secondary restart reconnects and reacquires known device");
                results.Add("PASS late downloader insertion, no duplicate USE, peer loss/restart, and stale-device filtering");
                configured = false; session.Tick(() => {}, poll, files); Assert(configured && state.Devices[0].Owned, "missing manual endpoint repaired even with client already alive");
                var secondaryFirst = new UsbController(cfg, x => {}, transport, () => {}, listening, ms => {});
                Assert(secondaryFirst.PollPrimary().PeerConnected, "secondary-first is also supported");
                session.Stop(); int stoppedEnsureCalls = ensureCalls;
                Assert(session.Tick(() => {}, poll, files) == null && ensureCalls == stoppedEnsureCalls, "stop cannot resurrect USB polling");
                results.Add("PASS secondary-first, endpoint configuration repaired without client exit, and Stop suppresses reconnect");
            } finally { peer.Stop(); }
            string wrongPort = "<state><server><connection host='127.0.0.1' port='1' state='3'/><device address='1'/></server></state>";
            Assert(!UsbController.ParseSnapshot(wrongPort, "127.0.0.1", port).PeerConnected, "wrong USB port ignored");
        }
        public static int Run(string reportPath) {
            var results = new List<string>();
            string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)), "transfer-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root); string source = Path.Combine(root, "source"), inbox = Path.Combine(root, "inbox"); Directory.CreateDirectory(source);
            try {
                PublicTests(results);
                RecoveryTests(results);
                BoardTests(results, root);
                bool usbStarted = false;
                var isolated = Startup.Independent(() => { usbStarted = true; return new List<UsbDevice>(); }, () => { throw new IOException("simulated receiver startup failure"); });
                Assert(usbStarted && isolated.UsbError == null && isolated.FileError != null, "receiver failure must not prevent USB startup");
                bool filesStarted = false;
                isolated = Startup.Independent(() => { throw new IOException("simulated USB startup failure"); }, () => { filesStarted = true; });
                Assert(filesStarted && isolated.UsbError != null && isolated.FileError == null, "USB failure must not prevent file startup");
                results.Add("PASS USB and file services start independently when either service fails");
                int bindingAttempts = 0, bindingWaits = 0;
                var retried = TransferServer.BindWithRetry(() => {
                    if (++bindingAttempts < 3) throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressNotAvailable);
                    var candidate = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); candidate.Start(); return candidate;
                }, ms => { bindingWaits++; });
                retried.Stop(); Assert(bindingAttempts == 3 && bindingWaits == 2, "transient bind retry");
                int occupiedAttempts = 0;
                try { TransferServer.BindWithRetry(() => { occupiedAttempts++; throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressAlreadyInUse); }, ms => {}); } catch (System.Net.Sockets.SocketException) {}
                Assert(occupiedAttempts == 1, "do not retry or take over an occupied port");
                results.Add("PASS temporary address-not-ready retries; occupied ports fail without takeover");
                string xml = "<state><server><connection host='192.0.2.20' hostname='SECONDARY' port='7575' state='3'/><device product='TUL' vendor='Xilinx' idVendor='1027' idProduct='24592' deviceSerial='TEST-CABLE-001' address='9' state='3' boundClientHostname='" + Environment.MachineName + "'/></server><server><connection host='10.0.0.9'/><device address='1' product='Disk'/></server></state>";
                var devices = UsbController.ParseDevices(xml, "192.0.2.20"); Assert(devices.Count == 1 && devices[0].Address == "SECONDARY.9" && devices[0].Owned, "USB device selection and ownership"); results.Add("PASS USB filters the configured secondary and identifies existing ownership");
                using (var m = new MemoryStream()) { using (var w = new BinaryWriter(m, Encoding.UTF8, true)) w.Write(5L * 1024 * 1024 * 1024 + 123); m.Position = 0; using (var r = new BinaryReader(m)) Assert(r.ReadInt64() == 5L * 1024 * 1024 * 1024 + 123, "64-bit protocol size"); }
                foreach (string bad in new[] { "../outside.bin", "C:\\outside.bin", "a/../b", "CON.txt", ".partial/evil", "a:evil", "a//b" }) {
                    bool rejected = false; try { TransferServer.SafePath(inbox, bad); } catch (IOException) { rejected = true; } Assert(rejected, "path rejection " + bad);
                }
                results.Add("PASS 64-bit lengths and unsafe path rejection");
                const string key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                using (var server = new TransferServer("127.0.0.1", "127.0.0.1", 0, inbox, key, x => {})) {
                    server.Start(); int port = server.ActualPort;
                    var sender = new TransferSender("127.0.0.1", port, key, p => {});
                    string big = Path.Combine(source, "large.bin"); WritePattern(big, 32 * 1024 * 1024 + 17, 3);
                    var sent = sender.Send(Item(big, "多文件/大文件.bin"), CancellationToken.None); Assert(Hash(big) == Hash(sent.Destination), "32 MB complete transfer"); results.Add("PASS 32 MB + 17 bytes transmitted and independently read-back SHA-256 verified");
                    for (int i = 0; i < 15; i++) {
                        string path = Path.Combine(source, "file" + i + ".bin"); WritePattern(path, i * 65537, i);
                        var result = sender.Send(Item(path, "多文件/子目录/文件" + i + ".bin"), CancellationToken.None); Assert(Hash(path) == Hash(result.Destination), "multi-file " + i);
                    }
                    var directory = sender.Send(new TransferItem { Directory = true, Relative = "多文件/空文件夹" }, CancellationToken.None); Assert(Directory.Exists(directory.Destination), "empty directory"); results.Add("PASS 15 mixed-size files, Unicode paths, zero-byte file and empty directory");
                    string resume = Path.Combine(source, "resume.bin"); WritePattern(resume, 9 * 1024 * 1024 + 19, 8);
                    var resumeItem = Item(resume, "resume.bin");
                    var broken = new TransferSender("127.0.0.1", port, key, p => {}) { TestDisconnectAfter = 3 * 1024 * 1024 };
                    try { broken.Send(resumeItem, CancellationToken.None); throw new Exception("Expected disconnection"); } catch (IOException) {}
                    Thread.Sleep(150);
                    var resumed = sender.Send(resumeItem, CancellationToken.None); Assert(resumed.ResumeBytes >= 3 * 1024 * 1024 && Hash(resume) == Hash(resumed.Destination), "resume after loss"); results.Add("PASS forced interruption and verified-prefix resume");
                    string reset = Path.Combine(source, "reset.bin"); WritePattern(reset, 6 * 1024 * 1024 + 1, 7); var resetItem = Item(reset, "reset.bin");
                    broken = new TransferSender("127.0.0.1", port, key, p => {}) { TestDisconnectAfter = 2 * 1024 * 1024 };
                    try { broken.Send(resetItem, CancellationToken.None); } catch (IOException) {}
                    Thread.Sleep(200); string partial = TransferServer.PartPath(inbox, resetItem.Relative, resetItem.Size, Hash(reset));
                    using (var file = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None)) { file.WriteByte(99); file.Flush(true); }
                    var resetResult = sender.Send(resetItem, CancellationToken.None); Assert(resetResult.ResumeBytes == 0 && Hash(reset) == Hash(resetResult.Destination), "corrupt prefix reset"); results.Add("PASS altered partial file causes restart instead of false resume");
                    string corrupt = Path.Combine(source, "corrupt.bin"); WritePattern(corrupt, 2 * 1024 * 1024, 5); var corruptItem = Item(corrupt, "corrupt.bin");
                    var corruptSender = new TransferSender("127.0.0.1", port, key, p => {}) { TestCorruptChunk = true };
                    bool refused = false; try { corruptSender.Send(corruptItem, CancellationToken.None); } catch (IOException) { refused = true; }
                    Assert(refused && !File.Exists(Path.Combine(inbox, "corrupt.bin")), "bad chunk blocked"); sender.Send(corruptItem, CancellationToken.None); results.Add("PASS deliberately corrupted chunk is rejected and clean retry succeeds");
                    var duplicate = sender.Send(resumeItem, CancellationToken.None); Assert(duplicate.Skipped, "same file skipped");
                    File.WriteAllText(Path.Combine(inbox, "collision.bin"), "keep original"); var collisionItem = Item(resume, "collision.bin"); var collision = sender.Send(collisionItem, CancellationToken.None);
                    Assert(collision.Destination != Path.Combine(inbox, "collision.bin") && File.ReadAllText(Path.Combine(inbox, "collision.bin")) == "keep original", "collision preserved");
                    var collisionAgain = sender.Send(collisionItem, CancellationToken.None); Assert(collisionAgain.Skipped && collisionAgain.Destination == collision.Destination, "collision receipt deduplicates"); results.Add("PASS identical files skipped, conflicting name preserved, repeated send uses receipt");
                    bool badKey = false; try { new TransferSender("127.0.0.1", port, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", p => {}).Send(resumeItem, CancellationToken.None); } catch { badKey = true; } Assert(badKey, "pair authentication"); results.Add("PASS wrong pairing key rejected");
                    // Test a real >4 GiB logical file with a verified sparse prefix. Only the tail crosses the wire.
                    long logicalLength = 4L * 1024 * 1024 * 1024 + 2 * 1024 * 1024 + 123;
                    long resumeOffset = 4L * 1024 * 1024 * 1024 + 1024 * 1024;
                    string huge = Path.Combine(source, "over4GB.bin"); Sparse(huge, logicalLength);
                    using (var f = new FileStream(huge, FileMode.Open, FileAccess.Write)) { f.Position = logicalLength - 37; f.Write(Encoding.UTF8.GetBytes("verified tail beyond four gigabytes"), 0, 34); f.Flush(true); }
                    string hugeHash = Hash(huge); string hugePart = TransferServer.PartPath(inbox, "over4GB.bin", logicalLength, hugeHash); Sparse(hugePart, resumeOffset);
                    var hugeResult = sender.Send(Item(huge, "over4GB.bin"), CancellationToken.None);
                    Assert(hugeResult.ResumeBytes == resumeOffset && new FileInfo(hugeResult.Destination).Length == logicalLength && Hash(hugeResult.Destination) == hugeHash, "real >4GB resume");
                    results.Add("PASS >4 GiB sparse logical file: resume offset " + resumeOffset + ", final size " + logicalLength + ", complete-file SHA-256 independently verified");
                }
                results.Add("ALL TESTS PASSED"); File.WriteAllLines(reportPath, results, new UTF8Encoding(false)); return 0;
            } catch (Exception e) { results.Add("FAILED: " + e); File.WriteAllLines(reportPath, results, new UTF8Encoding(false)); return 1; }
        }
    }
}
