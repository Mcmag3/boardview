#!/usr/bin/env python3
"""
Prepare a YOLO training dataset from sample schematic images.

This script creates a basic dataset structure with images copied and
placeholder labels. The labels should then be refined using the LabelEditor
in the BoardviewBuilder app.

Usage:
    python prepare_dataset.py
"""

import os
import shutil
from pathlib import Path

# Find the repository root
script_dir = Path(__file__).parent
repo_root = script_dir.parent

# Paths
samples_dir = repo_root / "BoardviewBuilder" / "samples"
dataset_dir = repo_root / "BoardviewBuilder" / "dataset"
images_dir = dataset_dir / "images"
labels_dir = dataset_dir / "labels"

# YOLO classes (must match LabelEditor.DefaultClasses)
CLASSES = ["R", "C", "D", "Q", "U", "L", "IC", "OTHER"]

# Image extensions to process
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".webp", ".jfif"}


def main():
    print("Preparing YOLO dataset...")
    print(f"Samples: {samples_dir}")
    print(f"Dataset: {dataset_dir}")

    # Create directories
    images_dir.mkdir(parents=True, exist_ok=True)
    labels_dir.mkdir(parents=True, exist_ok=True)

    # Write classes.txt
    classes_file = dataset_dir / "classes.txt"
    classes_file.write_text("\n".join(CLASSES), encoding="utf-8")
    print(f"Wrote {classes_file}")

    # Find all sample images
    sample_images = []
    for f in samples_dir.iterdir():
        if f.suffix.lower() in IMAGE_EXTENSIONS:
            sample_images.append(f)

    print(f"\nFound {len(sample_images)} sample images:")
    for img in sorted(sample_images):
        print(f"  - {img.name}")

    # Copy images to dataset (if not already there)
    copied = 0
    skipped = 0
    for img in sample_images:
        # Create a clean stem (remove spaces, special chars)
        stem = img.stem.replace(" ", "_").replace("(", "").replace(")", "")
        dest_img = images_dir / f"{stem}.png"
        dest_lbl = labels_dir / f"{stem}.txt"

        if dest_img.exists():
            print(f"  Skip (exists): {stem}")
            skipped += 1
            continue

        # Copy image (convert to PNG for consistency)
        try:
            from PIL import Image
            with Image.open(img) as im:
                # Convert to RGB if needed (removes alpha channel)
                if im.mode in ("RGBA", "P"):
                    im = im.convert("RGB")
                im.save(dest_img, "PNG")
            print(f"  Copied: {stem}.png")
            copied += 1

            # Create empty label file (to be filled in LabelEditor)
            if not dest_lbl.exists():
                dest_lbl.write_text("", encoding="utf-8")
        except ImportError:
            # Fallback: just copy the file
            shutil.copy2(img, dest_img)
            print(f"  Copied (raw): {img.name} -> {dest_img.name}")
            copied += 1
            if not dest_lbl.exists():
                dest_lbl.write_text("", encoding="utf-8")
        except Exception as e:
            print(f"  Error: {img.name}: {e}")

    print(f"\nDataset prepared:")
    print(f"  - Copied: {copied} images")
    print(f"  - Skipped: {skipped} (already exist)")
    print(f"  - Total images in dataset: {len(list(images_dir.glob('*')))}")
    print(f"\nNext steps:")
    print(f"  1. Open BoardviewBuilder")
    print(f"  2. Load each image from {images_dir}")
    print(f"  3. Use 'Label for training...' to add bounding boxes")
    print(f"  4. Save labels for each image")
    print(f"  5. Click 'Train model' when done")


if __name__ == "__main__":
    main()
