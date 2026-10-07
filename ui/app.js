/**
 * Google Flow Desktop - Prompt Studio & Hardware Engine (Obsidian Edition)
 */

// Global State
const state = {
  currentTab: 'studio',
  prompt: 'Киберпанк девушка на мотоцикле под неоновым дождём в ночном городе будущего',
  selectedRatio: '16:9',
  activePresetId: null,
  activeChips: new Set(),
  activePresetSuffix: '',
  
  // Protective Filters
  russianOnly: true,
  photoLikeness: true,
  zeroClutter: true,
  enableWrapper: true,
  enableNegative: true
};

// Data Dictionaries
const DICTIONARIES = {
  optics: [
    { id: "opt_35mm", label: "35mm Street", val: "35mm street photography" },
    { id: "opt_85mm", label: "85mm Portrait", val: "85mm portrait lens" },
    { id: "opt_wide", label: "Широкий угол", val: "wide-angle dramatic perspective" },
    { id: "opt_low", label: "Нижний ракурс", val: "cinematic low-angle hero shot" },
    { id: "opt_macro", label: "Макро", val: "macro close-up" },
    { id: "opt_drone", label: "Дрон / Сверху", val: "drone aerial view" }
  ],
  lighting: [
    { id: "light_golden", label: "Золотой час", val: "golden hour natural backlight" },
    { id: "light_chiaroscuro", label: "Кьяроскуро", val: "dramatic chiaroscuro" },
    { id: "light_cyber", label: "Неоновый контур", val: "cyberpunk neon rim lighting" },
    { id: "light_softbox", label: "Студийный софтбокс", val: "soft diffused studio softbox" },
    { id: "light_overcast", label: "Пасмурный дневной", val: "overcast moody daylight" }
  ],
  art_styles: [
    { id: "art_photo", label: "Фотореализм", val: "photorealistic cinematic still" },
    { id: "art_oil", label: "Масло на холсте", val: "oil on textured canvas" },
    { id: "art_vector", label: "Векторный арт", val: "vector flat illustration" },
    { id: "art_pixar", label: "3D Pixar Render", val: "3D Pixar-style digital render" },
    { id: "art_anime80", label: "Ретро-аниме 80х", val: "retro 80s anime OVA" },
    { id: "art_pixel", label: "Изометрический Pixel", val: "isometric pixel art" }
  ],
  video_dynamics: [
    { id: "dyn_steady", label: "Стэдикам трекинг", val: "smooth steady-cam tracking" },
    { id: "dyn_pan", label: "Панорама L->R", val: "slow cinematic pan left to right" },
    { id: "dyn_vertigo", label: "Dolly Zoom (Vertigo)", val: "dolly zoom vertigo effect" },
    { id: "dyn_chase", label: "Экшн с рук", val: "fast-paced handheld camera chase" },
    { id: "dyn_timelapse", label: "Таймлапс неба", val: "timelapse sky transition" }
  ]
};

const PRESETS = [
  {
    id: "cyberpunk_noir",
    name: "Киберпанк & Неонуар",
    desc: "Неоновый свет, дождь, асфальт",
    tags: ["cyberpunk aesthetic", "high-tech gritty urban", "wet reflective asphalt"],
    suffix: ", cyberpunk neon noir aesthetic, rain-slicked reflective surfaces, glowing cyan and violet neon lights, anamorphic lens, high contrast, cinematic movie still, 8k"
  },
  {
    id: "photorealism_portrait",
    name: "Фотореализм & Портрет",
    desc: "Поры кожи, боке 85mm f/1.4",
    tags: ["authentic portrait photography", "natural skin pores", "sharp eye focus"],
    suffix: ", photorealistic professional photography, shot on 85mm prime lens, f/1.4 aperture, natural soft diffused lighting, genuine skin texture, shallow depth of field, 8k resolution"
  },
  {
    id: "epic_dark_fantasy",
    name: "Эпическое & Тёмное фэнтези",
    desc: "Древние руины, лучи богов",
    tags: ["epic dark fantasy art", "ancient gothic ruins", "ethereal aura"],
    suffix: ", epic fantasy digital concept art, dramatic volumetric light shafts, ancient textured stones, ethereal glowing particles, atmospheric haze, trending on ArtStation, 8k"
  },
  {
    id: "animation_3d_pixar",
    name: "3D Мультфильм (Pixar)",
    desc: "3D рендер, бархатная глина",
    tags: ["Pixar and Disney style 3D render", "smooth clay texture"],
    suffix: ", Pixar Disney 3D style render, expressive design, subsurface scattering, Octane render 8k"
  },
  {
    id: "animation_ghibli",
    name: "Аниме Ghibli (Миядзаки)",
    desc: "Живописные акварельные облака",
    tags: ["Studio Ghibli aesthetic", "painterly clouds", "Hayao Miyazaki style"],
    suffix: ", Studio Ghibli Hayao Miyazaki anime aesthetic, watercolor painted landscape, lush clouds, masterpiece"
  },
  {
    id: "animation_80s_ova",
    name: "Ретро 80-е OVA",
    desc: "Целлулоид, зерно VHS",
    tags: ["1980s retro anime aesthetic", "cel shading", "slight VHS grain"],
    suffix: ", 1980s retro anime OVA style, cel shaded animation, vintage colors, VHS aesthetic"
  },
  {
    id: "product_commercial",
    name: "Коммерческая съёмка",
    desc: "Студийный софтбокс, макро 100mm",
    tags: ["commercial product photography", "clean minimalist studio background"],
    suffix: ", commercial product shot, clean studio background, studio softbox lighting, pristine reflective details, sharp focus, magazine advertising photography, 8k"
  }
];

