using System;
using System.IO;

namespace GoogleFlowDesktop
{
    public static class UnlockerScript
    {
        private const string DomBridge = @"
(function() {
    window.__flowNativeBridge = {
        insertPrompt: function(promptText) {
            const selectors = [
                'textarea[placeholder*=""Describe"" i]',
                'textarea[placeholder*=""prompt"" i]',
                'textarea[aria-label*=""prompt"" i]',
                'textarea',
                '[contenteditable=""true""]'
            ];
            let target = null;
            for (let s of selectors) {
                const elems = document.querySelectorAll(s);
                for (let el of elems) {
                    if (el.offsetParent !== null) { target = el; break; }
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
                return true;
            }
            if (navigator.clipboard) { navigator.clipboard.writeText(promptText); }
            return false;
        }
    };
})();
";

        public static string GetScript()
        {
            // 1. Try local unlocker.js in app directory
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unlocker.js");
            if (File.Exists(localPath))
            {
                try
                {
                    return File.ReadAllText(localPath);
                }
                catch { }
            }

            // 2. Try user's document path
            string userPath = @"C:\Users\danny\Documents\новый 1.txt";
            if (File.Exists(userPath))
            {
                try
                {
                    string content = File.ReadAllText(userPath);
                    if (!string.IsNullOrEmpty(content) && content.Contains("cPZSdc"))
                    {
                        return content + "\n\n" + DomBridge;
                    }
                }
                catch { }
            }

            return DomBridge;
        }
    }
}
