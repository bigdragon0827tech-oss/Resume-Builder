# Resume JSON contract

This is the exact JSON ResumeBuilder consumes. A complete example lives next to this file in
[`gpt-resume-json-example.json`](gpt-resume-json-example.json).

The split of responsibilities is fixed:

| GPT decides | The renderer decides |
| --- | --- |
| Resume content | How a style token becomes DOCX or PDF formatting |
| A style configuration drawn from the tokens below | Word styles, twips, half-points, page setup |

GPT never sends OOXML, CSS, HTML, Markdown, python-docx or any other code, script or Word
instruction. Anything outside the tokens listed here is ignored (and reported) or rejected.

---

## 1. Content

Return **the profile object itself**, in a single ```json code block. The first six top-level
properties are required and must be spelled exactly like this:

```json
{
  "info": { "name": "", "title": "", "location": "", "email": "", "phone": "", "linkedin": "" },
  "summary": "",
  "skills": [ { "category": "", "skills": [] } ],
  "experience": [ { "title": "", "company": "", "startDate": "", "endDate": "", "location": "", "descriptionLines": [] } ],
  "certifications": [ { "name": "", "issuer": "", "date": "" } ],
  "education": [ { "degree": "", "major": "", "school": "", "startDate": "", "endDate": "" } ]
}
```

### Required

| Field | Type | Notes |
| --- | --- | --- |
| `info` | object | `name`, `title`, `location`, `email`, `phone`, `linkedin`, all strings |
| `summary` | string | One paragraph. Never a list, never an object |
| `skills[]` | array | Each item: `category` (string) and `skills` (array of strings) |
| `experience[]` | array | See below. Order and count are never changed by the app |
| `certifications[]` | array | Each item: `name`, `issuer`, `date` |
| `education[]` | array | Each item: `degree`, `major`, `school`, `startDate`, `endDate` |

Use an empty string or an empty array where the source does not support a value. Never invent
employers, dates, schools, contact details, certifications, technologies or metrics.

### Experience entries

| Field | Required | Notes |
| --- | --- | --- |
| `title` | yes | The role, on its own. Not combined with the company |
| `company` | yes | The employer, on its own |
| `startDate` | yes | For example `Sep 2025` |
| `endDate` | yes | For example `Present` |
| `location` | yes | May be an empty string |
| `employmentType` | optional | For example `Full-time`, `Contract` |
| `workArrangement` | optional | For example `Remote`, `Hybrid`, `On-site` |
| `subtitle` | optional | A project or product name, rendered as Heading 3 |
| `descriptionLines` | yes | Array of bullets — see section 2 |

**Locked metadata.** Never send a pre-combined heading string. The renderer builds it:

```
Company | Role | Start Date - End Date
Caterpillar Inc. | Senior AI Software Engineer | Sep 2025 - Present
```

and the line beneath it:

```
Location | Employment Type | Work Arrangement
Peoria, IL | Full-time | Hybrid
```

Empty values disappear together with their separator, so `California | Full-time |` can never occur.

---

## 2. Description lines (bullets)

A bullet is either a plain string:

```json
"descriptionLines": [
  "Architected production AI services handling 40 million daily inference requests."
]
```

or a segmented line, which is how inline emphasis is expressed:

```json
"descriptionLines": [
  {
    "segments": [
      { "text": "Architected production ", "bold": false },
      { "text": "Generative AI and RAG services", "bold": true },
      { "text": " using Python and AWS.", "bold": false }
    ]
  }
]
```

Rules:

- **Never use Markdown** (`**bold**`, `*italics*`, `#`) anywhere in resume text. Emphasis is
  structural so it can never be mistaken for content.
- Spacing inside `text` is preserved exactly — the segments are concatenated as written.
- Emphasis must be **sparse**. A bullet whose every segment asks for bold is rendered entirely
  regular.
- Emphasis is accepted **only** in experience bullets. The summary, the skill values, education and
  certifications always render at regular weight.

---

## 3. The style object

`style` is a **nested** object — the same `ResumeStyle` schema the renderers consume. There are no
flat style fields: `bodyFontSize`, `nameFontSize`, `metadataFontSize`, `fontFamily` or margins at the
style root do not exist and are ignored (or rejected by the strict check). A size is always set on
its own section, e.g. `style.body.fontSize`, `style.metadata.fontSize`.

- **Resume prompt mode:** `style` is **optional**. A profile without it renders with the `promV4.12`
  preset, which is exactly how every older profile rendered.
- **Normal prompt mode:** `style` is **required**. An answer without a top-level `style` object is
  invalid output: nothing is saved, no DOCX/PDF is generated, and the job goes down the ordinary
  ChatGPT retry path. The execution instruction Normal mode sends ends with a STYLE CONTRACT,
  generated from the same tables this page describes, that asks for the 9 pt body standard
  (`body`, `bullet`, `skillValues`, `education` all `fontSize: 9`).

GPT only sends the values it wants to change; everything else is inherited from the preset.

```json
"style": {
  "preset": "promV4.12",
  "colors": { "primary": "#17365D" }
}
```

### Top level

