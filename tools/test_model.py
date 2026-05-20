#!/usr/bin/env python3
"""Quick test to verify the ONNX model works."""

import numpy as np
import cv2
from pathlib import Path

def main():
    script_dir = Path(__file__).parent
    repo_root = script_dir.parent

    model_path = repo_root / "BoardviewBuilder" / "models" / "symbols.onnx"
    test_image = repo_root / "BoardviewBuilder" / "samples" / "lm358n_amplifier_circuit.jpg"

    if not model_path.exists():
        print(f"Model not found: {model_path}")
        return

    if not test_image.exists():
        print(f"Test image not found: {test_image}")
        return

    # Load with ONNX Runtime
    import onnxruntime as ort

    session = ort.InferenceSession(str(model_path))

    # Get input/output info
    input_info = session.get_inputs()[0]
    output_info = session.get_outputs()[0]

    print(f"Input: {input_info.name}, shape: {input_info.shape}, type: {input_info.type}")
    print(f"Output: {output_info.name}, shape: {output_info.shape}, type: {output_info.type}")

    # Load and preprocess image
    img = cv2.imread(str(test_image))
    print(f"Image shape: {img.shape}")

    # Letterbox resize to 640x640
    h, w = img.shape[:2]
    scale = min(640/w, 640/h)
    new_w, new_h = int(w*scale), int(h*scale)
    resized = cv2.resize(img, (new_w, new_h))

    # Pad to 640x640
    padded = np.full((640, 640, 3), 114, dtype=np.uint8)
    pad_x = (640 - new_w) // 2
    pad_y = (640 - new_h) // 2
    padded[pad_y:pad_y+new_h, pad_x:pad_x+new_w] = resized

    # Convert to NCHW float32, normalize
    input_tensor = padded.transpose(2, 0, 1).astype(np.float32) / 255.0
    input_tensor = input_tensor[np.newaxis, ...]  # Add batch dim

    print(f"Input tensor shape: {input_tensor.shape}")

    # Run inference
    outputs = session.run(None, {input_info.name: input_tensor})
    output = outputs[0]

    print(f"Output shape: {output.shape}")
    print(f"Output min/max: {output.min():.4f} / {output.max():.4f}")

    # YOLOv8 output is [1, 4+nc, 8400] where nc=8 classes
    # Rows 0-3: cx, cy, w, h
    # Rows 4+: class scores

    if len(output.shape) == 3:
        num_classes = output.shape[1] - 4
        num_boxes = output.shape[2]
        print(f"Detected format: {num_classes} classes, {num_boxes} candidate boxes")

        # Find max confidence per box
        class_scores = output[0, 4:, :]  # Shape: [nc, 8400]
        max_scores = class_scores.max(axis=0)  # Shape: [8400]

        print(f"Max class score per box - min: {max_scores.min():.4f}, max: {max_scores.max():.4f}, mean: {max_scores.mean():.4f}")

        # Count detections at various thresholds
        for thresh in [0.5, 0.25, 0.1, 0.05, 0.01]:
            count = (max_scores > thresh).sum()
            print(f"  Boxes with conf > {thresh}: {count}")

        # Show top 10 detections
        top_indices = np.argsort(max_scores)[-10:][::-1]
        print("\nTop 10 detections:")
        classes = ["R", "C", "D", "Q", "U", "L", "IC", "OTHER"]
        for idx in top_indices:
            scores = class_scores[:, idx]
            best_class = scores.argmax()
            conf = scores[best_class]
            cx, cy, w, h = output[0, :4, idx]
            print(f"  {classes[best_class]}: conf={conf:.4f}, box=({cx:.0f},{cy:.0f},{w:.0f},{h:.0f})")


if __name__ == "__main__":
    main()
