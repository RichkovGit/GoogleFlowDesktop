# Flow interface audit · 21 October 2026

Observed in the authenticated first-party website. This is a feature inventory, **not a claim of complete desktop parity**. Private projects were read only; mutations and paid tests use separate QA projects. Sharing, publishing, permanent deletion and account security settings were not executed.

| Area | Observed website controls | Desktop state |
|---|---|---|
| Home | New project; project card; rename; delete; search | Account project list/open/create/rename; recoverable local copies; project delete not exposed |
| Account | Credits, plan, account switch, sign out, watermark, legal/privacy, licenses | Real credits, manual Google login, DPAPI session, refresh/disconnect; plan/watermark/account administration not mirrored |
| Project header | Editable title; search; filter/sort; add media; settings; more | Native title management, search/sort, original import, library preferences |
| Sidebar | All, video/image, characters, scenes, collections, tools, trash, collapse | Native media filters, collections/trash, local montage; dedicated characters/custom tools still absent |
| Filters | Media types, recent ordering, search | Native all/video/image, name/newest/oldest, favorites, collection filtering |
| Library layout | Grid/batch, S/M/L, info, hover audio, mute video, clear sent prompt | Native equivalents implemented |
| Media context | Favorite, regenerate, add scene, add prompt, download, copy, rename, share, YouTube, project cover, report, trash | Favorite, original save, montage, reuse prompt, details, rename, cover, move collection, trash. Share/YouTube/report not implemented |
| Download 360p | GIF 270p, original 360p, upscale 720p | Original download implemented. GIF/upscale actions not implemented |
| Download 720p | GIF 270p, original 720p, upscale 1080p, 4K plan gate | Original download implemented. Upscale/GIF actions not implemented |
| Composer | Prompt, ingredients menu, agent switch, generation settings, submit | Native prompt/modes/references/count/model/ratio/variant controls |
| Image settings | 16:9, 4:3, 1:1, 3:4, 9:16; family; x1–4 | Native catalog and request construction; live service UI image path not yet implemented |
| Video settings | Frames/ingredients, 16:9/9:16, family, 360p/720p, 4/6/8/10 s, x1–4, actual price | Native filtering and requests. Service browser driver currently text-to-video Omni only |
| Families | Omni 1.1 Flash; Veo 3.1 Lite/Fast/Quality | Catalog families separated from duration/resolution usages; service driver rejects unsupported families before submitting |
| References | Upload/project picker, first/last frame, ingredients | Native image upload and wire contracts implemented/tested synthetically; service browser reference path not yet implemented |
| Scene editor | Name, back, history, favorite, download, trash, Done; aspect; save frame; mute; playback; fullscreen; loop; trim; zoom; add/continue; natural-language clip edit | Native local trim/order/volume/mute/normalize/export and playback; cloud scene model/history/continue/edit not fully mirrored |
| Timeline context | Copy/paste, save in project, download, delete | Local ordering/removal/export; cloud scene manipulation not implemented |
| Media picker | Video/images/uploads tabs, upload, search, sort, select/add, preview | Native originals import and account library; dedicated website-style picker not identical |
| Collections | Create/nested collections, name/move/favorite/cover/archive | Native management protocol/UI implemented with cross-project checks. Archive is folder-only; recursive site trash semantics not claimed |
| Trash | Restore, permanent deletion/empty trash | Native workflow/folder restore. Permanent delete/Empty Trash not exposed |
| Agent | Expand, history, new session, prompt, ingredients, instructions, settings, send | Native history/settings plus selectable native or external-browser sending. Live sending verification tracked separately |
| Agent settings | Always/Never approval, image/video family/ratio/count, reference instructions | Native RPC settings/instructions preserve existing image/entity/likeness links |
| Agent approvals | Website approval cards; modern bidi messages | Legacy native approval only. Modern browser approval integration incomplete; no automatic approval |
| Characters | Prompt presets (eccentric/professional/symbol/familiar/villain/fantasy), prompt, model, upload, add from project; images/uploads picker | Not implemented in native UI |
| Tools | My tools, community, templates; create | Not implemented in native UI |
| Tool creation | Style morph, playback control, image filter examples; prompt; submit; no credits currently displayed | Not implemented in native UI |
| Global more | Flow Music, About/Learn/Feedback, legal, privacy/improve Flow, Delete All Projects | Not mirrored; destructive/security/privacy controls intentionally not executed during audit |

## Live evidence

- Website tests used a separate synthetic QA project.
- Website originals decoded successfully: cat `640×360`, 4.01 s, 96 frames; robot `1280×720`, 4.01 s, 96 frames. These were **website tests**, not desktop transport success.
- Saved screenshots: `artifacts/ui-audit/flow-360p.png`, `flow-720p.png`.
- Desktop direct native matrix: `artifacts/live-verification/native-matrix-20261021/summary.json`; generation and agent send refused by Google with code 7 / HTTP 403; balance unchanged.
- Desktop browser attempts: `artifacts/live-verification/browser-app-20261021*/report.json`. Read these reports for current results; a submitted-once checkpoint means an attempt was started, not that Google accepted it.

The service browser uses only the app-owned login profile and ordinary Flow controls. No Gemini API, private reCAPTCHA function extraction, authentication-form automation or CAPTCHA solving. The supplied country userscript is not executed wholesale: age/authentication/logout modifications are excluded.
