using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TimeoutException = System.TimeoutException;

namespace UsbRemoteAssistant {
    [DataContract]
    public class Settings {
        [DataMember] public bool ManageEasyTier = true;
        [DataMember] public string NetworkName = "";
        [DataMember] public string NetworkSecret = "";
        [DataMember] public string EasyTierPeers = EasyTierOptions.DefaultPeer;
        [DataMember] public string EasyTierPath = "";
        [DataMember] public string PrimaryIp = "";
        [DataMember] public string SecondaryIp = "";
        [DataMember] public int UsbPort = 7575;
        [DataMember] public int TransferPort = 17875;
        [DataMember] public string PairKey;
        [DataMember] public string ReceiveFolder = "";
        [DataMember] public string DeviceSerial = "";
        [DataMember] public int DeviceVendor;
        [DataMember] public int DeviceProduct;
        [DataMember] public string VirtualHerePath = "";
        [DataMember] public bool AutoStart = true;
        [DataMember] public bool BoardEnabled;
        [DataMember] public string BoardIp = "";
        [DataMember] public int BoardSshPort = 22;
        [DataMember] public int BoardListenPort = 2222;
        [DataMember] public string BoardUser = "";
        [OnDeserializing] void BeforeLoad(StreamingContext context) { ManageEasyTier = true; NetworkName = ""; NetworkSecret = ""; EasyTierPeers = EasyTierOptions.DefaultPeer; EasyTierPath = ""; PrimaryIp = ""; SecondaryIp = ""; DeviceSerial = ""; VirtualHerePath = ""; ReceiveFolder = ""; UsbPort = 7575; TransferPort = 17875; AutoStart = true; BoardIp = ""; BoardUser = ""; BoardSshPort = 22; BoardListenPort = 2222; }
        public string RemoteIp { get { return Program.IsPrimary ? SecondaryIp : PrimaryIp; } }
        public string OwnIp { get { return Program.IsPrimary ? PrimaryIp : SecondaryIp; } }
        public string Inbox { get { return String.IsNullOrWhiteSpace(ReceiveFolder) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "USB远程传输", "收到的文件") : ReceiveFolder; } }
        public void Validate() {
            IPAddress a, b;
            if (!IPAddress.TryParse(PrimaryIp, out a) || !IPAddress.TryParse(SecondaryIp, out b) || a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork) throw new Exception("请输入有效的 IPv4 地址。");
            if (a.Equals(b) || a.Equals(IPAddress.Any) || b.Equals(IPAddress.Any)) throw new Exception("主机和副机应使用不同的具体 IPv4 地址，不能使用 0.0.0.0。");
            if (TransferPort < 1024 || TransferPort > 65535 || UsbPort < 1 || UsbPort > 65535 || TransferPort == UsbPort) throw new Exception("传输端口无效或与 USB 端口冲突。");
            if (!System.Text.RegularExpressions.Regex.IsMatch(PairKey ?? "", "^[0-9a-fA-F]{64}$")) throw new Exception("请在连接与配对中生成或导入配对密钥。");
            EasyTierOptions.Validate(this);
            BoardOptions.From(this).Validate(this);
        }
        public bool IsConfigured { get { try { Validate(); return true; } catch { return false; } } }
        public static string ProfileDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbRemoteAssistant.Public", Program.Role); } }
        public static string ProfilePath { get { return Path.Combine(ProfileDirectory, "settings.json"); } }
        public static string NewPairKey() { byte[] bytes = new byte[32]; using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes); return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        public static Settings Load() {
            string path = ProfilePath;
            if (!File.Exists(path)) return new Settings();
            using (var s = File.OpenRead(path)) return (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(s);
        }
        public void Save() {
            lock (this) { Validate();
            Directory.CreateDirectory(ProfileDirectory);
            string path = ProfilePath;
            string temp = path + ".new";
            using (var s = File.Create(temp)) new DataContractJsonSerializer(typeof(Settings)).WriteObject(s, this);
            if (File.Exists(path)) File.Replace(temp, path, path + ".backup"); else File.Move(temp, path);
            }
        }
    }

