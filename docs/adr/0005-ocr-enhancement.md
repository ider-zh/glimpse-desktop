# ADR 0005: Adaptive small-text OCR enlargement

Date: 2026-11-16. Status: implemented, one-device rendered corpus measured.

Keep Windows.Media.Ocr. Add a native-size first pass, then retry at up to 2× only if median recognized word height is at most 14 physical pixels, or no words were recognized but sampled pixels show contrast. Uniform blanks do not require a second pass. Large glyphs retain native recognition because enlargement sometimes worsens them.

The bilinear preprocessing operates entirely in memory, supports cancellation, obeys the OCR dimension limit (application cap 8192), and caps enlarged output at 32 MiB. Already-large native images are not enlarged beyond that budget. Bounding boxes map back by actual horizontal/vertical scale, including dimension/budget-limited non-integer enlargement.

UI offers automatic enhancement, original size, and fixed 2×. The preference survives restart; `GLIMPSE_OCR_SCALE` accepts 0/1/2 respectively. CLI OCR accepts optional scale after language. The benchmark additionally evaluates 3×, which is not a normal UI option.

Rendered corpus v1: 12 labeled two-line cases (Chinese, mixed, English; fonts 10/12/16/24 at 96 DPI), 696 expected characters per mode. Initial native errors 161/696 (23.13%), fixed 2× 50/696 (7.18%), adaptive 49/696 (7.04%). CER removes whitespace and applies NFKC for measurement only; original OCR remains preserved. These are not real-page or universal accuracy claims. Chinese at 10 pixels remains difficult; enlarging the source app font may still be necessary. Fixed acceptance for this corpus is adaptive aggregate CER ≤10% and no worse than native, blank output empty, all boxes inside the original frame.