const DEFAULT_NEGATIVE_PROMPT = 
  "blurry, low quality, distorted anatomy, extra limbs, bad proportions, " +
  "watermark, text artifacts, oversaturated, pixelated, jpeg compression, glitch, " +
  "double faces, disfigured fingers, unnatural skin texture, extra people, random bystanders, " +
  "background crowd, unprompted characters, distorted face, changed facial features, " +
  "different hair, wrong clothes, non-reference appearance, English text, foreign letters, " +
  "cluttered room, unwanted objects, bad anatomy, mutated fingers, low quality";

document.addEventListener('DOMContentLoaded', () => {
  setupNavigation();
  initChipsAndPresets();
  initAspectRatios();
  initFormInputs();
  initUpdateControls();
  updateCompiledPrompt();

  postHostMessage({ type: 'UI_READY' });
});

// Navigation (Rail items)
function setupNavigation() {
  document.querySelectorAll('.flow-nav-item[data-tab]').forEach(btn => {
    btn.addEventListener('click', () => {
      const tab = btn.dataset.tab;
      document.querySelectorAll('.flow-nav-item[data-tab]').forEach(b => b.classList.remove('active'));
      document.querySelectorAll('.flow-tab-pane').forEach(p => p.classList.remove('active'));
      
      btn.classList.add('active');
      const pane = document.getElementById(`tab-${tab}`);
      if (pane) pane.classList.add('active');
      state.currentTab = tab;
    });
  });

  const btnTrim = document.getElementById('btnTrimRamQuick');
  if (btnTrim) {
    btnTrim.addEventListener('click', () => {
      postHostMessage({ type: 'TRIM_MEMORY' });
      showMiniToast('🧹 Память процесса очищена!');
    });
  }

  const btnTrimBig = document.getElementById('btnTrimMemoryNow');
  if (btnTrimBig) {
    btnTrimBig.addEventListener('click', () => {
      postHostMessage({ type: 'TRIM_MEMORY' });
      showMiniToast('🧹 Память dGPU процесса очищена!');
    });
  }

  const btnReapply = document.getElementById('btnReapplyGpuRegistry');
  if (btnReapply) {
    btnReapply.addEventListener('click', () => {
      postHostMessage({ type: 'REAPPLY_GPU' });
      showMiniToast('⚙️ Реестр dGPU DirectX обновлен!');
    });
  }
}

// Chips & Presets
function initChipsAndPresets() {
  renderChipGroup('chipsOptics', DICTIONARIES.optics);
  renderChipGroup('chipsLighting', DICTIONARIES.lighting);
  renderChipGroup('chipsArtStyles', DICTIONARIES.art_styles);
  renderChipGroup('chipsVideoDynamics', DICTIONARIES.video_dynamics);

  const pGrid = document.getElementById('presetGrid');
  if (pGrid) {
    pGrid.innerHTML = '';
    PRESETS.forEach(p => {
      const card = document.createElement('div');
      card.className = 'preset-card-item';
      card.id = `preset-${p.id}`;
      card.innerHTML = `
        <div class="preset-item-name">${p.name}</div>
        <div class="preset-item-desc">${p.desc}</div>
      `;
      card.addEventListener('click', () => togglePreset(p));
      pGrid.appendChild(card);
    });
  }

  const negIn = document.getElementById('negativePromptInput');
  if (negIn) negIn.value = DEFAULT_NEGATIVE_PROMPT;
}

