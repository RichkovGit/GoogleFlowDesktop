<div align="center">

# ⚡ Google Flow Desktop

### Нативное высокопроизводительное десктопное приложение для [flow.google.com](https://flow.google.com) под Windows

[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/RichkovGit/GoogleFlowDesktop)
[![Architecture: C# .NET](https://img.shields.io/badge/Architecture-C%23%20%7C%20WinForms%20%2B%20Edge%20WebView2-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://github.com/RichkovGit/GoogleFlowDesktop)
[![Acceleration: DirectX 11 / NVDEC](https://img.shields.io/badge/GPU%20Decoders-NVIDIA%20GTX%201650%20(dGPU)-76B900?style=for-the-badge&logo=nvidia&logoColor=white)](https://github.com/RichkovGit/GoogleFlowDesktop)
[![Bypass: Labs Country Unlocker v4.1](https://img.shields.io/badge/Bypass-Google%20Labs%20Unlocker%20v4.1-00D2FF?style=for-the-badge)](https://github.com/RichkovGit/GoogleFlowDesktop)
[![OTA Updates: Live](https://img.shields.io/badge/OTA%20Updates-In--App%201--Click-34D399?style=for-the-badge)](https://github.com/RichkovGit/GoogleFlowDesktop)
[![License: MIT](https://img.shields.io/badge/License-MIT-F59E0B?style=for-the-badge)](LICENSE)

<br/>

**Google Flow Desktop** — нативное десктопное приложение для генерации видео и графики в Google Flow на базе Microsoft Edge WebView2 (Chromium). Решает ключевые проблемы веб-версии на ноутбуках: перегрев процессора при рендеринге на встроенной графике, утечки оперативной памяти, отсутствие обхода геоблокировок и неудобство сборки сложных кинематографичных промптов.

</div>

---

## 🌟 Ключевые возможности

### 1. 🎮 Принудительное аппаратное ускорение на дискретной видеокарте (dGPU)
* **Проблема веб-версии:** При воспроизведении и прогрузке превью Google Flow стандартные браузеры используют встроенное видеоядро процессора (Intel UHD Graphics / AMD Radeon Graphics). Это приводит к 100% загрузке ЦП, шуму кулеров и троттлингу.
* **Решение:** 
  * Приложение автоматически прописывает политику высокой производительности в реестре Windows (`HKCU\Software\Microsoft\DirectX\UserGpuPreferences -> GpuPreference=2;`).
  * Флаги движка Chromium: `--force_high_performance_gpu --gpu-preference=2 --enable-features=VaapiVideoDecoder,D3D11VideoDecoder,PlatformHEVCDecoderSupport --enable-accelerated-video-decode`.
  * Видеопотоки декодируются через выделенный блок **NVDEC** на видеокарте например NVIDIA GeForce GTX 1650, освобождая процессор для фоновых задач.

### 2. 🔓 Встроенный обход геоблокировок (Google Labs Country Unlocker v4.1)
* Не требуется установка Tampermonkey, расширений или VPN.
* Автоматическая инъекция скрипта на этапе создания DOM (`document-start`):
  * Патч Google Closure Stream (RPC-методы `cPZSdc`, `KV2T2d`, `rThb8d`, `cO7JOb`, `md9xJf`).
  * Перехват Next.js tRPC ответов (`availabilityState = 'AVAILABLE'`).
  * Подавление принудительного логаута Google Labs и авто-перенаправление с экранов ошибки региона на рабочий холст.

### 3. ◫ Двухоконный сплит-интерфейс Windows 11 Fluent Design
* **Постоянный хедер управления (Permanent Header):** Кнопки `[◫ Сплит (50/50)]`, `[🎨 Только Flow]` и `[✨ Только Студия]` зафиксированы в шапке окна и доступны всегда. Вы никогда не потеряете доступ к панели управления.
* **Плавающий док возврата:** Непосредственно на холст `flow.google.com` встроен элегантный виджет `⚡ Flow [◫ Сплит] [✨ Студия]`, позволяющий выйти из полноэкранного режима Flow в один клик.

### 4. ✨ Prompt Studio & Интеллектуальный сборщик промптов
* **Справочник тегов (Chips):**
  * **Оптика и ракурсы:** 35mm street photography, 85mm portrait lens, wide-angle dramatic perspective, cinematic low-angle hero shot, macro close-up, drone aerial view.
  * **Освещение:** golden hour natural backlight, dramatic chiaroscuro, cyberpunk neon rim lighting, soft diffused studio softbox, overcast moody daylight.
  * **Художественные стили:** photorealistic cinematic still, oil on textured canvas, vector flat illustration, 3D Pixar-style digital render, retro 80s anime OVA, isometric pixel art.
  * **Динамика видео:** smooth steady-cam tracking, slow cinematic pan left to right, dolly zoom vertigo effect, fast-paced handheld camera chase, timelapse sky transition.
* **1-Клик Пресеты:** Киберпанк & Неонуар, Фотореализм & Портрет, Эпическое фэнтези, 3D Мультфильм Pixar, Аниме Ghibli, Ретро 80-е OVA, Предметная съёмка.
* **Защитные фильтры:**
  * Строгий русский язык для всех текстовых элементов и диалогов.
  * Портретное сходство (100% likeness).
  * Фильтр «Zero Clutter» (запрет лишних людей и посторонних предметов).
  * Prompt Wrapper (техническая рамка Masterpiece 8K).
  * Универсальный блок негативных промптов (Negative Prompt Sanitizer).

### 5. ⚡ Пакетный генератор задач (Batch Engine)
* **Multi-Seed Generator:** Генерация $N$ вариаций одной сцены с независимыми криптографическими сидами (`secure_random_uint32`).
* **Cartesian Product Matrix:** Декартово произведение выбранных стилей и вариантов освещения ($M \times K$).
* **Batch Reference Mapper:** Пакетная привязка списка файлов-референсов к единому модифицирующему промпту.

### 6. 📋 Диспетчер воркеров & Очередь задач (Queue Manager)
* Асинхронная очередь FIFO с семафорами параллелизма (`SemaphoreSlim(4, 4)` для изображений, `SemaphoreSlim(2, 2)` для видео).
* **Zero-Memory SSD Streamer:** Прямая потоковая запись медиафайлов на диск без накопления в оперативной памяти.
* **Sidecar JSON Metadata:** Автоматическое сохранение метаданных (промпт, модель, сид, параметры, метка времени) в формате `<file>.json` рядом с каждым файлом.
* **Memory Trimmer:** Автоматическая оптимизация рабочего набора памяти (`SetProcessWorkingSetSize`) — потребление держится на стабильном уровне **~45–68 МБ ОЗУ**.

### 7. 🔄 Система обновлений "По воздуху" (OTA Updates)
* Приложение автоматически проверяет наличие новых версий на GitHub при запуске.
* Встроенное диалоговое окно обновления с полным списком изменений (Release Notes).
* Автоматическая загрузка нового исполняемого файла и самообновление в 1 клик через `updater.bat` без необходимости ручного скачивания.

### 8. 📊 Живая телеметрия оборудования (WMI Telemetry)
* Динамическое определение характеристик конкретной системы:
  * Дискретный GPU и VRAM (например, `NVIDIA GeForce GTX 1650 4.0 GB VRAM`).
  * Модель CPU, физические ядра и логические потоки (например, `11th Gen Intel Core i5-11400H, 6C/12T`).
  * Интегрированное видеоядро и его разгрузка.
  * Общий и свободный объем системной памяти (RAM).
  * Реальный объем памяти процесса в мегабайтах с обновлением каждые 2.5 секунды.

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
│                   GoogleFlowDesktop.exe (C# WinForms)                  │
├────────────────────────────────────────────────────────────────────────┤
│  [Permanent Navigation Bar: Split (50/50) | Only Flow | Only Studio]   │
├──────────────────────────────────┬─────────────────────────────────────┤
│  Panel 1: Prompt Studio (UI)     │  Panel 2: Google Flow Canvas        │
│  - Microsoft Edge WebView2       │  - Microsoft Edge WebView2          │
│  - Local HTTP Server (ui/)       │  - Target: https://flow.google.com  │
│  - Chips & Dropdowns Presets     │  - Injected: unlocker.js v4.1       │
│  - Batch Task Generators         │  - Injected: Floating Return Dock   │
│  - Real-time Queue Manager       │  - Direct3D 11 / NVDEC Pipeline     │
│  - OTA Update Notifications      │  - Desktop Chrome User-Agent        │
├──────────────────────────────────┴─────────────────────────────────────┤
│                         C# Backend Services                            │
│  ├── GpuConfig.cs        -> DirectX 11 Registry & WMI Telemetry        │
│  ├── WorkerDispatcher.cs -> FIFO Queue, Semaphores (4 img / 2 vid)     │
│  ├── TaskEngine.cs       -> Multi-Seed / Cartesian / Ref Mapper        │
│  ├── UpdateManager.cs    -> GitHub Live Version Checker & OTA Updater  │
│  ├── Notifier.cs         -> Windows Toast Notifications & Chimes       │
│  └── LocalServer.cs      -> Embedded HttpListener for Static Assets    │
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
├── unlocker.js                  # Скрипт Google Labs Country Unlocker v4.1 + Return Dock
├── LICENSE                      # Лицензия MIT
├── README.md                    # Документация проекта
├── Microsoft.Web.WebView2.Core.dll      # Ядро движка WebView2
├── Microsoft.Web.WebView2.WinForms.dll  # WinForms-контрол WebView2
├── WebView2Loader.dll                   # Нативный загрузчик WebView2
├── src/                         # Исходный код C# (.NET)
│   ├── Program.cs               # Точка входа WinForms с защитой от сбоев
│   ├── MainForm.cs              # Главное окно: Header, SplitContainer, WebMessage мост
│   ├── GpuConfig.cs             # WMI детекция железа, реестр DirectX, триммер ОЗУ
│   ├── UpdateManager.cs         # Фоновый чекер релизов GitHub и OTA апдейтер
│   ├── TaskEngine.cs            # Алгоритмы Multi-Seed, Cartesian, Ref Mapper
│   ├── WorkerDispatcher.cs      # Очередь FIFO, семафоры, Zero-Memory SSD Streamer
│   ├── UnlockerScript.cs        # Загрузчик и провайдер unlocker.js
│   ├── LocalServer.cs           # Встроенный легковесный веб-сервер HttpListener
│   └── Notifier.cs              # Windows Toast уведомления и звуковые сигналы
└── ui/                          # Frontend интерфейс Prompt Studio (Fluent Dark)
    ├── index.html               # Разметка: Studio, Batch, Queue, Media, dGPU, OTA Modal
    ├── style.css                # Стили Windows 11 Fluent Design (Dark Acrylic)
    └── app.js                   # Реактивная логика чипов, очереди, телеметрии и OTA
```

---

## 💻 Системные требования

* **ОС:** Windows 10 (1809+) или Windows 11 (x64)
* **Видеокарта:** Дискретная видеокарта NVIDIA / AMD / Intel с поддержкой DirectX 11 (рекомендуется NVIDIA GeForce GTX 1650 или мощнее)
* **Процессор:** Intel Core i3 / AMD Ryzen 3 или лучше
* **Оперативная память:** от 4 ГБ RAM (приложение потребляет всего ~50 МБ)
* **Среда выполнения:** Microsoft Edge WebView2 Runtime (встроена в Windows 10/11 по умолчанию)

---

## 📄 Лицензия

Проект распространяется под открытой лицензией [MIT](LICENSE).

Автор: **[RichkovGit](https://github.com/RichkovGit)**
