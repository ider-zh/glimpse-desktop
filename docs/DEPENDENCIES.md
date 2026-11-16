# Runtime dependencies

- .NET 10 / WPF / Windows SDK projections: Microsoft runtime and SDK components; portable publish includes runtime files and the SDK-supplied third-party notices.
- Microsoft.Data.Sqlite 10.0.15: [upstream source and license](https://github.com/dotnet/efcore).
- SQLitePCLRaw transitive managed bindings; native `SQLitePCLRaw.lib.e_sqlite3` explicitly pinned to 3.53.3 to avoid the restore audit finding in the initial 2.1.11 native library: [upstream](https://github.com/ericsink/SQLitePCL.raw), [SQLite source](https://sqlite.org/).
- Windows.Media.Ocr is supplied by Windows and uses the user's installed OCR language capabilities. No model files are bundled.
- HTTP Agent adapters use the .NET HttpClient/runtime. CodeBuddy CLI is optional, supplied and authenticated by the user; it is not downloaded or bundled. Windows DPAPI uses OS protection APIs without adding a credential package.
- Microsoft.Web.WebView2 1.0.4314.35 hosts the login terminal. Its SDK/loader is packaged; Microsoft Edge WebView2 Runtime is a separate machine prerequisite, with basic-mode fallback if unavailable. [Official distribution documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).
- `@xterm/xterm` 6.0.0 and `@xterm/addon-fit` 0.11.0 (MIT) are bundled as local terminal assets with license notices and registry integrity records in `TerminalAssets/VENDOR.md`; no runtime npm/CDN access. [xterm.js upstream](https://github.com/xtermjs/xterm.js).
- xUnit, VSTest, and Microsoft.NET.Test.Sdk are development-only dependencies.

`dotnet list Glimpse.slnx package --include-transitive` lists the resolved dependency graph. Restore vulnerability auditing remains enabled and warnings are errors. No dependency advisory is suppressed. The repository's own license is still undecided.