function renderChipGroup(containerId, list) {
  const container = document.getElementById(containerId);
  if (!container) return;
  container.innerHTML = '';
  list.forEach(item => {
    const chip = document.createElement('button');
    chip.className = 'chip-tag';
    chip.textContent = item.label;
    chip.dataset.val = item.val;
    chip.addEventListener('click', () => toggleChip(chip, item.val));
    container.appendChild(chip);
  });
}

function toggleChip(chipEl, tagVal) {
  const input = document.getElementById('promptInput');
  let current = input.value.trim();

  if (chipEl.classList.contains('active')) {
    chipEl.classList.remove('active');
    state.activeChips.delete(tagVal);
    const regex = new RegExp(`(,?\\s*${tagVal})`, 'gi');
    current = current.replace(regex, '').replace(/^,\s*/, '').trim();
  } else {
    chipEl.classList.add('active');
    state.activeChips.add(tagVal);
    if (current && !current.endsWith(',')) current += ', ';
    else if (current) current += ' ';
    current += tagVal;
  }
  input.value = current;
  updateCompiledPrompt();
}

function togglePreset(preset) {
  const card = document.getElementById(`preset-${preset.id}`);
  const isSelected = card && card.classList.contains('active');

  document.querySelectorAll('.preset-card-item').forEach(c => c.classList.remove('active'));

  if (isSelected) {
    state.activePresetId = null;
    state.activePresetSuffix = '';
  } else {
    if (card) card.classList.add('active');
    state.activePresetId = preset.id;
    state.activePresetSuffix = preset.suffix;
  }
  updateCompiledPrompt();
}

// Aspect Ratios
function initAspectRatios() {
  document.querySelectorAll('.flow-ratio-pill').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.flow-ratio-pill').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      state.selectedRatio = btn.dataset.ratio;
      updateCompiledPrompt();
    });
  });
}

// Form Inputs & Filters
function initFormInputs() {
  const promptIn = document.getElementById('promptInput');
  if (promptIn) {
    promptIn.addEventListener('input', () => {
      updatePromptCharCount();
      updateCompiledPrompt();
    });
    updatePromptCharCount();
  }

  const btnClear = document.getElementById('btnClearPrompt');
  if (btnClear) {
    btnClear.addEventListener('click', () => {
      if (promptIn) promptIn.value = '';
      document.querySelectorAll('.chip-tag').forEach(c => c.classList.remove('active'));
      document.querySelectorAll('.preset-card-item').forEach(c => c.classList.remove('active'));
      state.activeChips.clear();
      state.activePresetSuffix = '';
      updatePromptCharCount();
      updateCompiledPrompt();
    });
  }

  const btnCopyDirect = document.getElementById('btnCopyPromptDirect');
  if (btnCopyDirect) {
    btnCopyDirect.addEventListener('click', () => {
      if (promptIn) {
        navigator.clipboard.writeText(promptIn.value);
        showMiniToast('📋 Базовый текст скопирован!');
      }
    });
  }

  // AI Prompt Optimizer
  const btnAi = document.getElementById('btnAiOptimize');
  if (btnAi) {
    btnAi.addEventListener('click', () => {
      let cur = promptIn.value.trim();
      if (!cur) cur = 'Кинематографичная сцена в городе будущего';
      const enrichments = [
        "cinematic volumetric atmosphere, intricate photorealistic reflections, dramatic lighting",
        "shot on 85mm prime lens f/1.4, creamy background bokeh, hyper-detailed textures, Octane 8k",
        "golden hour natural sidelight, dramatic chiaroscuro shadows, award-winning cinematography",
        "hyper-detailed, authentic ambiance, natural shadows, crisp focus, 8k resolution"
      ];
      const picked = enrichments[Math.floor(Math.random() * enrichments.length)];
      promptIn.value = `${cur}, ${picked}`;
      showMiniToast('🪄 Промпт улучшен с помощью ИИ!');
      updatePromptCharCount();
      updateCompiledPrompt();
    });
  }

  // Filter Checkboxes
  ['chkRussianOnly', 'chkPhotoLikeness', 'chkZeroClutter', 'chkPromptWrapper', 'chkEnableNegative'].forEach(id => {
    const el = document.getElementById(id);
    if (el) {
      el.addEventListener('change', () => {
        state[id.replace('chk', '').charAt(0).toLowerCase() + id.replace('chk', '').slice(1)] = el.checked;
        updateCompiledPrompt();
      });
    }
  });

  const btnCopyComp = document.getElementById('btnCopyCompiled');
  if (btnCopyComp) {
    btnCopyComp.addEventListener('click', () => {
      const text = getCompiledPrompt();
      navigator.clipboard.writeText(text);
      showMiniToast('📋 Итоговый промпт скопирован!');
    });
  }

  const btnCopyTop = document.getElementById('btnCopyPromptTop');
  if (btnCopyTop) {
    btnCopyTop.addEventListener('click', () => {
      const text = getCompiledPrompt();
      navigator.clipboard.writeText(text);
      showMiniToast('📋 Итоговый промпт скопирован!');
    });
  }

  // Flow Injection Buttons
  const sendFlowHandler = () => {
    const text = getCompiledPrompt();
    postHostMessage({ type: 'SEND_TO_FLOW', prompt: text });
    showMiniToast('🚀 Промпт передан в Google Flow Canvas!');
  };

  const btnSendTop = document.getElementById('btnSendToFlow');
  if (btnSendTop) btnSendTop.addEventListener('click', sendFlowHandler);

  const btnSendBottom = document.getElementById('btnSendToFlowBottom');
  if (btnSendBottom) btnSendBottom.addEventListener('click', sendFlowHandler);
}

