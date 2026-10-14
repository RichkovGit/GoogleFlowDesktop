<div align="center">

# ⚡ Google Flow Desktop

### Нативное высокопроизводительное десктопное приложение для [flow.google.com](https://flow.google.com) под Windows

[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/RichkovGit/GoogleFlowDesktop)
[![Architecture: Native C#](https://img.shields.io/badge/Architecture-C%23%20%7C%20Native%20WinForms-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://github.com/RichkovGit/GoogleFlowDesktop)
[![Acceleration: DirectX 11 / Direct3D](https://img.shields.io/badge/GPU%20Decoders-DirectX%2011%20%7C%20dGPU%20(NVIDIA%20%7C%20AMD%20%7C%20Intel)-76B900?style=for-the-badge&logo=nvidia&logoColor=white)](https://github.com/RichkovGit/GoogleFlowDesktop)
[![Bypass: Labs Country Unlocker v4.1](https://img.shields.io/badge/Bypass-Google%20Labs%20Unlocker%20v4.1-00D2FF?style=for-the-badge)](https://github.com/RichkovGit/GoogleFlowDesktop)
[![OTA Updates: Live](https://img.shields.io/badge/OTA%20Updates-In--App%201--Click-34D399?style=for-the-badge)](https://github.com/RichkovGit/GoogleFlowDesktop)
[![License: MIT](https://img.shields.io/badge/License-MIT-F59E0B?style=for-the-badge)](LICENSE)

<br/>

**Google Flow Desktop** — нативное десктопное приложение для генерации видео и графики в Google Flow. Интерфейс полностью построен на настоящих Windows-контролах (WinForms + GDI+), а единственная веб-поверхность — это сам холст `flow.google.com` внутри Microsoft Edge WebView2 (Chromium). Приложение решает ключевые проблемы веб-версии на ноутбуках: перегрев процессора при рендеринге на встроенной графике, утечки оперативной памяти, отсутствие обхода геоблокировок и неудобство сборки сложных кинематографичных промптов.

</div>

---

## 🌟 Ключевые возможности

### 1. 🎮 Принудительное аппаратное ускорение на дискретной видеокарте (dGPU)
* **Проблема веб-версии:** При воспроизведении и прогрузке превью Google Flow стандартные браузеры используют встроенное видеоядро процессора (Intel UHD Graphics / AMD Radeon Graphics). Это приводит к 100% загрузке ЦП, шуму кулеров и троттлингу.
* **Решение:** 
  * Приложение автоматически прописывает политику высокой производительности в реестре Windows (`HKCU\Software\Microsoft\DirectX\UserGpuPreferences -> GpuPreference=2;`).
  * Флаги движка Chromium: `--force_high_performance_gpu --gpu-preference=2 --use-angle=d3d11 --enable-features=VaapiVideoDecoder,D3D11VideoDecoder,PlatformHEVCDecoderSupport --enable-accelerated-video-decode`.
  * Видеопотоки декодируются через выделенный блок аппаратного ускорения на дискретной видеокарте (dGPU: NVIDIA / AMD / Intel Arc), полностью освобождая центральный процессор.

### 2. 🔓 Встроенный обход геоблокировок (Google Labs Country Unlocker v4.1)
* Не требуется установка Tampermonkey, расширений или VPN.
* Автоматическая инъекция скрипта на этапе создания DOM (`document-start`):
  * Патч Google Closure Stream (RPC-методы `cPZSdc`, `KV2T2d`, `rThb8d`, `cO7JOb`, `md9xJf`).
  * Перехват Next.js tRPC ответов (`availabilityState = 'AVAILABLE'`).
  * Подавление принудительного логаута Google Labs и авто-перенаправление с экранов ошибки региона на рабочий холст.
* Поверх unlocker.js всегда добавляется нативный DOM-мост `window.__flowNativeBridge` для надёжной вставки промпта в React/Quill-инпуты Flow.

### 3. ◫ Нативный сплит-интерфейс Windows 11 Fluent Design
* **Постоянный хедер управления (Permanent Header):** Кнопки `[◫ Сплит]`, `[🎨 Только Flow]` и `[✨ Только Студия]` зафиксированы в шапке окна и доступны всегда. Вы никогда не потеряете доступ к панели управления.
* **Левая навигационная рейка (Rail):** Быстрое переключение между разделами `✨ Студия` и `🎮 Железо`, а также кнопки `🧹 RAM` и `🔄 OTA`.
* **Нативная тёмная тема:** своя палитра, скруглённые карточки, чипы-переключатели, анимированные тумблеры и тёмная строка заголовка Windows — всё отрисовано штатными средствами GDI+, без HTML/CSS.
* **Единственная веб-поверхность** — сам Google Flow внутри `WebView2`; никакого встроенного локального веб-сервера и HTML-панели больше нет.

### 4. ✨ Prompt Studio & Интеллектуальный сборщик промптов
* **Справочник тегов (Chips):**
  * **Оптика и ракурсы:** 35mm street photography, 85mm portrait lens, wide-angle dramatic perspective, cinematic low-angle hero shot, macro close-up, drone aerial view.
  * **Освещение:** golden hour natural backlight, dramatic chiaroscuro, cyberpunk neon rim lighting, soft diffused studio softbox, overcast moody daylight.
  * **Художественные стили:** photorealistic cinematic still, oil on textured canvas, vector flat illustration, 3D Pixar-style digital render, retro 80s anime OVA, isometric pixel art.
  * **Динамика видео:** smooth steady-cam tracking, slow cinematic pan left to right, dolly zoom vertigo effect, fast-paced handheld camera chase, timelapse sky transition.
* **1-Клик Пресеты:** Киберпанк & Неонуар, Фотореализм & Портрет, Эпическое & Тёмное фэнтези, 3D Мультфильм (Pixar), Аниме Ghibli, Ретро 80-е OVA, Коммерческая съёмка.
* **Соотношение сторон:** 1:1, 16:9, 9:16, 4:3, 3:4.
* **🪄 Улучшить с ИИ:** локальное обогащение промпта деталями (кино-свет, оптика, текстуры, разрешение).
* **Защитные фильтры:**
  * Строгий русский язык для всех текстовых элементов и диалогов.
  * Портретное сходство (100% likeness).
  * Фильтр «Zero Clutter» (запрет лишних людей и посторонних предметов).
  * Prompt Wrapper (техническая рамка Masterpiece 8K).
  * Универсальный блок негативных промптов (Negative Prompt Sanitizer).

### 5. 🚀 Мгновенная интеграция с Google Flow Canvas
* **1-Клик вставка во Flow:** Кнопка «🚀 Вставить в Google Flow Canvas» автоматически инжектирует готовый собранный промпт в поле ввода холста Flow через нативный DOM-мост, переводит фокус и копирует текст в буфер обмена Windows для удобства `Ctrl+V`.
* **📸 Снимок холста (Snapshot):** сохранение текущего кадра Google Flow в PNG в один клик в папку `Изображения\Google Flow Desktop`.

### 6. 🔄 Система обновлений "По воздуху" (OTA Updates)
* Приложение автоматически проверяет наличие новых версий на GitHub при запуске.
* Встроенное нативное диалоговое окно обновления с полным списком изменений (Release Notes).
* Автоматическая загрузка нового исполняемого файла и самообновление в 1 клик через `updater.bat` без необходимости ручного скачивания.
* Загруженный файл проверяется по сигнатуре PE (`MZ`), поэтому приложение не будет заменено HTML-страницей ошибки.

### 7. 📊 Живая телеметрия оборудования (WMI Telemetry)
* Динамическое определение характеристик конкретной системы:
  * Дискретный GPU и VRAM (динамическое определение любой NVIDIA GeForce / AMD Radeon / Intel Arc; корректный объём памяти > 4 ГБ через `HardwareInformation.qwMemorySize`).
  * Модель CPU, физические ядра и логические потоки (например, `11th Gen Intel Core i5-11400H, 6C/12T`).
  * Интегрированное видеоядро и его разгрузка.
  * Общий и свободный объем системной памяти (RAM).
  * Реальный объем памяти процесса в мегабайтах с обновлением каждые 2.5 секунды (в лёгком нативном таймере).
* **🧹 Очистка RAM** выполняется только вручную (кнопка на рейке или в разделе «Железо») — без агрессивной фоновой сборки мусора.

### 8. 🖥️ Нативный пользовательский опыт
* Запоминание размера, позиции окна, положения сплиттера и режима просмотра между запусками.
* Запрет повторного запуска приложения (single instance).
* Нативные уведомления Windows (balloon) без запуска PowerShell.
* Горячие клавиши: `F5` — перезагрузить Flow, `Ctrl+Enter` — вставить промпт, `Ctrl+Shift+S` — снимок, `Ctrl+1/2/3` — режимы Сплит / Только Flow / Только Студия.

---

## 🚀 Быстрый запуск

### Вариант 1: Запуск скомпилированного EXE
Просто запустите файл:
```bash
GoogleFlowDesktop.exe
```

### Вариант 2: Запуск через скрипт
Запустите батник:
```bash
run.bat
```

### Вариант 3: Перекомпиляция исходников в 1 клик
Если вы внесли изменения в исходный код C#, запустите:
```bash
build.bat
```
Скрипт использует стандартный компилятор `csc.exe` из поставки Windows (.NET Framework 4.8) и мгновенно пересобирает бинарник без необходимости установки тяжелых IDE.

---

## 🏗️ Архитектура системы

```text
┌────────────────────────────────────────────────────────────────────────┐
│                   GoogleFlowDesktop.exe (Native C# WinForms)           │
├────────────────────────────────────────────────────────────────────────┤
│  [Header: Split | Only Flow | Only Studio | Snapshot | Folder | OTA]   │
├───────────┬──────────────────────────────────┬─────────────────────────┤
│  Rail     │  Panel 1: Prompt Studio          │  Panel 2: Google Flow   │
│  ✨ Студия │  - Native WinForms controls      │  - Microsoft Edge       │
│  🎮 Железо │  - Chips, Presets, Filters       │    WebView2 (Chromium)  │
│  🧹 RAM   │  - Compiled Prompt Preview       │  - Target: flow.google  │
│  🔄 OTA   │  - 1-Click Prompt Injector       │  - Injected: unlocker   │
│           │  - AI Prompt Optimizer           │    + native DOM bridge  │
│           │  -> NativeTelemetry (page 2)     │  - Direct3D 11 / dGPU   │
├───────────┴──────────────────────────────────┴─────────────────────────┤
│                         Native C# Backend Services                     │
│  ├── Theme.cs / ControlsExtra.cs -> Fluent dark theme + custom controls│
│  ├── GpuConfig.cs        -> DirectX 11 Registry & WMI Telemetry        │
│  ├── UpdateManager.cs    -> GitHub Live Version Checker & OTA Updater  │
│  ├── Notifier.cs         -> Native Windows Balloon Notifications       │
│  └── ClipboardUtil.cs    -> STA-safe clipboard access                  │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 📁 Структура репозитория

```text
GoogleFlowDesktop/
├── GoogleFlowDesktop.exe        # Скомпилированный нативный бинарник (Windows x64)
├── run.bat                      # Скрипт быстрого запуска
├── build.bat                    # Скрипт перекомпиляции C# исходников (1 клик)
├── version.json                 # Манифест актуальной версии для OTA-обновлений
├── unlocker.js                  # Скрипт Google Labs Country Unlocker v4.1
├── LICENSE                      # Лицензия MIT
├── README.md                    # Документация проекта
├── Microsoft.Web.WebView2.Core.dll      # Ядро движка WebView2
├── Microsoft.Web.WebView2.WinForms.dll  # WinForms-контрол WebView2
├── WebView2Loader.dll                   # Нативный загрузчик WebView2
└── src/                         # Исходный код C# (.NET)
    ├── Program.cs               # Точка входа WinForms, single instance, защита от сбоев
    ├── MainForm.cs              # Главное окно: Header, Rail, SplitContainer, WebView2, снимки
    ├── Theme.cs                 # Палитра Fluent Dark, CardLayout, FlatButton, тёмный titlebar
    ├── ControlsExtra.cs         # Собственные контролы: InputHost, ToggleSwitch, WrapPanel, Ui
    ├── NativeStudio.cs          # Prompt Studio на нативных WinForms-контролах
    ├── NativeTelemetry.cs       # Страница «Железо» на нативных контролах
    ├── PromptKit.cs             # Данные тегов/пресетов и конвейер сборки промпта
    ├── GpuConfig.cs             # WMI детекция железа, реестр DirectX, триммер ОЗУ
    ├── UnlockerScript.cs        # Провайдер unlocker.js + нативный DOM-мост вставки
    ├── UpdateManager.cs         # Фоновый чекер релизов GitHub и OTA апдейтер
    ├── UpdateDialog.cs          # Нативное диалоговое окно обновления
    ├── Notifier.cs              # Нативные Windows balloon-уведомления
    ├── ClipboardUtil.cs         # STA-безопасная работа с буфером обмена
    └── AppIcon.cs               # Генерация иконки приложения в рантайме
```

---

## 💻 Системные требования

* **ОС:** Windows 10 (1809+) или Windows 11 (x64)
* **Видеокарта:** Дискретная видеокарта (dGPU) NVIDIA / AMD / Intel с поддержкой DirectX 11 / Direct3D
* **Процессор:** Intel Core i3 / AMD Ryzen 3 или лучше
* **Оперативная память:** от 4 ГБ RAM (приложение потребляет всего ~50 МБ)
* **Среда выполнения:** Microsoft Edge WebView2 Runtime (встроена в Windows 10/11 по умолчанию)

---

## 🗒️ Что нового в v1.1.0

* 🪟 **Полностью нативный интерфейс:** Prompt Studio переписан настоящими Windows-контролами (WinForms); удалены встроенная HTML/CSS/JS-панель (`ui/`) и локальный веб-сервер (`LocalServer.cs`), а также фиктивные движки `TaskEngine.cs` / `WorkerDispatcher.cs`.
* 🎨 **Обновлённый дизайн:** тёмная Fluent-тема, нативная тёмная строка заголовка, скруглённые карточки, чипы и анимированные тумблеры.
* 🖼️ **Навигация:** левая рейка «Студия / Железо», сплит-режим и кнопки «Только Flow» / «Только Студия».
* 📸 **Снимок холста Flow** в PNG (`Ctrl+Shift+S`) с сохранением в `Изображения\Google Flow Desktop`.
* 🎯 **Исправлено** определение VRAM (dGPU > 4 ГБ), падение при запуске из-за `SplitContainer`, вставку промпта в React-инпуты Flow.
* 🧹 Убрана агрессивная сборка мусора каждые 2.5 с; очистка памяти — только вручную.
* 🔔 Нативные уведомления, OTA с проверкой PE-сигнатуры, сохранение состояния окна, запрет повторного запуска.

---

## 📄 Лицензия

Проект распространяется под открытой лицензией [MIT](LICENSE).

Автор: **[RichkovGit](https://github.com/RichkovGit)**
