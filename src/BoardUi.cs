using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UsbRemoteAssistant {
    public sealed partial class MainForm {
        readonly object boardGate = new object();
        readonly PeerTracker peerTracker = new PeerTracker();
        readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        BoardRelay boardRelay;
        volatile UsbSnapshot latestUsb = new UsbSnapshot();
        volatile AssistantStatus ownStatus;
        volatile bool boardReachable;
        string boardServiceError = "", lastBoardError = "", peerStatusError = "";
        bool syncingStatus, boardDirty, loadingBoard, savingBoard;
        Label boardStatus, boardCommand;
        TextBox boardIpInput, boardSshInput, boardListenInput, boardUserInput, ethernetInfo;
        CheckBox boardEnabledInput;
        Button boardSave;

        void BuildBoardPage() {
            var page = new TabPage("板卡 SSH") { BackColor = Color.White, Padding = new Padding(12) }; tabs.TabPages.Add(page);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); page.Controls.Add(layout);
            boardStatus = Label("板卡 SSH：尚未配置", 13, true); layout.Controls.Add(boardStatus, 0, 0);
            layout.Controls.Add(Label("主机 → 网络 / VPN → 副机 → 网线 → Linux 板卡", 10, false), 0, 1);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3 };
            for (int row = 0; row < 3; row++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            string[] labels = { "板卡网口 IPv4", "SSH 端口", "SSH 用户名", "副机转发端口" };
            var inputs = new TextBox[4];
            for (int i = 0; i < 4; i++) {
                grid.Controls.Add(Label(labels[i], 9, false), (i % 2) * 2, i / 2);
                inputs[i] = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(5, 6, 10, 4) };
                inputs[i].TextChanged += (s, e) => { if (!loadingBoard) boardDirty = true; }; grid.Controls.Add(inputs[i], (i % 2) * 2 + 1, i / 2);
            }
            boardIpInput = inputs[0]; boardSshInput = inputs[1]; boardUserInput = inputs[2]; boardListenInput = inputs[3];
            boardEnabledInput = new CheckBox { Text = "开启板卡 SSH 转发（随一键开启 / 关闭）", AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
            boardEnabledInput.CheckedChanged += (s, e) => { if (!loadingBoard) boardDirty = true; };
            grid.Controls.Add(boardEnabledInput, 0, 2); grid.SetColumnSpan(boardEnabledInput, 4); layout.Controls.Add(grid, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            boardSave = Button(Program.IsPrimary ? "保存并同步到副机" : "保存板卡设置", Color.FromArgb(29, 110, 225), async (s, e) => await SaveBoard()); buttons.Controls.Add(boardSave);
            buttons.Controls.Add(Button("检测 SSH", Color.White, async (s, e) => await TestBoard()));
            buttons.Controls.Add(Button("复制 SSH 命令", Color.White, (s, e) => {
                try { var options = ReadBoardInputs(); if (options.User == "") throw new IOException("请先填写板卡 SSH 用户名。"); Clipboard.SetText(options.Command(cfg.SecondaryIp)); }
                catch (Exception x) { MessageBox.Show(this, x.Message, "SSH 命令"); }
            }));
            var open = Button("打开 SSH 终端", Color.White, (s, e) => OpenBoardTerminal()); open.Visible = Program.IsPrimary; buttons.Controls.Add(open); layout.Controls.Add(buttons, 0, 3);
            boardCommand = Label("请填写板卡地址和用户名；软件不保存 SSH 密码。", 10, false); boardCommand.AutoSize = false; boardCommand.Dock = DockStyle.Fill; layout.Controls.Add(boardCommand, 0, 4);
            ethernetInfo = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(247, 249, 253) }; layout.Controls.Add(ethernetInfo, 0, 5);
            ShowBoardOptions(BoardOptions.From(cfg));
            ethernetInfo.Text = Program.IsPrimary ? "在任意一端填写板卡设置并保存。主机端会通过已配对连接同步到副机。\r\n副机的有线网口需与板卡位于同一网段，板卡必须已启动 SSH 服务。\r\n网线不会自动分配 IP；此软件不会自动修改板卡系统或副机网卡地址。\r\n两端都更新至 2.0 后可同步设置与 USB 接入状态。" : (preview ? "运行时将显示副机网口信息。" : PhysicalAddresses());
        }
        string PhysicalAddresses() {
            try {
                var lines = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && n.Description.IndexOf("EasyTier", StringComparison.OrdinalIgnoreCase) < 0 && n.Description.IndexOf("Wintun", StringComparison.OrdinalIgnoreCase) < 0 && !n.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Name + " · " + n.OperationalStatus + "\r\n" + String.Join("\r\n", n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => "  IPv4 " + a.Address + " / 掩码 " + a.IPv4Mask)));
                return "副机网口信息（供核对板卡网段）：\r\n" + String.Join("\r\n", lines) + "\r\n板卡需已启动 SSH 服务；此软件不自动分配或修改网口 IP。";
            } catch (Exception e) { return e.Message; }
        }
        BoardOptions ReadBoardInputs() {
            int sshPort, relayPort;
            if (!Int32.TryParse(boardSshInput.Text, out sshPort) || !Int32.TryParse(boardListenInput.Text, out relayPort)) throw new IOException("端口应为整数。");
            var options = new BoardOptions { Enabled = boardEnabledInput.Checked, Ip = boardIpInput.Text.Trim(), User = boardUserInput.Text.Trim(), SshPort = sshPort, ListenPort = relayPort };
            options.Validate(cfg); return options;
        }
        void ShowBoardOptions(BoardOptions options) {
            if (options == null) return;
            loadingBoard = true;
            try { boardEnabledInput.Checked = options.Enabled; boardIpInput.Text = options.Ip; boardSshInput.Text = options.SshPort.ToString(); boardListenInput.Text = options.ListenPort.ToString(); boardUserInput.Text = options.User; boardDirty = false; }
            finally { loadingBoard = false; }
            boardCommand.Text = options.Command(cfg.SecondaryIp);
        }
        void StoreBoard(BoardOptions options) {
            lock (cfg) {
                var previous = BoardOptions.From(cfg);
                try { options.Apply(cfg); cfg.Save(); }
                catch { previous.Apply(cfg); throw; }
            }
        }
        async Task SaveBoard() {
            if (preview || savingBoard || operation) return;
            savingBoard = true; boardSave.Enabled = false;
            try {
                var options = ReadBoardInputs();
                if (Program.IsPrimary) {
                    if (!recovery.Enabled) throw new IOException("请先在两端点击一键开启，再同步板卡设置。");
                    string response = await Task.Run(() => AssistantClient.Request(cfg.RemoteIp, cfg.TransferPort, cfg.PairKey, "assistant-board-v1", JsonData.Write(options)));
                    options = JsonData.Read<BoardOptions>(response); options.Validate(cfg);
                }
                StoreBoard(options); ShowBoardOptions(options);
                if (!Program.IsPrimary && recovery.Enabled) { await usbGate.WaitAsync(); try { await Task.Run((Action)EnsureBoard); } finally { usbGate.Release(); } }
                boardStatus.Text = options.Enabled ? "设置已保存 · 等待副机转发状态刷新" : "板卡 SSH 转发未启用";
                Log("板卡 SSH 设置已保存" + (Program.IsPrimary ? "并同步到副机。" : "。"));
                await SyncStatus();
            } catch (Exception e) { MessageBox.Show(this, e.GetBaseException().Message, "板卡设置未完成"); }
            finally { savingBoard = false; boardSave.Enabled = !operation; }
        }
        string HandleControl(string command, string payload) {
            if (command == "assistant-status-v1") {
                peerTracker.Record(JsonData.Read<AssistantStatus>(payload), Program.IsPrimary ? "副机端" : "主机端", DateTime.UtcNow);
                return JsonData.Write(ownStatus);
            }
            if (command == "assistant-network-v2") return HandleNetworkControl(payload);
            if (command != "assistant-board-v1") throw new IOException("未知控制命令。");
            if (Program.IsPrimary) throw new IOException("板卡转发只能在副机端配置。");
            var options = JsonData.Read<BoardOptions>(payload); options.Validate(cfg); StoreBoard(options);
            Ui(() => { ShowBoardOptions(options); boardStatus.Text = "已收到主机设置 · 自动应用中"; Log("已收到主机同步的板卡 SSH 设置。"); });
            return JsonData.Write(options);
        }
        void EnsureBoard() {
            if (Program.IsPrimary || !recovery.Enabled) return;
            var options = BoardOptions.From(cfg); options.Validate(cfg);
            lock (boardGate) {
                if (!recovery.Enabled) return;
                if (!options.Enabled) { if (boardRelay != null) boardRelay.Dispose(); boardRelay = null; boardReachable = false; return; }
                if (boardRelay == null || !boardRelay.Running || boardRelay.Identity != options.RelayIdentity) {
                    if (boardRelay != null) boardRelay.Dispose(); boardRelay = null;
                    Sys.Firewall("USBRemotePublic-BoardSSH", cfg.SecondaryIp, cfg.PrimaryIp, options.ListenPort);
                    var next = new BoardRelay(cfg.SecondaryIp, cfg.PrimaryIp, options.ListenPort, options.Ip, options.SshPort, options.RelayIdentity, Log);
                    try { next.Start(); boardRelay = next; } catch { next.Dispose(); throw; }
                    Log("板卡 SSH 转发已开启 · " + cfg.SecondaryIp + ":" + options.ListenPort + " → " + options.Ip + ":" + options.SshPort);
                }
            }
            boardReachable = Sys.CanConnect(options.Ip, options.SshPort, 1000);
        }
        void StopBoard() { lock (boardGate) { if (boardRelay != null) boardRelay.Dispose(); boardRelay = null; boardReachable = false; } }
        void LogChangedBoardError() { if (lastBoardError != boardServiceError) { Log("板卡转发：" + boardServiceError); lastBoardError = boardServiceError; } }
        AssistantStatus MakeStatus() {
            var current = new AssistantStatus { Role = Program.Role, Board = BoardOptions.From(cfg), UsbConnected = Program.IsPrimary && latestUsb.PeerConnected, UsbOwned = Program.IsPrimary && latestUsb.Devices.Any(x => x.Owned) };
            if (!Program.IsPrimary) {
                current.UsbConnected = Sys.UsbClientConnected(cfg);
                lock (boardGate) { current.RelayRunning = boardRelay != null && boardRelay.Running; current.SshSessions = boardRelay == null ? 0 : boardRelay.Active; }
                current.BoardReachable = boardReachable;
                current.RelayError = boardServiceError;
            }
            return current;
        }
        void UpdateSecondaryUsb() {
            bool tcp = false; try { tcp = Sys.UsbClientConnected(cfg); } catch {}
            usbStatus.Text = PeerTracker.SecondaryUsbText(Process.GetProcessesByName("vhusbdwinw64").Length > 0, tcp, peerTracker.Fresh(DateTime.UtcNow));
            usbStatus.ForeColor = tcp ? Color.FromArgb(19, 127, 77) : Color.FromArgb(171, 103, 23);
        }
        async Task SyncStatus() {
            if (preview || syncingStatus || !recovery.Enabled || operation) return;
            syncingStatus = true;
            try {
                var response = await Task.Run(() => {
                    ownStatus = MakeStatus();
                    return JsonData.Read<AssistantStatus>(AssistantClient.Request(cfg.RemoteIp, cfg.TransferPort, cfg.PairKey, "assistant-status-v1", JsonData.Write(ownStatus)));
                });
                if (!recovery.Enabled || IsDisposed) return;
                peerTracker.Record(response, Program.IsPrimary ? "副机端" : "主机端", DateTime.UtcNow); peerStatusError = "";
            } catch (Exception e) { peerStatusError = e.GetBaseException().Message; }
            finally {
                syncingStatus = false;
                if (recovery.Enabled && !IsDisposed) {
                    if (!Program.IsPrimary) UpdateSecondaryUsb();
                    RenderBoardStatus();
                }
            }
        }
        void RenderBoardStatus() {
            var peer = peerTracker.Fresh(DateTime.UtcNow);
            var status = Program.IsPrimary ? peer : ownStatus;
            if (Program.IsPrimary && peer != null && peer.Board != null && !boardDirty && !savingBoard) ShowBoardOptions(peer.Board);
            if (status == null) { boardStatus.Text = "对端状态未同步 · 请确认两端均为 2.0 且已开启"; return; }
            if (status.Board == null || !status.Board.Enabled) { boardStatus.Text = "板卡 SSH 转发未启用 · 填写设置后保存"; return; }
            if (!String.IsNullOrEmpty(status.RelayError)) { boardStatus.Text = "转发未就绪：" + status.RelayError; return; }
            if (!status.RelayRunning) { boardStatus.Text = "副机转发尚未就绪 · 正在等待自动重试"; return; }
            boardStatus.Text = status.BoardReachable ? "SSH 转发已开启 · 板卡端口可达 · 当前会话 " + status.SshSessions : "SSH 转发已开启 · 等待板卡联网或 SSH 服务";
            boardStatus.ForeColor = status.BoardReachable ? Color.FromArgb(19, 127, 77) : Color.FromArgb(171, 103, 23);
            boardCommand.Text = status.Board.Command(cfg.SecondaryIp);
            if (peerStatusError != "") boardStatus.Text += " · 状态同步暂不可用";
        }
        async Task TestBoard() {
            if (preview) return;
            try {
                if (boardDirty) throw new IOException("请先保存板卡设置再检测。");
                var peer = peerTracker.Fresh(DateTime.UtcNow);
                var options = Program.IsPrimary && peer != null && peer.Board != null ? peer.Board : BoardOptions.From(cfg);
                if (!options.Enabled) throw new IOException("请先启用并保存板卡 SSH 转发。");
                string ip = Program.IsPrimary ? cfg.SecondaryIp : options.Ip; int port = Program.IsPrimary ? options.ListenPort : options.SshPort;
                string banner = await Task.Run(() => ReadSshBanner(ip, port));
                MessageBox.Show(this, "已收到板卡 SSH 握手标识：\r\n" + banner + "\r\n尚未登录或执行命令。", "SSH 连接检测成功");
            } catch (Exception e) { MessageBox.Show(this, e.GetBaseException().Message, "SSH 连接尚未完成"); }
        }
        internal static string ReadSshBanner(string ip, int port) {
            using (var client = new TcpClient()) {
                if (!client.ConnectAsync(ip, port).Wait(5000)) throw new IOException("连接超时，请检查板卡 IP、SSH 服务和副机网线。");
                client.ReceiveTimeout = 5000;
                using (var reader = new StreamReader(client.GetStream(), Encoding.ASCII)) {
                    for (int line = 0; line < 10; line++) { var text = new StringBuilder(); int b; while ((b = reader.Read()) != -1 && b != '\n') { if (text.Length >= 512) throw new IOException("SSH 服务标识过长。"); text.Append((char)b); } string value = text.ToString().TrimEnd('\r'); if (value.StartsWith("SSH-2.0-")) return value; if (b == -1) break; }
                }
                throw new IOException("端口已连接，但没有收到有效 SSH 标识，请检查目标端口。");
            }
        }
        void OpenBoardTerminal() {
            try {
                if (boardDirty) throw new IOException("请先保存板卡设置。");
                var peer = peerTracker.Fresh(DateTime.UtcNow);
                if (peer == null || peer.Board == null || !peer.RelayRunning) throw new IOException("尚未确认副机 SSH 转发已开启，请等待连接或检测 SSH。");
                var options = peer.Board; options.Validate(cfg);
                if (options.User == "") throw new IOException("请先填写板卡 SSH 用户名并保存。");
                string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
                if (!File.Exists(ssh)) throw new IOException("未找到 Windows OpenSSH 客户端，可复制命令到已安装的 SSH 工具中使用。");
                Process.Start(new ProcessStartInfo(ssh, "-p " + options.ListenPort + " -l " + options.User + " " + cfg.SecondaryIp) { UseShellExecute = true });
            } catch (Exception e) { MessageBox.Show(this, e.Message, "打开板卡 SSH"); }
        }
    }
}
