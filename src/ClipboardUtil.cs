using System;
using System.Threading;
using System.Windows.Forms;

namespace GoogleFlowDesktop
{
    /// <summary>
    /// Clipboard access that survives the "OpenClipboard failed" race
    /// (another process holding the clipboard) by retrying a few times.
    /// </summary>
    public static class ClipboardUtil
    {
        public static bool Set(string text)
        {
            if (text == null) text = "";
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true, 4, 120);
                    return true;
                }
                catch
                {
                    Thread.Sleep(60);
                }
            }
            return false;
        }
    }
}
