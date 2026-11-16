# Runtime dependencies

- .NET 10 / WPF / Windows SDK projections: Microsoft runtime and SDK components; portable publish includes runtime files and the SDK-supplied third-party notices.
- Microsoft.Data.Sqlite 10.0.15: [upstream source and license](https://github.com/dotnet/efcore).
- SQLitePCLRaw transitive managed bindings; native `SQLitePCLRaw.lib.e_sqlite3` explicitly pinned to 3.53.3 to avoid the restore audit finding in the initial 2.1.11 native library: [upstream](https://github.com/ericsink/SQLitePCL.raw), [SQLite source](https://sqlite.org/).
- Windows.Media.Ocr is supplied by Windows and uses the user's installed OCR language capabilities. No model files are bundled.
- xUnit, VSTest, and Microsoft.NET.Test.Sdk are development-only dependencies.

`dotnet list Glimpse.slnx package --include-transitive` lists the resolved dependency graph. Restore vulnerability auditing remains enabled and warnings are errors. No dependency advisory is suppressed. The repository's own license is still undecided.
