using System;
using System.IO;
using System.Threading.Tasks;

namespace UsbRemoteAssistant {
    public sealed partial class MainForm {
        bool networkSwitching;
        void UpdateNetworkAddress() {
            addressStatus.Text = (cfg.ManageEasyTier ? "EasyTier · " : "现有网络 · ") + cfg.OwnIp + "    →    " + cfg.RemoteIp;
            ShowBoardOptions(BoardOptions.From(cfg));
        }
        Settings ReadNetworkCandidate(string payload) {
            if (Program.IsPrimary) throw new IOException("网络设置只能由主机同步到副机。");
            if (operation || closing || networkSwitching || (sendTask != null && !sendTask.IsCompleted)) throw new IOException("副机正在执行操作，请先完成或取消传输。");
            var document = JsonData.Read<PairingDocument>(PairingCipher.Open(payload, cfg.PairKey));
            var candidate = SettingsCopy.Clone(cfg); document.ApplyTo(candidate, false); candidate.Validate();
            if (candidate.ManageEasyTier) EasyTierOptions.ResolveCore(candidate);
            return candidate;
        }
        string HandleNetworkControl(string payload) {
            ReadNetworkCandidate(payload);
            return "network-staged-v2";
        }
        void CompleteNetworkControl(string command, string payload) {
            if (command != "assistant-network-v2") return;
            // Queue switching only after the acknowledgement has been flushed to the old connection.
            var candidate = ReadNetworkCandidate(payload);
            Ui(async () => {
                if (closing || networkSwitching) return;
                networkSwitching = true; Buttons(false);
                try {
                    await Task.Delay(750);
                    candidate.Save();
                    await StopAll(); SettingsCopy.Apply(candidate, cfg); UpdateNetworkAddress();
                    Log("已收到主机的加密网络设置，正在启动新 EasyTier 网络。");
                    await StartAll();
                } catch (Exception e) { Log("网络切换未完成：" + e.GetBaseException().Message + "；可导入同一配对文件恢复。"); }
                finally { networkSwitching = false; Buttons(false); }
            });
        }
    }
}
