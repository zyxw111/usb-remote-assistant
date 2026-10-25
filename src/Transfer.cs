using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UsbRemoteAssistant {
    public static class Wire {
        public const int ChunkSize = 1024 * 1024;
        public static void Text(BinaryWriter w, string value) { byte[] b = Encoding.UTF8.GetBytes(value); if (b.Length > 65536) throw new IOException("文本字段太长。"); w.Write(b.Length); w.Write(b); }
        public static string Text(BinaryReader r) { int n = r.ReadInt32(); if (n < 0 || n > 65536) throw new IOException("无效字段长度。"); return Encoding.UTF8.GetString(Bytes(r, n)); }
        public static byte[] Bytes(BinaryReader r, int n) { byte[] b = r.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException("连接中断，未完成的数据保留供续传。"); return b; }
        public static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }
        public static bool Equal(byte[] a, byte[] b) { if (a.Length != b.Length) return false; int x = 0; for (int i = 0; i < a.Length; i++) x |= a[i] ^ b[i]; return x == 0; }
        public static byte[] Digest(byte[] b) { using (var h = SHA256.Create()) return h.ComputeHash(b); }
        public static string Hash(Stream file, long count, CancellationToken token, Action<long> progress) {
            file.Position = 0;
            byte[] buffer = new byte[Wire.ChunkSize];
            long remaining = count, done = 0;
            using (var hash = SHA256.Create()) {
                while (remaining > 0) {
                    token.ThrowIfCancellationRequested();
                    int n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (n == 0) throw new EndOfStreamException("源文件长度在读取时发生变化。");
                    hash.TransformBlock(buffer, 0, n, buffer, 0); remaining -= n; done += n;
                    if (progress != null) progress(done);
                }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(hash.Hash);
            }
        }
        public static byte[] Proof(string key, string purpose, byte[] first, byte[] second) {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(key))) return h.ComputeHash(Encoding.UTF8.GetBytes(purpose).Concat(first).Concat(second).ToArray());
        }
        public static byte[] Nonce() { byte[] b = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b); return b; }
        public static void ServerAuth(BinaryReader r, BinaryWriter w, string key) {
            if (Text(r) != "USBREMOTE-FILES-1") throw new IOException("文件传输协议不匹配。");
            byte[] server = Nonce(); w.Write(server); w.Flush();
            byte[] client = Bytes(r, 32), proof = Bytes(r, 32);
            if (!Equal(proof, Proof(key, "client", server, client))) throw new IOException("文件传输配对失败，请使用同一套主副机软件包。");
            w.Write(Proof(key, "server", server, client)); w.Flush();
        }
        public static void ClientAuth(BinaryReader r, BinaryWriter w, string key) {
            Text(w, "USBREMOTE-FILES-1"); w.Flush();
            byte[] server = Bytes(r, 32), client = Nonce();
            w.Write(client); w.Write(Proof(key, "client", server, client)); w.Flush();
            if (!Equal(Bytes(r, 32), Proof(key, "server", server, client))) throw new IOException("对端配对验证失败。");
        }
    }

    public class TransferItem {
        public string Source, Relative, State = "等待发送";
        public long Size;
        public bool Directory;
    }
    public class TransferProgress {
        public TransferItem Item;
        public long Position;
        public string Phase;
        public TransferProgress(TransferItem item, long position, string phase) { Item = item; Position = position; Phase = phase; }
    }
    public class TransferResult {
        public string Destination, Hash;
        public long ResumeBytes;
        public bool Skipped;
    }

    public sealed class TransferServer : IDisposable {
        readonly string bindIp, peerIp, root, key;
        readonly int port;
        readonly Action<string> log;
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly List<TcpClient> connections = new List<TcpClient>();
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        TcpListener listener;
        public Func<string, string, string> ControlHandler;
        public Action<string, string> ControlCompleted;
        public bool Running { get { return listener != null && !stop.IsCancellationRequested; } }
        public TransferServer(string local, string remote, int listenPort, string receiveRoot, string pairKey, Action<string> logger) { bindIp = local; peerIp = remote; port = listenPort; root = Path.GetFullPath(receiveRoot); key = pairKey; log = logger; }
        public int ActualPort { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public void Start() {
            Directory.CreateDirectory(root); CheckReparse(root);
            try {
                listener = BindWithRetry(() => {
                    var candidate = new TcpListener(IPAddress.Parse(bindIp), port);
                    try { candidate.Start(4); return candidate; } catch { candidate.Stop(); throw; }
                }, milliseconds => { log("文件服务正在等待本机地址就绪 · " + bindIp); Thread.Sleep(milliseconds); });
            } catch (SocketException e) {
                throw new IOException("文件接收无法监听 " + bindIp + ":" + port + "（" + e.SocketErrorCode + "）。USB 服务会继续独立启动。", e);
            }
            Task.Run((Action)AcceptLoop);
            log("文件接收已开启 · " + bindIp + ":" + ActualPort);
        }
        internal static TcpListener BindWithRetry(Func<TcpListener> bind, Action<int> wait) {
            for (int attempt = 0; ; attempt++) {
                try { return bind(); }
                catch (SocketException e) {
                    if (e.SocketErrorCode != SocketError.AddressNotAvailable || attempt >= 19) throw;
                    wait(500);
                }
            }
        }
        void AcceptLoop() {
            while (!stop.IsCancellationRequested) {
                try {
                    TcpClient client = listener.AcceptTcpClient();
                    if (((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString() != peerIp) { client.Close(); continue; }
                    lock (connections) connections.Add(client);
                    Task.Run(() => Handle(client));
                } catch (Exception e) { if (!stop.IsCancellationRequested) log("接收监听错误：" + e.Message); }
            }
        }
        void Handle(TcpClient client) {
            bool acquired = false;
            try {
                client.NoDelay = true; client.ReceiveTimeout = 8000; client.SendTimeout = 8000;
                using (client) using (var stream = client.GetStream()) using (var r = new BinaryReader(stream, Encoding.UTF8, true)) using (var w = new BinaryWriter(stream, Encoding.UTF8, true)) {
                    Wire.ServerAuth(r, w, key);
                    try {
                        string command = Wire.Text(r), relative = Wire.Text(r);
                        if (command == "assistant-status-v1" || command == "assistant-board-v1" || command == "assistant-network-v2") {
                            if (ControlHandler == null) throw new IOException("对端不支持此功能，请更新到 2.0。");
                            string response = ControlHandler(command, relative);
                            Wire.Text(w, "done"); Wire.Text(w, response); w.Flush();
                            if (ControlCompleted != null) {
                                try { ControlCompleted(command, relative); }
                                catch { log("控制响应已发送，但后续操作未完成，请重试或重新导入配对文件。"); }
                            }
                            return;
                        }
                        gate.Wait(stop.Token); acquired = true;
                        client.ReceiveTimeout = 120000; client.SendTimeout = 120000;
                        string destination = SafePath(root, relative);
                        if (command == "mkdir") { SafeCreateDirectory(destination); Wire.Text(w, "done"); Wire.Text(w, destination); w.Flush(); return; }
                        if (command != "file") throw new IOException("未知传输命令。");
                        long length = r.ReadInt64(), ticks = r.ReadInt64();
                        string expected = Wire.Text(r);
                        if (length < 0 || !System.Text.RegularExpressions.Regex.IsMatch(expected, "^[0-9a-f]{64}$")) throw new IOException("文件长度或校验值无效。");
                        log("开始接收 · " + relative + " · " + MainForm.Size(length));
                        SafeCreateDirectory(Path.GetDirectoryName(destination));
                        string partial = PartPath(root, relative, length, expected);
                        string receipt = partial + ".receipt";
                        if (File.Exists(receipt)) {
                            CheckReparse(receipt);
                            string previous = File.ReadAllText(receipt, Encoding.UTF8);
                            string allowedRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                            if (previous.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(previous) && new FileInfo(previous).Length == length) {
                                CheckReparse(previous); Wire.Text(w, "checking"); w.Flush();
                                using (var existing = File.OpenRead(previous)) {
                                    if (Wire.Hash(existing, length, stop.Token, Heartbeat(w)) == expected) {
                                        Wire.Text(w, "done"); Wire.Text(w, previous); Wire.Text(w, expected); w.Write(true); w.Flush(); return;
                                    }
                                }
                            }
                        }
                        if (File.Exists(destination)) {
                            CheckReparse(destination);
                            Wire.Text(w, "checking"); w.Flush();
                            if (new FileInfo(destination).Length == length) {
                                using (var existing = File.OpenRead(destination)) {
                                    if (Wire.Hash(existing, length, stop.Token, Heartbeat(w)) == expected) {
                                        Wire.Text(w, "done"); Wire.Text(w, destination); Wire.Text(w, expected); w.Write(true); w.Flush();
                                        log("已存在且校验一致 · " + relative); return;
                                    }
                                }
                            }
                        }
                        SafeCreateDirectory(Path.GetDirectoryName(partial));
                        if (File.Exists(partial)) CheckReparse(partial);
                        long offset;
                        using (var file = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, Wire.ChunkSize)) {
                            if (file.Length > length) file.SetLength(0);
                            offset = file.Length;
                            Wire.Text(w, "checking"); w.Flush();
                            string prefixHash = Wire.Hash(file, offset, stop.Token, Heartbeat(w));
                            Wire.Text(w, "ready"); w.Write(offset); Wire.Text(w, prefixHash); w.Flush();
                            if (!r.ReadBoolean()) { file.SetLength(0); offset = 0; }
                            file.Position = offset; w.Write(offset); w.Flush();
                            long position = offset;
                            int chunks = 0;
                            while (true) {
                                stop.Token.ThrowIfCancellationRequested();
                                int n = r.ReadInt32();
                                if (n == 0) break;
                                if (n < 0 || n > Wire.ChunkSize || n > length - position) throw new IOException("文件分块长度异常。");
                                byte[] hash = Wire.Bytes(r, 32), bytes = Wire.Bytes(r, n);
                                if (!Wire.Equal(hash, Wire.Digest(bytes))) throw new IOException("分块校验失败；该块未写入，重新发送即可续传。");
                                file.Write(bytes, 0, n); position += n;
                                file.Flush(++chunks % 8 == 0);
                                w.Write((byte)1); w.Flush();
                            }
                            if (position != length) throw new IOException("接收到的文件长度不完整。");
                            file.Flush(true);
                        }
                        Wire.Text(w, "verifying"); w.Flush();
                        string actual;
                        using (var file = File.OpenRead(partial)) actual = Wire.Hash(file, length, stop.Token, Heartbeat(w));
                        if (actual != expected) throw new IOException("接收文件 SHA-256 校验失败，未生成最终文件。");
                        stop.Token.ThrowIfCancellationRequested();
                        destination = UniqueDestination(destination);
                        // Staging and final destination are on the same volume. File.Move never replaces an existing file.
                        byte[] receiptBytes = Encoding.UTF8.GetBytes(destination);
                        if (File.Exists(receipt)) CheckReparse(receipt);
                        using (var record = new FileStream(receipt, FileMode.Create, FileAccess.Write, FileShare.None)) { record.Write(receiptBytes, 0, receiptBytes.Length); record.Flush(true); }
                        File.Move(partial, destination);
                        if (ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks) { try { File.SetLastWriteTimeUtc(destination, new DateTime(ticks, DateTimeKind.Utc)); } catch {} }
                        Wire.Text(w, "done"); Wire.Text(w, destination); Wire.Text(w, actual); w.Write(false); w.Flush();
                        log("接收完成，SHA-256 一致 · " + relative);
                    } catch (Exception e) {
                        try { Wire.Text(w, "error"); Wire.Text(w, e.Message); w.Flush(); } catch {}
                        throw;
                    }
                }
            } catch (Exception e) { if (!stop.IsCancellationRequested) log("接收未完成：" + e.Message); }
            finally { client.Close(); lock (connections) connections.Remove(client); if (acquired) gate.Release(); }
        }
        Action<long> Heartbeat(BinaryWriter w) {
            DateTime previous = DateTime.UtcNow;
            return bytes => { if ((DateTime.UtcNow - previous).TotalSeconds >= 1) { Wire.Text(w, "progress"); w.Write(bytes); w.Flush(); previous = DateTime.UtcNow; } };
        }
        public static string SafePath(string root, string relative) {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Length > 16000) throw new IOException("无效的相对文件路径。");
            string[] parts = relative.Replace('/', '\\').Split('\\');
            foreach (string part in parts) {
                if (part == "" || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Equals(".partial", StringComparison.OrdinalIgnoreCase)) throw new IOException("文件路径包含不允许的名称。");
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (System.Text.RegularExpressions.Regex.IsMatch(stem, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$")) throw new IOException("文件名为 Windows 保留名称。");
            }
            string fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(Path.Combine(fullRoot, String.Join("\\", parts)));
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("文件路径越出接收目录。");
            CheckReparse(fullRoot.TrimEnd('\\'));
            string cursor = fullRoot.TrimEnd('\\');
            foreach (string part in parts) { cursor = Path.Combine(cursor, part); if (File.Exists(cursor) || Directory.Exists(cursor)) CheckReparse(cursor); }
            return full;
        }
        public static void CheckReparse(string path) {
            string full = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(full)) {
                if ((File.Exists(full) || Directory.Exists(full)) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("接收路径包含链接目录，请换用普通目录。");
                string parent = Path.GetDirectoryName(full);
                if (parent == full) break;
                full = parent;
            }
        }
        static void SafeCreateDirectory(string path) { CheckReparse(path); Directory.CreateDirectory(path); CheckReparse(path); }
        public static string PartPath(string root, string relative, long length, string hash) {
            string id = Wire.Hex(Wire.Digest(Encoding.UTF8.GetBytes(relative.Replace('/', '\\').ToUpperInvariant() + "\n" + length + "\n" + hash)));
            return Path.Combine(root, ".partial", id + ".part");
        }
        static string UniqueDestination(string path) {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string directory = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path);
            for (int i = 1; i < 100000; i++) {
                string candidate = Path.Combine(directory, name + " (" + i + ")" + extension);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
            throw new IOException("同名文件数量过多。");
        }
        public void Dispose() {
            stop.Cancel(); if (listener != null) listener.Stop();
            lock (connections) foreach (var c in connections.ToArray()) c.Close();
        }
    }

    public sealed class TransferSender {
        readonly string remote, key;
        readonly int port;
        readonly Action<TransferProgress> progress;
        TcpClient current;
        public long TestDisconnectAfter;
        public bool TestCorruptChunk;
        public TransferSender(string remoteIp, int remotePort, string pairKey, Action<TransferProgress> update) { remote = remoteIp; port = remotePort; key = pairKey; progress = update; }
        public void Cancel() { var c = current; if (c != null) try { c.Close(); } catch {} }
        void Report(TransferItem item, long bytes, string phase) { if (progress != null) progress(new TransferProgress(item, bytes, phase)); }
        public TransferResult Send(TransferItem item, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            using (var client = new TcpClient()) {
                current = client;
                using (token.Register(Cancel)) {
                    var connect = client.ConnectAsync(remote, port);
                    if (!connect.Wait(8000)) throw new IOException("对端文件接收服务未连接，请先开启对端软件。");
                    client.NoDelay = true; client.ReceiveTimeout = 120000; client.SendTimeout = 120000;
                    using (var stream = client.GetStream()) using (var r = new BinaryReader(stream, Encoding.UTF8, true)) using (var w = new BinaryWriter(stream, Encoding.UTF8, true)) {
                        Wire.ClientAuth(r, w, key);
                        if (item.Directory) {
                            Wire.Text(w, "mkdir"); Wire.Text(w, item.Relative); w.Flush();
                            string status = Wire.Text(r); if (status == "error") throw new IOException(Wire.Text(r)); if (status != "done") throw new IOException("创建文件夹失败。");
                            return new TransferResult { Destination = Wire.Text(r) };
                        }
                        using (var file = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.ChunkSize)) {
                            item.Size = file.Length;
                            Report(item, 0, "计算源文件 SHA-256");
                            string expected = Wire.Hash(file, file.Length, token, bytes => Report(item, bytes, "计算源文件 SHA-256"));
                            Wire.Text(w, "file"); Wire.Text(w, item.Relative); w.Write(file.Length); w.Write(File.GetLastWriteTimeUtc(item.Source).Ticks); Wire.Text(w, expected); w.Flush();
                            string state = ReadState(r, item, token);
                            if (state == "done") return ReadDone(r, expected, 0);
                            if (state != "ready") throw new IOException("接收端返回了异常状态。");
                            long proposed = r.ReadInt64(); string prefix = Wire.Text(r);
                            if (proposed < 0 || proposed > file.Length) throw new IOException("续传位置无效。");
                            Report(item, proposed, "核对断点");
                            bool valid = proposed == 0 || Wire.Hash(file, proposed, token, null) == prefix;
                            w.Write(valid); w.Flush();
                            long offset = r.ReadInt64();
                            if (offset != (valid ? proposed : 0)) throw new IOException("续传位置不一致。");
                            file.Position = offset;
                            long position = offset;
                            byte[] buffer = new byte[Wire.ChunkSize];
                            Report(item, position, offset > 0 ? "断点续传" : "传输中");
                            while (position < file.Length) {
                                token.ThrowIfCancellationRequested();
                                int n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, file.Length - position));
                                if (n == 0) throw new IOException("源文件不完整。");
                                byte[] bytes = n == buffer.Length ? buffer : buffer.Take(n).ToArray();
                                byte[] digest = Wire.Digest(bytes);
                                if (TestCorruptChunk) { digest[0] ^= 1; TestCorruptChunk = false; }
                                w.Write(n); w.Write(digest); w.Write(bytes, 0, n); w.Flush();
                                if (r.ReadByte() != 1) throw new IOException("接收端分块校验未通过。");
                                position += n; Report(item, position, "传输中");
                                if (TestDisconnectAfter > 0 && position >= TestDisconnectAfter) throw new IOException("TEST: simulated connection loss");
                            }
                            w.Write(0); w.Flush();
                            Report(item, file.Length, "接收端落盘校验");
                            state = ReadState(r, item, token);
                            if (state != "done") throw new IOException("接收端未确认完成。");
                            return ReadDone(r, expected, offset);
                        }
                    }
                }
            }
        }
        string ReadState(BinaryReader r, TransferItem item, CancellationToken token) {
            while (true) {
                token.ThrowIfCancellationRequested();
                string state = Wire.Text(r);
                if (state == "error") throw new IOException(Wire.Text(r));
                if (state == "progress") { Report(item, r.ReadInt64(), "对端校验中"); continue; }
                if (state == "checking" || state == "verifying") { Report(item, 0, state == "checking" ? "对端核对断点" : "接收端落盘校验"); continue; }
                return state;
            }
        }
        static TransferResult ReadDone(BinaryReader r, string expected, long resume) {
            string destination = Wire.Text(r), actual = Wire.Text(r); bool skipped = r.ReadBoolean();
            if (actual != expected) throw new IOException("最终 SHA-256 不一致，传输不能标记为完成。");
            return new TransferResult { Destination = destination, Hash = actual, ResumeBytes = resume, Skipped = skipped };
        }
        public static List<TransferItem> FolderPlan(string folder) {
            var items = new List<TransferItem>();
            string root = Path.GetFullPath(folder).TrimEnd('\\');
            string parent = Path.GetDirectoryName(root);
            if (String.IsNullOrEmpty(parent)) throw new IOException("请选择一个文件夹，而不是整个磁盘根目录。");
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0) {
                string directory = pending.Pop();
                TransferServer.CheckReparse(directory);
                items.Add(new TransferItem { Source = directory, Relative = directory.Substring(parent.Length + 1), Directory = true });
                foreach (string file in Directory.EnumerateFiles(directory)) {
                    TransferServer.CheckReparse(file);
                    items.Add(new TransferItem { Source = file, Relative = file.Substring(parent.Length + 1), Size = new FileInfo(file).Length });
                }
                foreach (string child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
            return items;
        }
    }
}
