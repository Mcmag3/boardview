#!/usr/bin/env python3
"""
Simple YOLO labeling tool using OpenCV.

Usage:
    python label_images.py <image_path>

Controls:
    - Left-click and drag to draw a box
    - 1-8: Select class (1=R, 2=C, 3=D, 4=Q, 5=U, 6=L, 7=IC, 8=OTHER)
    - D: Delete last box
    - S: Save and exit
    - Q/ESC: Quit without saving
    - Mouse wheel: Zoom in/out
"""

import cv2
import numpy as np
import sys
import os
from pathlib import Path

CLASSES = ["R", "C", "D", "Q", "U", "L", "IC", "OTHER"]
COLORS = [
    (0, 69, 255),    # R - OrangeRed (BGR)
    (255, 144, 30),  # C - DodgerBlue
    (0, 215, 255),   # D - Gold
    (219, 112, 147), # Q - MediumPurple
    (50, 205, 50),   # U - LimeGreen
    (255, 255, 0),   # L - Cyan
    (180, 105, 255), # IC - HotPink
    (211, 211, 211), # OTHER - LightGray
]

class LabelTool:
    def __init__(self, image_path):
        self.image_path = Path(image_path)
        self.original = cv2.imread(str(image_path))
        if self.original is None:
            raise ValueError(f"Cannot load image: {image_path}")

        self.boxes = []  # List of (class_idx, x1, y1, x2, y2) in image coords
        self.current_class = 0
        self.drawing = False
        self.start_point = None
        self.current_point = None
        self.scale = 1.0
        self.offset = [0, 0]

        # Calculate initial scale to fit in window
        h, w = self.original.shape[:2]
        max_dim = max(w, h)
        if max_dim > 1200:
            self.scale = 1200 / max_dim

        cv2.namedWindow("Label Tool", cv2.WINDOW_NORMAL)
        cv2.setMouseCallback("Label Tool", self.mouse_callback)

    def mouse_callback(self, event, x, y, flags, param):
        # Convert screen coords to image coords
        img_x = int(x / self.scale)
        img_y = int(y / self.scale)

        if event == cv2.EVENT_LBUTTONDOWN:
            self.drawing = True
            self.start_point = (img_x, img_y)
            self.current_point = (img_x, img_y)

        elif event == cv2.EVENT_MOUSEMOVE:
            if self.drawing:
                self.current_point = (img_x, img_y)

        elif event == cv2.EVENT_LBUTTONUP:
            if self.drawing:
                self.drawing = False
                x1 = min(self.start_point[0], img_x)
                y1 = min(self.start_point[1], img_y)
                x2 = max(self.start_point[0], img_x)
                y2 = max(self.start_point[1], img_y)

                # Only add if box is big enough
                if x2 - x1 > 5 and y2 - y1 > 5:
                    self.boxes.append((self.current_class, x1, y1, x2, y2))
                    print(f"Added {CLASSES[self.current_class]} box at ({x1},{y1})-({x2},{y2})")

                self.start_point = None
                self.current_point = None

        elif event == cv2.EVENT_MOUSEWHEEL:
            # Zoom
            if flags > 0:
                self.scale = min(3.0, self.scale * 1.1)
            else:
                self.scale = max(0.1, self.scale / 1.1)

    def draw(self):
        # Create display image
        display = self.original.copy()

        # Draw existing boxes
        for cls_idx, x1, y1, x2, y2 in self.boxes:
            color = COLORS[cls_idx]
            cv2.rectangle(display, (x1, y1), (x2, y2), color, 2)
            cv2.putText(display, CLASSES[cls_idx], (x1 + 2, y1 + 15),
                       cv2.FONT_HERSHEY_SIMPLEX, 0.5, color, 2)

        # Draw current box being drawn
        if self.drawing and self.start_point and self.current_point:
            x1 = min(self.start_point[0], self.current_point[0])
            y1 = min(self.start_point[1], self.current_point[1])
            x2 = max(self.start_point[0], self.current_point[0])
            y2 = max(self.start_point[1], self.current_point[1])
            cv2.rectangle(display, (x1, y1), (x2, y2), (255, 255, 255), 1)

        # Resize for display
        h, w = display.shape[:2]
        new_w = int(w * self.scale)
        new_h = int(h * self.scale)
        display = cv2.resize(display, (new_w, new_h))

        # Add status bar
        status = f"Class: {CLASSES[self.current_class]} ({self.current_class + 1}) | Boxes: {len(self.boxes)} | Keys: 1-8=class, D=delete, S=save, Q=quit"
        cv2.putText(display, status, (10, 25), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)

        return display

    def save(self):
        # Find project root
        script_dir = Path(__file__).parent.resolve()
        repo_root = script_dir.parent
        dataset_dir = repo_root / "dataset"
        images_dir = dataset_dir / "images"
        labels_dir = dataset_dir / "labels"

        images_dir.mkdir(parents=True, exist_ok=True)
        labels_dir.mkdir(parents=True, exist_ok=True)

        # Write classes.txt
        with open(dataset_dir / "classes.txt", "w") as f:
            f.write("\n".join(CLASSES))

        # Find unique filename
        stem = self.image_path.stem
        img_path = images_dir / f"{stem}.png"
        lbl_path = labels_dir / f"{stem}.txt"
        suffix = 1
        while img_path.exists() or lbl_path.exists():
            img_path = images_dir / f"{stem}_{suffix}.png"
            lbl_path = labels_dir / f"{stem}_{suffix}.txt"
            suffix += 1

        # Save image
        cv2.imwrite(str(img_path), self.original)

        # Save labels in YOLO format
        h, w = self.original.shape[:2]
        with open(lbl_path, "w") as f:
            for cls_idx, x1, y1, x2, y2 in self.boxes:
                cx = (x1 + x2) / 2 / w
                cy = (y1 + y2) / 2 / h
                bw = (x2 - x1) / w
                bh = (y2 - y1) / h
                f.write(f"{cls_idx} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}\n")

        print(f"Saved {len(self.boxes)} boxes to:")
        print(f"  Image: {img_path}")
        print(f"  Labels: {lbl_path}")
        return True

    def run(self):
        print("\nLabel Tool Controls:")
        print("  1-8: Select class (1=R, 2=C, 3=D, 4=Q, 5=U, 6=L, 7=IC, 8=OTHER)")
        print("  Left-click + drag: Draw box")
        print("  D: Delete last box")
        print("  S: Save and exit")
        print("  Q/ESC: Quit without saving")
        print()

        while True:
            display = self.draw()
            cv2.imshow("Label Tool", display)

            key = cv2.waitKey(30) & 0xFF

            if key == ord('q') or key == 27:  # Q or ESC
                print("Quit without saving")
                break
            elif key == ord('s'):
                if self.save():
                    break
            elif key == ord('d'):
                if self.boxes:
                    removed = self.boxes.pop()
                    print(f"Deleted {CLASSES[removed[0]]} box")
            elif ord('1') <= key <= ord('8'):
                self.current_class = key - ord('1')
                print(f"Selected class: {CLASSES[self.current_class]}")

        cv2.destroyAllWindows()


def main():
    if len(sys.argv) < 2:
        # If no argument, look for sample images
        script_dir = Path(__file__).parent
        samples_dir = script_dir.parent / "BoardviewBuilder" / "samples"

        if samples_dir.exists():
            images = list(samples_dir.glob("*.png")) + list(samples_dir.glob("*.jpg")) + \
                     list(samples_dir.glob("*.webp")) + list(samples_dir.glob("*.jfif"))
            if images:
                print("Available sample images:")
                for i, img in enumerate(images):
                    print(f"  {i+1}. {img.name}")
                print(f"\nUsage: python {sys.argv[0]} <image_path>")
                print(f"   or: python {sys.argv[0]} {images[0]}")
                return

        print(f"Usage: python {sys.argv[0]} <image_path>")
        return

    image_path = sys.argv[1]
    if not os.path.exists(image_path):
        print(f"Error: File not found: {image_path}")
        return

    tool = LabelTool(image_path)
    tool.run()


if __name__ == "__main__":
    main()
