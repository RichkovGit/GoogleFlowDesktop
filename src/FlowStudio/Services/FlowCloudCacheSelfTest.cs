using System.IO;

namespace FlowStudio.Services;

/// <summary>Pure cache-path checks; no user project or cache file is read or changed.</summary>
public static class FlowCloudCacheSelfTest
{
    public static Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var checks = 0;
        void Require(bool condition, string message)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        const string project = "60eb88ad-40ac-4b64-96dc-5470f7a1c57d";
        var projectPath = FlowCloudCache.GetProjectPath(project);
        var mediaDirectory = FlowCloudCache.GetMediaDirectory(project);
        Require(projectPath == Path.Combine(FlowCloudCache.CacheDirectory, project, "project.flowstudio"),
            "Cloud project cache did not use its fixed manifest path.");
        Require(FlowCloudCache.GetProjectPath(project.ToUpperInvariant()) == projectPath &&
            FlowCloudCache.GetProjectPath("projects/" + project) == projectPath,
            "Equivalent cloud project identifiers did not map to the same cache.");
        Require(FlowCloudCache.IsCloudCachePath(projectPath) && FlowCloudCache.IsCloudCachePath(mediaDirectory),
            "Cloud cache containment rejected its own generated paths.");
        Require(!FlowCloudCache.IsCloudCachePath(FlowCloudCache.CacheDirectory) &&
            !FlowCloudCache.IsCloudCachePath(FlowCloudCache.CacheDirectory + "-other" + Path.DirectorySeparatorChar + "project.flowstudio") &&
            !FlowCloudCache.IsCloudCachePath(Path.Combine(FlowCloudCache.CacheDirectory, project, "..", "..", "outside.flowstudio")) &&
            !FlowCloudCache.IsCloudCachePath("relative.flowstudio") && !FlowCloudCache.IsCloudCachePath(null),
            "Cloud cache containment accepted a sibling, traversal, or relative path.");

        foreach (var invalid in new[]
        {
            "", "..", "../" + project, project + "/..", "projects/../" + project,
            "https://flow.google.com/project/" + project, @"C:\outside", @"\\server\share",
            project + ":stream", project + "\0", "{60eb88ad-40ac-4b64-96dc-5470f7a1c57d}",
            "60eb88ad40ac4b6496dc5470f7a1c57d", "projects/projects/" + project
        })
        {
            var rejected = false;
            try { _ = FlowCloudCache.GetProjectPath(invalid); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "Cloud cache accepted a malformed project ID or path traversal.");
        }

        const string mediaId = "media/synthetic-cloud-id.0";
        var stableId = FlowCloudCache.GetAssetId(project, mediaId);
        Require(stableId == FlowCloudCache.GetAssetId("projects/" + project.ToUpperInvariant(), mediaId) &&
            stableId != FlowCloudCache.GetAssetId("00000000-0000-0000-0000-000000000001", mediaId),
            "Cloud media identity changed on repeat sync or collided across projects.");
        var mediaPath = FlowCloudCache.GetMediaPath(project, mediaId, ".PNG");
        Require(mediaPath == Path.Combine(mediaDirectory, stableId + ".png") && FlowCloudCache.IsCloudCachePath(mediaPath),
            "Cloud media did not use a stable allowed-format destination.");
        var arbitraryIdPath = FlowCloudCache.GetMediaPath(project, "../../untrusted:remote*identifier", ".mp4");
        Require(FlowCloudCache.IsCloudCachePath(arbitraryIdPath) &&
            Path.GetDirectoryName(arbitraryIdPath) == mediaDirectory,
            "A remote media ID escaped its hashed filename.");
        foreach (var invalidExtension in new[] { ".exe", ".mp4/../../outside", "", ".png:stream" })
        {
            var rejected = false;
            try { _ = FlowCloudCache.GetMediaPath(project, mediaId, invalidExtension); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "Cloud media accepted an executable or unsafe extension.");
        }
        var urlRejected = false;
        try { _ = FlowCloudCache.GetAssetId(project, "https://storage.googleapis.com/synthetic?signature=temporary"); }
        catch (ArgumentException) { urlRejected = true; }
        Require(urlRejected, "Temporary remote URLs must not be used as persistent media identities.");
        return Task.FromResult($"{checks} pure checks: canonical UUID paths, containment, traversal rejection, stable cloud media identity, extension allowlist, and no signed-URL identity. No project files were accessed.");
    }
}