function updatePromptCharCount() {
  const pInput = document.getElementById('promptInput');
  const countEl = document.getElementById('promptCharCount');
  if (pInput && countEl) {
    countEl.textContent = `${pInput.value.length} симв.`;
  }
}

function getCompiledPrompt() {
  const baseEl = document.getElementById('promptInput');
  const base = baseEl ? baseEl.value.trim() : '';
  let result = base;

  if (state.activePresetSuffix) {
    result += state.activePresetSuffix;
  }

  const wrapperEl = document.getElementById('chkPromptWrapper');
  const wrapperEnabled = wrapperEl ? wrapperEl.checked : true;
  const russianOnly = document.getElementById('chkRussianOnly') ? document.getElementById('chkRussianOnly').checked : true;
  const photoLikeness = document.getElementById('chkPhotoLikeness') ? document.getElementById('chkPhotoLikeness').checked : true;
  const zeroClutter = document.getElementById('chkZeroClutter') ? document.getElementById('chkZeroClutter').checked : true;

  if (wrapperEnabled) {
    let rules = [];
    if (photoLikeness) rules.push("Exact likeness to reference images, preserve all facial features and clothing details");
    if (zeroClutter) rules.push("ONLY the specified characters and objects in the scene, absolutely NO extra people or random strangers");
    if (russianOnly) rules.push("All visible text and spoken dialogue strictly in Russian");
    rules.push("Masterpiece, 8k resolution, cinematic photorealism");

    result = `${result} | STRICT REQUIREMENTS: ${rules.join('. ')}.`;
  }

  return result;
}

function updateCompiledPrompt() {
  const display = document.getElementById('compiledPromptDisplay');
  if (display) {
    display.textContent = getCompiledPrompt();
  }
}

// Mini Toast Notification
function showMiniToast(msg) {
  let toast = document.getElementById('uiMiniToast');
  if (!toast) {
    toast = document.createElement('div');
    toast.id = 'uiMiniToast';
    toast.style.cssText = `
      position: fixed; bottom: 18px; right: 18px;
      background: #171822; color: #38bdf8; border: 1px solid #00d2ff;
      padding: 8px 16px; border-radius: 20px; font-size: 11px; font-weight: 700;
      box-shadow: 0 4px 18px rgba(0,0,0,0.6); z-index: 99999;
      transition: all 0.25s ease; opacity: 0; transform: translateY(8px);
    `;
    document.body.appendChild(toast);
  }
  toast.textContent = msg;
  toast.style.opacity = '1';
  toast.style.transform = 'translateY(0)';
  setTimeout(() => {
    toast.style.opacity = '0';
    toast.style.transform = 'translateY(8px)';
  }, 2800);
}

function postHostMessage(data) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(JSON.stringify(data));
  }
}

