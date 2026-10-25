using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UsbRemoteAssistant {
    public static partial class Tests {
        public static int RunPublic(string reportPath) {
            var results = new List<string>();
            try { PublicTests(results); results.Add("ALL PUBLIC CONFIGURATION TESTS PASSED"); File.WriteAllLines(reportPath, results, new UTF8Encoding(false)); return 0; }
            catch (Exception e) { results.Add("FAILED: " + e); File.WriteAllLines(reportPath, results, new UTF8Encoding(false)); return 1; }
        }
        static void PublicTests(List<string> results) {
            var blank = new Settings();
            Assert(!blank.IsConfigured && String.IsNullOrEmpty(blank.PrimaryIp) && String.IsNullOrEmpty(blank.SecondaryIp) && String.IsNullOrEmpty(blank.NetworkName) && String.IsNullOrEmpty(blank.NetworkSecret) && String.IsNullOrEmpty(blank.PairKey), "public build has no preconfigured peer or shared secret");
            Assert(blank.DeviceVendor == 0 && blank.DeviceProduct == 0 && blank.DeviceSerial == "" && blank.VirtualHerePath == "", "no remembered personal USB device or tool path");
            string first = Settings.NewPairKey(), second = Settings.NewPairKey();
            Assert(first != second && first.Length == 64 && System.Text.RegularExpressions.Regex.IsMatch(first, "^[0-9a-f]{64}$"), "fresh random 256-bit pairing keys");
            var original = new Settings { ManageEasyTier = false, PrimaryIp = "192.0.2.10", SecondaryIp = "192.0.2.20", PairKey = first, VirtualHerePath = "local-client.exe", ReceiveFolder = "local-inbox", DeviceSerial = "LOCAL-DEVICE", DeviceVendor = 1, DeviceProduct = 2 };
            original.Validate(); Assert(!original.ManageEasyTier, "existing network mode works without managed network credentials");
            var document = PairingDocument.From(original); string json = JsonData.Write(document);
            foreach (string local in new[] { "local-client.exe", "local-inbox", "LOCAL-DEVICE", "VirtualHerePath", "ReceiveFolder", "DeviceSerial", "BoardUser" }) Assert(!json.Contains(local), "pairing export excludes local setting " + local);
            var receiver = new Settings { VirtualHerePath = "receiver-tool.exe", ReceiveFolder = "receiver-inbox", DeviceSerial = "OLD-DEVICE", BoardEnabled = true };
            JsonData.Read<PairingDocument>(json).ApplyTo(receiver); receiver.Validate();
            Assert(receiver.PairKey == first && receiver.PrimaryIp == original.PrimaryIp && receiver.SecondaryIp == original.SecondaryIp, "pairing import shares the same link and secret");
            Assert(receiver.VirtualHerePath == "receiver-tool.exe" && receiver.ReceiveFolder == "receiver-inbox" && receiver.DeviceSerial == "" && !receiver.BoardEnabled, "import preserves local tool and folder but resets peer-specific state");
            bool wrongFormat = false; try { JsonData.Read<PairingDocument>(JsonData.Write(original)).ApplyTo(receiver); } catch (IOException) { wrongFormat = true; } Assert(wrongFormat, "a local settings file is not silently accepted as a pairing document");
            var clone = SettingsCopy.Clone(original); clone.PrimaryIp = "192.0.2.30"; Assert(original.PrimaryIp == "192.0.2.10", "cancelled setup draft does not modify live settings");
            clone = SettingsCopy.Clone(original); clone.ManageEasyTier = true; Assert(!clone.IsConfigured, "managed EasyTier requires a user-defined name and password");
            Assert(Settings.ProfileDirectory.Contains("UsbRemoteAssistant.Public"), "public app profile is isolated from private app configuration");
            results.Add("PASS blank public defaults, random per-pair secrets, LAN/VPN setup, export/import privacy, setup cancellation, and isolated profiles");

            bool owned = true; int releases = 0;
            var cfg = new Settings { SecondaryIp = "192.0.2.20", DeviceVendor = 1, DeviceProduct = 2 };
            Func<string, string> transport = command => {
                if (command.StartsWith("STOP USING,")) { releases++; owned = false; return "OK"; }
                if (command.StartsWith("USE,")) { owned = true; return "OK"; }
                if (command != "GET CLIENT STATE") throw new IOException("Unexpected IPC request " + command);
                return "<state><server><connection host='192.0.2.20' port='7575' state='3' hostname='TEST-SERVER'/><device address='1' idVendor='1' idProduct='2' state='" + (owned ? "3" : "1") + "' boundClientHostname='" + (owned ? Environment.MachineName : "") + "'/></server></state>";
            };
            var controller = new UsbController(cfg, x => {}, transport, () => {}, () => true, ms => {});
            controller.Use(controller.Devices()[0]); controller.Stop(); Assert(releases == 0 && owned, "Stop leaves a pre-existing device binding alone");
            owned = false; controller.Use(controller.Devices()[0]); controller.Stop(); Assert(releases == 1 && !owned, "Stop releases a binding acquired by this app");
            results.Add("PASS shutdown preserves pre-existing device bindings and releases only the app-acquired USB binding");
            var unselected = new Settings { SecondaryIp = "192.0.2.20" };
            var unselectedController = new UsbController(unselected, x => {}, command => {
                if (command == "MANUAL HUB LIST") return "192.0.2.20:7575";
                if (command == "GET CLIENT STATE") return "<state><server><connection host='192.0.2.20' port='7575' state='3' hostname='TEST-SERVER'/><device address='1' idVendor='0' idProduct='0' state='1'/></server></state>";
                throw new IOException("No USB device may be automatically claimed before explicit selection.");
            }, () => {}, () => true, ms => {});
            Assert(unselectedController.PollPrimary().Devices.Count == 1, "no automatic USB claim in a fresh public profile");
            results.Add("PASS first-run public profile does not automatically select an unknown USB device");
            EasyTierTests(results);
        }
    }
}
