// Google Flow & Labs Country Unlocker v4.1
(function() {
    'use strict';
    const win = (typeof unsafeWindow !== 'undefined') ? unsafeWindow : window;
    console.log('[Flow Desktop Unlocker v4.1] 🚀 Initialized on:', location.href);

    if (location.href.includes('3d3ec587-30a3-488a-a3b7-03fae3d45417')) {
        location.replace('https://flow.google.com/');
        return;
    }

    try {
        let _wiz = win.WIZ_global_data || {};
        _wiz.awbSEf = true;
        win.WIZ_global_data = new Proxy(_wiz, {
            set: function(target, prop, value) {
                target[prop] = value;
                target.awbSEf = true;
                return true;
            }
        });
        Object.defineProperty(win, 'WIZ_global_data', {
            get: () => _wiz,
            set: (v) => {
                if (v && typeof v === 'object') {
                    v.awbSEf = true;
                    _wiz = new Proxy(v, {
                        set: function(target, prop, value) {
                            target[prop] = value;
                            target.awbSEf = true;
                            return true;
                        }
                    });
                }
            },
            configurable: true,
            enumerable: true
        });
    } catch(e) {
        console.warn('[Flow Unlocker] WIZ_global_data hook warning:', e);
    }

    const TARGET_RPCS = ['cPZSdc', 'KV2T2d', 'rThb8d', 'cO7JOb', 'md9xJf'];

    function patchProtobufJson(str, rpcid) {
        try {
            let inner = JSON.parse(str);
            if (Array.isArray(inner)) {
                if (rpcid === 'cPZSdc') {
                    while (inner.length < 32) inner.push(null);
                    inner[30] = 1;
                    inner[31] = 1;
                    console.log('[Flow Unlocker] ✅ Patched cPZSdc (country=1, age=1)');
                } else if (TARGET_RPCS.includes(rpcid)) {
                    if (inner.length === 0) inner.push(1);
                    else inner[0] = 1;
                    console.log(`[Flow Unlocker] ✅ Patched ${rpcid} (status=1 AVAILABLE)`);
                }
                return JSON.stringify(inner);
            }
        } catch(e) {}
        return str;
    }

    function patchGoogleStream(raw) {
        if (!raw || typeof raw !== 'string') return raw;
        if (!TARGET_RPCS.some(id => raw.includes(id))) return raw;

        let headerEnd = raw.indexOf(")]}'\n\n");
        let ua = 0;
        let prefix = "";
        if (headerEnd >= 0) {
            ua = headerEnd + 6;
            prefix = raw.slice(0, ua);
        } else {
            headerEnd = raw.indexOf("\n\n");
            if (headerEnd >= 0) {
                ua = headerEnd + 2;
                prefix = raw.slice(0, ua);
            }
        }

        let body = raw.slice(ua);
        try {
            let data = JSON.parse(body);
            if (Array.isArray(data)) {
                let modified = false;
                for (let item of data) {
                    if (Array.isArray(item) && TARGET_RPCS.includes(item[1]) && typeof item[2] === 'string') {
                        item[2] = patchProtobufJson(item[2], item[1]);
                        modified = true;
                    }
                }
                if (modified) return prefix + JSON.stringify(data);
            }
        } catch(_) {}

        let out = prefix;
        let pos = ua;
        let anyModified = false;

        while (pos < raw.length) {
            let nextNl = raw.indexOf("\n", pos);
            if (nextNl === -1) {
                out += raw.slice(pos);
                break;
            }
            let lenStr = raw.substring(pos, nextNl);
            let len = Number(lenStr);
            if (isNaN(len) || nextNl + len > raw.length) {
                out += raw.slice(pos);
                break;
            }

            let chunkWithNl = raw.substr(nextNl, len);
            let chunkJson = chunkWithNl.slice(1);
            try {
                let chunk = JSON.parse(chunkJson);
                let chunkModified = false;
                for (let item of chunk) {
                    if (Array.isArray(item) && TARGET_RPCS.includes(item[1]) && typeof item[2] === 'string') {
                        item[2] = patchProtobufJson(item[2], item[1]);
                        chunkModified = true;
                        anyModified = true;
                    }
                }
                if (chunkModified) {
                    let newChunkJson = JSON.stringify(chunk);
                    let newLen = 1 + newChunkJson.length;
                    out += String(newLen) + '\n' + newChunkJson;
                } else {
                    out += lenStr + chunkWithNl;
                }
            } catch(err) {
                out += lenStr + chunkWithNl;
            }
            pos = nextNl + len;
        }

        return anyModified ? out : raw;
    }

    function patchDeepJson(data) {
        if (!data || typeof data !== 'object') return data;
        if (Array.isArray(data)) {
            for (let i = 0; i < data.length; i++) {
                data[i] = patchDeepJson(data[i]);
            }
        } else {
            for (let k of Object.keys(data)) {
                if (k === 'availabilityState') {
                    if (data[k] !== 'AVAILABLE') {
                        console.log(`[Flow Unlocker] ✅ Patched availabilityState: "${data[k]}" -> "AVAILABLE"`);
                        data[k] = 'AVAILABLE';
                    }
                } else if (k === 'isCountrySupported' || k === 'is_country_supported') {
                    data[k] = true;
                } else if (k === 'isAgeSupported' || k === 'is_age_supported') {
                    data[k] = true;
                } else if (typeof data[k] === 'object') {
                    data[k] = patchDeepJson(data[k]);
                }
            }
        }
        return data;
    }

    try {
        const XHR = win.XMLHttpRequest;
        if (XHR && XHR.prototype) {
            const origTextDesc = Object.getOwnPropertyDescriptor(XHR.prototype, 'responseText');
            const origRespDesc = Object.getOwnPropertyDescriptor(XHR.prototype, 'response');

            if (origTextDesc && origTextDesc.get) {
                Object.defineProperty(XHR.prototype, 'responseText', {
                    get: function() {
                        try {
                            if (this.responseType && this.responseType !== 'text') {
                                return origTextDesc.get.call(this);
                            }
                            const raw = origTextDesc.get.call(this);
                            return patchGoogleStream(raw);
                        } catch(e) {
                            try { return origTextDesc.get.call(this); } catch(_) { return ''; }
                        }
                    },
                    configurable: true
                });
            }

            if (origRespDesc && origRespDesc.get) {
                Object.defineProperty(XHR.prototype, 'response', {
                    get: function() {
                        try {
                            if (this.responseType && this.responseType !== 'text' && this.responseType !== '') {
                                return origRespDesc.get.call(this);
                            }
                            const raw = origRespDesc.get.call(this);
                            if (typeof raw === 'string') {
                                return patchGoogleStream(raw);
                            }
                            return raw;
                        } catch(e) {
                            try { return origRespDesc.get.call(this); } catch(_) { return null; }
                        }
                    },
                    configurable: true
                });
            }
        }
    } catch(e) {
        console.error('[Flow Unlocker] XHR hook error:', e);
    }

    try {
        const origFetch = win.fetch;
        win.fetch = async function(...args) {
            const url = typeof args[0] === 'string' ? args[0] : (args[0] && args[0].url ? args[0].url : '');

            if (url.includes('api/auth/signout')) {
                console.warn('[Flow Unlocker] 🚫 Prevented NextAuth signOut call');
                return new Response(JSON.stringify({ url: location.href }), {
                    status: 200,
                    headers: { 'Content-Type': 'application/json' }
                });
            }

            const res = await origFetch.apply(this, args);

            if (url.includes('batchexecute')) {
                try {
                    const raw = await res.text();
                    const patched = patchGoogleStream(raw);
                    const headers = new Headers(res.headers);
                    headers.delete('content-length');
                    return new Response(patched, {
                        status: res.status,
                        statusText: res.statusText,
                        headers: headers
                    });
                } catch(e) {
                    return res;
                }
            }

            if (url.includes('fetchToolAvailability') ||
                url.includes('checkAppAvailability') ||
                url.includes('trpc/general.')) {
                try {
                    const text = await res.text();
                    let headers = new Headers(res.headers);
                    headers.delete('content-length');
                    try {
                        const data = JSON.parse(text);
                        patchDeepJson(data);
                        return new Response(JSON.stringify(data), {
                            status: 200,
                            statusText: 'OK',
                            headers: headers
                        });
                    } catch(err) {
                        return new Response(text, {
                            status: res.status,
                            statusText: res.statusText,
                            headers: headers
                        });
                    }
                } catch(e) {
                    return res;
                }
            }

            return res;
        };
    } catch(e) {
        console.error('[Flow Unlocker] Fetch hook error:', e);
    }

    if (location.pathname.includes('unsupported-country') || location.pathname.includes('unavailable')) {
        console.log('[Flow Unlocker] Blocked path detected, navigating to /');
        location.replace('/');
    }
})();

