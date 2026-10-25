using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TimeoutException = System.TimeoutException;

[assembly: System.Reflection.AssemblyVersion("2.0.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.1.0")]

namespace UsbRemoteAssistant {
    public static class Program {
#if PRIMARY
        public static readonly bool IsPrimary = true;
#else
        public static readonly bool IsPrimary = false;
#endif
        public static string Role { get { return IsPrimary ? "主机端" : "副机端"; } }
        [STAThread]
        public static int Main(string[] args) {
            try {
                #if TESTS
                if (args.Contains("--self-test")) { string report = args.Length > 1 ? args[1] : "test-report.txt"; return Tests.Run(report); }
                if (args.Contains("--recovery-test")) return Tests.RunRecovery(args[1]);
                if (args.Contains("--board-test")) return Tests.RunBoard(args[1]);
                if (args.Contains("--public-test")) return Tests.RunPublic(args[1]);
                if (args.Contains("--core-smoke-test")) return Tests.RunCoreSmoke(args[1], args[2]);
                #endif
                if (args.Contains("--connection-check")) {
                    var cfg = Settings.Load(); cfg.Validate();
                    var lines = new List<string> { "USB Remote Assistant 2.0", "Role=" + Role, "LocalNetworkIP=" + Sys.LocalNetworkIp(cfg) };
                    var bind = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Parse(cfg.OwnIp), 0);
                    try { bind.Start(); lines.Add("LocalAddressBind=OK"); } finally { bind.Stop(); }
                    lines.Add("SecondaryUsbPort=" + Sys.CanConnect(cfg.SecondaryIp, cfg.UsbPort, 5000));
                    using (var peer = new System.Net.Sockets.TcpClient()) {
                        if (!peer.ConnectAsync(cfg.RemoteIp, cfg.TransferPort).Wait(8000)) throw new IOException("对端文件服务连接超时。");
                        peer.ReceiveTimeout = 8000; peer.SendTimeout = 8000;
                        using (var stream = peer.GetStream()) using (var r = new BinaryReader(stream, Encoding.UTF8, true)) using (var w = new BinaryWriter(stream, Encoding.UTF8, true)) Wire.ClientAuth(r, w, cfg.PairKey);
                    }
                    lines.Add("PeerFileAuthentication=OK"); lines.Add("No files were uploaded or changed by this check.");
                    File.WriteAllLines(args[1], lines, new UTF8Encoding(false)); return 0;
                }
                if (args.Contains("--send-file-check")) {
                    var cfg = Settings.Load(); cfg.Validate();
                    if (args.Length != 4) throw new ArgumentException("Expected report, source file and relative destination name.");
                    var info = new FileInfo(args[2]);
                    var sender = new TransferSender(cfg.RemoteIp, cfg.TransferPort, cfg.PairKey, p => {});
                    var result = sender.Send(new TransferItem { Source = info.FullName, Relative = args[3], Size = info.Length }, CancellationToken.None);
                    File.WriteAllLines(args[1], new[] { "SendAndReceiverReadBackVerification=OK", "Bytes=" + info.Length, "ReceiverPath=" + result.Destination, "SHA256=" + result.Hash, "Skipped=" + result.Skipped }, new UTF8Encoding(false)); return 0;
                }
                if (args.Contains("--probe")) {
                    var cfg = Settings.Load(); cfg.Validate();
                    bool reachable = Sys.CanConnect(cfg.SecondaryIp, cfg.UsbPort, 3000);
                    var lines = new List<string> { "Role=" + Role, "LocalIP=" + Sys.LocalNetworkIp(cfg), "USBRemoteTCP=" + reachable };
                    if (IsPrimary) { var snapshot = UsbController.WithReachability(new UsbController(cfg, x => {}).Snapshot(), reachable); lines.Add("UsbPeerConnected=" + snapshot.PeerConnected); lines.Add("Devices=" + snapshot.Devices.Count); lines.Add("OwnedDevices=" + snapshot.Devices.Count(x => x.Owned)); lines.Add("Status=" + snapshot.Status); }
                    File.WriteAllLines(args[1], lines, new UTF8Encoding(false)); return 0;
                }
                if (args.Contains("--usb-cycle-test")) {
                    var cfg = Settings.Load(); cfg.Validate(); var lines = new List<string>();
                    Action<string> record = line => { lines.Add(line); File.WriteAllLines(args[1], lines, new UTF8Encoding(false)); };
                    var controller = new UsbController(cfg, record);
                    record("Testing start"); controller.StartPrimary(); record("PASS start / connect"); record("Testing close"); controller.Stop(); record("PASS close");
                    record("Testing reopen"); controller.StartPrimary(); record("PASS reopen / connect"); return 0;
                }
                if (args.Contains("--render-preview")) {
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new MainForm(Settings.Load(), true)) form.RenderPreview(args[1]); return 0;
                }
                if (args.Contains("--render-setup-preview")) {
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new SetupDialog(new Settings(), true)) form.RenderPreview(args[1]); return 0;
                }
                bool preview = args.Contains("--preview");
                if (!preview && !Sys.IsAdmin) {
                    try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--elevated") { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory }); }
                    catch { MessageBox.Show("启动需要管理员权限，用于启动网络服务及设置本机连接规则。请再次打开并确认 Windows 提示。", "USB 远程助手"); }
                    return 0;
                }
                bool created;
                using (var mutex = new Mutex(true, "Local\\UsbRemoteAssistantPublic-" + Role + (preview ? "-preview" : ""), out created)) {
                    if (!created) { MessageBox.Show("这个端的助手已经运行，请查看任务栏中的窗口。", "USB 远程助手"); return 0; }
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    var settings = Settings.Load();
                    Application.Run(new MainForm(settings, preview));
                }
                return 0;
            } catch (Exception e) {
                if (args.Any(x => x.StartsWith("--") && x != "--elevated" && x != "--preview") && args.Length > 1) { File.AppendAllText(args[1], "FAILED: " + e + Environment.NewLine, new UTF8Encoding(false)); return 1; }
                MessageBox.Show(e.GetBaseException().Message, "USB 远程助手 · 未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
    }

    public sealed partial class MainForm : Form {
        readonly Settings cfg;
        readonly bool preview;
        readonly UsbController usb;
        readonly EasyTierController network = new EasyTierController();
        readonly object logLock = new object();
        readonly string logPath;
        TransferServer receiver;
        TransferSender sender;
        CancellationTokenSource sendCancel;
        Task sendTask;
        bool operation, closing, closedReady;
        readonly RecoverySession recovery = new RecoverySession();
        string fileMonitorError = "";
        readonly SemaphoreSlim usbGate = new SemaphoreSlim(1, 1);
        readonly System.Windows.Forms.Timer monitor = new System.Windows.Forms.Timer { Interval = 8000 };
        bool monitoring;
        string monitorError = "";
        DateTime lastProgress = DateTime.MinValue;
        string lastPhase = "";
        readonly List<TransferItem> queue = new List<TransferItem>();
        readonly Dictionary<TransferItem, ListViewItem> rows = new Dictionary<TransferItem, ListViewItem>();
        Button start, stop, settings, refresh, use, send, cancel;
        Label usbStatus, filesStatus, currentFile, queueStatus;
        ComboBox deviceList;
        ListView fileList;
        ProgressBar progress;
        TextBox logs;
        TabControl tabs;
        Label addressStatus;
        protected override bool ShowWithoutActivation { get { return preview; } }
        public MainForm(Settings config, bool isPreview) {
            cfg = config; preview = isPreview;
            logPath = Path.Combine(Settings.ProfileDirectory, "activity.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            usb = new UsbController(cfg, Log);
            Text = "USB 远程助手 · " + Program.Role + " · 2.0 EasyTier";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(930, 690); MinimumSize = new Size(820, 620);
            BackColor = Color.FromArgb(244, 247, 252); StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            ownStatus = new AssistantStatus { Role = Program.Role, Board = BoardOptions.From(cfg) };
            statusTimer.Tick += async (s, e) => await SyncStatus();
            monitor.Tick += async (s, e) => await MonitorUsb();
            Shown += async (s, e) => { if (preview) { Log("界面预览：未启动服务。"); return; } if (!cfg.IsConfigured) await EditSettings(); if (cfg.IsConfigured && cfg.AutoStart) await StartAll(); else Log("先完成连接与配对，再点击一键开启。"); };
            FormClosing += OnClosing;
        }
        Button Button(string text, Color color, EventHandler action) {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(110, 36), Height = 36, BackColor = color, ForeColor = color == Color.White ? Color.FromArgb(40, 52, 72) : Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(5), Padding = new Padding(8, 0, 8, 0) };
            b.FlatAppearance.BorderColor = color == Color.White ? Color.FromArgb(208, 218, 232) : color;
            b.Click += action; return b;
        }
        Label Label(string text, int size, bool bold) { return new Label { Text = text, AutoSize = true, ForeColor = Color.FromArgb(37, 52, 75), Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(8) }; }
        void BuildUi() {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            Controls.Add(root);
            var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(27, 49, 81) };
            var title = Label("USB 远程助手  /  " + Program.Role, 21, true); title.ForeColor = Color.White; title.Location = new Point(12, 5);
            var subtitle = Label(Program.IsPrimary ? "接入副机下载器 · 双向文件传输 · 板卡 SSH" : "共享本机下载器 · 双向文件传输 · 板卡 SSH", 10, false); subtitle.ForeColor = Color.FromArgb(199, 217, 241); subtitle.Location = new Point(15, 49);
            header.Controls.Add(title); header.Controls.Add(subtitle); root.Controls.Add(header, 0, 0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
            start = Button("一键开启", Color.FromArgb(29, 110, 225), async (s, e) => await StartAll());
            stop = Button("一键关闭", Color.FromArgb(190, 65, 65), async (s, e) => await StopAll());
            settings = Button("连接与配对", Color.White, async (s, e) => await EditSettings());
            var diagnostics = Button("导出日志", Color.White, (s, e) => {
                using (var dialog = new SaveFileDialog { FileName = "USB远程助手-" + Program.Role + "-诊断.txt", Filter = "文本文件|*.txt" }) {
                    if (dialog.ShowDialog(this) == DialogResult.OK) {
                        try { File.WriteAllText(dialog.FileName, "USB 远程助手 2.0\r\n角色：" + Program.Role + "\r\n本机：" + cfg.OwnIp + "\r\n对端：" + cfg.RemoteIp + "\r\n自动恢复：" + recovery.Enabled + "\r\n文件接收：" + (receiver != null && receiver.Running) + "\r\n板卡状态：" + boardStatus.Text + "\r\n状态同步：" + peerStatusError + "\r\n\r\n" + logs.Text, new UTF8Encoding(false)); }
                        catch (Exception x) { MessageBox.Show(this, x.Message, "日志未保存"); }
                    }
                }
            });
            buttons.Controls.Add(start); buttons.Controls.Add(stop); buttons.Controls.Add(settings); buttons.Controls.Add(diagnostics); root.Controls.Add(buttons, 0, 1);
            tabs = new TabControl { Dock = DockStyle.Fill, Font = Font };
            var usbPage = new TabPage("USB 下载器") { BackColor = Color.White, Padding = new Padding(12) };
            var filesPage = new TabPage("文件传输") { BackColor = Color.White, Padding = new Padding(12) };
            tabs.TabPages.Add(usbPage); tabs.TabPages.Add(filesPage); root.Controls.Add(tabs, 0, 2);
            var usbLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            usbLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); usbLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); usbLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); usbLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); usbLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            usbPage.Controls.Add(usbLayout);
            usbStatus = Label("USB 服务：等待开启", 15, true); usbLayout.Controls.Add(usbStatus, 0, 0);
            addressStatus = Label("本机 " + cfg.OwnIp + "    →    对端 " + cfg.RemoteIp, 10, false); usbLayout.Controls.Add(addressStatus, 0, 1);
            var devicePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            deviceList = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 480, Margin = new Padding(7, 11, 8, 0) };
            refresh = Button("刷新设备", Color.White, async (s, e) => await RefreshDevices());
            use = Button("接入所选", Color.FromArgb(29, 110, 225), async (s, e) => await UseSelected());
            devicePanel.Controls.Add(deviceList); devicePanel.Controls.Add(refresh); devicePanel.Controls.Add(use);
            devicePanel.Visible = Program.IsPrimary; usbLayout.Controls.Add(devicePanel, 0, 2);
            usbLayout.Controls.Add(Label(Program.IsPrimary ? "就绪后，在 Vivado / PDS 中按本地下载器操作。" : "下载器插在这台电脑上；保持供电、联网，避免休眠。", 10, false), 0, 3);
            logs = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(247, 249, 253), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 9F) };
            usbLayout.Controls.Add(logs, 0, 4);
            BuildFiles(filesPage);
            BuildBoardPage();
            var footer = Label("关闭窗口会停止 USB、文件和 SSH 转发。中断的文件可重新发送续传。", 9, false); footer.Margin = new Padding(0, 5, 0, 0); root.Controls.Add(footer, 0, 3);
            stop.Enabled = false;
        }
        void BuildFiles(TabPage page) {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            page.Controls.Add(layout);
            filesStatus = Label("文件接收：等待开启 · 支持大文件、文件夹和断点续传", 11, true); layout.Controls.Add(filesStatus, 0, 0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            buttons.Controls.Add(Button("添加文件", Color.White, (s, e) => AddFiles()));
            buttons.Controls.Add(Button("添加文件夹", Color.White, async (s, e) => await AddFolder()));
            send = Button("开始发送", Color.FromArgb(29, 110, 225), async (s, e) => await SendQueue()); buttons.Controls.Add(send);
            cancel = Button("中断 / 续传", Color.White, (s, e) => { if (sendCancel != null) sendCancel.Cancel(); }); buttons.Controls.Add(cancel);
            buttons.Controls.Add(Button("打开接收目录", Color.White, (s, e) => { Directory.CreateDirectory(cfg.Inbox); Process.Start(new ProcessStartInfo(cfg.Inbox) { UseShellExecute = true }); }));
            buttons.Controls.Add(Button("清空队列", Color.White, (s, e) => { if (sendTask != null && !sendTask.IsCompleted) return; queue.Clear(); rows.Clear(); fileList.Items.Clear(); UpdateQueue(); }));
            layout.Controls.Add(buttons, 0, 1);
            fileList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
            fileList.Columns.Add("文件 / 相对路径", 380); fileList.Columns.Add("大小", 110); fileList.Columns.Add("状态", 245);
            layout.Controls.Add(fileList, 0, 2);
            currentFile = Label("选择文件或文件夹，然后发送到 " + cfg.RemoteIp, 9, false); currentFile.AutoSize = false; currentFile.Dock = DockStyle.Fill; layout.Controls.Add(currentFile, 0, 3);
            progress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000 }; layout.Controls.Add(progress, 0, 4);
            queueStatus = Label("只有接收端落盘后 SHA-256 一致，才显示完成。", 9, false); layout.Controls.Add(queueStatus, 0, 5);
            cancel.Enabled = false;
        }
        void Ui(Action action) {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke(action); } catch {} } else action();
        }
        void Log(string text) {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
            lock (logLock) {
                try { File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8); }
                catch { try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "activity.log"), line + Environment.NewLine, Encoding.UTF8); } catch {} }
            }
            Ui(() => { if (logs.TextLength > 100000) logs.Text = logs.Text.Substring(logs.TextLength - 60000); logs.AppendText(line + Environment.NewLine); if (text.StartsWith("开始接收") || text.StartsWith("接收完成") || text.StartsWith("接收未完成")) filesStatus.Text = text; });
        }
        void Buttons(bool busy) {
            operation = busy; bool unavailable = busy || networkSwitching; start.Enabled = !unavailable; stop.Enabled = !unavailable && (recovery.Enabled || receiver != null || Process.GetProcessesByName(Program.IsPrimary ? "vhui64" : "vhusbdwinw64").Length > 0);
            settings.Enabled = !unavailable; refresh.Enabled = !unavailable; use.Enabled = !unavailable;
            boardSave.Enabled = !unavailable && !savingBoard;
        }
        async Task StartAll() {
            if (operation || preview) return;
            if (!cfg.IsConfigured) { await EditSettings(); if (!cfg.IsConfigured) return; }
            Buttons(true); usbStatus.Text = "正在开启…";
            try {
                cfg.Validate(); recovery.Start();
                Log("USB 远程助手 2.0 · 自动恢复已开启，两端启动顺序不限。");
                await usbGate.WaitAsync();
                try { await RecoverOnce(); } finally { usbGate.Release(); }
            } catch (Exception e) { Log("启动检查失败：" + e.Message); }
            finally { Buttons(false); if (recovery.Enabled) { monitor.Start(); statusTimer.Start(); } }
            await SyncStatus();
        }
        void EnsureNetwork() {
            network.Ensure(cfg, Log);
            var bind = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Parse(cfg.OwnIp), 0);
            try { bind.Start(); } finally { bind.Stop(); }

        }
        async Task RecoverOnce() {
            UsbSnapshot snapshot = null;
            var result = await Task.Run(() => recovery.Tick(EnsureNetwork,
                () => { if (Program.IsPrimary) { snapshot = usb.PollPrimary(); return snapshot.Devices; } usb.StartSecondary(); return new List<UsbDevice>(); }, StartFiles, EnsureBoard));
            if (result == null) return;
            if (result.NetworkError != null) {
                latestUsb = new UsbSnapshot();
                 usbStatus.Text = "本机网络尚未就绪 · 自动重试中";
                usbStatus.ForeColor = Color.FromArgb(171, 103, 23);
                if (receiver == null) filesStatus.Text = "文件接收：等待本机网络 · 自动重试中";
                string error = "网络检查：" + result.NetworkError.Message;
                if (monitorError != error) { Log(error); monitorError = error; } return;
            }
            if (result.UsbError == null) {
                if (Program.IsPrimary) {
                    latestUsb = snapshot;
                    PopulateDevices(result.Devices); usbStatus.Text = snapshot.Status;
                    usbStatus.ForeColor = snapshot.PeerConnected ? Color.FromArgb(19, 127, 77) : Color.FromArgb(171, 103, 23);
                } else { UpdateSecondaryUsb(); }
                if (monitorError != usbStatus.Text) { Log(usbStatus.Text); monitorError = usbStatus.Text; }
            } else {
                latestUsb = new UsbSnapshot();
                usbStatus.Text = "USB 服务尚未就绪 · 自动重试中"; usbStatus.ForeColor = Color.FromArgb(171, 103, 23);
                string error = "USB 自动重连：" + result.UsbError.Message;
                if (monitorError != error) { Log(error); monitorError = error; }
            }
            SetFileStatus(result.FileError);
            if (result.BoardError != null) { boardServiceError = result.BoardError.GetBaseException().Message; LogChangedBoardError(); }
            else boardServiceError = "";
        }
        void StartFiles() {
            if (receiver != null && receiver.Running) return;
            if (receiver != null) { receiver.Dispose(); receiver = null; }
            Sys.Firewall("USBRemotePublic-FileTransfer-" + Program.Role, cfg.OwnIp, cfg.RemoteIp, cfg.TransferPort);
            var next = new TransferServer(cfg.OwnIp, cfg.RemoteIp, cfg.TransferPort, cfg.Inbox, cfg.PairKey, Log);
            next.ControlHandler = HandleControl; next.ControlCompleted = CompleteNetworkControl;
            try { next.Start(); receiver = next; } catch { next.Dispose(); throw; }
        }
        void SetFileStatus(Exception error) {
            if (error == null) { filesStatus.Text = "文件接收已开启 · 对端可发送到此电脑"; filesStatus.ForeColor = Color.FromArgb(19, 127, 77); fileMonitorError = ""; }
            else { filesStatus.Text = "文件接收未开启 · 自动重试中"; filesStatus.ForeColor = Color.FromArgb(171, 103, 23); if (fileMonitorError != error.Message) { Log("文件服务启动失败：" + error.Message); fileMonitorError = error.Message; } }
        }
        async Task MonitorUsb() {
            if (operation || preview || monitoring || !recovery.Enabled) return;
            monitoring = true;
            await usbGate.WaitAsync();
            try {
                if (operation || !recovery.Enabled) return;
                await RecoverOnce();
            } catch (Exception e) { Log("服务检查失败：" + e.Message); }
            finally { usbGate.Release(); monitoring = false; }
        }
        async Task StopAll() {
            if (operation || preview) return;
            Buttons(true); recovery.Stop();  monitor.Stop(); statusTimer.Stop(); peerTracker.Clear(); latestUsb = new UsbSnapshot();
            try {
                if (sendCancel != null) sendCancel.Cancel();
                if (sendTask != null) { try { await sendTask; } catch {} }
                await usbGate.WaitAsync();
                StopBoard();
                if (receiver != null) { receiver.Dispose(); receiver = null; }
                try { await Task.Run(() => {
                    try { usb.Stop(); } catch (TimeoutException) { if (Process.GetProcessesByName("vhui64").Length > 0) throw; }
                    finally { network.Dispose(); }
                }); } finally { usbGate.Release(); }
                usbStatus.Text = "USB 服务已关闭"; filesStatus.Text = "文件接收已关闭 · 临时数据保留供续传"; usbStatus.ForeColor = Color.FromArgb(80, 91, 107); filesStatus.ForeColor = usbStatus.ForeColor;
                deviceList.Items.Clear(); Log("一键关闭完成。");
                boardStatus.Text = "板卡 SSH 转发已关闭";
            } catch (Exception e) { Log("关闭未完全完成：" + e.GetBaseException().Message); usbStatus.Text = "关闭未完成，请查看日志"; }
            finally { Buttons(false); }
        }
        void PopulateDevices(List<UsbDevice> devices) {
            deviceList.Items.Clear(); foreach (var d in devices) deviceList.Items.Add(d);
            if (devices.Count > 0) { int known = devices.FindIndex(x => x.Serial == cfg.DeviceSerial && x.Vendor == cfg.DeviceVendor && x.Product == cfg.DeviceProduct); deviceList.SelectedIndex = known >= 0 ? known : 0; }
        }
        async Task RefreshDevices() {
            if (operation || preview) return;
            try { var devices = await Task.Run(() => usb.Devices()); PopulateDevices(devices); Log("设备列表已更新，共 " + devices.Count + " 个设备。"); } catch (Exception e) { Log(e.GetBaseException().Message); }
        }
        async Task UseSelected() {
            var device = deviceList.SelectedItem as UsbDevice;
            if (device == null || operation || preview) return;
            Buttons(true);
            await usbGate.WaitAsync();
            try { await Task.Run(() => usb.Use(device)); cfg.DeviceSerial = device.Serial; cfg.DeviceVendor = device.Vendor; cfg.DeviceProduct = device.Product; cfg.Save(); usbStatus.Text = "下载器已接入 · 可以使用 Vivado / PDS"; }
            catch (Exception e) { Log(e.GetBaseException().Message); }
            finally { usbGate.Release(); Buttons(false); }
        }
        void Add(TransferItem item) {
            if (queue.Any(x => x.Source == item.Source && x.Relative == item.Relative)) return;
            if (!item.Directory && queue.Any(x => x.Relative.Equals(item.Relative, StringComparison.OrdinalIgnoreCase) && x.Source != item.Source)) {
                string dir = Path.GetDirectoryName(item.Relative), name = Path.GetFileNameWithoutExtension(item.Relative), ext = Path.GetExtension(item.Relative);
                for (int i = 2; ; i++) {
                    string alternative = Path.Combine(dir ?? "", name + " (" + i + ")" + ext);
                    if (!queue.Any(x => x.Relative.Equals(alternative, StringComparison.OrdinalIgnoreCase))) { item.Relative = alternative; break; }
                }
            }
            queue.Add(item); var row = new ListViewItem(item.Relative); row.SubItems.Add(item.Directory ? "文件夹" : Size(item.Size)); row.SubItems.Add(item.State); rows[item] = row; fileList.Items.Add(row);
        }
        void AddFiles() {
            if (sendTask != null && !sendTask.IsCompleted) return;
            using (var dialog = new OpenFileDialog { Multiselect = true, Title = "选择发送到对端的文件" }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (string path in dialog.FileNames) Add(new TransferItem { Source = path, Relative = Path.GetFileName(path), Size = new FileInfo(path).Length });
            }
            UpdateQueue();
        }
        async Task AddFolder() {
            if (sendTask != null && !sendTask.IsCompleted) return;
            using (var dialog = new FolderBrowserDialog { Description = "选择文件夹；包含子文件夹、空文件夹和所有普通文件" }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try {
                    if (queue.Any(x => x.Directory && x.Source == dialog.SelectedPath.TrimEnd('\\'))) { Log("该文件夹已经在队列中。需要重新选择时请先清空队列。"); return; }
                    var items = await Task.Run(() => TransferSender.FolderPlan(dialog.SelectedPath));
                    string rootName = items[0].Relative, chosen = rootName;
                    for (int i = 2; queue.Any(x => x.Relative.Split('\\', '/')[0].Equals(chosen, StringComparison.OrdinalIgnoreCase)); i++) chosen = rootName + " (" + i + ")";
                    if (chosen != rootName) foreach (var item in items) item.Relative = chosen + item.Relative.Substring(rootName.Length);
                    foreach (var item in items) Add(item); UpdateQueue();
                }
                catch (Exception e) { MessageBox.Show(this, "添加未完成：" + e.GetBaseException().Message, "文件夹检查"); }
            }
        }
        void UpdateQueue() {
            long total = 0; foreach (var item in queue) total = checked(total + item.Size);
            queueStatus.Text = queue.Count(x => !x.Directory) + " 个文件 · " + Size(total) + " · 已校验完成 " + queue.Count(x => x.State == "完成" || x.State == "已存在，校验一致") + " 项";
        }
        public new static string Size(long bytes) {
            string[] names = { "B", "KB", "MB", "GB", "TB", "PB" }; double n = bytes; int i = 0; while (n >= 1024 && i < names.Length - 1) { n /= 1024; i++; } return n.ToString(i == 0 ? "0" : "0.00") + " " + names[i];
        }
        void TransferUpdate(TransferProgress update) {
            DateTime now = DateTime.UtcNow;
            if (lastPhase == update.Phase && (now - lastProgress).TotalMilliseconds < 150) return;
            lastProgress = now; lastPhase = update.Phase;
            Ui(() => {
                currentFile.Text = update.Phase + " · " + update.Item.Relative + " · " + Size(update.Position) + " / " + Size(update.Item.Size);
                progress.Value = update.Item.Size == 0 ? 0 : (int)Math.Min(1000, 1000.0 * update.Position / update.Item.Size);
                ListViewItem row; if (rows.TryGetValue(update.Item, out row)) row.SubItems[2].Text = update.Phase;
            });
        }
        async Task SendQueue() {
            if (preview || networkSwitching || sendTask != null && !sendTask.IsCompleted) return;
            var plan = queue.Where(x => x.State != "完成" && x.State != "已存在，校验一致").ToList();
            if (plan.Count == 0) { Log("发送队列为空，或所有文件已经校验完成。"); return; }
            if (receiver == null) { Log("请先一键开启，让两端的文件接收服务运行。"); return; }
            sendCancel = new CancellationTokenSource(); var token = sendCancel.Token;
            sender = new TransferSender(cfg.RemoteIp, cfg.TransferPort, cfg.PairKey, TransferUpdate);
            send.Enabled = false; cancel.Enabled = true;
            sendTask = Task.Run(() => {
                int failed = 0;
                foreach (var item in plan) {
                    if (token.IsCancellationRequested) break;
                    bool done = false;
                    for (int attempt = 0; attempt < 3 && !done; attempt++) {
                        try {
                            token.ThrowIfCancellationRequested();
                            var result = sender.Send(item, token);
                            item.State = result.Skipped ? "已存在，校验一致" : "完成"; done = true;
                            Log("发送完成 · " + item.Relative + (result.ResumeBytes > 0 ? " · 从 " + Size(result.ResumeBytes) + " 续传" : ""));
                            Ui(() => { ListViewItem row; if (rows.TryGetValue(item, out row)) { row.SubItems[2].Text = item.State; row.ForeColor = Color.FromArgb(19, 127, 77); } UpdateQueue(); });
                        } catch (Exception e) {
                            if (token.IsCancellationRequested) { item.State = "已中断，可续传"; Ui(() => { ListViewItem row; if (rows.TryGetValue(item, out row)) row.SubItems[2].Text = item.State; }); break; }
                            item.State = "未完成：" + e.GetBaseException().Message;
                            Log(item.Relative + " · " + item.State);
                            if (attempt < 2) { if (token.WaitHandle.WaitOne(1500)) break; }
                        }
                    }
                    if (!done && !token.IsCancellationRequested) { failed++; Ui(() => { ListViewItem row; if (rows.TryGetValue(item, out row)) { row.SubItems[2].Text = item.State; row.ForeColor = Color.FromArgb(180, 62, 62); } }); }
                }
                Ui(() => { currentFile.Text = token.IsCancellationRequested ? "传输已中断。再次开始发送，会核对断点并继续。" : failed == 0 ? "本次队列全部完成，接收端 SHA-256 已确认一致。" : failed + " 项未完成。再次开始发送只重试未完成项。"; progress.Value = failed == 0 && !token.IsCancellationRequested ? 1000 : progress.Value; });
            });
            try { await sendTask; } finally { send.Enabled = true; cancel.Enabled = false; sender = null; sendCancel.Dispose(); sendCancel = null; }
        }
        async Task EditSettings() {
            if (operation || networkSwitching) return;
            if (sendTask != null && !sendTask.IsCompleted) { MessageBox.Show(this, "请先完成或取消文件传输，再更改网络。", "连接与配对"); return; }
            var candidate = SettingsCopy.Clone(cfg); bool sync;
            using (var form = new SetupDialog(candidate, preview, Program.IsPrimary && recovery.Enabled)) {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                sync = form.SyncRequested;
            }
            if (preview) { SettingsCopy.Apply(candidate, cfg); return; }
            bool wasRunning = recovery.Enabled;
            networkSwitching = true; Buttons(false);
            try {
                if (sync) {
                    if (!Program.IsPrimary || !wasRunning) throw new IOException("请先在两端开启并连通，再同步网络；首次使用请导出配对文件。");
                    if (candidate.ManageEasyTier) EasyTierOptions.ResolveCore(candidate);
                    string payload = PairingCipher.Seal(JsonData.Write(PairingDocument.From(candidate)), cfg.PairKey);
                    string response = await Task.Run(() => AssistantClient.Request(cfg.RemoteIp, cfg.TransferPort, cfg.PairKey, "assistant-network-v2", payload));
                    if (response != "network-staged-v2") throw new IOException("副机没有确认网络配置，请使用配对文件。");
                    Log("副机已收到加密网络设置，两端正在切换。若连接中断，请用导出的配对文件恢复。");
                }
                candidate.Save();
                if (wasRunning) await StopAll();
                SettingsCopy.Apply(candidate, cfg); UpdateNetworkAddress();
                if (wasRunning) await StartAll();
            } catch (Exception e) { MessageBox.Show(this, "设置未完成：" + e.GetBaseException().Message + "\r\n如副机已收到新配置，请导出并在两端导入同一配对文件。", "EasyTier 配置"); }
            finally { networkSwitching = false; Buttons(false); }
        }
        public void RenderPreview(string path) {
            Show(); Application.DoEvents();
            tabs.SelectedIndex = 2; PerformLayout(); Application.DoEvents();
            using (var image = new Bitmap(Width, Height)) { DrawToBitmap(image, new Rectangle(0, 0, Width, Height)); image.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
            Hide();
        }
        async void OnClosing(object s, FormClosingEventArgs e) {
            if (preview || closedReady) return;
            if (closing) { e.Cancel = true; return; }
            e.Cancel = true; closing = true;
            while (operation || networkSwitching) await Task.Delay(200);
            await StopAll(); monitor.Dispose(); statusTimer.Dispose(); closedReady = true; Close();
        }
    }
}
