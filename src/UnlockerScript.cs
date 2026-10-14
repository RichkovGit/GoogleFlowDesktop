using System;
using System.IO;

namespace GoogleFlowDesktop
{
    /// <summary>
    /// Provides the script injected into the Flow webview. The full Google Labs
    /// Country Unlocker lives in unlocker.js next to the executable; if it is
    /// missing we still inject a minimal prompt-injection bridge.
    /// </summary>
    public static class UnlockerScript
    {
        /// <summary>Robust prompt inserter that works with React/Quill-style inputs.</summary>
        private const string DomBridge = @"
(function() {
    if (window.__flowNativeBridge && window.__flowNativeBridge.__v === 2) return;
    window.__flowNativeBridge = {
        __v: 2,
        findInput: function() {
            const selectors = [
                'textarea[placeholder*=""Describe"" i]',
                'textarea[placeholder*=""prompt"" i]',
                'textarea[aria-label*=""prompt"" i]',
                'div[contenteditable=""true""][role=""textbox""]',
                'div[contenteditable=""true""]',
                'textarea'
            ];
            for (let s of selectors) {
                const elems = document.querySelectorAll(s);
                for (let el of elems) {
                    const rect = el.getBoundingClientRect();
                    if (rect.width > 0 && rect.height > 0) return el;
                }
            }
            return null;
        },
        insertPrompt: function(promptText) {
            const el = window.__flowNativeBridge.findInput();
            if (!el) {
                if (navigator.clipboard) { try { navigator.clipboard.writeText(promptText); } catch(e) {} }
                return false;
            }
            el.focus();
            const tag = el.tagName.toLowerCase();
            if (tag === 'textarea' || tag === 'input') {
                const proto = (tag === 'textarea') ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
                setter.call(el, promptText);
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
            } else {
                el.textContent = '';
                const sel = window.getSelection();
                const range = document.createRange();
                range.selectNodeContents(el);
                sel.removeAllRanges();
                sel.addRange(range);
                let ok = false;
                try { ok = document.execCommand('insertText', false, promptText); } catch(e) { ok = false; }
                if (!ok) { el.textContent = promptText; }
                el.dispatchEvent(new InputEvent('input', { bubbles: true, data: promptText }));
            }
            if (navigator.clipboard) { try { navigator.clipboard.writeText(promptText); } catch(e) {} }
            return true;
        }
    };
})();
";

        public static string GetScript()
        {
            try
            {
                string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unlocker.js");
                if (File.Exists(localPath))
                {
                    string content = File.ReadAllText(localPath);
                    if (!string.IsNullOrEmpty(content))
                    {
                        return content + "\n\n" + DomBridge;
                    }
                }
            }
            catch { }

            return DomBridge;
        }
    }
}
