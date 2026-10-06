using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace GoogleFlowDesktop
{
    public class TaskItem
    {
        public string TaskId { get; set; }
        public string BatchId { get; set; }
        public string Prompt { get; set; }
        public string NegativePrompt { get; set; }
        public string AspectRatio { get; set; }
        public uint Seed { get; set; }
        public string Model { get; set; }
        public string MediaType { get; set; } // "image" or "video"
        public string ReferencePath { get; set; }
        public string TaskType { get; set; } // "standard", "multi_seed", "cartesian", "reference"
        public string Status { get; set; } // "waiting", "generating", "completed", "failed"
        public int Progress { get; set; }
        public string OutputPath { get; set; }
        public string SidecarPath { get; set; }
        public string ErrorMessage { get; set; }
        public int RetryCount { get; set; }
        public double CreatedAt { get; set; }

        public TaskItem()
        {
            TaskId = "task_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            BatchId = "";
            Prompt = "";
            NegativePrompt = "";
            AspectRatio = "1:1";
            Seed = TaskBatchEngine.GenerateSecureSeed();
            Model = "Flow High-Quality (dGPU)";
            MediaType = "image";
            TaskType = "standard";
            Status = "waiting";
            Progress = 0;
            RetryCount = 0;
            CreatedAt = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        }
    }

    public static class TaskBatchEngine
    {
        private static readonly RNGCryptoServiceProvider Rng = new RNGCryptoServiceProvider();

        public static uint GenerateSecureSeed()
        {
            byte[] bytes = new byte[4];
            Rng.GetBytes(bytes);
            return BitConverter.ToUInt32(bytes, 0);
        }

        public static List<TaskItem> GenerateMultiSeedBatch(string basePrompt, int count, string model, string negativePrompt, string aspectRatio, string mediaType)
        {
            string batchId = "batch_seed_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var tasks = new List<TaskItem>();
            int safeCount = Math.Max(1, Math.Min(count, 32));

            for (int i = 0; i < safeCount; i++)
            {
                var item = new TaskItem
                {
                    BatchId = batchId,
                    Prompt = basePrompt,
                    NegativePrompt = negativePrompt,
                    AspectRatio = aspectRatio,
                    Seed = GenerateSecureSeed(),
                    Model = string.IsNullOrEmpty(model) ? "Flow High-Quality (dGPU)" : model,
                    MediaType = string.IsNullOrEmpty(mediaType) ? "image" : mediaType,
                    TaskType = "multi_seed"
                };
                tasks.Add(item);
            }
            return tasks;
        }

        public static List<TaskItem> GenerateCartesianMatrixBatch(string subject, List<string> styleTags, List<string> lightingTags, string negativePrompt, string aspectRatio)
        {
            string batchId = "batch_cartesian_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var tasks = new List<TaskItem>();

            var styles = (styleTags != null && styleTags.Count > 0) ? styleTags : new List<string> { "" };
            var lights = (lightingTags != null && lightingTags.Count > 0) ? lightingTags : new List<string> { "" };

            foreach (var s in styles)
            {
                foreach (var l in lights)
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(subject)) parts.Add(subject.Trim());
                    if (!string.IsNullOrEmpty(s)) parts.Add(s.Trim());
                    if (!string.IsNullOrEmpty(l)) parts.Add(l.Trim());

                    string fullPrompt = string.Join(", ", parts.ToArray());

                    var item = new TaskItem
                    {
                        BatchId = batchId,
                        Prompt = fullPrompt,
                        NegativePrompt = negativePrompt,
                        AspectRatio = string.IsNullOrEmpty(aspectRatio) ? "16:9" : aspectRatio,
                        Seed = GenerateSecureSeed(),
                        Model = "Flow High-Quality (dGPU)",
                        MediaType = "image",
                        TaskType = "cartesian"
                    };
                    tasks.Add(item);
                }
            }
            return tasks;
        }

        public static List<TaskItem> GenerateBatchReferenceMapper(List<string> imagePaths, string modifyingPrompt, string negativePrompt, string aspectRatio)
        {
            string batchId = "batch_ref_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var tasks = new List<TaskItem>();

            if (imagePaths != null)
            {
                foreach (var path in imagePaths)
                {
                    var item = new TaskItem
                    {
                        BatchId = batchId,
                        Prompt = modifyingPrompt,
                        NegativePrompt = negativePrompt,
                        AspectRatio = string.IsNullOrEmpty(aspectRatio) ? "1:1" : aspectRatio,
                        Seed = GenerateSecureSeed(),
                        Model = "Flow High-Quality (dGPU)",
                        MediaType = "image",
                        ReferencePath = path,
                        TaskType = "reference"
                    };
                    tasks.Add(item);
                }
            }
            return tasks;
        }
    }
}
