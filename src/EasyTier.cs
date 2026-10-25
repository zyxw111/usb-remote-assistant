using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace UsbRemoteAssistant {
    public static class EasyTierOptions {
        public const string DefaultPeer = "tcp://public.easytier.cn:11010";
        public static string NewPassword() {
            byte[] data = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(data);
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        public static string[] Peers(string value) { return (value ?? "").Split(new[] { ' ', '\t', '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries); }
        public static void Validate(Settings cfg) {
            if (!cfg.ManageEasyTier) return;
            if (String.IsNullOrWhiteSpace(cfg.NetworkName) || cfg.NetworkName.Length > 128 || cfg.NetworkName.Any(Char.IsControl)) throw new IOException("请自定义网络名（1–128 个字符，不能包含控制字符）。");
            if (String.IsNullOrEmpty(cfg.NetworkSecret) || cfg.NetworkSecret.Length < 8 || cfg.NetworkSecret.Length > 256 || cfg.NetworkSecret.Any(Char.IsControl)) throw new IOException("网络密码需为 8–256 个字符，可随机生成或自行输入。");
            // EasyTier expands ${...} while loading TOML, even inside quoted strings.
            if (cfg.NetworkName.Contains("${") || cfg.NetworkSecret.Contains("${")) throw new IOException("网络名和密码不能包含 ${，以免被 EasyTier 作为环境变量展开。");
            var peers = Peers(cfg.EasyTierPeers);
            if (peers.Length == 0 || peers.Length > 16) throw new IOException("请填写 1–16 个共享节点地址，例如 tcp://节点地址:11010。");
            foreach (string peer in peers) {
                Uri uri;
                if (!Uri.TryCreate(peer, UriKind.Absolute, out uri) || !new[] { "tcp", "udp", "ws", "wss", "quic", "wg" }.Contains(uri.Scheme) || String.IsNullOrEmpty(uri.Host) || uri.Port < 1 || uri.Port > 65535 || uri.UserInfo != "" || peer.Contains("${")) throw new IOException("共享节点地址无效，应包含协议、主机及端口。");
            }
            IPAddress a, b;
            if (!IPAddress.TryParse(cfg.PrimaryIp, out a) || !IPAddress.TryParse(cfg.SecondaryIp, out b) || a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork) throw new IOException("请填写两端的虚拟 IPv4。");
            byte[] x = a.GetAddressBytes(), y = b.GetAddressBytes();
            if (!x.Take(3).SequenceEqual(y.Take(3)) || x[3] == 0 || x[3] == 255 || y[3] == 0 || y[3] == 255 || x[0] == 0 || x[0] == 127 || x[0] >= 224) throw new IOException("助手创建的两端虚拟 IP 应在同一个 /24 网段，且使用有效主机地址。");
        }
        internal static string TomlString(string value) {
            var s = new StringBuilder("\"");
            foreach (char c in value) {
                switch (c) {
                    case '\\': s.Append("\\\\"); break;
                    case '"': s.Append("\\\""); break;
                    case '\n': s.Append("\\n"); break;
                    case '\r': s.Append("\\r"); break;
                    case '\t': s.Append("\\t"); break;
                    default: if (Char.IsControl(c)) s.Append("\\u" + ((int)c).ToString("X4")); else s.Append(c); break;
                }
            }
            return s.Append('"').ToString();
        }
        public static string BuildConfig(Settings cfg, string instanceId, string ownIp) {
            Validate(cfg);
            var text = new StringBuilder();
            text.AppendLine("instance_id = " + TomlString(instanceId));
            text.AppendLine("instance_name = \"usb-remote-assistant\"");
            text.AppendLine("hostname = " + TomlString(Program.IsPrimary ? "usb-primary" : "usb-secondary"));
            text.AppendLine("ipv4 = " + TomlString(ownIp + "/24"));
            text.AppendLine("dhcp = false");
            text.AppendLine("listeners = [\"tcp://0.0.0.0:0\", \"udp://0.0.0.0:0\"]");
            text.AppendLine("[network_identity]");
            text.AppendLine("network_name = " + TomlString(cfg.NetworkName));
            text.AppendLine("network_secret = " + TomlString(cfg.NetworkSecret));
            foreach (string peer in Peers(cfg.EasyTierPeers)) { text.AppendLine("[[peer]]"); text.AppendLine("uri = " + TomlString(peer)); }
            text.AppendLine("[flags]");
            text.AppendLine("enable_encryption = true");
            text.AppendLine("relay_network_whitelist = \"\"");
            return text.ToString();
        }
        public static string ResolveCore(Settings cfg) {
            if (!String.IsNullOrWhiteSpace(cfg.EasyTierPath)) {
                if (!File.Exists(cfg.EasyTierPath)) throw new IOException("未找到所选 easytier-core.exe，请在连接与配对中重新选择。");
                return Path.GetFullPath(cfg.EasyTierPath);
            }
            string adjacent = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "easytier-core.exe");
            if (File.Exists(adjacent)) return adjacent;
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
                try { string candidate = Path.Combine(folder.Trim().Trim('"'), "easytier-core.exe"); if (File.Exists(candidate)) return Path.GetFullPath(candidate); } catch (ArgumentException) { }
            }
            throw new IOException("请从 EasyTier 官网获取 Windows x64 命令行完整包，解压后选择 easytier-core.exe，保留同目录 DLL 和驱动。");
        }
        internal static string Argument(string text) {
            var result = new StringBuilder("\""); int backslashes = 0;
            foreach (char c in text) {
                if (c == '\\') { backslashes++; continue; }
                result.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes); result.Append(c); backslashes = 0;
            }
            result.Append('\\', backslashes * 2); return result.Append('"').ToString();
        }
    }

    // Only this controller's child process is stopped. Existing GUI/service networks are never killed.
    public sealed class EasyTierController : IDisposable {
        Process child;
        string configPath;
        readonly string instanceId = Guid.NewGuid().ToString();
        public bool Running { get { return child != null && !child.HasExited; } }
        public void Ensure(Settings cfg, Action<string> log) {
            if (!cfg.ManageEasyTier) { Sys.LocalNetworkIp(cfg); return; }
            if (!Running) {
                if (child != null) { int code = child.ExitCode; Cleanup(); throw new IOException("EasyTier 核心已退出（代码 " + code + "）。请检查版本、同目录 DLL/驱动及共享节点配置；不会输出含密码的核心日志。"); }
                bool occupied = false; try { Sys.LocalNetworkIp(cfg); occupied = true; } catch (IOException) { }
                if (occupied) throw new IOException("本端虚拟 IP 已由其他网络占用。请关闭重复网络，或取消“由助手启动 EasyTier”复用现有网络。");
                string exe = EasyTierOptions.ResolveCore(cfg);
                Directory.CreateDirectory(Settings.ProfileDirectory);
                configPath = Path.Combine(Settings.ProfileDirectory, "easytier-runtime.toml");
                File.WriteAllText(configPath, EasyTierOptions.BuildConfig(cfg, instanceId, cfg.OwnIp), new UTF8Encoding(false));
                var process = new Process();
                process.StartInfo = new ProcessStartInfo(exe, "-c " + EasyTierOptions.Argument(configPath) + " --rpc-portal 127.0.0.1:0 --console-log-level off") {
                    WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                // Core can print its full TOML at startup. Drain both pipes without logging secrets.
                process.OutputDataReceived += (s, e) => { }; process.ErrorDataReceived += (s, e) => { };
                try { process.Start(); child = process; process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
                catch { process.Dispose(); Cleanup(); throw new IOException("EasyTier 启动失败，请检查核心程序、运行库和管理员权限。"); }
                log("已启动本端 EasyTier · 正在等待虚拟网卡。关闭助手时会停止本次创建的网络。");
            }
            for (int i = 0; i < 15; i++) {
                if (!Running) break;
                try { Sys.LocalNetworkIp(cfg); return; } catch (IOException) { Thread.Sleep(200); }
            }
            throw new IOException("EasyTier 虚拟网卡尚未就绪，将自动重试。");
        }
        void Cleanup() {
            if (child != null) { child.Dispose(); child = null; }
            if (configPath != null && File.Exists(configPath)) File.Delete(configPath);
            configPath = null;
        }
        public void Dispose() {
            if (child != null && !child.HasExited) { child.Kill(); child.WaitForExit(5000); }
            Cleanup();
        }
    }

    // Network credentials need confidentiality even when the old link is a trusted LAN.
    public static class PairingCipher {
        static byte[] Key(string pairKey, string purpose) {
            using (var hash = SHA256.Create()) return hash.ComputeHash(Encoding.UTF8.GetBytes("usb-remote-network-v2/" + purpose + "/" + pairKey.ToLowerInvariant()));
        }
        public static string Seal(string plain, string pairKey) {
            byte[] iv, encrypted;
            using (var aes = Aes.Create()) {
                aes.Key = Key(pairKey, "encryption"); aes.GenerateIV(); iv = aes.IV;
                using (var transform = aes.CreateEncryptor()) { var bytes = Encoding.UTF8.GetBytes(plain); encrypted = transform.TransformFinalBlock(bytes, 0, bytes.Length); }
            }
            byte[] body = iv.Concat(encrypted).ToArray(), mac;
            using (var hmac = new HMACSHA256(Key(pairKey, "authentication"))) mac = hmac.ComputeHash(body);
            return Convert.ToBase64String(body.Concat(mac).ToArray());
        }
        public static string Open(string envelope, string pairKey) {
            byte[] all;
            try { all = Convert.FromBase64String(envelope); } catch (FormatException) { throw new IOException("网络配对数据格式无效。"); }
            if (all.Length < 64 || all.Length > 131072 || (all.Length - 48) % 16 != 0) throw new IOException("网络配对数据长度无效。");
            byte[] body = all.Take(all.Length - 32).ToArray(), actual;
            using (var hmac = new HMACSHA256(Key(pairKey, "authentication"))) actual = hmac.ComputeHash(body);
            int mismatch = 0; for (int i = 0; i < 32; i++) mismatch |= actual[i] ^ all[body.Length + i];
            if (mismatch != 0) throw new IOException("网络配对数据认证失败。");
            using (var aes = Aes.Create()) {
                aes.Key = Key(pairKey, "encryption"); aes.IV = body.Take(16).ToArray();
                using (var transform = aes.CreateDecryptor()) return Encoding.UTF8.GetString(transform.TransformFinalBlock(body, 16, body.Length - 16));
            }
        }
    }
}
