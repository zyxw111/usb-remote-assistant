# USB Remote Assistant

Windows 主机／副机远程开发助手：远程 USB 下载器、双向文件传输，以及通过副机有线网口访问 Linux 板卡的 SSH 转发。

公开通用版 **2.0.0**，适用于 Windows 10 / 11 x64 和 .NET Framework 4.8。源码与自行构建的 EXE 中没有预置网络 ID、IP、配对密钥、下载器序列号或用户目录。

```text
主机（开发软件） ── 局域网 / VPN ── 副机
                                    ├─ USB → JTAG / UART / 开发板
                                    └─ 网线 → Linux 板卡（SSH）
```

## 功能

- 主机端与副机端独立 EXE，一键开启／关闭、后台恢复连接。
- 通过 VirtualHere 接入远端 USB 设备；支持手动选择并记住下载器。
- 双向多文件和文件夹传输：64 位文件长度、分块和整文件 SHA-256、断点续传、同名冲突保留。
- Linux 板卡 SSH 转发：可配置地址、端口与用户名；两端同步配置，检测 SSH 标识，主机打开 SSH 终端。
- 副机区分“等待主机”“主机已连接”和“USB 设备已接入主机”，文件传输时仍可同步状态。
- 使用既有局域网／VPN；也可选择由助手管理已安装的 ZeroTier。

## 获取和首次使用

从维护者提供的 Release 包中分别解压 `UsbRemoteAssistant.Primary.exe`（主机端）和 `UsbRemoteAssistant.Secondary.exe`（副机端）。保留同名 `.exe.config`。

1. 确保两台电脑已经可以通过 IPv4 互访。不同地点可以先配置 VPN；两端不要求拥有公网 IP。
2. 首次打开任意一端，在“连接与配对”中填写主机、副机的 IPv4 地址。默认文件服务端口为 `17875`，USB 服务端口为 `7575`。
3. 点击“生成新密钥”，再“导出配对文件”，最后“保存配置”。每一对电脑应生成自己的密钥。
4. 将配对文件私下传到另一端，点击“导入配对文件”并保存。配对文件包含访问密钥，**不要上传仓库、Issue 或公开下载页**。
5. 两端“一键开启”，顺序不限。首次运行需要 Windows 管理员权限配置本机防火墙规则和所需服务。

配置和日志写入当前 Windows 用户的 `%LOCALAPPDATA%\UsbRemoteAssistant.Public\主机端` 或 `副机端`。程序不会自动读取解压目录中的私人 `settings.json`，也不会自动导入其他版本的配置。

### 远程 USB

USB 转发依赖 **VirtualHere**，它不是本项目的开源代码，也不包含在本项目的仓库或发布 ZIP 中。

