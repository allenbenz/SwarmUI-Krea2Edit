"""Krea2Edit grounding-px image scaler.

Caps the longest side of an image at `grounding_px`, only downscales, keeps
aspect ratio, "area" resampling. Replicates Krea2EditGroundedEncode._prep from
comfyui-krea2edit.
"""

import torch
import comfy.utils


class Krea2EditGroundingPx:
    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "image": ("IMAGE",),
                "grounding_px": ("INT", {"default": 768, "min": 0, "max": 4096, "step": 64}),
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "scale"
    CATEGORY = "krea2edit"

    def scale(self, image, grounding_px):
        if not grounding_px:
            return (image,)

        h, w = image.shape[1], image.shape[2]
        longest = max(h, w)
        if longest <= grounding_px:
            return (image,)

        scale = grounding_px / longest
        new_w = max(round(w * scale), 1)
        new_h = max(round(h * scale), 1)

        samples = image.movedim(-1, 1)  # B,H,W,C -> B,C,H,W
        samples = comfy.utils.common_upscale(samples, new_w, new_h, "area", "disabled")
        return (samples.movedim(1, -1),)


NODE_CLASS_MAPPINGS = {
    "Krea2EditGroundingPx": Krea2EditGroundingPx,
}
NODE_DISPLAY_NAME_MAPPINGS = {
    "Krea2EditGroundingPx": "Krea 2 Edit Grounding Px",
}