// DOM Bridge for prompt injection
(function() {
    window.__flowNativeBridge = {
        insertPrompt: function(promptText) {
            console.log('[Flow Bridge] Attempting to insert prompt into Flow UI...');
            const selectors = [
                'textarea[placeholder*="Describe" i]',
                'textarea[placeholder*="prompt" i]',
                'textarea[aria-label*="prompt" i]',
                'textarea',
                '[contenteditable="true"]'
            ];
            
            let target = null;
            for (let s of selectors) {
                const elems = document.querySelectorAll(s);
                for (let el of elems) {
                    if (el.offsetParent !== null) {
                        target = el;
                        break;
                    }
                }
                if (target) break;
            }

            if (target) {
                target.focus();
                if (target.tagName.toLowerCase() === 'textarea' || target.tagName.toLowerCase() === 'input') {
                    target.value = promptText;
                    target.dispatchEvent(new Event('input', { bubbles: true }));
                    target.dispatchEvent(new Event('change', { bubbles: true }));
                } else {
                    target.innerText = promptText;
                    target.dispatchEvent(new Event('input', { bubbles: true }));
                }
                console.log('[Flow Bridge] ✅ Prompt successfully injected into input element');
                return true;
            } else {
                console.warn('[Flow Bridge] Active prompt input not found. Copied to clipboard.');
                if (navigator.clipboard) {
                    navigator.clipboard.writeText(promptText);
                }
                return false;
            }
        }
    };
})();