- 主机从 [VirtualHere Client 官网](https://www.virtualhere.com/usb_client_software) 获取 Windows x64 Client。
- 副机从 [VirtualHere Server 官网](https://www.virtualhere.com/windows_server_software) 获取 Windows x64 Server。
- 在各自“连接与配对”中选择对应 EXE，也可以将原名程序放入助手旁的 `tools` 文件夹。
- USB 服务端口若改成非默认值，还需在 VirtualHere Server 中配置相同端口；助手不会自动修改第三方程序的设置。
- 启动后，主机刷新设备，选择目标并点击“接入所选”。之后在 Vivado、PDS 或串口工具中按本地设备使用。
- 第三方驱动安装和许可由 VirtualHere 自行管理。其 Windows 试用版同时共享一个设备，限制以[官方网站](https://www.virtualhere.com/windows_server_software)为准。

不安装 VirtualHere 时，文件传输和板卡 SSH 功能仍可独立工作；USB 页会提示缺少依赖。设备兼容性和实时性需针对实际下载器、驱动与网络验证。

如果客户端或 Server 在助手启动前已存在，助手不会全局关闭它；关闭时只释放本次由助手接入的设备。使用自己维护的独立客户端时，仍请自行管理它的其他设备和连接。

### 文件传输

在任意一端添加文件或文件夹并发送。默认接收目录为用户下载目录下的 `USB远程传输\收到的文件`，可在设置中修改。

接收端将文件落盘、重新读取并核对完整 SHA-256 后才报告成功。相同文件跳过，同名不同内容保留两份。中断后重新发送同一文件，可核对已有前缀并继续。大文件最终校验仍需要时间，请等待“完成”。

### 通过网线连接 Linux 板卡

1. 副机网口与板卡 IP 应在同一网段，板卡需要已启动 SSH 服务。
2. 在“板卡 SSH”中填写板卡地址、SSH 端口和账号，转发端口默认 `2222`。软件不内置任何板卡账号或密码。
3. 勾选开启转发，在主机“保存并同步到副机”，或直接在副机保存。
4. 点击“检测 SSH”，确认收到板卡 SSH 标识；再在主机“打开 SSH 终端”，由 SSH 客户端处理主机密钥和登录凭据。

板卡不必安装 VPN，也不必访问互联网。软件不会自动给网口分配 IP、修改板卡系统或猜测账号。SSH 断线后需要重新连接，关闭副机助手会关闭它创建的转发和会话。

### 可选 ZeroTier 管理

自行从 [ZeroTier 官网](https://www.zerotier.com/download/)安装并加入自己的网络。如果希望助手启动和检查 ZeroTier，在设置中勾选自动管理并填写自己的网络 ID；节点授权仍由网络所有者完成。

不勾选时直接复用现有网络，不要求安装 ZeroTier。停止 ZeroTier 的选项仅在自动管理模式下可用，默认关闭。

## 从源码构建

在 Windows PowerShell 或 PowerShell 中运行：

```powershell
.\build.ps1 -RunTests
```

脚本使用 Windows .NET Framework 的 x64 C# 编译器，无 NuGet 或 Python 依赖。产物在 `.artifacts\bin\Primary` 和 `.artifacts\bin\Secondary`。也可用安装了 .NET Framework 4.8 Developer / Targeting Pack 的 Visual Studio 分别打开两个 `.csproj`。

完整文件协议回归（包含超过 4 GiB 的稀疏逻辑文件校验）使用：

```powershell
.\build.ps1 -FullTests
```

生成公开源码 ZIP、两端 EXE ZIP 和 SHA-256 清单：

```powershell
.\package.ps1
```

输出在 `.artifacts\release`。打包脚本使用明确的文件清单，不打包用户配置、配对文件、日志、测试临时数据、第三方 EXE 或 Git 历史。详细说明见 [发布指南](docs/RELEASING.md)。

## 验证范围与连接安全

自动测试覆盖配对文件和默认配置、断线恢复、状态过期、SSH 双向字节转发及半关闭、来源 IP 限制、端口冲突、活动会话关闭，以及文件续传和校验。超过 4 GiB 的测试使用稀疏文件前缀，不等同于已经跨网络传输了完整 4 GiB 数据。实际 FPGA 编程、串口、特定 VPN 和 Linux 登录仍需实机测试。

文件／状态通道执行配对密钥双向认证，绑定指定本机地址并限制对端 IP；**本项目不自行加密文件载荷**。跨不可信网络应使用可信 VPN，不要直接向公网暴露这些服务。SSH 本身的登录和加密由 SSH 客户端与板卡完成。

日志可能包含运行设备的 IP、路径和文件名。提交问题前应脱敏；不要提交密钥、配对文件、私人文件或未经检查的日志。详见 [安全与隐私](SECURITY.md)。

## 许可

本项目原创代码使用 [MIT License](LICENSE)。VirtualHere、ZeroTier、Windows OpenSSH 和 .NET Framework 均由各自许可约束；MIT 不适用于这些第三方程序。见 [第三方说明](THIRD_PARTY_NOTICES.md)。
