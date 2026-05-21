# Boardview Builder

Convert schematic images (JPG, PNG, BMP, PDF) to boardview-compatible netlist files.

## Features

- **OCR Text Recognition**: Automatically detects component designators (R1, C2, U3, etc.) and net labels
- **YOLO Symbol Detection**: Uses machine learning to detect electronic symbols (resistors, capacitors, diodes, etc.)
- **Multi-step Extraction Workflow**: Manual editing at each stage for accuracy
- **Wire Tracing**: Automatically traces connections between components
- **Dark Theme UI**: Full dark mode support including scrollbars

## Requirements

- Windows 10/11
- .NET 10.0 or later
- Tesseract OCR (included via NuGet)
- ONNX Runtime (included via NuGet)

## Usage

### Loading a Schematic

1. Launch the application
2. Click **Browse...** to select a schematic image (JPG, PNG, BMP) or PDF file
3. For multi-page PDFs, select which page to load
4. The image loads automatically after selection

### Image Adjustments

Before extraction, you can adjust the image for better OCR results:

- **Grayscale**: Convert to grayscale
- **Invert**: Invert colors (useful for white-on-black schematics)
- **Brightness/Contrast**: Adjust image levels
- **Threshold**: Apply binary threshold for cleaner text detection
- **Rotate 90°**: Rotate the image
- **Reset**: Reset all adjustments

### Multi-Step Extraction Workflow

#### Step 1: OCR + Symbols
Click **1: OCR+Symbols** to run:
- OCR text detection (finds component designators and net labels)
- YOLO symbol detection (finds component symbols)

This opens the **Symbol Box Editor**:
- YOLO-detected boxes shown in magenta
- OCR text boxes shown in red (toggle with "Show OCR" checkbox)
- **Left-drag**: Draw a new symbol box
- **Right-click**: Select/delete a box
- **Middle-drag**: Pan the view
- **Mouse wheel**: Zoom
- Select symbol type from dropdown before drawing
- Click **OK** when done

#### Step 2: Detect Pins
Click **2: Detect Pins** to automatically detect pin locations at symbol edges.

This opens the **Pin Editor**:
- Detected pins shown as orange circles
- Manual pins shown as green circles
- **Left-drag**: Box-select pins for deletion
- **Right-click**: Add a new pin near symbol edge
- **Middle-drag**: Pan the view
- **Mouse wheel**: Zoom
- Selected pins turn cyan - click **Delete Selected Pins** to remove
- Click **OK** when done

#### Step 3: Trace Wires
Click **3: Trace Wires** to trace connections between pins and build the netlist.

### Viewing Results

Toggle overlays on the main view:
- **Hide OCR boxes**: Hide/show OCR text boxes
- **Show Pins**: Show/hide detected pins
- **Show Wires**: Show/hide traced wire connections

### Netlist Editing

The right panel shows the extracted netlist in text format:
- Edit directly in the text area
- Click **Apply edits** to parse and validate changes
- **Save netlist...**: Export to file
- **Load netlist...**: Import from file

### Netlist Format

```
# Comments start with hash
NET VCC
  U1.1
  R1.1
  C1.1

NET GND
  U1.4
  R1.2
  C1.2

COMPONENT R1 Resistor
  PIN 1 VCC
  PIN 2 GND
```

## Keyboard Shortcuts

### Main Window
- **Mouse wheel**: Zoom
- **Left-drag**: Pan image

### Symbol Box Editor
- **Delete/Backspace**: Delete selected box
- **Escape**: Deselect

### Pin Editor
- **Delete/Backspace**: Delete selected pin
- **Escape**: Deselect

## Training YOLO Model

The application includes tools for training custom YOLO models:

1. Click **Label...** to open the labeling editor
2. Draw bounding boxes around symbols
3. Export labels in YOLO format
4. Train using YOLOv8 (see `yolo_training/` folder)

## File Structure

```
BoardviewBuilder/
├── MainForm.cs              # Main application window
├── SymbolBoxEditor.cs       # Symbol box editing dialog
├── PinEditor.cs             # Pin editing dialog
├── SchematicImageLoader.cs  # Image loading and OCR
├── WireTracer.cs            # Wire tracing algorithm
├── SymbolDetector.cs        # YOLO inference
└── models/
    └── symbols.onnx         # YOLO model for symbol detection
```

## Troubleshooting

### OCR not detecting text
- Try adjusting brightness/contrast
- Enable threshold mode
- Ensure image resolution is sufficient (300+ DPI recommended)

### YOLO not detecting symbols
- Model may need retraining for your schematic style
- Use the Label tool to create training data

### Dark scrollbars not working
- Requires Windows 10 version 1809 or later
- Dark mode must be enabled in Windows settings

## License

MIT License
