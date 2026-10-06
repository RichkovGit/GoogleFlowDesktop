"""
Google Flow Desktop - Configurations, Presets, Tags & Protective Filters
"""
import os

APP_NAME = "Google Flow Desktop"
APP_VERSION = "2.4.0 (NVIDIA dGPU Accelerated)"
APP_DATA_DIR = os.path.expanduser(r"~\.google_flow_desktop")
PROJECTS_DIR = os.path.join(APP_DATA_DIR, "projects")
CACHE_DIR = os.path.join(APP_DATA_DIR, "cache")
LOGS_DIR = os.path.join(APP_DATA_DIR, "logs")

for d in [APP_DATA_DIR, PROJECTS_DIR, CACHE_DIR, LOGS_DIR]:
    os.makedirs(d, exist_ok=True)

# 1. Справочник пресетов для выпадающих списков (Dropdowns & Chips)
CHIPS_DICTIONARY = {
    "optics": {
        "title": "Оптика и ракурсы",
        "items": [
            {"id": "opt_35mm", "label": "35mm Street", "value": "35mm street photography"},
            {"id": "opt_85mm", "label": "85mm Portrait", "value": "85mm portrait lens"},
            {"id": "opt_wide", "label": "Широкий угол", "value": "wide-angle dramatic perspective"},
            {"id": "opt_low", "label": "Нижний ракурс", "value": "cinematic low-angle hero shot"},
            {"id": "opt_macro", "label": "Макро", "value": "macro close-up"},
            {"id": "opt_drone", "label": "Дрон / Сверху", "value": "drone aerial view"}
        ]
    },
    "lighting": {
        "title": "Стили освещения",
        "items": [
            {"id": "light_golden", "label": "Золотой час", "value": "golden hour natural backlight"},
            {"id": "light_chiaroscuro", "label": "Кьяроскуро", "value": "dramatic chiaroscuro"},
            {"id": "light_cyber", "label": "Неоновый контур", "value": "cyberpunk neon rim lighting"},
            {"id": "light_softbox", "label": "Студийный софтбокс", "value": "soft diffused studio softbox"},
            {"id": "light_overcast", "label": "Пасмурный дневной", "value": "overcast moody daylight"}
        ]
    },
    "art_styles": {
        "title": "Художественные стили",
        "items": [
            {"id": "art_photo", "label": "Фотореализм", "value": "photorealistic cinematic still"},
            {"id": "art_oil", "label": "Масло на холсте", "value": "oil on textured canvas"},
            {"id": "art_vector", "label": "Векторный арт", "value": "vector flat illustration"},
            {"id": "art_pixar", "label": "3D Pixar Render", "value": "3D Pixar-style digital render"},
            {"id": "art_anime80", "label": "Ретро-аниме 80х", "value": "retro 80s anime OVA"},
            {"id": "art_pixel", "label": "Изометрический Pixel", "value": "isometric pixel art"}
        ]
    },
    "video_dynamics": {
        "title": "Динамика видео",
        "items": [
            {"id": "dyn_steady", "label": "Стэдикам трекинг", "value": "smooth steady-cam tracking"},
            {"id": "dyn_pan", "label": "Панорама L->R", "value": "slow cinematic pan left to right"},
            {"id": "dyn_vertigo", "label": "Dolly Zoom (Vertigo)", "value": "dolly zoom vertigo effect"},
            {"id": "dyn_chase", "label": "Экшн с рук", "value": "fast-paced handheld camera chase"},
            {"id": "dyn_timelapse", "label": "Таймлапс неба", "value": "timelapse sky transition"}
        ]
    }
}