| Property | Values |
| --- | --- |
| `preset` | `promV4.12` (default), `compact`, `classic` |
| `page` | `size` (`LETTER`, `A4`), `marginTop`, `marginBottom`, `marginLeft`, `marginRight` — inches, 0.3–1.25 |
| `colors` | `primary`, `body`, `secondary` — `#RRGGBB` only |
| `fonts` | `family` — Arial, Calibri, Cambria, Garamond, Georgia, Helvetica, Tahoma, Times New Roman, Verdana |

A section that does not name its own `color` follows the palette: headings take `colors.primary`,
metadata and subtitles take `colors.secondary`, everything else takes `colors.body`.

### Text sections

`name`, `headline`, `contact`, `sectionHeading`, `skillCategory`, `skillValues`, `companyHeading`,
`subtitle`, `metadata`, `body`, `bullet`, `education`.

Every section accepts: `fontSize`, `bold`, `italic`, `color`, `alignment`, `spaceBefore`,
`spaceAfter`, `lineSpacing`. Some accept more:

| Section | Also accepts |
| --- | --- |
| `sectionHeading` | `uppercase`, `bottomBorder`, `keepWithNext` |
| `skillCategory`, `companyHeading` | `uppercase`, `keepWithNext` |
| `subtitle` | `keepWithNext` |
| `skillValues` | `leftIndent` |
| `bullet` | `leftIndent`, `hangingIndent` |

Units: `fontSize`, `spaceBefore` and `spaceAfter` are points; `leftIndent` and `hangingIndent` are
inches; `lineSpacing` is a multiple; `alignment` is `left`, `center`, `right` or `justify`.

`uppercase` controls the section headings, which are stored as *Professional Summary*, *Technical
Skills*, *Professional Experience*, *Certifications* and *Education*. Every preset upper-cases them;
setting `uppercase: false` keeps the title case.

### Ranges and hard rules

| Value | Allowed |
| --- | --- |
| Any `fontSize` | 8–24 pt |
| `body`, `bullet`, `education`, `skillValues` `fontSize` | **≥ 9 pt** (the presets still use 11 / 10.5) |
| `lineSpacing` | **≥ 1.0** (max 3.0) |
| `spaceBefore`, `spaceAfter` | 0–48 pt |
| `leftIndent`, `hangingIndent` | 0–1.5 in |
| Page margins | 0.3–1.25 in |
| Colours | `#RRGGBB` |

Font sizes are best given in multiples of 0.5 pt. The DOCX stores a size in whole half points, so
8.7 pt is written as 8.5 pt in the DOCX while the PDF keeps 8.7 pt.

These always hold, whatever the style asks for:

- The professional summary is regular weight (`body` style) with no inline bold.
- Skill categories may be bold; the skill values themselves are always regular weight.
- Experience bullets may carry sparse inline bold; a fully bold bullet is demoted to regular.
- Education is ordinary body text with no keyword emphasis.
- Single column only.

### What is not supported

No text boxes, sidebars, icons, skill bars, graphics, layout tables, colored backgrounds, gradients,
columns, images, headers/footers, borders other than the section-heading rule, or any second font.
There is no way to express them in this schema, and any attempt is ignored or rejected.

---

## 4. What happens to an out-of-range value

Two different paths, deliberately:

| Path | Behaviour |
| --- | --- |
| A captured AI answer (the normal flow) | Values are **clamped** to the nearest allowed value and every correction is reported. A styling mistake never fails a job whose content is good. The corrections are written to `results\<jobId>.docgen.txt` |
| `CandidateProfileStore.ValidateResumeJson(json)` (the contract check, used by the tests) | The same values are **errors**, reported one per line: `style.body.fontSize must be >= 9 pt`, `style.colors.primary is not a valid hex color (#RRGGBB)`, `experience[0].endDate is missing` |

Either way nothing silently produces a corrupted resume.

---

## 4b. Which prompt was used

Resume Builder has two prompt modes, chosen in Settings → General:

- **Resume** sends the configured Master Prompt file.
- **Normal** sends your own prompt file, so it can contain any resume-writing instructions you like.

Either way Resume Builder appends the same COMPLETE JOB PAYLOAD (the job plus the full candidate
profile) and the same execution instructions, so **this contract applies in both modes**. Normal
mode's instructions differ in three places: the opening sentence ("the resume instructions above"
instead of the Master Prompt), the verification rule (which also requires `style`), and a STYLE
CONTRACT appended at the end. A Normal prompt therefore does not need to describe the JSON shape
itself. Resume mode's text is unchanged, byte for byte.

Style is optional in Resume mode (leave it out and the `promV4.12` preset is used) and **required in
Normal mode** (see section 3).

## 5. Output

Documents are written under the Resume Root Folder configured in Settings, organized by day and job:

```
ResumeRoot\2026-09-17\Caterpillar Inc - Senior AI Software Engineer\Resume.docx
                                                                   \Resume.pdf
                                                                   \resume-info.json
```

Nothing is overwritten: generating the same job again on the same day produces `Resume (2).docx`
and its matching set. `resume-info.json` records the job id, job URL, company, role, timestamp and
the filenames produced.

The tailored JSON is saved separately as `results\<jobId>.json`, and the fully resolved style is
written next to it as `results\<jobId>.effective-style.json` — that file shows exactly what the
preset, the overrides and the safety rules added up to.

The master resume (`candidate-profile.json`) is never written by document generation.
