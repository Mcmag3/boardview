# BoardviewBuilder — AI Session Handoff

> **Purpose of this doc**: catch a fresh AI assistant (or human) up on what
> has been done, why we're doing it, what's broken, and exactly what to
> implement next. Read it top to bottom before touching code.

---

## 1. The overall goal

The user wants to turn **raster schematics + PCB photos** into a `.brd`
file that can be opened in **FlexBV** (a boardview viewer). The existing
project (`BoardviewBuilder`, WinForms .NET app) already handles the
"CSV → BRD" path; the new work is the **"Schematic image → Netlist → BRD"**
path.

User asked us to do this incrementally — one symbol class at a time —
and eventually add a **PDF parser** so multi-page schematic PDFs can be
fed in.

---

## 2. Project layout (today)

```
boardview/
├── example.brd / example_x10.brd        ← reference output files
├── README.md
├── HANDOFF.md                            ← this file
└── BoardviewBuilder/                     ← the .NET 8 WinForms app
    ├── BoardviewBuilder.csproj
    ├── Program.cs                        ← entry point (standard WinForms)
    ├── MainForm.cs                       ← two-tab UI (CSV→BRD, Schematic→Netlist)
    ├── Models.cs                         ← BoardModel / Part / Pin / Net / Nail
    ├── CsvLoader.cs                      ← parts.csv / pins.csv / nets.csv → BoardModel
    ├── BrdGenerator.cs                   ← BoardModel → .brd text
    │
    ├── SchematicImageLoader.cs           ← jpg/png → Bitmap + runs OCR/trace pipeline
    ├── ImageAdjustments.cs               ← brightness/contrast/threshold/rotate
    ├── OcrEngine.cs                      ← Tesseract wrapper (tessdata/ has eng.traineddata)
    ├── tessdata/                         ← Tesseract language data
    ├── Netlist.cs                        ← Component / Pin / Net data classes
    ├── NetlistTextFormat.cs              ← parse/format human-editable netlist text
    ├── WireTracer.cs                     ← flood-fill wires, attach pins, build Netlist
    ├── SymbolDetector.cs                 ← geometric matchers (R rect/zigzag, C plate-pair)
    └── LabelEditor.cs                    ← (NEW) click-drag dataset labelling UI
```

The app is built with `dotnet build` from inside `BoardviewBuilder/`. Run
with `dotnet run --project BoardviewBuilder` from the repo root.

---

## 3. What works today

1. **CSV → BRD tab** — fully working. Loads `parts.csv`, `pins.csv`,
   `nets.csv` (+ optional `outline.csv`, `nails.csv`) from a folder, builds
   `BoardModel`, generates `.brd`, lets the user save it. Untouched by
   recent work.

2. **Schematic → Netlist tab** — partially working:
   - Load a raster schematic image.
   - Image-adjustment row: grayscale, invert, brightness, contrast,
     threshold, rotate-90 — applied to a copy, original is preserved.
   - "Extract from image" runs:
     - `OcrEngine` (Tesseract) → recognised text words with bboxes.
     - Classifies words into reference designators (`R1`, `C12`, `U3`, …)
       and net labels (`VCC`, `GND`, …) using regexes.
     - `WireTracer` flood-fills the connected component near each
       designator to find which wires touch the symbol.
     - `SymbolDetector` runs a per-letter matcher to draw a tight bbox
       around the actual symbol shape (currently: **resistors only** —
       rectangle IEC + zig-zag US).
   - Overlay paints results on the displayed bitmap:
     - **Red** = designator text
     - **Green** = net label text
     - **Yellow** = other OCR words
     - **Blue** = detected symbol bbox
   - Netlist appears as editable text on the right; can be saved/loaded.

3. **Label editor (new, just landed)** — `LabelEditor.cs` + "Label for
   training…" button on the Schematic tab. Lets the user click-drag
   bboxes on the currently loaded processed bitmap, pick a class
   (R/C/D/Q/U/L/IC/OTHER), and save YOLO-format labels into `dataset/`.
   Pre-fills with whatever the geometric detectors already found.

---

## 4. What's broken / why we're switching approaches

The geometric `SymbolDetector` works OK for resistors but **completely
fails on capacitors** despite three attempts:

