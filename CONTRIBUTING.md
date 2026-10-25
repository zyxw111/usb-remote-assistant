# Contributing

1. Use Windows 10/11 x64 with .NET Framework 4.8.
2. Run `./build.ps1 -RunTests` for configuration, recovery, relay and control tests.
3. Run `./build.ps1 -FullTests` when changing the transfer protocol or file integrity behavior. The large-file case uses NTFS sparse files.
4. Keep runtime configuration, pairing documents, third-party executables, downloaded files and local logs out of commits and release bundles.
5. Use documentation/test addresses and synthetic device identifiers in examples. Do not add working shared secrets.
6. Describe changes and relevant checks in pull requests. Physical hardware compatibility must be reported separately from loopback tests.

The build does not require third-party applications. Obtain the official EasyTier CLI package separately for managed network use, and VirtualHere for USB use. Tests do not start VirtualHere, change firewall rules, or program devices.

The optional official-core smoke test is documented in docs/VALIDATION.md; it uses no TUN interface and only a loopback peer.
