using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace UsbRemoteAssistant {
    public sealed class SetupDialog : Form {
        readonly Settings original;
        readonly bool preview;
        Settings draft;
        TextBox primary, secondary, usbPort, filePort, networkName, password, key, core, peers, tool, inbox;
        CheckBox manage, auto;
        public bool SyncRequested { get; private set; }
        public SetupDialog(Settings cfg, bool isPreview, bool canSync = false) {
            original = cfg; preview = isPreview; draft = SettingsCopy.Clone(cfg);
            if (String.IsNullOrEmpty(draft.PairKey)) draft.PairKey = Settings.NewPairKey();
            if (String.IsNullOrEmpty(draft.NetworkSecret)) draft.NetworkSecret = EasyTierOptions.NewPassword();
            if (String.IsNullOrEmpty(draft.PrimaryIp)) draft.PrimaryIp = "10.144.144.1";
            if (String.IsNullOrEmpty(draft.SecondaryIp)) draft.SecondaryIp = "10.144.144.2";
            Text = "EasyTier 连接与配对 · " + Program.Role; Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(860, 760); MinimumSize = new Size(820, 700); StartPosition = FormStartPosition.CenterParent;
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 2 };
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); Controls.Add(outer);
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; outer.Controls.Add(scroll, 0, 0);
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 0, 10, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 215)); scroll.Controls.Add(grid);
            int row = 0;
            AddWide(grid, new Label { Text = "自定义网络名，密码自动交给副机", Font = new Font(Font.FontFamily, 14F, FontStyle.Bold), Dock = DockStyle.Fill }, row++, 40);
            AddWide(grid, new Label { Text = "首次：填写网络名 → 导出配对文件 → 副机导入 → 两端一键开启。\r\n密码已随机生成，也可直接修改。首次未连通时需要私下传递配对文件；不会上传到云端。", Dock = DockStyle.Fill }, row++, 53);
            manage = new CheckBox { Text = "由助手启动 EasyTier（取消后复用已连接的 EasyTier / 局域网）", AutoSize = true }; AddWide(grid, manage, row++, 34);
            networkName = Field(grid, "网络名", row++, "每次新建网络由你填写");
            password = Field(grid, "网络密码", row, "随机生成或自行输入"); password.UseSystemPasswordChar = true;
            var secretButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            secretButtons.Controls.Add(Action("随机生成", () => password.Text = EasyTierOptions.NewPassword()));
            var show = new CheckBox { Text = "显示", AutoSize = true, Margin = new Padding(7, 9, 0, 0) }; show.CheckedChanged += (s, e) => password.UseSystemPasswordChar = !show.Checked;
            secretButtons.Controls.Add(show); grid.Controls.Add(secretButtons, 2, row++);
            core = Field(grid, "EasyTier 核心", row, "选择 easytier-core.exe；留空时从 tools 或 PATH 查找");
            var coreButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            coreButtons.Controls.Add(Action("选择程序", PickCore)); coreButtons.Controls.Add(Action("官网下载", () => Process.Start("https://easytier.cn/guide/download.html"))); grid.Controls.Add(coreButtons, 2, row++);
            peers = Field(grid, "共享节点", row++, "多个地址用空格分隔"); grid.SetColumnSpan(peers, 2);
            AddWide(grid, new Label { Text = "默认使用 EasyTier 公共共享节点，可替换为自己的节点。\r\n核心需来自 Windows x64 命令行完整包；保留 DLL/驱动，不要选择 GUI 程序。", Dock = DockStyle.Fill }, row++, 47);
            primary = Field(grid, "主机虚拟 IPv4", row++, ""); secondary = Field(grid, "副机虚拟 IPv4", row++, "");
            usbPort = Field(grid, "USB 服务端口", row++, ""); filePort = Field(grid, "文件服务端口", row++, "");
            key = Field(grid, "应用配对密钥", row, "独立于 EasyTier 网络密码"); key.ReadOnly = true; key.UseSystemPasswordChar = true;
            grid.Controls.Add(Action("重新生成密钥", () => key.Text = Settings.NewPairKey()), 2, row++);
            tool = Field(grid, "VirtualHere", row, "可选；USB 功能需单独获取");
            var vh = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            vh.Controls.Add(Action("选择程序", () => { using (var pick = new OpenFileDialog { Filter = "Windows 程序|*.exe", Title = Program.IsPrimary ? "选择 VirtualHere Client" : "选择 VirtualHere Server" }) if (pick.ShowDialog(this) == DialogResult.OK) tool.Text = pick.FileName; }));
            vh.Controls.Add(Action("官网下载", () => Process.Start(Program.IsPrimary ? "https://www.virtualhere.com/usb_client_software" : "https://www.virtualhere.com/windows_server_software"))); grid.Controls.Add(vh, 2, row++);
            inbox = Field(grid, "接收目录", row++, "留空时使用用户下载目录"); grid.SetColumnSpan(inbox, 2);
            auto = new CheckBox { Text = "下次打开软件后自动开启服务", AutoSize = true }; AddWide(grid, auto, row++, 32);
            AddWide(grid, new Label { Text = "虚拟 IP 使用同一 /24 网段，并避开现有网段。更改网络名/密码后需同步或重新导入。\r\n关闭助手会停止它创建的 EasyTier；独立 GUI/系统服务不会被关闭。USB 非默认端口需同时配置 VirtualHere Server。", Dock = DockStyle.Fill }, row++, 62);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            buttons.Controls.Add(Action("保存配置", () => Save(false)));
            if (Program.IsPrimary) { var sync = Action("同步副机并切换", () => Save(true)); sync.Enabled = canSync; buttons.Controls.Add(sync); }
            buttons.Controls.Add(Action("导出配对文件", Export)); buttons.Controls.Add(Action("导入配对文件", Import));
            buttons.Controls.Add(Action("取消", () => { DialogResult = DialogResult.Cancel; Close(); })); outer.Controls.Add(buttons, 0, 1);
            manage.CheckedChanged += (s, e) => { networkName.Enabled = password.Enabled = core.Enabled = peers.Enabled = manage.Checked; }; LoadFields();
        }
        void AddWide(TableLayoutPanel grid, Control control, int row, int height) { grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); grid.Controls.Add(control, 0, row); grid.SetColumnSpan(control, 3); }
        TextBox Field(TableLayoutPanel grid, string label, int row, string hint) {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 39)); grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 9, 0, 0) }, 0, row);
            var field = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 7, 8, 5) }; grid.Controls.Add(field, 1, row); new ToolTip().SetToolTip(field, hint); return field;
        }
        Button Action(string label, System.Action callback) {
            var button = new Button { Text = label, AutoSize = true, MinimumSize = new Size(86, 30), Margin = new Padding(2, 3, 2, 3) };
            button.Click += (s, e) => { try { callback(); } catch (Exception ex) { MessageBox.Show(this, ex.GetBaseException().Message, "配置未完成"); } }; return button;
        }
        void PickCore() { using (var pick = new OpenFileDialog { Filter = "EasyTier 核心|easytier-core.exe", Title = "选择命令行包中的 easytier-core.exe" }) if (pick.ShowDialog(this) == DialogResult.OK) core.Text = pick.FileName; }
        void LoadFields() {
            primary.Text = draft.PrimaryIp; secondary.Text = draft.SecondaryIp; usbPort.Text = draft.UsbPort.ToString(); filePort.Text = draft.TransferPort.ToString();
            networkName.Text = draft.NetworkName; password.Text = draft.NetworkSecret; core.Text = draft.EasyTierPath; peers.Text = draft.EasyTierPeers;
            manage.Checked = draft.ManageEasyTier; networkName.Enabled = password.Enabled = core.Enabled = peers.Enabled = manage.Checked;
            key.Text = draft.PairKey ?? ""; tool.Text = draft.VirtualHerePath; inbox.Text = draft.ReceiveFolder; auto.Checked = draft.AutoStart;
        }
        Settings ReadFields() {
            int usb, files; if (!Int32.TryParse(usbPort.Text, out usb) || !Int32.TryParse(filePort.Text, out files)) throw new IOException("端口应为整数。");
            var candidate = ReadLocalDraft(); candidate.PrimaryIp = primary.Text.Trim(); candidate.SecondaryIp = secondary.Text.Trim(); candidate.UsbPort = usb; candidate.TransferPort = files;
            candidate.ManageEasyTier = manage.Checked; candidate.NetworkName = networkName.Text.Trim(); candidate.NetworkSecret = password.Text; candidate.EasyTierPeers = peers.Text.Trim(); candidate.PairKey = key.Text;
            candidate.Validate(); return candidate;
        }
        void Export() {
            var candidate = ReadFields(); using (var pick = new SaveFileDialog { FileName = "usb-remote-easytier.pairing.json", Filter = "配对文件|*.pairing.json" })
                if (pick.ShowDialog(this) == DialogResult.OK) File.WriteAllText(pick.FileName, JsonData.Write(PairingDocument.From(candidate)), new UTF8Encoding(false));
        }
        void Import() {
            using (var pick = new OpenFileDialog { Filter = "配对文件|*.pairing.json|JSON 文件|*.json" }) {
                if (pick.ShowDialog(this) != DialogResult.OK) return;
                var candidate = ReadLocalDraft(); PairingDocument.Load(pick.FileName).ApplyTo(candidate); draft = candidate; LoadFields();
            }
        }
        Settings ReadLocalDraft() { var result = SettingsCopy.Clone(draft); result.EasyTierPath = core.Text.Trim().Trim('"'); result.VirtualHerePath = tool.Text.Trim().Trim('"'); result.ReceiveFolder = inbox.Text.Trim(); result.AutoStart = auto.Checked; return result; }
        void Save(bool sync) { var candidate = ReadFields(); SettingsCopy.Apply(candidate, original); SyncRequested = sync; DialogResult = DialogResult.OK; Close(); }
        public void RenderPreview(string path) {
            Show(); Application.DoEvents(); using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); } Hide();
        }
        protected override bool ShowWithoutActivation { get { return preview; } }
    }
}
