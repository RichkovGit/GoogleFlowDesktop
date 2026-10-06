using System;
using System.IO;
using System.Net;
using System.Threading;

namespace GoogleFlowDesktop
{
    public class LocalServer
    {
        private readonly HttpListener listener;
        private readonly string uiDirectory;
        private readonly string projectsDirectory;
        private bool isRunning;

        public int Port { get; private set; }

        public LocalServer(string uiDir, string projectsDir, int defaultPort = 49152)
        {
            this.uiDirectory = uiDir;
            this.projectsDirectory = projectsDir;
            this.listener = new HttpListener();

            // Try preferred port, fallback to dynamic port if busy
            int chosenPort = defaultPort;
            for (int p = defaultPort; p < defaultPort + 100; p++)
            {
                try
                {
                    listener.Prefixes.Clear();
                    listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", p));
                    listener.Start();
                    chosenPort = p;
                    break;
                }
                catch
                {
                    continue;
                }
            }

            this.Port = chosenPort;
            this.isRunning = true;

            ThreadPool.QueueUserWorkItem(ListenLoop);
        }

        private void ListenLoop(object state)
        {
            while (isRunning)
            {
                try
                {
                    var context = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(ProcessRequest, context);
                }
                catch
                {
                    if (!isRunning) break;
                }
            }
        }

        private void ProcessRequest(object state)
        {
            var context = (HttpListenerContext)state;
            try
            {
                string rawUrl = context.Request.RawUrl ?? "/";
                string cleanPath = rawUrl.Split('?')[0];

                string localFilePath = null;
                string contentType = "application/octet-stream";

                if (cleanPath.StartsWith("/media/"))
                {
                    string filename = cleanPath.Substring(7);
                    localFilePath = Path.Combine(projectsDirectory, filename);
                }
                else
                {
                    string rel = cleanPath.TrimStart('/');
                    if (string.IsNullOrEmpty(rel)) rel = "index.html";
                    localFilePath = Path.Combine(uiDirectory, rel);
                }

                if (File.Exists(localFilePath))
                {
                    string ext = Path.GetExtension(localFilePath).ToLowerInvariant();
                    switch (ext)
                    {
                        case ".html": contentType = "text/html; charset=utf-8"; break;
                        case ".css": contentType = "text/css; charset=utf-8"; break;
                        case ".js": contentType = "application/javascript; charset=utf-8"; break;
                        case ".json": contentType = "application/json; charset=utf-8"; break;
                        case ".png": contentType = "image/png"; break;
                        case ".jpg":
                        case ".jpeg": contentType = "image/jpeg"; break;
                        case ".mp4": contentType = "video/mp4"; break;
                        case ".svg": contentType = "image/svg+xml"; break;
                    }

                    byte[] buffer = File.ReadAllBytes(localFilePath);
                    context.Response.ContentType = contentType;
                    context.Response.ContentLength64 = buffer.Length;
                    context.Response.StatusCode = 200;
                    context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
            }
            catch { }
            finally
            {
                try { context.Response.OutputStream.Close(); } catch { }
            }
        }

        public void Stop()
        {
            isRunning = false;
            try { listener.Stop(); } catch { }
        }
    }
}
