#!/usr/bin/env python3
"""
train_yolo.py - Train a YOLOv8n model for schematic symbol detection.

This script reads the labelled dataset created by BoardviewBuilder's
LabelEditor, trains a YOLOv8n model, and exports it to ONNX format
for use in the C# application.

Usage:
    cd tools
    pip install -r requirements.txt
    python train_yolo.py

Input:
    ../dataset/
        images/     <- PNG images from LabelEditor
        labels/     <- YOLO-format .txt files (cls cx cy w h)
        classes.txt <- class names, one per line

Output:
    ../BoardviewBuilder/models/symbols.onnx
    ../BoardviewBuilder/models/symbols.classes.txt

The script automatically:
    1. Creates an 80/20 train/val split
    2. Generates data.yaml for ultralytics
    3. Trains YOLOv8n for 100 epochs (or until early stopping)
    4. Exports best weights to ONNX (opset 12, imgsz=640)
"""

import os
import sys
import shutil
import random
from pathlib import Path

def main():
    # Paths relative to this script
    script_dir = Path(__file__).parent.resolve()
    repo_root = script_dir.parent
    dataset_dir = repo_root / "dataset"
    images_dir = dataset_dir / "images"
    labels_dir = dataset_dir / "labels"
    classes_file = dataset_dir / "classes.txt"

    output_dir = repo_root / "BoardviewBuilder" / "models"

    # Validate dataset exists
    if not images_dir.exists():
        print(f"ERROR: No images found at {images_dir}")
        print("Please label some images first using the 'Label for training...' button.")
        sys.exit(1)

    if not classes_file.exists():
        print(f"ERROR: classes.txt not found at {classes_file}")
        sys.exit(1)

    # Read class names
    with open(classes_file, 'r', encoding='utf-8') as f:
        class_names = [line.strip() for line in f if line.strip()]

    print(f"Classes: {class_names}")

    # Find all image/label pairs
    image_files = list(images_dir.glob("*.png")) + list(images_dir.glob("*.jpg"))
    pairs = []
    for img_path in image_files:
        label_path = labels_dir / (img_path.stem + ".txt")
        if label_path.exists():
            pairs.append((img_path, label_path))
        else:
            print(f"WARNING: No label file for {img_path.name}, skipping")

    if len(pairs) < 2:
        print(f"ERROR: Need at least 2 labelled images, found {len(pairs)}")
        sys.exit(1)

    print(f"Found {len(pairs)} labelled images")

    # Create train/val split (80/20)
    random.seed(42)
    random.shuffle(pairs)
    split_idx = max(1, int(len(pairs) * 0.8))
    train_pairs = pairs[:split_idx]
    val_pairs = pairs[split_idx:] if split_idx < len(pairs) else [pairs[-1]]

    print(f"Train: {len(train_pairs)}, Val: {len(val_pairs)}")

    # Create ultralytics dataset structure
    yolo_dataset = script_dir / "yolo_dataset"
    train_images = yolo_dataset / "train" / "images"
    train_labels = yolo_dataset / "train" / "labels"
    val_images = yolo_dataset / "val" / "images"
    val_labels = yolo_dataset / "val" / "labels"

    # Clean and recreate
    if yolo_dataset.exists():
        shutil.rmtree(yolo_dataset)

    for d in [train_images, train_labels, val_images, val_labels]:
        d.mkdir(parents=True)

    # Copy files
    for img_path, label_path in train_pairs:
        shutil.copy(img_path, train_images / img_path.name)
        shutil.copy(label_path, train_labels / label_path.name)

    for img_path, label_path in val_pairs:
        shutil.copy(img_path, val_images / img_path.name)
        shutil.copy(label_path, val_labels / label_path.name)

    # Write data.yaml
    data_yaml = yolo_dataset / "data.yaml"
    with open(data_yaml, 'w', encoding='utf-8') as f:
        f.write(f"path: {yolo_dataset.as_posix()}\n")
        f.write("train: train/images\n")
        f.write("val: val/images\n")
        f.write(f"nc: {len(class_names)}\n")
        f.write(f"names: {class_names}\n")

    print(f"Created dataset at {yolo_dataset}")

    # Now import ultralytics and train
    try:
        from ultralytics import YOLO
    except ImportError:
        print("ERROR: ultralytics not installed. Run: pip install -r requirements.txt")
        sys.exit(1)

    # Train YOLOv8n
    print("\n" + "="*60)
    print("Starting YOLOv8n training...")
    print("="*60 + "\n")

    model = YOLO("yolov8n.pt")  # Start from pretrained weights

    results = model.train(
        data=str(data_yaml),
        epochs=100,
        imgsz=640,
        batch=16,
        patience=20,  # Early stopping
        save=True,
        project=str(script_dir / "runs"),
        name="symbols",
        exist_ok=True,
        verbose=True,
    )

    # Find best weights
    best_pt = script_dir / "runs" / "symbols" / "weights" / "best.pt"
    if not best_pt.exists():
        # Try alternate location
        best_pt = Path(results.save_dir) / "weights" / "best.pt"

    if not best_pt.exists():
        print(f"ERROR: Could not find best.pt at {best_pt}")
        sys.exit(1)

    print(f"\nBest weights: {best_pt}")

    # Export to ONNX
    print("\n" + "="*60)
    print("Exporting to ONNX...")
    print("="*60 + "\n")

    model = YOLO(str(best_pt))
    onnx_path = model.export(
        format="onnx",
        imgsz=640,
        opset=12,
        simplify=True,
    )

    # Copy to output location
    output_dir.mkdir(parents=True, exist_ok=True)
    final_onnx = output_dir / "symbols.onnx"
    final_classes = output_dir / "symbols.classes.txt"

    shutil.copy(onnx_path, final_onnx)
    shutil.copy(classes_file, final_classes)

    print("\n" + "="*60)
    print("SUCCESS!")
    print("="*60)
    print(f"Model: {final_onnx}")
    print(f"Classes: {final_classes}")
    print(f"\nThe model is ready to use. Restart BoardviewBuilder to load it.")


if __name__ == "__main__":
    main()