// Floating Return Dock for Fullscreen Flow Canvas Mode
(function() {
    function injectFloatingReturnDock() {
        if (document.getElementById('__flowDesktopReturnDock')) return;
        if (!document.body) return;
        const dock = document.createElement('div');
        dock.id = '__flowDesktopReturnDock';
        dock.style.cssText = 'position:fixed;top:12px;right:18px;z-index:99999999;display:flex;gap:6px;background:rgba(17,20,30,0.85);backdrop-filter:blur(10px);border:1px solid rgba(0,210,255,0.3);border-radius:20px;padding:4px 10px;box-shadow:0 4px 16px rgba(0,0,0,0.5);font-family:Segoe UI,sans-serif;font-size:11px;font-weight:600;color:#fff;align-items:center;';
        dock.innerHTML = '<span style="color:#00d2ff;margin-right:2px;font-size:10px;">⚡ Flow</span>' +
            '<button id="__btnDockSplit" style="background:#202a3e;border:1px solid #00d2ff;color:#00d2ff;padding:2px 8px;border-radius:10px;cursor:pointer;font-size:10px;font-weight:600;">◫ Сплит</button>' +
            '<button id="__btnDockStudio" style="background:#181c28;border:1px solid #334155;color:#94a3b8;padding:2px 8px;border-radius:10px;cursor:pointer;font-size:10px;font-weight:600;">✨ Студия</button>';
        document.body.appendChild(dock);

        const btnSplit = document.getElementById('__btnDockSplit');
        if (btnSplit) {
            btnSplit.addEventListener('click', () => {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'SET_VIEW_MODE', mode: 'split' }));
                }
            });
        }
        const btnStudio = document.getElementById('__btnDockStudio');
        if (btnStudio) {
            btnStudio.addEventListener('click', () => {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'SET_VIEW_MODE', mode: 'studio' }));
                }
            });
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', injectFloatingReturnDock);
    } else {
        injectFloatingReturnDock();
    }
    setInterval(injectFloatingReturnDock, 3000);
})();

