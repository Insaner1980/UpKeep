# UpKeep

A compact, on-demand Windows 11 tray utility for software updates and reviewed cleanup.

Requires Windows 11 24H2 or later, x64. Development uses .NET SDK 10.0.403 and Windows App SDK 2.6.4.

```powershell
dotnet restore UpKeep.slnx
dotnet build UpKeep.slnx -c Debug -p:Platform=x64
dotnet test tests/UpKeep.Tests/UpKeep.Tests.csproj -c Debug -p:Platform=x64 --filter 'Category!=LiveReadOnly&FullyQualifiedName!~UpKeep.Tests.NativeRecycleIntegrationTests'
```

The optional `tools/bc.ps1`, `db.ps1`, `dc.ps1`, `lc.ps1`, `ql.ps1`, `sc.ps1`, `ss.ps1`, and `tc.ps1` wrappers require the separate Windows-check checkout. By default they look for `Windows-check` beside this repository. Set `WINDOWS_CHECK_ROOT` to that checkout's directory when it is elsewhere. The direct `dotnet` commands above do not require Windows-check.

Publish a portable, self-contained x64 folder and ZIP (app and helper together):

```powershell
./scripts/Publish.ps1
```

Extract the entire ZIP, then start `UpKeep.App.exe`. Keep the helper and runtime files beside it. The 420 × 566 DIP panel opens above the taskbar at the monitor's right edge, clamped to the available work area. Click the tray icon to toggle it. Closing/hiding the panel preserves work; use the tray's **Exit** command to exit. Windows may initially place the icon in the notification-area overflow.

Use **Check for updates** and **Analyze** explicitly. No user maintenance is scheduled or performed at startup; only recognized abandoned UpKeep scratch files may be removed. Add project folders in Settings; the suggested Dev folder is unchecked until selected. Review individual results and confirmation text before maintenance. **Stop after current** does not terminate an active installer.

Installed catalogs and managers determine update availability. Native tools without a verified read-only latest-version check offer an explicit **Run updater**, excluded from **Select all updates**. Stopped WSL distributions are checked only after a separate confirmation; APT uses existing indexes and requires transaction review. TruffleHog without a verified owner is unsupported. VS Code extension scope is the editor's default profile. Missing/stale installations and failed sources remain unavailable, not up to date.

Cleanup uses a per-file analysis manifest, checks identities again, and never recursively deletes selected project roots. Project artifacts go to the Recycle Bin; old eligible temporary files are explicitly permanent. Logical bytes are not promised freed disk space. Managed caches and AVDs require separate selection. Unsupported npx/runtime/browser/internal cache cleanup stays protected. AVD size/image metadata is currently unavailable; active-emulator removal is blocked.

Runtime state is under `%LOCALAPPDATA%\UpKeep`: settings, one bounded operation summary, and owned temporary manifests. No diagnostic history, background polling, autostart registration, scheduled tasks, or notifications are installed.

Historical verification was recorded locally in `artifacts/tests/verification.md`. The ignored `artifacts/` directory and its reports are not included in this repository or a fresh checkout. The default test command above uses isolated fixtures and excludes live manager/catalog queries and the native Recycle Bin integration test. Running the full test project also queries installed managers/catalogs and recycles and restores an exact disposable test file; those checks require separate authorization. No real installed software was updated, live cache pruned, or user AVD removed during implementation.

Protected system-temp removal is disabled pending validation of the real elevated helper handshake. Remaining interactive scaling, high-contrast, Narrator, Explorer-restart and published-app UI checks are unverified because Computer Use was stopped by the user. These limitations are not passing checks.

Provider references: [WinGet COM interoperability](https://www.nuget.org/packages/Microsoft.WindowsPackageManager.ComInterop/1.29.380), [Windows App SDK deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-unpackaged-apps), [pip caching](https://pip.pypa.io/en/stable/topics/caching/), [Windows application registration](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration).
