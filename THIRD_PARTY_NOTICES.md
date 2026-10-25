# Third-party software

No third-party application executable, driver, credential, license key, or installer is distributed with this repository or its default release packages.

## VirtualHere

Optional dependency for USB redirection. This project communicates with the locally installed VirtualHere client through its documented API. VirtualHere is a separate product; this repository does not grant rights to redistribute it or alter its licensing restrictions.

- Client: https://www.virtualhere.com/usb_client_software
- Server: https://www.virtualhere.com/windows_server_software
- API: https://www.virtualhere.com/client_api
- Windows FAQ and license reference: https://www.virtualhere.com/windows_server_faq

Users should obtain the appropriate software from the official vendor and review the applicable license there.

## EasyTier

Optional, separately obtained network dependency, licensed by its authors under LGPL-3.0. This project launches an unmodified local `easytier-core.exe` with a generated configuration; it does not incorporate EasyTier source code or distribute EasyTier binaries, drivers, or installers. Keep the full official CLI package together when installing it locally.

- Downloads: https://easytier.cn/guide/download.html
- Source and license: https://github.com/EasyTier/EasyTier

The default peer is the EasyTier public shared node. Users may replace it with a node they operate or trust. No maintainer-owned server, network credentials, or account is embedded.

## Microsoft .NET Framework and Windows OpenSSH

The application targets .NET Framework 4.8 and can launch the user's installed Windows OpenSSH client. These components are supplied separately under their respective licenses. This project does not redistribute them.

The MIT license in this repository covers this project's original source code only. Product names are used to describe interoperability and do not imply endorsement.
