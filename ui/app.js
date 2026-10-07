/**
 * Google Flow Desktop - Studio Frontend Logic (Google Flow Obsidian Edition)
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
  enableNegative: true,
  
  // Tasks & Gallery
  tasks: {},
  galleryItems: [],
  selectedGalleryItem: null
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
  initBatchGenerators();
  initQueueControls();
  initMediaStack();
  initUpdateControls();
  updateCompiledPrompt();

  postHostMessage({ type: 'UI_READY' });
});

// Navigation (Google Flow Vertical Rail)
function setupNavigation() {
  document.querySelectorAll('.flow-nav-item[data-tab], .uwp-tab[data-tab]').forEach(btn => {
    btn.addEventListener('click', () => {
      const tab = btn.dataset.tab;
      document.querySelectorAll('.flow-nav-item[data-tab], .uwp-tab[data-tab]').forEach(b => b.classList.remove('active'));
      document.querySelectorAll('.flow-tab-pane, .tab-pane').forEach(p => p.classList.remove('active'));
      
      btn.classList.add('active');
      const pane = document.getElementById(`tab-${tab}`);
      if (pane) pane.classList.add('active');
      state.currentTab = tab;
    });
  });

  // Tools in rail
  const btnFolder = document.getElementById('btnOpenFolder');
  if (btnFolder) {
    btnFolder.addEventListener('click', () => {
      postHostMessage({ type: 'OPEN_PROJECTS_FOLDER' });
    });
  }

  const btnTrim = document.getElementById('btnTrimRamQuick');
  if (btnTrim) {
    btnTrim.addEventListener('click', () => {
      postHostMessage({ type: 'TRIM_MEMORY' });
      showMiniToast('🧹 Память процесса успешно очищена!');
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
  document.querySelectorAll('.flow-ratio-pill, .ratio-chip').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.flow-ratio-pill, .ratio-chip').forEach(b => b.classList.remove('active'));
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

  // Flow Injection Buttons
  const sendFlowHandler = () => {
    const text = getCompiledPrompt();
    postHostMessage({ type: 'SEND_TO_FLOW', prompt: text });
    showMiniToast('🚀 Промпт отправлен во Flow Canvas!');
  };

  const btnSendTop = document.getElementById('btnSendToFlow');
  if (btnSendTop) btnSendTop.addEventListener('click', sendFlowHandler);

  const btnSendBottom = document.getElementById('btnSendToFlowBottom');
  if (btnSendBottom) btnSendBottom.addEventListener('click', sendFlowHandler);

  // Queue Addition Buttons
  const queueAddHandler = () => {
    triggerSingleGeneration();
  };

  const btnGenNow = document.getElementById('btnGenerateNow');
  if (btnGenNow) btnGenNow.addEventListener('click', queueAddHandler);

  const btnGenNowBottom = document.getElementById('btnGenerateNowBottom');
  if (btnGenNowBottom) btnGenNowBottom.addEventListener('click', queueAddHandler);
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

function triggerSingleGeneration() {
  const finalPrompt = getCompiledPrompt();
  const negEl = document.getElementById('chkEnableNegative');
  const negInput = document.getElementById('negativePromptInput');
  const negPrompt = (negEl && negEl.checked && negInput) ? negInput.value.trim() : '';

  const taskId = 'task_' + Math.random().toString(16).slice(2, 10);
  const seed = Math.floor(Math.random() * 4294967295);

  const task = {
    task_id: taskId,
    prompt: finalPrompt,
    negative_prompt: negPrompt,
    aspect_ratio: state.selectedRatio,
    media_type: 'image',
    seed: seed,
    status: 'waiting',
    progress: 0,
    model: 'Flow High-Quality (dGPU)'
  };

  state.tasks[taskId] = task;
  renderQueue();
  postHostMessage({ type: 'ADD_SINGLE_TASK', task: task });
  showMiniToast('⚡ Задача добавлена в очередь!');

  const tabQueue = document.querySelector('.flow-nav-item[data-tab="queue"], .uwp-tab[data-tab="queue"]');
  if (tabQueue) tabQueue.click();
}

// Batch Generators
function initBatchGenerators() {
  document.querySelectorAll('.flow-segmented-pills .sub-seg-btn, .sub-segmented .segmented-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.sub-seg-btn, .segmented-btn').forEach(b => b.classList.remove('active'));
      document.querySelectorAll('.batch-sub-panel, .sub-pane').forEach(p => p.classList.remove('active'));
      btn.classList.add('active');
      const target = document.getElementById(`batch-sub-${btn.dataset.sub}`);
      if (target) target.classList.add('active');
    });
  });

  let seedCount = 4;
  const countLabel = document.getElementById('seedCountLabel');
  const btnMinus = document.getElementById('btnSeedMinus');
  const btnPlus = document.getElementById('btnSeedPlus');

  if (btnMinus) {
    btnMinus.addEventListener('click', () => {
      seedCount = Math.max(1, seedCount - 1);
      if (countLabel) countLabel.textContent = seedCount;
    });
  }
  if (btnPlus) {
    btnPlus.addEventListener('click', () => {
      seedCount = Math.min(16, seedCount + 1);
      if (countLabel) countLabel.textContent = seedCount;
    });
  }

  // Launch Multi-Seed
  const btnLaunchMulti = document.getElementById('btnLaunchMultiSeed');
  if (btnLaunchMulti) {
    btnLaunchMulti.addEventListener('click', () => {
      const prompt = document.getElementById('multiSeedPrompt').value.trim();
      const mediaType = document.getElementById('multiSeedType').value;
      const negInput = document.getElementById('negativePromptInput');
      postHostMessage({
        type: 'LAUNCH_MULTI_SEED',
        prompt: prompt,
        count: seedCount,
        media_type: mediaType,
        aspect_ratio: state.selectedRatio,
        negative_prompt: negInput ? negInput.value.trim() : ''
      });
      showMiniToast(`🌱 Пакет из ${seedCount} задач отправлен в очередь!`);
      const tabQueue = document.querySelector('.flow-nav-item[data-tab="queue"], .uwp-tab[data-tab="queue"]');
      if (tabQueue) tabQueue.click();
    });
  }

  const btnSendMultiFlow = document.getElementById('btnSendMultiSeedToFlow');
  if (btnSendMultiFlow) {
    btnSendMultiFlow.addEventListener('click', () => {
      const prompt = document.getElementById('multiSeedPrompt').value.trim();
      postHostMessage({ type: 'SEND_TO_FLOW', prompt: prompt });
      showMiniToast('🚀 Промпт отправлен во Flow Canvas!');
    });
  }

  // Cartesian Matrix
  const sList = document.getElementById('cartesianStylesList');
  if (sList) {
    sList.innerHTML = '';
    DICTIONARIES.art_styles.forEach(item => {
      sList.innerHTML += `
        <label class="cb-item">
          <input type="checkbox" class="cb-cart-style" value="${item.val}" checked>
          <span>${item.label}</span>
        </label>
      `;
    });
  }

  const lList = document.getElementById('cartesianLightsList');
  if (lList) {
    lList.innerHTML = '';
    DICTIONARIES.lighting.forEach(item => {
      lList.innerHTML += `
        <label class="cb-item">
          <input type="checkbox" class="cb-cart-light" value="${item.val}" checked>
          <span>${item.label}</span>
        </label>
      `;
    });
  }

  function updateCartesianCount() {
    const sCount = document.querySelectorAll('.cb-cart-style:checked').length;
    const lCount = document.querySelectorAll('.cb-cart-light:checked').length;
    const badge = document.getElementById('cartesianCountBadge');
    if (badge) badge.textContent = sCount * lCount;
  }

  document.querySelectorAll('.cb-cart-style, .cb-cart-light').forEach(cb => {
    cb.addEventListener('change', updateCartesianCount);
  });
  updateCartesianCount();

  const btnLaunchCart = document.getElementById('btnLaunchCartesian');
  if (btnLaunchCart) {
    btnLaunchCart.addEventListener('click', () => {
      const subject = document.getElementById('cartesianSubject').value.trim();
      const styles = Array.from(document.querySelectorAll('.cb-cart-style:checked')).map(c => c.value);
      const lights = Array.from(document.querySelectorAll('.cb-cart-light:checked')).map(c => c.value);
      const totalCount = styles.length * lights.length;
      if (totalCount === 0) {
        alert('Пожалуйста, выберите хотя бы один стиль и одно освещение');
        return;
      }

      const negInput = document.getElementById('negativePromptInput');
      postHostMessage({
        type: 'LAUNCH_CARTESIAN',
        subject: subject,
        styles: styles,
        lights: lights,
        aspect_ratio: state.selectedRatio,
        negative_prompt: negInput ? negInput.value.trim() : ''
      });
      showMiniToast(`✖️ Матричный пакет из ${totalCount} задач отправлен в очередь!`);
      const tabQueue = document.querySelector('.flow-nav-item[data-tab="queue"], .uwp-tab[data-tab="queue"]');
      if (tabQueue) tabQueue.click();
    });
  }

  const btnSendCartFlow = document.getElementById('btnSendCartesianToFlow');
  if (btnSendCartFlow) {
    btnSendCartFlow.addEventListener('click', () => {
      const subject = document.getElementById('cartesianSubject').value.trim();
      const styles = Array.from(document.querySelectorAll('.cb-cart-style:checked')).map(c => c.value);
      const lights = Array.from(document.querySelectorAll('.cb-cart-light:checked')).map(c => c.value);
      const p = `${subject}, ${styles[0] || ''}, ${lights[0] || ''}`.replace(/,\s*,/g, ',');
      postHostMessage({ type: 'SEND_TO_FLOW', prompt: p });
      showMiniToast('🚀 Первый матричный промпт отправлен во Flow Canvas!');
    });
  }

  // Reference Mapper
  const dropzone = document.getElementById('refDropzone');
  const fileInput = document.getElementById('refFileInput');
  if (dropzone && fileInput) {
    dropzone.addEventListener('click', () => fileInput.click());
    fileInput.addEventListener('change', (e) => {
      selectedRefFiles = Array.from(e.target.files);
      renderRefFiles();
    });
  }

  let selectedRefFiles = [];
  function renderRefFiles() {
    const list = document.getElementById('refFilesList');
    if (list) {
      list.innerHTML = selectedRefFiles.map(f => `<span class="file-chip">📄 ${f.name}</span>`).join('');
    }
  }

  const btnLaunchRef = document.getElementById('btnLaunchRefMapper');
  if (btnLaunchRef) {
    btnLaunchRef.addEventListener('click', () => {
      const prompt = document.getElementById('refMapperPrompt').value.trim();
      const paths = selectedRefFiles.map(f => f.name || f.path);
      if (!paths.length) {
        alert('Пожалуйста, выберите изображения-референсы');
        return;
      }
      const negInput = document.getElementById('negativePromptInput');
      postHostMessage({
        type: 'LAUNCH_REF_MAPPER',
        prompt: prompt,
        paths: paths,
        aspect_ratio: state.selectedRatio,
        negative_prompt: negInput ? negInput.value.trim() : ''
      });
      showMiniToast(`🖼️ Reference пакет из ${paths.length} задач отправлен в очередь!`);
      const tabQueue = document.querySelector('.flow-nav-item[data-tab="queue"], .uwp-tab[data-tab="queue"]');
      if (tabQueue) tabQueue.click();
    });
  }
}

// Queue Controls
function initQueueControls() {
  const btnPause = document.getElementById('btnPauseQueue');
  if (btnPause) {
    btnPause.addEventListener('click', (e) => {
      const isPaused = e.target.textContent.includes('Возобновить');
      if (isPaused) {
        e.target.textContent = '⏸️ Пауза';
        postHostMessage({ type: 'RESUME_QUEUE' });
        showMiniToast('▶️ Очередь возобновлена');
      } else {
        e.target.textContent = '▶️ Возобновить';
        postHostMessage({ type: 'PAUSE_QUEUE' });
        showMiniToast('⏸️ Очередь на паузе');
      }
    });
  }

  const btnClear = document.getElementById('btnClearQueue');
  if (btnClear) {
    btnClear.addEventListener('click', () => {
      postHostMessage({ type: 'CLEAR_COMPLETED_TASKS' });
      for (let tid in state.tasks) {
        if (state.tasks[tid].status === 'completed' || state.tasks[tid].status === 'failed') {
          delete state.tasks[tid];
        }
      }
      renderQueue();
      showMiniToast('🧹 Завершенные задачи очищены');
    });
  }
}

function renderQueue() {
  const grid = document.getElementById('queueGrid');
  if (!grid) return;
  const taskIds = Object.keys(state.tasks);

  const badge = document.getElementById('queueCountBadge');
  if (badge) badge.textContent = taskIds.length;
  const statTot = document.getElementById('qStatTotal');
  if (statTot) statTot.textContent = taskIds.length;

  let active = 0, done = 0, failed = 0;
  taskIds.forEach(id => {
    const t = state.tasks[id];
    if (t.status === 'generating') active++;
    else if (t.status === 'completed') done++;
    else if (t.status === 'failed') failed++;
  });

  const sAct = document.getElementById('qStatActive');
  if (sAct) sAct.textContent = active;
  const sDone = document.getElementById('qStatDone');
  if (sDone) sDone.textContent = done;
  const sFail = document.getElementById('qStatFailed');
  if (sFail) sFail.textContent = failed;

  if (taskIds.length === 0) {
    grid.innerHTML = `
      <div class="queue-empty-state" id="queueEmptyState">
        <span class="empty-icon">📋</span>
        <h4>Очередь задач пуста</h4>
        <p>Создайте промпт в Студии или запустите Пакетный генератор для начала рендера</p>
      </div>
    `;
    return;
  }

  const empty = document.getElementById('queueEmptyState');
  if (empty) empty.remove();

  const statusLabels = {
    'waiting': 'В очереди',
    'generating': 'Рендер dGPU',
    'completed': 'Готово',
    'failed': 'Ошибка'
  };

  const reversed = [...taskIds].reverse();
  reversed.forEach(tid => {
    const task = state.tasks[tid];
    let card = document.getElementById(`task-card-${tid}`);
    if (!card) {
      card = document.createElement('div');
      card.id = `task-card-${tid}`;
      grid.appendChild(card);
    }
    card.className = `task-card ${task.status}`;

    const isDone = task.status === 'completed';
    const isGenerating = task.status === 'generating';
    const progress = task.progress !== undefined ? task.progress : (isDone ? 100 : 0);

    card.innerHTML = `
      <div class="tc-header">
        <span class="tc-id">${tid}</span>
        <span class="tc-status-tag status-${task.status}">
          ${isGenerating ? '⚡ ' : (isDone ? '✅ ' : '')}${statusLabels[task.status] || task.status}
        </span>
      </div>
      <div class="tc-prompt" title="${escapeHtml(task.prompt)}">${escapeHtml(task.prompt)}</div>
      <div class="tc-meta">
        <span>🎲 Seed: #${task.seed !== undefined ? task.seed : 'auto'}</span>
        <span>📐 ${task.aspect_ratio || '16:9'}</span>
        <span>${task.media_type === 'video' ? '🎬 Видео' : '🖼️ Картинка'}</span>
      </div>
      <div class="progress-track">
        <div class="progress-fill" style="width: ${progress}%;"></div>
      </div>
      <div class="tc-actions-bar">
        <button class="tc-btn tc-btn-flow" onclick="sendTaskPromptToFlow('${tid}')" title="Вставить этот промпт в Google Flow Canvas">
          🚀 Во Flow Canvas
        </button>
        <button class="tc-btn tc-btn-ghost" onclick="copyTaskPrompt('${tid}')" title="Скопировать промпт в буфер">
          📋 Копировать
        </button>
        ${isDone ? `<button class="tc-btn tc-btn-ghost" onclick="viewTaskResult('${tid}')">👁️ Просмотр</button>` : ''}
        ${task.status === 'failed' ? `<button class="tc-btn tc-btn-ghost" onclick="retryTask('${tid}')">🔄 Повторить</button>` : ''}
      </div>
    `;
  });
}

function escapeHtml(str) {
  if (!str) return '';
  return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

window.sendTaskPromptToFlow = function(tid) {
  const task = state.tasks[tid];
  if (task && task.prompt) {
    postHostMessage({ type: 'SEND_TO_FLOW', prompt: task.prompt });
    showMiniToast('🚀 Промпт задачи отправлен в Google Flow Canvas!');
  }
};

window.copyTaskPrompt = function(tid) {
  const task = state.tasks[tid];
  if (task && task.prompt) {
    navigator.clipboard.writeText(task.prompt);
    showMiniToast('📋 Промпт скопирован в буфер!');
  }
};

window.viewTaskResult = function(tid) {
  postHostMessage({ type: 'REFRESH_GALLERY' });
  const tabMedia = document.querySelector('.flow-nav-item[data-tab="media"], .uwp-tab[data-tab="media"]');
  if (tabMedia) tabMedia.click();
};

window.retryTask = function(tid) {
  postHostMessage({ type: 'RETRY_TASK', task_id: tid });
  showMiniToast('🔄 Перезапуск задачи...');
};

// Media Stack
function initMediaStack() {
  const btnRef = document.getElementById('btnRefreshGallery');
  if (btnRef) btnRef.addEventListener('click', () => postHostMessage({ type: 'REFRESH_GALLERY' }));

  const btnExp = document.getElementById('btnOpenInExplorer');
  if (btnExp) btnExp.addEventListener('click', () => postHostMessage({ type: 'OPEN_PROJECTS_FOLDER' }));

  const btnReuse = document.getElementById('btnReusePrompt');
  if (btnReuse) {
    btnReuse.addEventListener('click', () => {
      if (state.selectedGalleryItem && state.selectedGalleryItem.metadata) {
        const p = state.selectedGalleryItem.metadata.prompt;
        if (p) {
          postHostMessage({ type: 'SEND_TO_FLOW', prompt: p });
          showMiniToast('🚀 Промпт из истории отправлен во Flow Canvas!');
        }
      }
    });
  }

  const btnTrim = document.getElementById('btnTrimMemoryNow');
  if (btnTrim) {
    btnTrim.addEventListener('click', () => {
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

function renderGallery(items) {
  state.galleryItems = items || [];
  const gGrid = document.getElementById('galleryGrid');
  if (!gGrid) return;

  if (!items || items.length === 0) {
    gGrid.innerHTML = '<p class="card-hint" style="grid-column: 1/-1; padding: 20px;">Папка проектов пуста. Запустите генерацию для создания медиа.</p>';
    return;
  }

  gGrid.innerHTML = '';
  items.forEach(item => {
    const card = document.createElement('div');
    card.className = 'gallery-thumb-card';
    card.innerHTML = `
      <img src="${item.media_url}" alt="Preview" onerror="this.src='data:image/svg+xml,<svg xmlns=%22http://www.w3.org/2000/svg%22 width=%22200%22 height=%22140%22><rect width=%22100%%22 height=%22100%%22 fill=%22%23181b24%22/><text x=%2250%%22 y=%2250%%22 fill=%22%2300d2ff%22 text-anchor=%22middle%22 dy=%22.3em%22>Media</text></svg>'">
      <div class="gallery-info">
        <div class="gallery-info-title">${item.filename}</div>
        <div class="gallery-info-sub">Seed: #${item.metadata ? item.metadata.seed : 'N/A'}</div>
      </div>
    `;
    card.addEventListener('click', () => selectGalleryItem(item));
    gGrid.appendChild(card);
  });
}

function selectGalleryItem(item) {
  state.selectedGalleryItem = item;
  const jsonContent = document.getElementById('sidecarJsonContent');
  if (jsonContent) {
    jsonContent.textContent = JSON.stringify(item.metadata || {}, null, 2);
  }
  const actBar = document.getElementById('stackActionsBar');
  if (actBar) actBar.style.display = 'flex';

  if (item.metadata) {
    const srcL = document.getElementById('vSrcLabel');
    const varL = document.getElementById('vVarLabel');
    const upL = document.getElementById('vUpscaleLabel');
    if (srcL) srcL.textContent = item.filename;
    if (varL) varL.textContent = `Seed #${item.metadata.seed}`;
    if (upL) upL.textContent = `${item.metadata.aspect_ratio || '16:9'} Active`;
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

// Host Event Callbacks
window.onTaskAdded = function(task) {
  if (typeof task === 'string') {
    try { task = JSON.parse(task); } catch(e) {}
  }
  if (!task || !task.task_id) return;
  state.tasks[task.task_id] = {
    task_id: task.task_id,
    prompt: task.prompt || 'Flow Prompt',
    negative_prompt: task.negative_prompt || '',
    seed: task.seed !== undefined ? task.seed : 'auto',
    aspect_ratio: task.aspect_ratio || '16:9',
    media_type: task.media_type || 'image',
    status: task.status || 'waiting',
    progress: task.progress || 0
  };
  renderQueue();
};

window.onBatchStarted = function(batch_id, total) {
  showMiniToast(`🚀 Пакет запущен: ${total} задач (dGPU)`);
};

window.onItemProgress = function(task_id, status, percent) {
  if (!state.tasks[task_id]) {
    state.tasks[task_id] = { task_id: task_id, status: status, progress: percent };
  } else {
    state.tasks[task_id].status = status;
    state.tasks[task_id].progress = percent;
  }
  renderQueue();
};

window.onItemCompleted = function(task_id, filePath, sidecarPath) {
  if (state.tasks[task_id]) {
    state.tasks[task_id].status = 'completed';
    state.tasks[task_id].progress = 100;
    state.tasks[task_id].output_path = filePath;
  }
  renderQueue();
  postHostMessage({ type: 'REFRESH_GALLERY' });
};

window.onItemFailed = function(task_id, errorMsg) {
  if (state.tasks[task_id]) {
    state.tasks[task_id].status = 'failed';
    state.tasks[task_id].error_message = errorMsg;
  }
  renderQueue();
};

window.onBatchFinished = function(batch_id, success, failed) {
  showMiniToast(`🎉 Пакет завершён! Успешно: ${success}, Ошибок: ${failed}`);
};

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
    if (tRamSub) tRamSub.textContent = `Процесс Flow: ~${data.app_ram_mb || 25} MB • Zero-Memory SSD Streamer активен`;
  }

  if (data.igpu_name) {
    const tIgpu = document.getElementById('telemetryIgpuName');
    if (tIgpu) tIgpu.textContent = `${data.igpu_name} (${data.igpu_vram || 'Shared VRAM'})`;
  }
};

window.onGalleryUpdate = function(items) {
  renderGallery(items);
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
