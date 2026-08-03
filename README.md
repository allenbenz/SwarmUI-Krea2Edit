# SwarmUI-Krea2Edit

A SwarmUI extension for **Krea 2 Identity Edit** - instruction-based image editing
with the [comfyui-krea2edit](https://github.com/lbouaraba/comfyui-krea2edit) node
pack and the [Krea 2 Identity Edit LoRA](https://huggingface.co/conradlocke/krea2-identity-edit)
(v1 / v1.1 / v1.2). Everything is gated on the selected model being detected as
**Krea 2** and does nothing otherwise.

## How it wires the edit

1. **Grounded text encode.** Two new `SwarmClipTextEncodeAdvanced` nodes (positive
   + negative) are created with:
   - The Krea2Edit template as a raw `llama_template` string - vision markers
     before the prompt placeholder, matching how the Identity Edit LoRA was
     trained.
   - The source image(s) on the `images` input.
   - The user's prompt text from `T2IParamTypes.Prompt` / `NegativePrompt`.

   Because the node runs its normal `encode()` path, `<break>`, `[from:to:when]`,
   `[alter|nate]`, token weighting, and regions all still work.
2. **Source latent.** `VAEEncode` of the source image → `Krea2EditModelPatch.source_latent`.
3. **Pixel path (blur-proof).** The raw source image + VAE → `Krea2EditModelPatch.source_image`
   + `vae`, so the patch node can fit the source to the target grid in pixel space.
4. **Model patch.** `Krea2EditModelPatch` rebuilds the DiT forward as
   `[text | source(frame=1) | target(frame=0)]` so the LoRA's source-preservation
   tokens land on the layout they were trained against.

A second prompt image (if present) is wired as `image_b` / `source_image_b` /
`source_latent_b` for two-input edits (person + scene). The refiner pass is not
edit-patched.

## Dependencies

- **comfyui-krea2edit** (required) - the model patch + grounded-encode
  template. Install via the button in the parameter group.
- **ComfyUI_essentials** (required for Grounding Px) - provides `ImageResize+`
  for VLM image scaling. Install via the button in the parameter group.
  Set Grounding Px to 0 to skip this dependency.

## Parameters

The **Krea 2 Edit** parameter group has a master toggle. When it is off, the whole
extension is off for that generation. Turn it on, then:

- **K2E Grounding Px** *(advanced, default 768)* - cap on the longest side of the
  image fed to Qwen3-VL during grounded encoding. Lower = stronger edit
  adherence, higher = stronger identity/likeness. 0 = native resolution (no
  scaling). Default 768 (trained range 512–1536).
- **K2E Legacy Crop** *(advanced, default off)* - switch from v1.2's resampled
  `fit` geometry to the v1/v1.1 center-crop-then-resize geometry. Only for older
  Identity Edit weights.
- **K2E Ref Boost** *(advanced, default 4.0)* - reference-fidelity dial for the
  last reference. >1 pulls harder toward the reference, <1 loosens. 4.0 is the
  recommended value (stronger face + body likeness); 1.0 = off.
- **K2E Ref Boost A** *(advanced, default 1.0)* - same dial for the FIRST reference
  (the scene in two-image edits). No effect in single-image mode.

## Usage

1. Install the **comfyui-krea2edit** and **ComfyUI_essentials** node packs using
   the buttons in the **Krea 2 Edit** parameter group, then restart the ComfyUI
   backend when prompted.
2. Select a Krea 2 model and load the Krea 2 Identity Edit LoRA (`@1.0` strength).
3. Add at least one prompt image (the source to edit). Optionally add a second
   image for two-input edits.
4. Toggle the **Krea 2 Edit** group on.
5. Write your edit instruction as the positive prompt (e.g. "recolor the car to
   matte black"). Leave the negative empty for the training-matched unconditional.

## Tips

- **Turbo at 8 steps, CFG 1** is the fast path. For removals / "delete salient
  content" edits, switch to the Raw model at CFG 3, ~20 steps.
- **Generate at ≤2MP.** Above the trained range, source content can bleed.

## Acknowledgements

The [comfyui-krea2edit](https://github.com/lbouaraba/comfyui-krea2edit) nodes and
the [Krea 2 Identity Edit](https://huggingface.co/conradlocke/krea2-identity-edit)
weights are by lbouaraba / conradlocke.
