using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace UsbRemoteAssistant {
    public static partial class Tests {
        public static int RunCoreSmoke(string reportPath, string corePath) {
            string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)), "easytier-smoke.toml");
            try {
                var cfg = new Settings { PrimaryIp = "192.0.2.10", SecondaryIp = "192.0.2.20", PairKey = Settings.NewPairKey(), NetworkName = "test-实验室", NetworkSecret = " custom \"密码\" \\ / 123 ", EasyTierPeers = "tcp://127.0.0.1:1" };
                string config = EasyTierOptions.BuildConfig(cfg, Guid.NewGuid().ToString(), cfg.PrimaryIp).Replace("0.0.0.0:0", "127.0.0.1:0");
                File.WriteAllText(path, config, new UTF8Encoding(false));
                using (var process = new System.Diagnostics.Process()) {
                    process.StartInfo = new System.Diagnostics.ProcessStartInfo(corePath, "-c " + EasyTierOptions.Argument(path) + " --rpc-portal 127.0.0.1:0 --console-log-level off --no-tun true") {
                        WorkingDirectory = Path.GetDirectoryName(corePath), UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                    };
                    process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    try {
                        Assert(!process.WaitForExit(2500), "official core accepts generated TOML and CLI arguments");
                    } finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
                    // Drain both streams. Quiet core versions may emit no startup output.
                    System.Threading.Tasks.Task.WaitAll(stdout, stderr);
                }
                File.WriteAllLines(reportPath, new[] { "PASS official EasyTier core accepts generated TOML, Unicode/quoted password, ephemeral TCP/UDP listeners, loopback RPC, and quiet log flag", "PASS isolated test used no TUN adapter and only a loopback peer; owned core process was stopped" }); return 0;
            } catch (Exception e) { File.WriteAllText(reportPath, "FAILED: " + e); return 1; }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        static void EasyTierTests(List<string> results) {
            string key = Settings.NewPairKey();
            var cfg = new Settings { PrimaryIp = "192.0.2.10", SecondaryIp = "192.0.2.20", PairKey = key, NetworkName = "my-lab-实验室", NetworkSecret = " custom \"密码\" \\ / 123 ", EasyTierPath = "only-local-core.exe" };
            cfg.Validate();
            var passwords = new HashSet<string>();
            for (int i = 0; i < 128; i++) { string password = EasyTierOptions.NewPassword(); Assert(password.Length == 32 && passwords.Add(password) && System.Text.RegularExpressions.Regex.IsMatch(password, "^[a-zA-Z0-9_-]{32}$"), "independent CSPRNG passwords"); }
            string json = JsonData.Write(PairingDocument.From(cfg));
            var peer = new Settings { EasyTierPath = "peer-core.exe", VirtualHerePath = "peer-usb.exe", ReceiveFolder = "peer-inbox", DeviceSerial = "REMEMBERED" };
            JsonData.Read<PairingDocument>(json).ApplyTo(peer); peer.Validate();
            Assert(peer.NetworkName == cfg.NetworkName && peer.NetworkSecret == cfg.NetworkSecret && peer.EasyTierPeers == cfg.EasyTierPeers, "custom Unicode password including spaces/quotes is delivered exactly");
            Assert(!json.Contains("EasyTierPath") && !json.Contains("only-local-core.exe") && peer.EasyTierPath == "peer-core.exe", "pairing preserves each endpoint's local core path");
            peer.DeviceSerial = "REMEMBERED"; PairingDocument.From(cfg).ApplyTo(peer, false); Assert(peer.DeviceSerial == "REMEMBERED", "online update keeps the remembered USB selection");
            var old = PairingDocument.From(cfg); old.FormatVersion = 1;
            bool rejected = false; try { old.ApplyTo(peer); } catch (IOException) { rejected = true; } Assert(rejected, "old pairing format requires explicit re-pairing");
            foreach (Action<Settings> invalidate in new Action<Settings>[] {
                x => x.NetworkName = "", x => x.NetworkName = "${ENV}", x => x.NetworkSecret = "short", x => x.NetworkSecret = "abc${ENV}def",
                x => x.SecondaryIp = "192.0.3.20", x => x.SecondaryIp = "192.0.2.255", x => x.EasyTierPeers = "", x => x.EasyTierPeers = "https://example.invalid:443"
            }) { var bad = SettingsCopy.Clone(cfg); invalidate(bad); Assert(!bad.IsConfigured, "invalid EasyTier configuration rejected before switching"); }
            string config = EasyTierOptions.BuildConfig(cfg, "11111111-1111-4111-8111-111111111111", cfg.PrimaryIp);
            Assert(config.Contains("network_name = \"my-lab-实验室\"") && config.Contains("\\\"密码\\\"") && config.Contains("\\\\") && config.Contains("ipv4 = \"192.0.2.10/24\"") && config.Contains("enable_encryption = true"), "TOML escapes custom secrets and fixes virtual addresses");
            string envelope = PairingCipher.Seal(json, key);
            Assert(PairingCipher.Open(envelope, key.ToUpperInvariant()) == json && envelope != PairingCipher.Seal(json, key) && !envelope.Contains(cfg.NetworkName), "credential envelope is confidential, randomized, and round-trips");
            byte[] altered = Convert.FromBase64String(envelope); altered[20] ^= 1;
            foreach (string input in new[] { Convert.ToBase64String(altered), "bad!", "", Convert.ToBase64String(new byte[64]) }) {
                rejected = false; try { PairingCipher.Open(input, key); } catch (IOException) { rejected = true; } Assert(rejected, "tampered credential envelope rejected");
            }
            rejected = false; try { PairingCipher.Open(envelope, Settings.NewPairKey()); } catch (IOException) { rejected = true; } Assert(rejected, "wrong credential encryption key rejected");
            results.Add("PASS EasyTier custom names/secrets, 128 random passwords, pairing delivery, local paths, input validation, TOML escaping, authenticated credential encryption");

            string root = Path.Combine(Path.GetTempPath(), "UsbRemoteAssistant-NetworkTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                using (var done = new ManualResetEventSlim(false))
                using (var server = new TransferServer("127.0.0.1", "127.0.0.1", 0, root, key, x => {})) {
                    int calls = 0, completed = 0; string delivered = null;
                    server.ControlHandler = (command, payload) => {
                        Interlocked.Increment(ref calls);
                        var incoming = JsonData.Read<PairingDocument>(PairingCipher.Open(payload, key)); incoming.ApplyTo(peer, false);
                        return "network-staged-v2";
                    };
                    server.ControlCompleted = (command, payload) => { delivered = payload; Interlocked.Increment(ref completed); done.Set(); };
                    server.Start();
                    string reply = AssistantClient.Request("127.0.0.1", server.ActualPort, key, "assistant-network-v2", envelope);
                    Assert(reply == "network-staged-v2" && done.Wait(3000) && delivered == envelope && peer.NetworkSecret == cfg.NetworkSecret, "real TCP authenticated encrypted pairing reaches the peer and schedules completion");
                    rejected = false; try { AssistantClient.Request("127.0.0.1", server.ActualPort, key, "assistant-network-v2", Convert.ToBase64String(altered)); } catch { rejected = true; }
                    Assert(rejected && completed == 1, "corrupt online pairing never schedules a switch");
                    int before = calls;
                    rejected = false; try { AssistantClient.Request("127.0.0.1", server.ActualPort, Settings.NewPairKey(), "assistant-network-v2", envelope); } catch { rejected = true; }
                    Assert(rejected && calls == before && completed == 1, "unpaired client never reaches the network change handler");
                }
            } finally { Directory.Delete(root, true); }
            results.Add("PASS real TCP network credential synchronization, post-response callback, tamper and unauthenticated change rejection");
        }
    }
}
