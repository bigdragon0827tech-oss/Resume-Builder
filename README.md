# Resume Builder A6.6.9

Based on A6.6.8. Everything A6.6.6 (normalization), A6.6.7 (clipboard) and A6.6.8 (AI round trip)
does is unchanged.

## Change in A6.6.9 — automatic DOCX/PDF generation

When a tailored profile is captured and validated, the resume documents are now produced
automatically. No extra click, no Word, no Office automation.

```
results\<jobId>.json  ->  ResumeDocument  ->  <Company> - <Role>.docx   (DocumentFormat.OpenXml)
                                          ->  <Company> - <Role>.pdf    (WebView2 PrintToPdf)
```

- **Output folder:** the Resume Root Folder already configured in Settings.
- **Filenames:** `<Company> - <Role>.docx` / `.pdf`, nothing appended. Regenerating overwrites.
- **Checkboxes:** the existing DOCX / PDF checkboxes decide what is produced. Both off is reported as
  "documents disabled", not a failure.
- **DOCX** is built with Microsoft's Open XML SDK — a real `.docx` with proper paragraphs, so ATS
  parsers read it. **PDF** is printed by the WebView2 that the app already uses, from the same content
  model, so both documents always carry identical content.
- `candidate-profile.json` is never touched. Generation only ever *reads* the validated per-job JSON.

### If document generation fails

The job stays **✓ Completed** — the validated JSON is saved before generation starts and is never
affected by a document problem. The failure is reported on its own line (DOCX failed / PDF failed /
documents disabled / Resume Root not configured), written to `results\<jobId>.docgen.txt`, and
**Generate Documents** regenerates from the saved JSON once the cause is fixed (for example, closing
the file in Word). Documents are written to a temp file and moved into place, so an interrupted run
never leaves a half-written resume in your Resume Root.

### Also in A6.6.9

The manual fallback now routes like automatic capture: **Settings → Result → Validate + Save** writes
`results\<jobId>.json` when a job is pending and only writes `candidate-profile.json` for the BASELINE
flow — using the fallback can no longer overwrite the baseline every future job is tailored from. It
generates documents afterwards too.

## Unchanged

- Prepare & Send, armed clipboard capture, normalize -> strict validate -> save, and every A6.6.6
  output variation still handled.
- Strict validation is not weakened; nothing is invented; experience and education keep their count
  and order.
- The Master Prompt is still read from the external file configured in Settings; the job payload is
  still built with `System.Text.Json`; the canonical schema is unchanged.
- The manual Settings → Result path remains the fallback for every automation failure.

## Test

1. `dotnet clean`
2. `dotnet build`  (expect 0 errors, 0 warnings)
3. `dotnet run`
4. Prepare & send a job, press Enter, click ChatGPT's Copy button.
5. The job goes Completed and the DOCX/PDF appear in your Resume Root as `<Company> - <Role>`.
