# Local release workflow

This workflow creates local files only. It does not create a remote repository, authenticate to GitHub, push commits, or upload a release.

```powershell
./build.ps1 -FullTests
./package.ps1
```

The release directory contains:

- `UsbRemoteAssistant-2.0.1-source.zip`: source code, tests, build/package scripts, project files and public documentation.
- `UsbRemoteAssistant-2.0.1-Primary.zip`: the primary executable, application configuration and public documentation.
- `UsbRemoteAssistant-2.0.1-Secondary.zip`: the secondary executable, application configuration and public documentation.
- `SHA256SUMS.txt`: hashes for the ZIP files.

The source ZIP is assembled from an explicit allowlist, rather than recursively copying the working directory. The executable ZIPs are also assembled from explicit build outputs. No local `settings.json`, pairing documents, diagnostic logs, received files, VirtualHere/EasyTier binaries, or Git history are included.

Before manually publishing, check the staged repository content and ZIP entries, ensure examples are synthetic, and verify that third-party executables were not added. Build output is ignored by Git; release assets can be selected manually after review.

GitHub will show the identity of the account that publishes the repository and the authors of commits. This project cannot anonymize those account settings. There is no embedded repository URL, account name, contact address or personal copyright holder in the public files.

The EXEs are unsigned. A maintainer can independently arrange code signing; this build does not request, create or store a signing credential.