# 2. Универсальный блок негативных промптов (Negative Prompt Sanitizer)
DEFAULT_NEGATIVE_SANITIZER = (
    "blurry, low quality, distorted anatomy, extra limbs, bad proportions, "
    "watermark, text artifacts, oversaturated, pixelated, jpeg compression, glitch, "
    "double faces, disfigured fingers, unnatural skin texture, extra people, random bystanders, "
    "background crowd, unprompted characters, distorted face, changed facial features, "
    "different hair, wrong clothes, non-reference appearance, English text, foreign letters, "
    "cluttered room, unwanted objects, bad anatomy, mutated fingers, low quality"
)

# 3. Готовые комплексные пресеты (Presets)
FULL_PRESETS = [
    {
        "id": "cyberpunk_noir",
        "name": "Киберпанк & Неонуар",
        "desc": "Неоновый свет, дождь, мокрый асфальт, киберпанк",
        "tags": [
            "cyberpunk aesthetic", "high-tech gritty urban", "holographic displays",
            "wet reflective asphalt", "dark techno-thriller"
        ],
        "lighting": "vibrant neon rim lighting, dual-tone cyan and magenta, moody streetlamp glow, volumetric fog",
        "optics": "anamorphic lens flare, 35mm wide shot, low-angle perspective, cinematic color grading",
        "suffix": ", cyberpunk neon noir aesthetic, rain-slicked reflective surfaces, glowing cyan and violet neon lights, anamorphic lens, high contrast, cinematic movie still, 8k"
    },
    {
        "id": "photorealism_portrait",
        "name": "Фотореализм & Портретная съёмка",
        "desc": "Естественная текстура кожи, боке 85mm f/1.4, мягкий свет",
        "tags": [
            "authentic portrait photography", "natural skin pores",
            "unedited documentary look", "raw candid capture"
        ],
        "lighting": "soft diffused daylight, golden hour side lighting, Rembrandt lighting, gentle window light",
        "optics": "85mm f/1.4 prime lens, shallow depth of field, creamy bokeh background, sharp eye focus",
        "suffix": ", photorealistic professional photography, shot on 85mm prime lens, f/1.4 aperture, natural soft diffused lighting, genuine skin texture, shallow depth of field, 8k resolution"
    },
    {
        "id": "epic_dark_fantasy",
        "name": "Эпическое & Тёмное фэнтези",
        "desc": "Древние руины, лучи богов, мистическая атмосфера",
        "tags": [
            "epic dark fantasy art", "ancient gothic ruins",
            "mystical atmosphere", "ethereal aura", "concept art masterpiece"
        ],
        "lighting": "bioluminescent glow, dramatic god rays, flickering torchlight, misty moonlight",
        "optics": "sweeping wide-angle landscape, panoramic vista, monumental scale, atmospheric haze",
        "suffix": ", epic fantasy digital concept art, dramatic volumetric light shafts, ancient textured stones, ethereal glowing particles, atmospheric haze, trending on ArtStation, 8k"
    },
    {
        "id": "animation_3d_pixar",
        "name": "3D Мультфильм (Pixar / Disney)",
        "desc": "Выразительный 3D рендер, бархатистая глина, теплые тона",
        "tags": [
            "Pixar and Disney style 3D render", "expressive character design",
            "soft subsurface scattering", "vibrant warm colors", "smooth clay texture", "Octane render"
        ],
        "lighting": "warm volumetric sunlight, studio bounce light, soft rim light",
        "optics": "cinematic character lens, shallow focus",
        "suffix": ", Pixar Disney 3D style render, expressive design, subsurface scattering, Octane render 8k"
    },
    {
        "id": "animation_ghibli",
        "name": "Аниме Студии Ghibli",
        "desc": "Живописные облака Миядзаки, акварельный пейзаж",
        "tags": [
            "Studio Ghibli aesthetic", "hand-drawn anime scenery",
            "painterly clouds", "lush watercolor landscapes", "nostalgic nostalgic daylight", "Hayao Miyazaki style"
        ],
        "lighting": "nostalgic golden sunlight, soft painterly sky glow",
        "optics": "wide painted landscape view",
        "suffix": ", Studio Ghibli Hayao Miyazaki anime aesthetic, watercolor painted landscape, lush clouds, masterpiece"
    },
    {
        "id": "animation_80s_ova",
        "name": "Ретро 80-е OVA Аниме",
        "desc": "Целлулоидная анимация, зерно VHS, винтажная палитра",
        "tags": [
            "1980s retro anime aesthetic", "cel shading",
            "hand-painted background", "slight VHS grain", "vintage color palette"
        ],
        "lighting": "classic cel anime dramatic shadow, retro glow",
        "optics": "vintage 4:3 anime frame aesthetic",
        "suffix": ", 1980s retro anime OVA style, cel shaded animation, vintage colors, VHS aesthetic"
    },
    {
        "id": "product_commercial",
        "name": "Коммерческая & Предметная съёмка",
        "desc": "Минималистичный фон, софтбокс, макро 100mm, идеальные блики",
        "tags": [
            "commercial product photography", "clean minimalist studio background",
            "luxury advertising look", "crisps reflections"
        ],
        "lighting": "overhead softbox studio light, pristine white rim light, clean specular highlights",
        "optics": "100mm macro lens, ultra-sharp edge-to-edge focus, zero distortion, centered composition",
        "suffix": ", commercial product shot, clean studio background, studio softbox lighting, pristine reflective details, sharp focus, magazine advertising photography, 8k"
    }
]

