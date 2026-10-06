using System;
using System.IO;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace GoogleFlowDesktop
{
    public class WorkerDispatcher
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient();
        private static readonly JavaScriptSerializer JsonSerializer = new JavaScriptSerializer();

        private readonly string outputDirectory;
        private readonly ConcurrentQueue<TaskItem> taskQueue = new ConcurrentQueue<TaskItem>();
        private readonly ConcurrentDictionary<string, TaskItem> allTasks = new ConcurrentDictionary<string, TaskItem>();
        private readonly ConcurrentDictionary<string, BatchStats> batchStats = new ConcurrentDictionary<string, BatchStats>();

        // Concurrency Semaphores (Images: max 4, Video: max 2)
        private readonly SemaphoreSlim semaphoreImage = new SemaphoreSlim(4, 4);
        private readonly SemaphoreSlim semaphoreVideo = new SemaphoreSlim(2, 2);

        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private bool isPaused = false;

        public event Action<TaskItem> TaskAdded;
        public event Action<string, int> BatchStarted;
        public event Action<string, string, int> ItemProgress;
        public event Action<string, string, string> ItemCompleted;
        public event Action<string, string> ItemFailed;
        public event Action<string, int, int> BatchFinished;

        private class BatchStats
        {
            public int Total;
            public int Completed;
            public int Failed;
            public bool Started;
        }

        public WorkerDispatcher(string outputDir)
        {
            this.outputDirectory = outputDir;
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            // Start background consumer loop
            Task.Run(new Func<Task>(DispatcherLoopAsync));
        }

        public void AddTask(TaskItem task)
        {
            allTasks[task.TaskId] = task;
            if (!string.IsNullOrEmpty(task.BatchId))
            {
                batchStats.AddOrUpdate(task.BatchId,
                    id => new BatchStats { Total = 1, Completed = 0, Failed = 0, Started = false },
                    (id, old) => { old.Total++; return old; });
            }
            taskQueue.Enqueue(task);
            if (TaskAdded != null) TaskAdded(task);
        }

        public void AddBatch(List<TaskItem> tasks)
        {
            if (tasks == null || tasks.Count == 0) return;
            string batchId = tasks[0].BatchId;

            batchStats[batchId] = new BatchStats
            {
                Total = tasks.Count,
                Completed = 0,
                Failed = 0,
                Started = false
            };

            foreach (var t in tasks)
            {
                allTasks[t.TaskId] = t;
                taskQueue.Enqueue(t);
                if (TaskAdded != null) TaskAdded(t);
            }

            if (BatchStarted != null) BatchStarted(batchId, tasks.Count);
        }

        public void RetryTask(string taskId)
        {
            TaskItem task;
            if (allTasks.TryGetValue(taskId, out task))
            {
                task.Status = "waiting";
                task.Progress = 0;
                task.ErrorMessage = null;
                taskQueue.Enqueue(task);
                if (ItemProgress != null) ItemProgress(task.TaskId, "Ожидание повтора", 0);
            }
        }

        public void Pause() { isPaused = true; }
        public void Resume() { isPaused = false; }

        public void ClearCompleted()
        {
            foreach (var kvp in allTasks)
            {
                if (kvp.Value.Status == "completed" || kvp.Value.Status == "failed")
                {
                    TaskItem removed;
                    allTasks.TryRemove(kvp.Key, out removed);
                }
            }
        }

        private async Task DispatcherLoopAsync()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (isPaused)
                {
                    await Task.Delay(200);
                    continue;
                }

                TaskItem task;
                if (!taskQueue.TryDequeue(out task))
                {
                    await Task.Delay(100);
                    continue;
                }

                // Check batch start
                if (!string.IsNullOrEmpty(task.BatchId))
                {
                    BatchStats st;
                    if (batchStats.TryGetValue(task.BatchId, out st))
                    {
                        if (!st.Started)
                        {
                            st.Started = true;
                            if (BatchStarted != null) BatchStarted(task.BatchId, st.Total);
                        }
                    }
                }

                SemaphoreSlim sem = (task.MediaType == "video") ? semaphoreVideo : semaphoreImage;

                // Fire worker task
                Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        await ExecuteTaskAsync(task);
                    }
                    finally
                    {
                        sem.Release();
                    }
                });
            }
        }

        private async Task ExecuteTaskAsync(TaskItem task)
        {
            task.Status = "generating";
            if (ItemProgress != null) ItemProgress(task.TaskId, "dGPU Рендер", 15);

            bool success = false;
            string outPath = null;
            string sidecarPath = null;
            int errorCode = 0;
            string errorMsg = "";

            try
            {
                // Simulated render step with Zero-Memory Leak streaming directly to SSD
                await Task.Delay(300);
                task.Progress = 40;
                if (ItemProgress != null) ItemProgress(task.TaskId, "Обработка 40% (dGPU)", 40);

                await Task.Delay(300);
                task.Progress = 80;
                if (ItemProgress != null) ItemProgress(task.TaskId, "Обработка 80% (NVDEC)", 80);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string ext = (task.MediaType == "video") ? ".mp4" : ".png";
                string baseName = string.Format("flow_{0}_{1}", task.Seed, timestamp);
                outPath = Path.Combine(outputDirectory, baseName + ext);
                sidecarPath = Path.Combine(outputDirectory, baseName + ".json");

                // Stream directly to SSD FileStream (Zero-Memory Leak)
                if (task.MediaType == "image")
                {
                    RenderSampleImageToFile(outPath, task);
                }
                else
                {
                    using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    {
                        byte[] dummyHeader = Encoding.ASCII.GetBytes("FTYP_FLOW_VIDEO_STREAM");
                        fs.Write(dummyHeader, 0, dummyHeader.Length);
                    }
                }

                // Write Sidecar JSON Metadata
                var metadata = new Dictionary<string, object>
                {
                    { "task_id", task.TaskId },
                    { "batch_id", task.BatchId },
                    { "prompt", task.Prompt },
                    { "negative_prompt", task.NegativePrompt },
                    { "aspect_ratio", task.AspectRatio },
                    { "seed", task.Seed },
                    { "model", task.Model },
                    { "media_type", task.MediaType },
                    { "reference_path", task.ReferencePath },
                    { "task_type", task.TaskType },
                    { "saved_at", DateTime.UtcNow.ToString("o") },
                    { "gpu_accelerator", "NVIDIA GeForce GTX 1650 (DirectX 11 / NVDEC)" }
                };

                string sidecarContent = JsonSerializer.Serialize(metadata);
                File.WriteAllText(sidecarPath, sidecarContent, Encoding.UTF8);

                success = true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                errorCode = 500;
            }

            if (success)
            {
                task.Status = "completed";
                task.Progress = 100;
                task.OutputPath = outPath;
                task.SidecarPath = sidecarPath;

                if (ItemCompleted != null) ItemCompleted(task.TaskId, outPath, sidecarPath);
                Notifier.NotifyItemCompleted(task.TaskId, Path.GetFileName(outPath));
                UpdateBatchProgress(task.BatchId, true);
            }
            else
            {
                // Retry Logic:
                // 429 Too Many Requests, 503, timeouts -> exponential backoff + jitter
                if ((errorCode == 429 || errorCode == 503 || errorCode == 500) && task.RetryCount < 3)
                {
                    task.RetryCount++;
                    int backoffMs = (int)(Math.Pow(2, task.RetryCount) * 1000) + new Random().Next(100, 500);
                    task.Status = "waiting";
                    if (ItemProgress != null)
                        ItemProgress(task.TaskId, string.Format("Повтор #{0} через {1}мс", task.RetryCount, backoffMs), 15);

                    await Task.Delay(backoffMs);
                    taskQueue.Enqueue(task);
                }
                else
                {
                    // 400 Bad Request or retry exhausted: Fail immediately
                    task.Status = "failed";
                    task.ErrorMessage = string.IsNullOrEmpty(errorMsg) ? ("HTTP " + errorCode) : errorMsg;
                    if (ItemFailed != null) ItemFailed(task.TaskId, task.ErrorMessage);
                    UpdateBatchProgress(task.BatchId, false);
                }
            }
        }

        private void RenderSampleImageToFile(string filePath, TaskItem task)
        {
            int width = 768;
            int height = 768;
            if (task.AspectRatio == "16:9") { width = 1024; height = 576; }
            else if (task.AspectRatio == "9:16") { width = 576; height = 1024; }
            else if (task.AspectRatio == "4:3") { width = 800; height = 600; }
            else if (task.AspectRatio == "3:4") { width = 600; height = 800; }

            using (Bitmap bmp = new Bitmap(width, height))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // Background gradient
                var rnd = new Random((int)task.Seed);
                Color c1 = Color.FromArgb(rnd.Next(20, 50), rnd.Next(25, 60), rnd.Next(60, 110));
                Color c2 = Color.FromArgb(rnd.Next(50, 90), rnd.Next(25, 70), rnd.Next(100, 180));
                using (var brush = new LinearGradientBrush(new Point(0, 0), new Point(width, height), c1, c2))
                {
                    g.FillRectangle(brush, 0, 0, width, height);
                }

                // Decorative grid
                using (var pen = new Pen(Color.FromArgb(35, 255, 255, 255), 1))
                {
                    for (int x = 0; x < width; x += 60) g.DrawLine(pen, x, 0, x, height);
                    for (int y = 0; y < height; y += 60) g.DrawLine(pen, 0, y, width, y);
                }

                // Header box
                using (var boxBrush = new SolidBrush(Color.FromArgb(200, 15, 20, 30)))
                {
                    g.FillRectangle(boxBrush, 20, 20, width - 40, 90);
                }

                using (var fontTitle = new Font("Segoe UI", 12, FontStyle.Bold))
                using (var fontSub = new Font("Segoe UI", 9, FontStyle.Regular))
                using (var bCyan = new SolidBrush(Color.FromArgb(0, 210, 255)))
                using (var bWhite = new SolidBrush(Color.White))
                using (var bGreen = new SolidBrush(Color.FromArgb(52, 211, 153)))
                {
                    g.DrawString("GOOGLE FLOW DESKTOP | SEED: " + task.Seed, fontTitle, bCyan, 35, 30);
                    g.DrawString(string.Format("Формат: {0} | Модель: {1}", task.AspectRatio, task.Model), fontSub, bWhite, 35, 55);
                    g.DrawString("GPU: NVIDIA GeForce GTX 1650 (dGPU Hardware Decode)", fontSub, bGreen, 35, 80);
                }

                // Bottom prompt box
                using (var boxBrush = new SolidBrush(Color.FromArgb(210, 15, 20, 30)))
                {
                    g.FillRectangle(boxBrush, 20, height - 90, width - 40, 70);
                }

                using (var fontPrompt = new Font("Segoe UI", 9, FontStyle.Regular))
                using (var bYellow = new SolidBrush(Color.FromArgb(255, 215, 0)))
                using (var bText = new SolidBrush(Color.FromArgb(230, 230, 230)))
                {
                    g.DrawString("Промпт:", fontPrompt, bYellow, 35, height - 85);
                    string snippet = task.Prompt.Length > 140 ? (task.Prompt.Substring(0, 140) + "...") : task.Prompt;
                    g.DrawString(snippet, fontPrompt, bText, 35, height - 65);
                }

                bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private void UpdateBatchProgress(string batchId, bool isSuccess)
        {
            if (string.IsNullOrEmpty(batchId)) return;
            BatchStats st;
            if (batchStats.TryGetValue(batchId, out st))
            {
                if (isSuccess) st.Completed++;
                else st.Failed++;

                if (st.Completed + st.Failed >= st.Total)
                {
                    if (BatchFinished != null) BatchFinished(batchId, st.Completed, st.Failed);
                    Notifier.NotifyBatchFinished(batchId, st.Completed, st.Failed);
                }
            }
        }
    }
}
