using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Krea2Edit.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Krea2Edit;

public class Krea2Edit : Extension
{
    public const string EditFeatureFlag = "krea2_identity_edit";
    public const string EssentialsFeatureFlag = "comfyui_essentials";
    public const string EditModelPatchNodeName = "Krea2EditModelPatch";
    public const string EssentialsResizeNodeName = "ImageResize+";

    // v1.2 defaults to "fit"; "crop (legacy)" is the v1/v1.1 center-crop-then-resize
    // geometry for use with older weights.
    public const string FitModeFit = "fit";
    public const string FitModeCropLegacy = "crop (legacy)";

    // Qwen3-VL system prompt from the Identity Edit training template. Vision
    // tokens go before the prompt placeholder.
    public const string GroundingSystemPrompt =
        "Describe the image by detailing the color, shape, size, " +
        "texture, quantity, text, spatial relationships of the objects and background:";

    public const int DefaultGroundingPx = 768;

    public static T2IRegisteredParam<bool> LegacyCrop;
    public static T2IRegisteredParam<int> GroundingPx;
    public static T2IRegisteredParam<double> RefBoost;
    public static T2IRegisteredParam<double> RefBoostA;

    public static T2IParamGroup Krea2EditGroup;

    public override void OnInit()
    {
        Logs.Info("SwarmUI Krea 2 Edit Extension initializing...");
        ComfyTyped.Generated.NodeRegistrations.EnsureRegistered();
        Generated.NodeRegistrations.EnsureRegistered();
        ComfyUIBackendExtension.NodeToFeatureMap[EditModelPatchNodeName] = EditFeatureFlag;
        ComfyUIBackendExtension.NodeToFeatureMap[EssentialsResizeNodeName] = EssentialsFeatureFlag;
        InstallableFeatures.RegisterInstallableFeature(new(
            "Krea 2 Identity Edit",
            EditFeatureFlag,
            "https://github.com/lbouaraba/comfyui-krea2edit",
            "lbouaraba"
        ));
        InstallableFeatures.RegisterInstallableFeature(new(
            "ComfyUI Essentials",
            EssentialsFeatureFlag,
            "https://github.com/cubiq/ComfyUI_essentials",
            "matteo"
        ));
        ScriptFiles.Add("assets/krea2edit_install.js");

        Krea2EditGroup = new T2IParamGroup(
            Name: "Krea 2 Edit",
            Toggles: true,
            Open: false,
            IsAdvanced: false,
            OrderPriority: 9,
            Description: "Krea 2 Identity Edit: instruction-based image editing with a grounded text encoder (Qwen3-VL sees the source image) and a source-preservation model patch. Requires the Krea 2 Identity Edit LoRA."
        );

        int OrderPriority = 0;

        GroundingPx = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "K2E Grounding Px",
            Description: "Cap on the longest side of the image fed to Qwen3-VL during grounded encoding. Lower = stronger edit adherence, higher = stronger identity/likeness. 0 = native resolution. Default 768 (trained range 512–1536).",
            Default: "768",
            Min: 0, Max: 4096, Step: 64,
            IsAdvanced: true,
            Group: Krea2EditGroup,
            FeatureFlag: EditFeatureFlag,
            OrderPriority: OrderPriority++
        ));

        LegacyCrop = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "K2E Legacy Crop",
            Description: "Use the v1/v1.1 center-crop-then-resize source geometry instead of v1.2's resampled fit. Enable only when using older (v1/v1.1) Identity Edit weights.",
            Default: "false",
            IgnoreIf: "false",
            IsAdvanced: true,
            Group: Krea2EditGroup,
            FeatureFlag: EditFeatureFlag,
            OrderPriority: OrderPriority++
        ));

        RefBoost = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "K2E Ref Boost",
            Description: "Reference-fidelity dial for the last reference (the subject in two-image edits, the only reference in single-image edits). >1 pulls harder toward the reference's appearance, <1 loosens. 1.0 = off, 4.0 = recommended (stronger face + body likeness).",
            Default: "4.00",
            Min: 0, Max: 10, Step: 0.05,
            ViewMin: 0, ViewMax: 10,
            ViewType: ParamViewType.SLIDER,
            IsAdvanced: true,
            Group: Krea2EditGroup,
            FeatureFlag: EditFeatureFlag,
            OrderPriority: OrderPriority++
        ));

        RefBoostA = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "K2E Ref Boost A",
            Description: "Reference-fidelity dial for the FIRST reference (the scene in two-image edits). No effect in single-image mode. 1.0 = off.",
            Default: "1.00",
            Min: 0, Max: 10, Step: 0.05,
            ViewMin: 0, ViewMax: 10,
            ViewType: ParamViewType.SLIDER,
            IsAdvanced: true,
            Group: Krea2EditGroup,
            FeatureFlag: EditFeatureFlag,
            OrderPriority: OrderPriority++
        ));

        // After SwarmUI's text-encode step so FinalPrompt/FinalNegativePrompt
        // and CurrentModel/CurrentVae/CurrentTextEnc are set.
        WorkflowGenerator.AddStep(ApplyKrea2IdentityEdit, -5.5);
    }

    private static bool IsKrea2(WorkflowGenerator generator)
        => generator.CurrentCompatClass() == T2IModelClassSorter.CompatKrea2.ID;

    private static void ApplyKrea2IdentityEdit(WorkflowGenerator generator)
    {
        if (!generator.UserInput.TryGet(RefBoost, out _) || !IsKrea2(generator))
        {
            return;
        }
        if (generator.CurrentModel is null || generator.CurrentVae is null)
        {
            Logs.Debug("Krea 2 Edit: enabled but model/VAE tracker not ready; skipping.");
            return;
        }

        // Collect up to 2 prompt images: image[0] = source/scene, image[1] =
        // optional subject (two-input person-into-scene edits).
        List<JArray> refImages = [];
        for (int i = 0; i < 2; i++)
        {
            JArray img = generator.GetPromptImage(fixSize: true, promptSize: true, index: i);
            if (img is null)
            {
                break;
            }
            refImages.Add(img);
        }
        if (refImages.Count == 0)
        {
            Logs.Debug("Krea 2 Edit: enabled but no prompt image provided; skipping (no source to edit).");
            return;
        }

        JArray clipPath = generator.CurrentTextEnc?.Path;
        if (clipPath is null)
        {
            Logs.Debug("Krea 2 Edit: CurrentTextEnc not ready (no CLIP loaded); skipping.");
            return;
        }
        string positivePrompt = generator.UserInput.Get(T2IParamTypes.Prompt, "") ?? "";
        string negativePrompt = generator.UserInput.Get(T2IParamTypes.NegativePrompt, "") ?? "";

        int groundingPx = generator.UserInput.Get(GroundingPx, DefaultGroundingPx);
        bool legacyCrop = generator.UserInput.Get(LegacyCrop, false);
        string fitMode = legacyCrop ? FitModeCropLegacy : FitModeFit;
        double refBoost = generator.UserInput.Get(RefBoost, 1.0);
        double refBoostA = generator.UserInput.Get(RefBoostA, 1.0);
        bool twoRef = refImages.Count >= 2;

        JArray vae = generator.CurrentVae.Path;
        JArray sourceImage = refImages[0];
        JArray sourceImageB = twoRef ? refImages[1] : null;

        using WorkflowBridge bridge = BridgeSync.For(generator);

        // VAEEncode the source(s) for the patch node's source_latent input.
        JArray sourceLatent = VAEEncodeImage(bridge, sourceImage, vae);
        JArray sourceLatentB = twoRef ? VAEEncodeImage(bridge, sourceImageB, vae) : null;

        // Scale the source image(s) for the VLM. The patch node still gets
        // the full-resolution image via source_image.
        JArray vlmImage = groundingPx > 0
            ? ScaleForGrounding(generator, sourceImage, groundingPx)
            : sourceImage;
        JArray vlmImageB = twoRef && groundingPx > 0
            ? ScaleForGrounding(generator, sourceImageB, groundingPx)
            : (twoRef ? sourceImageB : null);

        // If two refs, batch them into a single IMAGE (SwarmClipTextEncodeAdvanced
        // splits the batch into individual images for each vision marker).
        JArray groundedImages = vlmImage;
        if (twoRef)
        {
            string batched = generator.CreateNode("ImageBatch", new JObject()
            {
                ["image1"] = vlmImage,
                ["image2"] = vlmImageB
            });
            groundedImages = [batched, 0];
        }

        // Grounded template: N vision markers before the prompt placeholder.
        // A raw string (not a named preset) so SwarmClipTextEncodeAdvanced
        // passes it through directly.
        string vis = string.Concat(System.Linq.Enumerable.Repeat(
            "<|vision_start|><|image_pad|><|vision_end|>", refImages.Count));
        string krea2EditTemplate =
            $"<|im_start|>system\n{GroundingSystemPrompt}<|im_end|>\n" +
            $"<|im_start|>user\n{vis}{{}}<|im_end|>\n" +
            "<|im_start|>assistant\n";

        generator.FinalPrompt = CreateGroundedEncode(generator, clipPath, positivePrompt,
            groundedImages, krea2EditTemplate);
        generator.FinalNegativePrompt = CreateGroundedEncode(generator, clipPath, negativePrompt,
            groundedImages, krea2EditTemplate);

        // Model patch.
        Krea2EditModelPatchNode patch = bridge.AddNode(new Krea2EditModelPatchNode().With(
            RefBoost: refBoost,
            RefBoostA: refBoostA,
            FitMode: fitMode));
        patch.Model.ConnectFromPath(bridge, generator.CurrentModel.Path);
        patch.SourceLatent.ConnectFromPath(bridge, sourceLatent);
        if (twoRef)
        {
            patch.SourceLatentB.ConnectFromPath(bridge, sourceLatentB);
        }
        patch.Vae.ConnectFromPath(bridge, vae);
        patch.SourceImage.ConnectFromPath(bridge, sourceImage);
        if (twoRef)
        {
            patch.SourceImageB.ConnectFromPath(bridge, sourceImageB);
        }
        generator.CurrentModel = patch.MODEL.ToWGNodeData(generator, WGNodeData.DT_MODEL);
    }

    /// <summary>Scale for VLM input via ComfyUI_essentials ImageResize+.
    /// method="keep proportion" + condition="downscale if bigger" + interpolation="area"
    /// with width=height=grounding_px caps the longest side at grounding_px, only
    /// downscaling.</summary>
    private static JArray ScaleForGrounding(WorkflowGenerator generator, JArray image, int groundingPx)
    {
        string nodeId = generator.CreateNode(EssentialsResizeNodeName, new JObject()
        {
            ["image"] = image,
            ["width"] = groundingPx,
            ["height"] = groundingPx,
            ["interpolation"] = "area",
            ["method"] = "keep proportion",
            ["condition"] = "downscale if bigger",
            ["multiple_of"] = 0
        });
        return [nodeId, 0];
    }

    /// <summary>Create a SwarmClipTextEncodeAdvanced node with the Krea2Edit
    /// grounded template.</summary>
    private static JArray CreateGroundedEncode(WorkflowGenerator generator, JArray clip,
        string prompt, JArray images, string llamaTemplate)
    {
        int steps = generator.UserInput.Get(T2IParamTypes.Steps);
        int width = generator.UserInput.GetImageWidth();
        int height = generator.UserInput.GetImageHeight();
        double guidance = generator.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1.0);
        string nodeId = generator.CreateNode("SwarmClipTextEncodeAdvanced", new JObject()
        {
            ["clip"] = clip,
            ["steps"] = steps,
            ["prompt"] = prompt,
            ["width"] = width,
            ["height"] = height,
            ["target_width"] = width,
            ["target_height"] = height,
            ["guidance"] = guidance,
            ["images"] = images,
            ["llama_template"] = llamaTemplate
        });
        return [nodeId, 0];
    }

    private static JArray VAEEncodeImage(WorkflowBridge bridge, JArray image, JArray vae)
    {
        VAEEncodeNode encode = bridge.AddNode(new VAEEncodeNode());
        encode.Pixels.ConnectFromPath(bridge, image);
        encode.Vae.ConnectFromPath(bridge, vae);
        return WorkflowBridge.ToPath(encode.LATENT);
    }
}