- v1: pair of thin parallel line segments via HoughLinesP. Failed.
- v2: pair of small parallel rectangles via FindContours + MinAreaRect.
  Filled and outlined plates both supported, tunable size/gap/angle
  thresholds. Failed.
- v3: erase long wires first (HoughLinesP → paint black, kill the wire
  stubs that fuse plates into one big contour), then look for rect-pair
  OR line-pair. **Still failed** per user feedback ("it still not
  working for Caps").

Root cause is fundamental: handcrafted geometric matchers don't
generalise. Each symbol class needs custom code, and small style
variations (filled vs outlined plates, hand-drawn schematics, rotation,
wires touching plates, varying stroke widths) break the heuristics.

**Each new symbol class (D, Q, U, L, …) would need the same painful
hand-tuning loop, and would likely break in the same way.**

---

## 5. The agreed plan: YOLOv8 ONNX detector

User picked **Option 1** from the options I presented:

> Train a YOLOv8n model (Ultralytics, Python, one-time) on a labelled
> schematic dataset → export to ONNX → run inference from C# with
> `Microsoft.ML.OnnxRuntime`. One model handles R/C/D/Q/U/L/IC at once.

Three commits planned. Commit 1 has landed; **commits 2 and 3 are
pending**.

### Commit 1 ✅ — Labelling UI (DONE, not yet committed to git)

- New file `BoardviewBuilder/LabelEditor.cs` — modal Form with:
  - Class combo (`R`, `C`, `D`, `Q`, `U`, `L`, `IC`, `OTHER`).
  - Click-drag LEFT mouse = create new bbox in that class.
  - Click an existing bbox = select; combo changes its class.
  - `Del` key / "Delete" button = remove selected.
  - "Clear all" button = wipe all bboxes on this image (confirms).
  - Wheel = zoom (anchored to cursor); middle/right-drag = pan.
  - Pre-fill via constructor param `IEnumerable<LabelBox>? prefill`.
  - "Save labels" writes:
    - `dataset/images/<stem>.png` (the EXACT processed bitmap shown).
    - `dataset/labels/<stem>.txt` (YOLO format: `cls cx cy w h` in 0..1).
    - `dataset/classes.txt` (always overwritten to match `DefaultClasses`).
  - Auto-uniques the stem if it already exists (`_1`, `_2`, …).
  - `FindProjectRoot()` walks up from cwd looking for `.csproj`/`.sln` or
    a `BoardviewBuilder/` subdir — so the `dataset/` folder always lands
    next to the project, not in some random working directory.

- `MainForm.cs` got a "Label for training…" button on the schematic-tab
  bottom row. Handler `OpenLabelEditor(status)` pre-fills the editor
  with every `_lastOcr.SymbolBoxes` entry, classified by the first
  letter of the designator (`R` → 0, `C` → 1, etc., else `OTHER`).

- **Build status**: `dotnet build` is **clean** (0 errors, 0 warnings)
  as of the last fix (null-forgiving `!` on `_canvas.Invalidate()` in
  the two lambdas where the compiler can't prove `_canvas` is assigned
  before the lambda fires).

- **Git status**: NOT YET COMMITTED. The latest git hash in the
  workspace config is `830494449a8d7331512823fffcfd1d0f1294f4b1`. The
  new `LabelEditor.cs` and the MainForm changes are uncommitted working
  tree edits. **First action for the next AI: verify build, run the
  app, label a couple of images to sanity-check the editor, then
  commit.**

### Commit 2 ⏳ — Training script (PENDING)

Add `tools/train_yolo.py` (one-off Python, run by the user, NOT a
runtime dep of the C# app):

```python
# tools/train_yolo.py
# - reads dataset/{images,labels}, writes data.yaml
# - trains with ultralytics: YOLO('yolov8n.pt').train(...)
# - exports to ONNX: model.export(format='onnx', imgsz=640, opset=12)
# - copies result to BoardviewBuilder/models/symbols.onnx
#   AND     BoardviewBuilder/models/symbols.classes.txt
```

Also add `tools/requirements.txt` pinning:
```
ultralytics>=8.0
onnx>=1.14
```

Notes:
- A YOLOv8n model is ~3 MB ONNX. Fast on CPU (~50-100ms/page).
- imgsz=640 is a good default; schematic crops will usually need to be
  letterboxed to this from a much larger source image.
- Need to do an 80/20 train/val split — script should do this
  automatically.
- Document in the script header how to run:
  `pip install -r tools/requirements.txt && python tools/train_yolo.py`.

### Commit 3 ⏳ — ONNX inference + wiring (PENDING)

1. Add NuGet package to `BoardviewBuilder.csproj`:
   ```xml
   <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.17.*" />
   ```
   (Use CPU package; can switch to `Microsoft.ML.OnnxRuntime.DirectML`
   later for GPU.)

2. New `BoardviewBuilder/SymbolDetectorYolo.cs`:
   ```csharp
   public sealed class SymbolDetectorYolo : IDisposable
   {
       public static SymbolDetectorYolo? TryLoad(string modelPath, string classesPath);
       public List<SymbolDetector.SymbolHit> Detect(Bitmap full, float confThreshold = 0.25f);
   }
   ```
   - Load once (singleton-friendly); model path = next to the .exe,
     `models/symbols.onnx`.
   - Preprocess: letterbox-resize to 640×640, BGR→RGB, /255, NCHW.
   - Postprocess YOLOv8 head: output shape is `[1, 4+nc, 8400]`.
     Decode `cx,cy,w,h` per anchor, take max class score, threshold,
     then class-wise NMS (IoU 0.5).
   - Map letterbox coords back to original image coords.
   - Return `List<SymbolHit>` matching the existing struct so the
     rest of the pipeline doesn't change.

3. Wire into `WireTracer` (or `SchematicImageLoader.ExtractFromBitmap`):
   - On extraction start, try `SymbolDetectorYolo.TryLoad("models/symbols.onnx", …)`
     — returns null if the model file doesn't exist, in which case fall
     back to today's geometric path.
   - Run `Detect()` ONCE on the full bitmap before per-designator
     dispatch.
   - Build a spatial lookup (`Dictionary<char, List<SymbolHit>>` keyed
     on class first letter).
   - For each designator, prefer the YOLO hit whose centre is closest
     to the designator text AND within ~6× text size; only fall back
     to the geometric matcher if no YOLO hit qualifies.
   - Add a status-bar line: "Symbols: 12 via YOLO, 3 via geometric".

4. Update `.gitignore` to exclude:
   ```
   /dataset/
   /BoardviewBuilder/models/symbols.onnx
   /tools/runs/         # ultralytics output
   /tools/__pycache__/
   ```
   (Commit `symbols.classes.txt` if it's small and stable.)

---

## 6. Suggested first labelling pass (for the user)

To bootstrap a usable v1 model, target **~30-50 labelled images**
covering the symbol classes that matter most. Concretely:

| Class | Target count | Notes                                 |
|-------|--------------|---------------------------------------|
| R     | 50           | Both IEC rect + US zig-zag styles.    |
| C     | 50           | Filled & outlined, polarised too.     |
| D     | 30           | Diodes, LEDs (triangle + bar).        |
| Q     | 30           | BJTs (NPN/PNP), MOSFETs.              |
| U     | 30           | ICs of varying pin counts.            |
| L     | 20           | Inductors (curls / boxes).            |

User should:
1. Load each test schematic.
2. Apply same image adjustments they'd use for OCR.
3. Click "Label for training…".
4. The R bboxes are already pre-filled (from the existing detector) —
   just correct/confirm them.
5. Click-drag the missing classes.
6. Click "Save labels".

After ~30 images they can run `python tools/train_yolo.py`, drop the
resulting `symbols.onnx` into `BoardviewBuilder/models/`, and re-run
the app.

---

## 7. Key files for a new AI to read first

In order of importance:

1. `BoardviewBuilder/SymbolDetector.cs` — current geometric detector
   (study what each matcher tries, so you understand what YOLO is
   replacing).
2. `BoardviewBuilder/WireTracer.cs` — how detector results flow into
   the netlist (where you'll need to wire the YOLO call).
3. `BoardviewBuilder/SchematicImageLoader.cs` — `ExtractFromBitmap`
   orchestrates OCR + WireTracer + SymbolDetector.
4. `BoardviewBuilder/MainForm.cs` — UI; look for `OpenLabelEditor` and
   `ReExtract` to see the entry points.
5. `BoardviewBuilder/LabelEditor.cs` — read end-to-end before adding
   the YOLO bits, especially `DefaultClasses` and the save format.
6. `BoardviewBuilder/Netlist.cs` + `NetlistTextFormat.cs` — the data
   model. The detector just provides bboxes; conversion to BRD is
   downstream and already working.

Do **not** edit:
- `BrdGenerator.cs`, `CsvLoader.cs`, `Models.cs` — CSV→BRD path, working.
- `OcrEngine.cs`, `tessdata/` — Tesseract, working.

---

## 8. Build / run / test commands (Windows PowerShell)

```pwsh
# Build (from repo root)
cd BoardviewBuilder; dotnet build -nologo

# Run the app
dotnet run --project BoardviewBuilder

# Kill a stuck instance
taskkill /im BoardviewBuilder.exe /f

# After training (commit 2 lands), regenerate the ONNX:
cd tools
pip install -r requirements.txt
python train_yolo.py
# → produces ../BoardviewBuilder/models/symbols.onnx
```

---

## 9. Known issues / gotchas for the next AI

1. **The `dataset/` folder doesn't exist yet.** It'll be created the
   first time the user clicks "Save labels". Don't assume it's there.

2. **The label editor uses the *displayed* bitmap**, including any
   user-applied threshold/grayscale/rotation. That's intentional — the
   model should train on what the OCR pipeline actually sees. But it
   means labelling and inference MUST happen on bitmaps processed the
   same way.

3. **Symbol coordinates are in PROCESSED-image space**, not original
   source-image space. If the user rotates 90° between extracting and
   labelling, the bboxes go stale. Document this for the user (or
   disable rotate after labelling starts — future enhancement).

4. **`SymbolDetector.SymbolHit.Kind`** is a free-form string today
   (`"resistor-rect"`, `"capacitor-rect-pair"`, …). When YOLO returns
   class names, use the SAME naming convention so downstream code that
   may switch on it keeps working. Simplest: just use the class name
   from `classes.txt` directly (`"R"`, `"C"`, …).

5. **There's a leftover loop bug from the previous session** where the
   AI repeatedly invoked `KillShell` instead of file tools. This was a
   tool-selection error on the AI's part, not an environment issue —
   the file tools work fine. Just don't fall into the same loop.

6. **Context-window pressure was real.** The last session got cut off
   at ~89% before committing the labelling UI to git. Next AI should
   **commit early and often** (small focused commits per logical
   change) rather than batching.

---

## 10. Verification checklist for next AI before committing anything

- [ ] `cd BoardviewBuilder; dotnet build -nologo` → 0 errors, 0 warnings.
- [ ] `dotnet run --project BoardviewBuilder` → app opens.
- [ ] Schematic tab → load any image from `BoardviewBuilder/samples/`.
- [ ] Click "Extract from image" → see overlay boxes.
- [ ] Click "Label for training…" → editor opens with pre-filled R
      boxes (if any were detected).
- [ ] Draw a new box, change class, delete it, save.
- [ ] Verify `dataset/images/`, `dataset/labels/`, `dataset/classes.txt`
      were created at the repo root.
- [ ] Open the saved `.txt` — verify YOLO format (`cls cx cy w h`,
      space-separated, normalised to 0..1).
- [ ] Then `git add -A && git commit` with a clear message describing
      commit 1 (labelling UI).

---

## 11. Long-term backlog (after YOLO lands)

- **PDF input**: integrate PDFium or `PdfToImage` so multi-page
  schematic PDFs become a series of bitmaps fed to the existing
  pipeline. User mentioned this as the eventual goal.
- **PCB photo path**: separate pipeline for board photographs —
  probably needs its own YOLO model (chips, passives, connectors) and
  a different post-processing step (no netlist; just component
  outlines + designators for the .brd).
- **Pin extraction**: YOLO gives us a body bbox; we still need pin
  locations. Either (a) hand-crafted post-processing per class (e.g.
  for a horizontal resistor: pins are on the long-side midpoints), or
  (b) a second YOLO model trained on pin-tip keypoints.
- **Schematic vs PCB classification**: auto-detect which kind of
  image the user loaded and pick the right pipeline.

---

*End of handoff. Good luck — the labelling UI is the foundation; the
YOLO inference path is well-scoped and should be ~2-3 hours of focused
work once a small dataset exists.*