# 4. Соотношения сторон
ASPECT_RATIOS = [
    {"label": "1:1 (Квадрат)", "value": "1:1"},
    {"label": "16:9 (Широкоэкранный)", "value": "16:9"},
    {"label": "9:16 (Stories / Reels)", "value": "9:16"},
    {"label": "4:3 (Классический)", "value": "4:3"},
    {"label": "3:4 (Портретный)", "value": "3:4"}
]

# 5. Системные директивы для ИИ (System Prompt)
SYSTEM_PROMPT_DIRECTIVES = """ЖЕСТКИЕ ПРАВИЛА ГЕНЕРАЦИИ:

ЯЗЫК: Весь текст, диалоги персонажей в кавычках, надписи на экранах телефонов, вывесках и предметах должны быть ИСКЛЮЧИТЕЛЬНО НА РУССКОМ ЯЗЫКЕ. Никакого английского текста в кадре и речи.
СООТВЕТСТВИЕ ФОТО: Максимальное портретное и визуальное сходство с предоставленными референсными изображениями. Запрещено менять черты лица, форму носа, прическу, цвет волос, одежду и аксессуары персонажей относительно их фото.
СТРОГИЙ СОСТАВ КАДРА (ZERO CLUTTER): Генерируй ТОЛЬКО тех персонажей и объекты, которые прямо указаны в запросе. Категорически запрещено добавлять посторонних людей, прохожих, случайных персонажей на заднем плане или несогласованные предметы мебели и техники. Если в кадре указаны 2 персонажа — в кадре должно быть ровно 2 персонажа."""

# 6. Автоматический шаблон-обертка (Prompt Wrapper)
PROMPT_WRAPPER_TEMPLATE = (
    "{user_prompt} | STRICT REQUIREMENTS: Exact likeness to reference images, "
    "preserve all facial features and clothing details from references. "
    "ONLY the specified characters and objects in the scene, absolutely NO extra people or random strangers. "
    "All visible text and spoken dialogue strictly in Russian. Masterpiece, 8k resolution, cinematic photorealism."
)

def wrap_prompt(user_prompt: str, apply_wrapper: bool = True, selected_preset_suffix: str = "") -> str:
    """Combines user prompt with preset suffix and strict protective wrapper."""
    combined = user_prompt.strip()
    if selected_preset_suffix:
        combined += " " + selected_preset_suffix.strip()
    if apply_wrapper:
        return PROMPT_WRAPPER_TEMPLATE.format(user_prompt=combined)
    return combined
