# Bundled terminal dependencies

The terminal loads these local files only; no CDN or runtime npm installation is used.

- `@xterm/xterm` **6.0.0**, MIT, official npm archive `https://registry.npmjs.org/@xterm/xterm/-/xterm-6.0.0.tgz`.
  Registry SHA-512 integrity: `sha512-TQwDdQGtwwDt+2cgKDLn0IRaSxYu1tSUjgKarSDkUM0ZNiSRXFpjxEsvc/Zgc5kq5omJ+V0a8/kIM2WD3sMOYg==`.
  Files: `vendor/xterm.js`, `vendor/xterm.css`, `vendor/xterm-LICENSE.txt`.
- `@xterm/addon-fit` **0.11.0**, MIT, official npm archive `https://registry.npmjs.org/@xterm/addon-fit/-/addon-fit-0.11.0.tgz`.
  Registry SHA-512 integrity: `sha512-jYcgT6xtVYhnhgxh3QgYDnnNMYTcf8ElbxxFzX0IZo+vabQqSPAjC3c1wJrKB5E19VwQei89QCiZZP86DCPF7g==`.
  Files: `vendor/addon-fit.js`, `vendor/addon-fit-LICENSE.txt`.

Both downloaded archives were verified against these registry integrity values before copying their distribution assets. Source maps are not bundled.

The WPF host uses Microsoft.Web.WebView2 **1.0.4314.35**, restored by NuGet. Microsoft Edge WebView2 Runtime is a separate machine prerequisite; the app presents a plain-text fallback when unavailable.
