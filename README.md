# USB Remote Assistant

Windows 主机／副机远程开发助手：远程 USB 下载器、双向文件传输，以及通过副机有线网口访问 Linux 板卡的 SSH 转发。

公开通用版 **2.0.1（2.0 EasyTier 版）**，适用于 Windows 10 / 11 x64 和 .NET Framework 4.8。源码与自行构建的 EXE 中没有预置私人网络名、网络密码、配对密钥、下载器序列号或用户目录。

```text
主机（开发软件） ── EasyTier / 局域网 ── 副机
                                    ├─ USB → JTAG / UART / 开发板
                                    └─ 网线 → Linux 板卡（SSH）
```

## 功能

- 主机端与副机端独立 EXE，一键开启／关闭、后台恢复连接。
- 通过 VirtualHere 接入远端 USB 设备；支持手动选择并记住下载器。
- 双向多文件和文件夹传输：64 位文件长度、分块和整文件 SHA-256、断点续传、同名冲突保留。
- Linux 板卡 SSH 转发：可配置地址、端口与用户名；两端同步配置，检测 SSH 标识，主机打开 SSH 终端。
- 副机区分“等待主机”“主机已连接”和“USB 设备已接入主机”，文件传输时仍可同步状态。
- 由助手启动／关闭 EasyTier 核心，自定义网络名，密码随机生成或自行填写；首次配对导入，连接后加密同步并切换。
- 可取消网络管理以复用已有 EasyTier 或局域网；不绑定维护者的服务器、账号或网络。

## 获取和首次使用

下载 **2.0.1 EasyTier 版**：

- [主机端压缩包](UsbRemoteAssistant-2.0.1-Primary.zip)
- [副机端压缩包](UsbRemoteAssistant-2.0.1-Secondary.zip)
- [源码压缩包](UsbRemoteAssistant-2.0.1-source.zip)
- [SHA-256 校验清单](SHA256SUMS.txt)

分别解压 `UsbRemoteAssistant.Primary.exe`（主机端）和 `UsbRemoteAssistant.Secondary.exe`（副机端），保留同名 `.exe.config`。仓库中的旧版 2.0.0 压缩包供历史版本使用；首次使用请选择上面的 2.0.1 包。

1. 两端分别从 [EasyTier 官网](https://easytier.cn/guide/download.html)获取 Windows x64 **命令行完整包**，解压并保留核心程序同目录的 DLL／驱动。在各自“连接与配对”选择 `easytier-core.exe`；也可将完整包解压到助手旁的 `tools`。不需要安装或加入其他组网软件。
2. 主机填写自己的**网络名**。软件首次自动生成随机网络密码，也可自行输入（8–256 个字符），或点击“随机生成”重新生成。保存后会复用该密码，不会每次启动悄悄更换。
3. 默认虚拟 IPv4 为主机 `10.144.144.1`、副机 `10.144.144.2`，可调整为同一 /24 网段中不冲突的两个地址。两端使用相同共享节点，默认是 EasyTier 公共共享节点，也可以填自己的节点；未预置维护者服务器。
4. 主机“导出配对文件”后“保存配置”，把文件私下交给副机。在副机“导入配对文件”并选择其本地核心程序、保存。**文件自动带入网络名、网络密码、地址和应用配对密钥，副机无需手输密码**。首次未连通时无法直接远程送达，需先用聊天、U 盘等方式传一次文件；不要上传仓库、Issue 或公开下载页。
5. 两端“一键开启”，顺序不限。首次运行需要 Windows 管理员权限。助手会启动 EasyTier，等待网卡就绪，然后启动 USB／文件／板卡服务；对端晚启动会持续重试。关闭时停止本次由助手创建的网络。
6. USB 功能还需分别配置 VirtualHere，见下文；文件传输和 SSH 不依赖 VirtualHere。

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

### 更换网络与密码

新建网络时由你填写新的网络名；密码可重新随机生成，也可自行定义。已有连接时，在主机“连接与配对”修改后点 **同步副机并切换**：通过原有应用配对连接加密发送新网络信息，副机确认后两端保存并重启各自网络。不需要在副机复制密码；两端都应使用本 EasyTier 版，且副机已选好可运行的核心程序。

切换前请完成文件传输和硬件调试。更改后如未恢复连接，请从主机重新导出配对文件并在两端导入同一文件。未连接时直接使用配对文件完成更换。“保存配置”只改本机，“同步副机并切换”才会同时改另一端。

已有 EasyTier GUI／系统服务可在取消“由助手启动 EasyTier”后复用，填写它实际分配的两端 IPv4。助手不会关闭独立 GUI 或系统服务。自动管理时若同一虚拟 IP 已被其他网络占用，会提示关闭重复网络或改为复用；它不会擅自停止其他程序。

共享节点用于发现和可能的中继，能否直连取决于实际网络。默认使用公共共享节点，因此不要求你的云服务器参与；需要自己的节点时更换“共享节点”。组网连接使用 EasyTier 的加密功能。

本版已移除旧组网软件的网络 ID、服务管理与启停选项。旧格式配对文件需重新生成；本机收到旧配置时会要求完成 EasyTier 配置，不会继续启动旧组网软件。

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

本项目原创代码使用 [MIT License](LICENSE)。VirtualHere、EasyTier、Windows OpenSSH 和 .NET Framework 均由各自许可约束；MIT 不适用于这些第三方程序。见 [第三方说明](THIRD_PARTY_NOTICES.md)。
