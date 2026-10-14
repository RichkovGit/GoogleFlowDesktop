using System;
using System.Collections.Generic;
using System.Text;

namespace GoogleFlowDesktop
{
    public class TagItem
    {
        public string Id;
        public string Label;
        public string Value;

        public TagItem(string id, string label, string value)
        {
            Id = id;
            Label = label;
            Value = value;
        }
    }

    public class PresetItem
    {
        public string Id;
        public string Name;
        public string Description;
        public string Suffix;

        public PresetItem(string id, string name, string description, string suffix)
        {
            Id = id;
            Name = name;
            Description = description;
            Suffix = suffix;
        }
    }

    /// <summary>
    /// All prompt-crafting data and the compile pipeline, previously living in ui/app.js.
    /// </summary>
    public static class PromptKit
    {
        public static readonly string DefaultNegativePrompt =
            "blurry, low quality, distorted anatomy, extra limbs, bad proportions, " +
            "watermark, text artifacts, oversaturated, pixelated, jpeg compression, glitch, " +
            "double faces, disfigured fingers, unnatural skin texture, extra people, random bystanders, " +
            "background crowd, unprompted characters, distorted face, changed facial features, " +
            "different hair, wrong clothes, non-reference appearance, English text, foreign letters, " +
            "cluttered room, unwanted objects, bad anatomy, mutated fingers, low quality";

        public static readonly string[] AspectRatios = { "1:1", "16:9", "9:16", "4:3", "3:4" };

        public static readonly string[] AspectRatioLabels =
        {
            "1:1  Квадрат",
            "16:9  Кино / ПК",
            "9:16  Shorts / Reels",
            "4:3  Классика",
            "3:4  Портрет"
        };

        public static readonly List<TagItem> Optics = new List<TagItem>
        {
            new TagItem("opt_35mm", "35mm Street", "35mm street photography"),
            new TagItem("opt_85mm", "85mm Portrait", "85mm portrait lens"),
            new TagItem("opt_wide", "Широкий угол", "wide-angle dramatic perspective"),
            new TagItem("opt_low", "Нижний ракурс", "cinematic low-angle hero shot"),
            new TagItem("opt_macro", "Макро", "macro close-up"),
            new TagItem("opt_drone", "Дрон / Сверху", "drone aerial view")
        };

        public static readonly List<TagItem> Lighting = new List<TagItem>
        {
            new TagItem("light_golden", "Золотой час", "golden hour natural backlight"),
            new TagItem("light_chiaroscuro", "Кьяроскуро", "dramatic chiaroscuro"),
            new TagItem("light_cyber", "Неоновый контур", "cyberpunk neon rim lighting"),
            new TagItem("light_softbox", "Студийный софтбокс", "soft diffused studio softbox"),
            new TagItem("light_overcast", "Пасмурный дневной", "overcast moody daylight")
        };

        public static readonly List<TagItem> ArtStyles = new List<TagItem>
        {
            new TagItem("art_photo", "Фотореализм", "photorealistic cinematic still"),
            new TagItem("art_oil", "Масло на холсте", "oil on textured canvas"),
            new TagItem("art_vector", "Векторный арт", "vector flat illustration"),
            new TagItem("art_pixar", "3D Pixar Render", "3D Pixar-style digital render"),
            new TagItem("art_anime80", "Ретро-аниме 80х", "retro 80s anime OVA"),
            new TagItem("art_pixel", "Изометрический Pixel", "isometric pixel art")
        };

        public static readonly List<TagItem> VideoDynamics = new List<TagItem>
        {
            new TagItem("dyn_steady", "Стэдикам трекинг", "smooth steady-cam tracking"),
            new TagItem("dyn_pan", "Панорама L->R", "slow cinematic pan left to right"),
            new TagItem("dyn_vertigo", "Dolly Zoom (Vertigo)", "dolly zoom vertigo effect"),
            new TagItem("dyn_chase", "Экшн с рук", "fast-paced handheld camera chase"),
            new TagItem("dyn_timelapse", "Таймлапс неба", "timelapse sky transition")
        };

        public static readonly List<PresetItem> Presets = new List<PresetItem>
        {
            new PresetItem("cyberpunk_noir", "Киберпанк & Неонуар", "Неон, дождь, мокрый асфальт",
                ", cyberpunk neon noir aesthetic, rain-slicked reflective surfaces, glowing cyan and violet neon lights, anamorphic lens, high contrast, cinematic movie still, 8k"),
            new PresetItem("photorealism_portrait", "Фотореализм & Портрет", "Поры кожи, боке 85mm f/1.4",
                ", photorealistic professional photography, shot on 85mm prime lens, f/1.4 aperture, natural soft diffused lighting, genuine skin texture, shallow depth of field, 8k resolution"),
            new PresetItem("epic_dark_fantasy", "Эпическое & Тёмное фэнтези", "Руины, объёмный свет",
                ", epic fantasy digital concept art, dramatic volumetric light shafts, ancient textured stones, ethereal glowing particles, atmospheric haze, trending on ArtStation, 8k"),
            new PresetItem("animation_3d_pixar", "3D Мультфильм (Pixar)", "Бархатная глина, SSS",
                ", Pixar Disney 3D style render, expressive design, subsurface scattering, Octane render 8k"),
            new PresetItem("animation_ghibli", "Аниме Ghibli (Миядзаки)", "Акварельные облака",
                ", Studio Ghibli Hayao Miyazaki anime aesthetic, watercolor painted landscape, lush clouds, masterpiece"),
            new PresetItem("animation_80s_ova", "Ретро 80-е OVA", "Целлулоид, зерно VHS",
                ", 1980s retro anime OVA style, cel shaded animation, vintage colors, VHS aesthetic"),
            new PresetItem("product_commercial", "Коммерческая съёмка", "Софтбокс, макро 100mm",
                ", commercial product shot, clean studio background, studio softbox lighting, pristine reflective details, sharp focus, magazine advertising photography, 8k")
        };

        public static readonly string[] AiEnrichments =
        {
            "cinematic volumetric atmosphere, intricate photorealistic reflections, dramatic lighting",
            "shot on 85mm prime lens f/1.4, creamy background bokeh, hyper-detailed textures, Octane 8k",
            "golden hour natural sidelight, dramatic chiaroscuro shadows, award-winning cinematography",
            "hyper-detailed, authentic ambiance, natural shadows, crisp focus, 8k resolution"
        };

        /// <summary>Builds the final "technical" prompt the same way the web UI did.</summary>
        public static string Compile(string basePrompt, string presetSuffix, bool wrapper,
                                     bool photoLikeness, bool zeroClutter, bool russianOnly)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(basePrompt == null ? "" : basePrompt.Trim());

            if (!string.IsNullOrEmpty(presetSuffix))
            {
                sb.Append(presetSuffix);
            }

            if (wrapper)
            {
                List<string> rules = new List<string>();
                if (photoLikeness) rules.Add("Exact likeness to reference images, preserve all facial features and clothing details");
                if (zeroClutter) rules.Add("ONLY the specified characters and objects in the scene, absolutely NO extra people or random strangers");
                if (russianOnly) rules.Add("All visible text and spoken dialogue strictly in Russian");
                rules.Add("Masterpiece, 8k resolution, cinematic photorealism");

                sb.Append(" | STRICT REQUIREMENTS: ").Append(string.Join(". ", rules.ToArray())).Append(".");
            }

            return sb.ToString();
        }
    }
}