    public static class Sys {
        public static bool IsAdmin { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }
        public static string Quote(string s) { return "'" + s.Replace("'", "''") + "'"; }
        public static string Run(string exe, string args, int timeout) {
            using (var p = new Process()) {
                p.StartInfo = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                p.Start();
                Task<string> stdout = p.StandardOutput.ReadToEndAsync(), stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeout)) { try { p.Kill(); } catch {} throw new Exception("操作超时：" + Path.GetFileName(exe)); }
                Task.WaitAll(stdout, stderr);
                if (p.ExitCode != 0) throw new Exception((stdout.Result + " " + stderr.Result).Trim());
                return stdout.Result;
            }
        }
        public static string PS(string command) {
            string full = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; " + command;
            return Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(full)), 30000);
        }
        public static string LocalNetworkIp(Settings cfg) {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var entry in adapter.GetIPProperties().UnicastAddresses)
                    if (entry.Address.AddressFamily == AddressFamily.InterNetwork && entry.Address.ToString() == cfg.OwnIp) return cfg.OwnIp;
            throw new IOException("本机虚拟 IP 尚未就绪，请检查 EasyTier 和本端地址设置。");
        }
        public static void Firewall(string name, string local, string remote, int port) {
            string n = Quote(name);
            string script = "$r=Get-NetFirewallRule -Name " + n + " -ErrorAction SilentlyContinue; if($r){Set-NetFirewallRule -Name " + n + " -Enabled True -Direction Inbound -Action Allow -Profile Any; $r|Get-NetFirewallAddressFilter|Set-NetFirewallAddressFilter -LocalAddress " + Quote(local) + " -RemoteAddress " + Quote(remote) + "; $r|Get-NetFirewallPortFilter|Set-NetFirewallPortFilter -Protocol TCP -LocalPort " + port + " -RemotePort Any}else{New-NetFirewallRule -Name " + n + " -DisplayName " + n + " -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalPort " + port + " -LocalAddress " + Quote(local) + " -RemoteAddress " + Quote(remote) + "|Out-Null}";
            PS(script);
        }
        public static bool CanConnect(string ip, int port, int timeout) {
            try { using (var client = new TcpClient()) { var t = client.ConnectAsync(ip, port); return t.Wait(timeout) && client.Connected; } } catch { return false; }
        }
        public static bool UsbClientConnected(Settings cfg) {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Any(x => UsbConnectionMatches(x.LocalEndPoint.Port, x.RemoteEndPoint.Address, x.State, cfg));
        }
        internal static bool UsbConnectionMatches(int localPort, IPAddress remote, TcpState state, Settings cfg) {
            string address = remote.AddressFamily == AddressFamily.InterNetworkV6 && remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4().ToString() : remote.ToString();
            return localPort == cfg.UsbPort && address == cfg.PrimaryIp && state == TcpState.Established;
        }
        public static string Tool(Settings cfg, bool primary) {
            if (!String.IsNullOrWhiteSpace(cfg.VirtualHerePath)) {
                if (!File.Exists(cfg.VirtualHerePath)) throw new IOException("指定的 VirtualHere 程序不存在，请在连接与配对中重新选择。");
                return Path.GetFullPath(cfg.VirtualHerePath);
            }
            string file = primary ? "vhui64.exe" : "vhusbdwinw64.exe";
            string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", file);
            if (File.Exists(bundled)) return bundled;
            throw new Exception("未找到 VirtualHere。请从官网获取本端程序，在连接与配对中选择文件；文件传输和 SSH 可独立使用。");
        }
        public static Process StartHidden(string exe) { return Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }); }
    }

    public sealed class StartupResult {
        public List<UsbDevice> Devices = new List<UsbDevice>();
        public Exception NetworkError, UsbError, FileError, BoardError;
    }
    public sealed class RecoverySession {
        public bool Enabled { get; private set; }
        public void Start() { Enabled = true; }
        public void Stop() { Enabled = false; }
        public StartupResult Tick(Action network, Func<List<UsbDevice>> usb, Action files, Action board = null) {
            if (!Enabled) return null;
            try { network(); } catch (Exception e) { return new StartupResult { NetworkError = e }; }
            if (!Enabled) return null;
            return Startup.Independent(usb, files, board);
        }
    }
    public static class Startup {
        public static StartupResult Independent(Func<List<UsbDevice>> usb, Action files, Action board = null) {
            var result = new StartupResult();
            var a = Task.Run(() => { try { result.Devices = usb(); } catch (Exception e) { result.UsbError = e; } });
            var b = Task.Run(() => { try { files(); } catch (Exception e) { result.FileError = e; } });
            var c = Task.Run(() => { try { if (board != null) board(); } catch (Exception e) { result.BoardError = e; } });
            Task.WaitAll(a, b, c); return result;
        }
    }

    public sealed class UsbDevice {
        public string Name, Address, Serial, BoundHost;
        public int Vendor, Product, State;
        public override string ToString() { return Name + " · " + Vendor.ToString("X4") + ":" + Product.ToString("X4") + (String.IsNullOrEmpty(Serial) ? "" : " · " + Serial); }
        public bool Owned { get { return State == 3 && String.Equals(BoundHost, Environment.MachineName, StringComparison.OrdinalIgnoreCase); } }
    }

    public sealed class UsbSnapshot {
        public bool PeerConnected;
        public List<UsbDevice> Devices = new List<UsbDevice>();
        public string Status {
            get {
                if (!PeerConnected) return "等待副机 USB 服务 · 自动重连中";
                if (Devices.Any(x => x.Owned)) return "USB 设备已接入 · 可在主机使用";
                return "副机已连接 · 等待插入下载器或选择 USB 设备";
            }
        }
    }

    public sealed class UsbController {
        readonly Settings cfg;
        readonly Action<string> log;
        readonly Func<string, string> api;
        readonly Action ensureClient;
        readonly Action<int> wait;
        readonly Func<bool> peerListening;
        Process startedClient, startedServer;
        readonly HashSet<string> claimedDevices = new HashSet<string>();
        readonly bool injectedRuntime;
        public UsbController(Settings settings, Action<string> logger) {
            cfg = settings; log = logger; api = Api; ensureClient = EnsureClient; wait = Thread.Sleep;
            peerListening = () => Sys.CanConnect(cfg.SecondaryIp, cfg.UsbPort, 1000);
        }
        internal UsbController(Settings settings, Action<string> logger, Func<string, string> transport, Action client, Func<bool> listening, Action<int> delay) {
            cfg = settings; log = logger; api = transport; ensureClient = client; peerListening = listening; wait = delay; injectedRuntime = true;
        }
        public static string Api(string command) {
            using (var pipe = new NamedPipeClientStream(".", "vhclient", PipeDirection.InOut)) {
                pipe.Connect(3000);
                pipe.ReadMode = PipeTransmissionMode.Message;
                byte[] cmd = Encoding.UTF8.GetBytes(command + "\n");
                pipe.Write(cmd, 0, cmd.Length); pipe.Flush();
                var read = Task.Run(() => {
                    using (var result = new MemoryStream()) {
                        byte[] buffer = new byte[16384];
                        do {
                            int n = pipe.Read(buffer, 0, buffer.Length);
                            if (n == 0) throw new IOException("VirtualHere 接口连接关闭。");
                            result.Write(buffer, 0, n);
                            if (result.Length > 8 * 1024 * 1024) throw new IOException("设备列表过大。");
                        } while (!pipe.IsMessageComplete);
                        return Encoding.UTF8.GetString(result.ToArray());
                    }
                });
                if (!read.Wait(12000)) throw new TimeoutException("VirtualHere 操作等待超时。");
                return read.Result.Trim();
            }
        }
        public static UsbSnapshot ParseSnapshot(string xml, string remoteIp, int port) {
            var snapshot = new UsbSnapshot();
            foreach (var server in XDocument.Parse(xml).Descendants("server")) {
                var connection = server.Elements("connection").FirstOrDefault(x => (string)x.Attribute("host") == remoteIp && (int?)x.Attribute("port") == port && (int?)x.Attribute("state") == 3);
                if (connection == null) continue;
                snapshot.PeerConnected = true;
                string host = (string)connection.Attribute("hostname");
                if (String.IsNullOrEmpty(host)) host = remoteIp;
                foreach (var device in server.Elements("device")) {
                    snapshot.Devices.Add(new UsbDevice {
                        Name = ((string)device.Attribute("vendor") + " " + (string)device.Attribute("product")).Trim(),
                        Address = host + "." + (string)device.Attribute("address"),
                        Vendor = (int?)device.Attribute("idVendor") ?? 0,
                        Product = (int?)device.Attribute("idProduct") ?? 0,
                        State = (int?)device.Attribute("state") ?? 0,
                        Serial = (string)device.Attribute("deviceSerial") ?? "",
                        BoundHost = (string)device.Attribute("boundClientHostname") ?? ""
                    });
                }
            }
            return snapshot;
        }
        public static List<UsbDevice> ParseDevices(string xml, string remoteIp) { return ParseSnapshot(xml, remoteIp, 7575).Devices; }
        public UsbSnapshot Snapshot() { return ParseSnapshot(api("GET CLIENT STATE"), cfg.SecondaryIp, cfg.UsbPort); }
        public static UsbSnapshot WithReachability(UsbSnapshot snapshot, bool reachable) { return reachable ? snapshot : new UsbSnapshot(); }
        public List<UsbDevice> Devices() { return Snapshot().Devices; }
        void EnsureClient() {
            try { Api("LIST"); } catch {
                if (Process.GetProcessesByName("vhui64").Length == 0) startedClient = Sys.StartHidden(Sys.Tool(cfg, true));
                bool ready = false;
                for (int i = 0; i < 10; i++) { Thread.Sleep(1000); try { Api("LIST"); ready = true; break; } catch {} }
                if (!ready) throw new Exception("VirtualHere 客户端没有响应，请检查首次启动提示。");
            }
        }
        public UsbSnapshot PollPrimary() {
            ensureClient();
            string target = cfg.SecondaryIp + ":" + cfg.UsbPort;
            string hubs = api("MANUAL HUB LIST");
            bool configured = hubs.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Any(x => x.Trim() == target);
            bool reachable = peerListening();
            var snapshot = Snapshot();
            // Reassert only our configured endpoint. Never remove hubs or interrupt an owned device.
            if (!configured || (!snapshot.PeerConnected && reachable)) {
                string result = api("MANUAL HUB ADD," + target);
                if (result.StartsWith("ERROR") || result.StartsWith("FAILED")) throw new IOException("副机连接配置失败：" + result);
                snapshot = Snapshot();
            }
            snapshot = WithReachability(snapshot, reachable);
            if (!snapshot.PeerConnected) return snapshot;
            var known = cfg.DeviceVendor == 0 || cfg.DeviceProduct == 0 ? new List<UsbDevice>() : snapshot.Devices.Where(d => d.Vendor == cfg.DeviceVendor && d.Product == cfg.DeviceProduct && d.Serial == cfg.DeviceSerial).ToList();
            if (known.Count == 1) Use(known[0]);
            return Snapshot();
        }
        public List<UsbDevice> StartPrimary() { return PollPrimary().Devices; }
        public void Use(UsbDevice d) {
            if (d.Owned) { log("下载器已接入 · " + d.Name); return; }
            string response = "";
            try { response = api("USE," + d.Address); } catch (TimeoutException) {}
            for (int i = 0; i < 12; i++) {
                var current = Devices().FirstOrDefault(x => x.Address == d.Address);
                if (current != null && current.Owned) { claimedDevices.Add(current.Address); log("下载器接入成功 · " + d.Name); return; }
                wait(750);
            }
            throw new Exception("下载器未接入，请检查占用情况和驱动。" + response);
        }
        public void StartSecondary() {
            if (Process.GetProcessesByName("vhusbdwinw64").Length > 0 && Sys.CanConnect(cfg.SecondaryIp, cfg.UsbPort, 1000)) return;
            Sys.Firewall("USBRemotePublic-VirtualHere", cfg.SecondaryIp, cfg.PrimaryIp, cfg.UsbPort);
            if (Process.GetProcessesByName("vhusbdwinw64").Length == 0) startedServer = Sys.StartHidden(Sys.Tool(cfg, false));
            for (int i = 0; i < 12; i++) { if (Sys.CanConnect(cfg.SecondaryIp, cfg.UsbPort, 1000)) { log("副机 USB 共享服务已开启。"); return; } Thread.Sleep(750); }
            throw new Exception("副机 USB 共享服务尚未监听，请检查驱动安装或首次启动提示。");
        }
        public void Stop() {
            if (Program.IsPrimary) {
                if (!injectedRuntime && Process.GetProcessesByName("vhui64").Length == 0) { log("USB 客户端已经关闭。"); return; }
                try {
                    foreach (var d in Devices().Where(x => x.Owned && claimedDevices.Contains(x.Address))) {
                        string result = api("STOP USING," + d.Address);
                        if (result.StartsWith("FAILED") || result.StartsWith("ERROR")) throw new Exception("释放设备失败：" + result);
                    }
                    claimedDevices.Clear();
                    // An existing client can also serve other hubs. Do not shut it down globally.
                    if (startedClient != null && !startedClient.HasExited && !XDocument.Parse(Api("GET CLIENT STATE")).Descendants("device").Any(x => (int?)x.Attribute("state") == 3)) {
                        try { Api("EXIT"); } catch (Exception) {}
                        if (!startedClient.WaitForExit(5000)) throw new IOException("本程序启动的 VirtualHere 客户端尚未退出。");
                    }
                } catch (TimeoutException) { if (Process.GetProcessesByName("vhui64").Length > 0) throw; }
            } else {
                if (startedServer != null && !startedServer.HasExited) { startedServer.Kill(); startedServer.WaitForExit(5000); }
            }
            log("USB 连接已关闭。");
        }
    }
}
