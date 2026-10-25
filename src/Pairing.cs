using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace UsbRemoteAssistant {
    [DataContract]
    public sealed class PairingDocument {
        [DataMember] public string Kind = "usb-remote-assistant-pairing";
        [DataMember] public int FormatVersion = 2;
        [DataMember] public bool ManageEasyTier;
        [DataMember] public string NetworkName, NetworkSecret, EasyTierPeers, PrimaryIp, SecondaryIp, PairKey;
        [DataMember] public int UsbPort, TransferPort;
        public static PairingDocument From(Settings cfg) {
            cfg.Validate();
            return new PairingDocument { ManageEasyTier = cfg.ManageEasyTier, NetworkName = cfg.NetworkName, NetworkSecret = cfg.NetworkSecret, EasyTierPeers = cfg.EasyTierPeers, PrimaryIp = cfg.PrimaryIp, SecondaryIp = cfg.SecondaryIp, PairKey = cfg.PairKey, UsbPort = cfg.UsbPort, TransferPort = cfg.TransferPort };
        }
        public void ApplyTo(Settings cfg, bool resetPeerState = true) {
            if (Kind != "usb-remote-assistant-pairing" || FormatVersion != 2) throw new IOException("请使用 EasyTier 版（格式 2）的配对文件；旧版请重新配对。");
            var candidate = new Settings { ManageEasyTier = ManageEasyTier, NetworkName = NetworkName ?? "", NetworkSecret = NetworkSecret ?? "", EasyTierPeers = EasyTierPeers ?? "", PrimaryIp = PrimaryIp, SecondaryIp = SecondaryIp, PairKey = PairKey, UsbPort = UsbPort, TransferPort = TransferPort };
            candidate.Validate();
            cfg.ManageEasyTier = candidate.ManageEasyTier; cfg.NetworkName = candidate.NetworkName; cfg.NetworkSecret = candidate.NetworkSecret; cfg.EasyTierPeers = candidate.EasyTierPeers;
            cfg.PrimaryIp = candidate.PrimaryIp; cfg.SecondaryIp = candidate.SecondaryIp; cfg.PairKey = candidate.PairKey;
            cfg.UsbPort = candidate.UsbPort; cfg.TransferPort = candidate.TransferPort;
            if (resetPeerState) { cfg.DeviceVendor = 0; cfg.DeviceProduct = 0; cfg.DeviceSerial = ""; cfg.BoardEnabled = false; }
        }
        public static PairingDocument Load(string path) {
            if (new FileInfo(path).Length > 65536) throw new IOException("配对文件过大。");
            return JsonData.Read<PairingDocument>(File.ReadAllText(path));
        }
    }
    public static class SettingsCopy {
        public static Settings Clone(Settings settings) { return JsonData.Read<Settings>(JsonData.Write(settings)); }
        public static void Apply(Settings source, Settings target) {
            // Public configuration fields only; no live service objects are part of Settings.
            foreach (var field in typeof(Settings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) field.SetValue(target, field.GetValue(source));
        }
    }
}