// Host Telemetry Callback
window.onTelemetryUpdate = function(data) {
  if (typeof data === 'string') {
    try { data = JSON.parse(data); } catch(e) {}
  }
  if (!data) return;

  const dgpuName = data.dgpu_name || 'Дискретная видеокарта (dGPU)';

  const tGpu = document.getElementById('telemetryDgpuName');
  if (tGpu) tGpu.textContent = `${dgpuName} (${data.dgpu_vram || 'dGPU'})`;
  const tGpuSub = document.getElementById('telemetryDgpuSub');
  if (tGpuSub) {
    const driverStr = data.dgpu_driver ? ` • Драйвер: ${data.dgpu_driver}` : '';
    tGpuSub.textContent = `DirectX 11 / Direct3D${driverStr} • GpuPreference=2 (High Performance)`;
  }

  if (data.cpu_name) {
    const tCpu = document.getElementById('telemetryCpuName');
    if (tCpu) tCpu.textContent = `${data.cpu_name}`;
    const tCpuSub = document.getElementById('telemetryCpuSub');
    if (tCpuSub) {
      const threads = data.cpu_threads || (data.cpu_cores ? data.cpu_cores * 2 : 12);
      tCpuSub.textContent = `${data.cpu_cores || 6} ядер, ${threads} потоков • Разгружен от декодирования видео`;
    }
  }

  if (data.total_ram_gb) {
    const tRamTot = document.getElementById('telemetryRamTotal');
    if (tRamTot) tRamTot.textContent = `${data.total_ram_gb} GB RAM (Свободно: ${data.free_ram_gb} GB)`;
    const tRamSub = document.getElementById('telemetryRamSub');
    if (tRamSub) tRamSub.textContent = `Процесс Flow: ~${data.app_ram_mb || 25} MB • Авто-тримминг активен`;
  }

  if (data.igpu_name) {
    const tIgpu = document.getElementById('telemetryIgpuName');
    if (tIgpu) tIgpu.textContent = `${data.igpu_name} (${data.igpu_vram || 'Shared VRAM'})`;
  }
};

// ==========================================
// OTA Updates Logic
// ==========================================
let pendingUpdateInfo = null;

function initUpdateControls() {
  const btnHeader = document.getElementById('btnCheckUpdateHeader');
  if (btnHeader) {
    btnHeader.addEventListener('click', () => {
      showMiniToast('🔍 Проверка обновлений на GitHub...');
      postHostMessage({ type: 'CHECK_UPDATES' });
    });
  }

  const btnClose = document.getElementById('btnUpdateModalClose');
  const btnLater = document.getElementById('btnUpdateModalLater');
  const backdrop = document.getElementById('updateModalBackdrop');

  if (btnClose) btnClose.addEventListener('click', () => backdrop.style.display = 'none');
  if (btnLater) btnLater.addEventListener('click', () => backdrop.style.display = 'none');

  const btnApply = document.getElementById('btnUpdateModalApply');
  if (btnApply) {
    btnApply.addEventListener('click', () => {
      if (!pendingUpdateInfo || !pendingUpdateInfo.DownloadUrl) {
        alert('Ссылка для загрузки обновления не найдена');
        return;
      }
      const pBox = document.getElementById('updateProgressBox');
      if (pBox) pBox.style.display = 'flex';
      btnApply.disabled = true;
      btnApply.textContent = '⏳ Загрузка обновления...';
      postHostMessage({ type: 'INSTALL_UPDATE', download_url: pendingUpdateInfo.DownloadUrl });
    });
  }
}

window.onUpdateAvailable = function(info) {
  showUpdateModal(info);
};

window.onUpdateCheckResult = function(info) {
  if (info && info.HasUpdate) {
    showUpdateModal(info);
  } else {
    showMiniToast('✅ У вас установлена самая последняя версия!');
  }
};

window.onUpdateDownloadProgress = function(pct) {
  const fill = document.getElementById('updateProgressFill');
  const pctLabel = document.getElementById('updateProgressPercent');
  if (fill) fill.style.width = pct + '%';
  if (pctLabel) pctLabel.textContent = pct + '%';
};

function showUpdateModal(info) {
  pendingUpdateInfo = info;
  const backdrop = document.getElementById('updateModalBackdrop');
  if (!backdrop) return;

  const curVer = document.getElementById('modalCurVer');
  const newVer = document.getElementById('modalNewVer');
  const relDate = document.getElementById('modalRelDate');
  if (curVer) curVer.textContent = `v${info.CurrentVersion || '1.0.0'}`;
  if (newVer) newVer.textContent = `v${info.LatestVersion || '1.0.0'}`;
  if (relDate) relDate.textContent = info.ReleaseDate || '2026-10-07';

  const list = document.getElementById('modalChangelogList');
  if (list) {
    list.innerHTML = '';
    if (info.ReleaseNotes && info.ReleaseNotes.length) {
      info.ReleaseNotes.forEach(note => {
        const li = document.createElement('li');
        li.textContent = note;
        list.appendChild(li);
      });
    } else {
      list.innerHTML = '<li>🚀 Улучшена производительность и стабильность работы.</li>';
    }
  }

  backdrop.style.display = 'flex';
  const btnApply = document.getElementById('btnUpdateModalApply');
  if (btnApply) {
    btnApply.disabled = false;
    btnApply.textContent = '🚀 Обновить и перезапустить';
  }
}
