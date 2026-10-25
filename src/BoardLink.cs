using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UsbRemoteAssistant {
    [DataContract]
    public sealed class BoardOptions {
        [DataMember] public bool Enabled;
        [DataMember] public string Ip = "";
        [DataMember] public int SshPort = 22, ListenPort = 2222;
        [DataMember] public string User = "";
        public static BoardOptions From(Settings cfg) { lock (cfg) return new BoardOptions { Enabled = cfg.BoardEnabled, Ip = cfg.BoardIp ?? "", SshPort = cfg.BoardSshPort, ListenPort = cfg.BoardListenPort, User = cfg.BoardUser ?? "" }; }
        public void Validate(Settings cfg) {
            IPAddress ip;
            if (SshPort < 1 || SshPort > 65535 || ListenPort < 1024 || ListenPort > 65535 || ListenPort == cfg.UsbPort || ListenPort == cfg.TransferPort) throw new IOException("SSH 或转发端口无效，转发端口不能与 USB、文件端口相同。");
            if (User != "" && !System.Text.RegularExpressions.Regex.IsMatch(User ?? "", "^[A-Za-z_][A-Za-z0-9_.-]{0,63}$")) throw new IOException("SSH 用户名仅支持字母、数字、下划线、点和连字符，且不能以数字或连字符开头。");
            if (!Enabled && String.IsNullOrWhiteSpace(Ip)) return;
            if (!IPAddress.TryParse(Ip, out ip) || ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || ip.GetAddressBytes()[0] == 0 || ip.GetAddressBytes()[0] >= 224 || ip.ToString() == cfg.PrimaryIp || ip.ToString() == cfg.SecondaryIp) throw new IOException("请填写板卡的有效 IPv4 地址，不能填写主机、副机或回环地址。");
            Ip = ip.ToString();
        }
        public void Apply(Settings cfg) { Validate(cfg); lock (cfg) { cfg.BoardEnabled = Enabled; cfg.BoardIp = Ip; cfg.BoardSshPort = SshPort; cfg.BoardListenPort = ListenPort; cfg.BoardUser = User; } }
        public string RelayIdentity { get { return Enabled + ":" + Ip + ":" + SshPort + ":" + ListenPort; } }
        public string Command(string secondaryIp) {
            if (String.IsNullOrWhiteSpace(User)) return "请填写板卡 SSH 用户名后生成命令。";
            return "ssh -p " + ListenPort + " -l " + User + " " + secondaryIp;
        }
    }
    public static class JsonData {
        public static string Write<T>(T value) { using (var m = new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(m, value); return Encoding.UTF8.GetString(m.ToArray()); } }
        public static T Read<T>(string text) { using (var m = new MemoryStream(Encoding.UTF8.GetBytes(text))) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(m); }
    }
    [DataContract]
    public sealed class AssistantStatus {
        [DataMember] public int Version = 1;
        [DataMember] public string Role;
        [DataMember] public bool UsbConnected, UsbOwned;
        [DataMember] public BoardOptions Board;
        [DataMember] public bool RelayRunning, BoardReachable;
        [DataMember] public int SshSessions;
        [DataMember] public string RelayError = "";
    }
    public sealed class PeerTracker {
        readonly object gate = new object();
        AssistantStatus report;
        DateTime seen;
        public void Record(AssistantStatus value, string expectedRole, DateTime now) {
            if (value == null || value.Version != 1 || value.Role != expectedRole) throw new IOException("对端状态协议或角色不匹配，请更新两端软件。");
            lock (gate) { report = value; seen = now; }
        }
        public AssistantStatus Fresh(DateTime now) { lock (gate) return report != null && now >= seen && now - seen <= TimeSpan.FromSeconds(25) ? report : null; }
        public void Clear() { lock (gate) report = null; }
        public static string SecondaryUsbText(bool serverRunning, bool tcpConnected, AssistantStatus primary) {
            if (!serverRunning) return "USB 共享尚未开启 · 自动重试中";
            if (!tcpConnected) return "USB 共享已开启 · 等待主机接入";
            if (primary == null) return "主机已连接 · USB 共享就绪（设备状态待同步）";
            if (primary.UsbConnected && primary.UsbOwned) return "下载器已接入主机 · 可以远程下载和调试";
            return primary.UsbConnected ? "主机已连接 · 等待选择 USB 设备" : "主机已连入 · 正在建立 USB 会话";
        }
    }
    public static class AssistantClient {
        public static string Request(string ip, int port, string key, string command, string payload) {
            using (var client = new TcpClient()) {
                if (!client.ConnectAsync(ip, port).Wait(2000)) throw new IOException("对端助手连接超时。");
                client.ReceiveTimeout = 4000; client.SendTimeout = 4000; client.NoDelay = true;
                using (var stream = client.GetStream()) using (var r = new BinaryReader(stream, Encoding.UTF8, true)) using (var w = new BinaryWriter(stream, Encoding.UTF8, true)) {
                    Wire.ClientAuth(r, w, key); Wire.Text(w, command); Wire.Text(w, payload); w.Flush();
                    string result = Wire.Text(r), value = Wire.Text(r);
                    if (result != "done") throw new IOException(value); return value;
                }
            }
        }
    }
    // Raw SSH remains end-to-end between the main computer and the board. No passwords are stored here.
    public sealed class BoardRelay : IDisposable {
        readonly string bindIp, allowedIp, targetIp;
        readonly int port, targetPort;
        readonly object gate = new object();
        readonly HashSet<TcpClient> sockets = new HashSet<TcpClient>();
        readonly Action<string> log;
        TcpListener listener;
        volatile bool stopped, accepting;
        int active;
        public string Identity { get; private set; }
        public bool Running { get { return listener != null && accepting && !stopped; } }
        public int Active { get { return Interlocked.CompareExchange(ref active, 0, 0); } }
        public int ActualPort { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public BoardRelay(string local, string allowed, int listenPort, string target, int sshPort, string identity, Action<string> logger) { bindIp = local; allowedIp = allowed; port = listenPort; targetIp = target; targetPort = sshPort; Identity = identity; log = logger; }
        public void Start() {
            var candidate = new TcpListener(IPAddress.Parse(bindIp), port); candidate.ExclusiveAddressUse = true;
            try { candidate.Start(16); listener = candidate; } catch { candidate.Stop(); throw; }
            accepting = true; Task.Run((Action)Accept);
        }
        bool Track(TcpClient socket) { lock (gate) { if (stopped) { socket.Close(); return false; } sockets.Add(socket); return true; } }
        void Accept() {
            try {
            while (!stopped) {
                try {
                    var client = listener.AcceptTcpClient();
                    if (((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString() != allowedIp) { client.Close(); continue; }
                    lock (gate) { if (sockets.Count >= 32) { client.Close(); continue; } }
                    if (Track(client)) Task.Run(() => Bridge(client));
                } catch (Exception e) { if (!stopped) { log("SSH 监听失败：" + e.Message); break; } }
            }
            } finally { accepting = false; }
        }
        async Task Bridge(TcpClient incoming) {
            var outgoing = new TcpClient(); bool counted = false;
            try {
                if (!Track(outgoing)) return;
                if (!outgoing.ConnectAsync(targetIp, targetPort).Wait(5000)) throw new IOException("板卡 SSH 连接超时，请检查网线、IP 和板卡 SSH 服务。");
                incoming.NoDelay = true; outgoing.NoDelay = true;
                Interlocked.Increment(ref active); counted = true;
                await Task.WhenAll(Pump(incoming, outgoing), Pump(outgoing, incoming));
            } catch (Exception e) { if (!stopped) log("SSH 会话结束：" + e.GetBaseException().Message); }
            finally {
                incoming.Close(); outgoing.Close();
                lock (gate) { sockets.Remove(incoming); sockets.Remove(outgoing); }
                if (counted) Interlocked.Decrement(ref active);
            }
        }
        static async Task Pump(TcpClient source, TcpClient destination) {
            try {
                var input = source.GetStream(); var output = destination.GetStream(); byte[] buffer = new byte[65536]; int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length)) > 0) await output.WriteAsync(buffer, 0, count);
                try { destination.Client.Shutdown(SocketShutdown.Send); } catch {} // Preserve the remaining receive direction after EOF.
            } catch { source.Close(); destination.Close(); throw; }
        }
        public void Dispose() {
            lock (gate) { stopped = true; if (listener != null) listener.Stop(); foreach (var socket in sockets) socket.Close(); sockets.Clear(); }
        }
    }
}
