using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.PlatformAbstractions;

[assembly: AssemblyMetadata("SCMVersion", "test-estell")]

namespace XIVLauncher.Common.Tests;

[TestClass]
public class DalamudPackageTests
{
    [TestMethod]
    public async Task HealthyMirrorDoesNotNeedGithub()
    {
        using var fixture = new Fixture();
        await fixture.Download();
        Assert.AreEqual(1, fixture.MirrorPackages);
        Assert.AreEqual(0, fixture.OriginalPackages);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Target.FullName, "Dalamud.dll")));
    }

    [TestMethod]
    public async Task InvalidMirrorPackageFallsBackBeforeReplacingFiles()
    {
        using var fixture = new Fixture { InvalidMirror = true };
        await fixture.Download();
        Assert.AreEqual(1, fixture.MirrorPackages);
        Assert.AreEqual(1, fixture.OriginalPackages);
    }

    [TestMethod]
    public async Task DifferentVersionIsRejectedAndOriginalFilesRemain()
    {
        using var fixture = new Fixture();
        fixture.Info.AssemblyVersion = "other-estell";
        fixture.Target.Create();
        File.WriteAllText(Path.Combine(fixture.Target.FullName, "preserved.txt"), "original");
        await Assert.ThrowsExceptionAsync<DalamudIntegrityException>(fixture.Download);
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(fixture.Target.FullName, "preserved.txt")));
    }

    [TestMethod]
    public async Task ManifestZipHashIsVerifiedBeforeReplacement()
    {
        using var fixture = new Fixture();
        fixture.Info.DownloadSha256 = new string('0', 64);
        await Assert.ThrowsExceptionAsync<DalamudIntegrityException>(fixture.Download);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Target.FullName, "Dalamud.dll")));
    }

    [TestMethod]
    public async Task HealthySameVersionDoesNotDownloadTwice()
    {
        using var fixture = new Fixture();
        await fixture.Download();
        await fixture.Download();
        Assert.AreEqual(1, fixture.MirrorPackages);
    }

    [DataTestMethod]
    [DataRow(503)]
    [DataRow(404)]
    public async Task HttpErrorUsesManifestMirror(int status)
    {
        using var fixture = new Fixture { RawStatus = (HttpStatusCode)status };
        var method = typeof(DalamudUpdater).GetMethod("GetVersionInfo", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(fixture.Updater, ["estell", "key"])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.IsNotNull(result.GetType().GetField("Item2")!.GetValue(result));
        Assert.AreEqual(1, fixture.MirrorManifests);
    }

    [TestMethod]
    public async Task UnavailableCustomTrackDoesNotSilentlySelectOfficialTrack()
    {
        using var fixture = new Fixture { RawStatus = HttpStatusCode.ServiceUnavailable, MirrorManifestUnavailable = true };
        var method = typeof(DalamudUpdater).GetMethod("GetVersionInfo", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.ThrowsExceptionAsync<DalamudIntegrityException>(async () => await (Task)method.Invoke(fixture.Updater, ["estell", "key"])!);
    }

    [TestMethod]
    public async Task StalledBodyIsCancelledWithinReadDeadline()
    {
        using var body = new StalledStream();
        using var handler = new BodyHandler(body);
        using var client = new HttpClient(handler);
        var path = Path.Combine(Path.GetTempPath(), "estell-stall-" + Guid.NewGuid().ToString("N"));
        try
        {
            var method = typeof(DalamudUpdater).Assembly.GetType("XIVLauncher.Common.Dalamud.DependencySource")!.GetMethod("Download")!;
            var task = (Task)method.Invoke(null, [client, "https://example.invalid/file", path, (Action<long?, long, double?>)((_, _, _) => { }), TimeSpan.FromMilliseconds(50)])!;
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Fail("本文転送の停止を検出できませんでした。");
            }
            catch (OperationCanceledException) { Assert.IsTrue(body.HasCancellation); }
        }
        finally { File.Delete(path); }
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly HttpClient client;
        public DirectoryInfo Root { get; } = new(Path.Combine(Path.GetTempPath(), "estell-package-test-" + Guid.NewGuid().ToString("N")));
        public DirectoryInfo Target => new(Path.Combine(Root.FullName, "Hooks", Info.AssemblyVersion));
        public DalamudUpdater Updater { get; }
        public DalamudVersionInfo Info { get; } = new() { Track="estell", AssemblyVersion="test-estell", Key="key", RuntimeVersion="10.0.14", DownloadUrl="https://github.com/example/test-estell.zip" };
        public bool InvalidMirror { get; init; }
        public bool MirrorManifestUnavailable { get; init; }
        public HttpStatusCode RawStatus { get; init; } = HttpStatusCode.OK;
        public int MirrorPackages, OriginalPackages, MirrorManifests;

        public Fixture()
        {
            Root.Create();
            client = new HttpClient(this, false);
            Updater = new DalamudUpdater(client, Root, Root, Root, null, "Control") { Overlay = new Overlay() };
        }

        public async Task Download() => await (Task)typeof(DalamudUpdater).GetMethod("DownloadDalamud", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Updater, [Target, Info])!;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            var mirror = uri.Host == "estelldprereleaserepo.net";
            if (uri.AbsolutePath.EndsWith(".zip"))
            {
                if (mirror) MirrorPackages++; else OriginalPackages++;
                var files = new Dictionary<string, byte[]> {
                    ["Dalamud.dll"] = mirror && InvalidMirror ? Encoding.UTF8.GetBytes("invalid PE") : File.ReadAllBytes(typeof(DalamudPackageTests).Assembly.Location),
                    ["Dalamud.Injector.exe"] = Encoding.UTF8.GetBytes("injector"),
                    ["ImGuiScene.dll"] = Encoding.UTF8.GetBytes("scene"),
                };
                var hashes = files.ToDictionary(file => file.Key, file => Convert.ToHexString(MD5.HashData(file.Value)));
                files["hashes.json"] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(hashes));
                using var bytes = new MemoryStream();
                using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                    foreach (var file in files) { using var output = zip.CreateEntry(file.Key).Open(); output.Write(file.Value); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) });
            }
            if (mirror) MirrorManifests++;
            var status = uri.Host == "raw.githubusercontent.com" ? RawStatus : mirror && MirrorManifestUnavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            var info = uri.Host == "kamori.goats.dev" ? new DalamudVersionInfo { Track="release",AssemblyVersion="official",DownloadUrl="https://example.invalid/official.zip" } : Info;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(JsonConvert.SerializeObject(info)) });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                client.Dispose();
                var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Root.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Root.Name.StartsWith("estell-package-test-"))
                    throw new InvalidOperationException("検証用フォルダー以外は削除できません。");
                Root.Delete(true);
            }
            base.Dispose(disposing);
        }
    }

    private sealed class Overlay : IDalamudLoadingOverlay
    {
        public void SetStep(IDalamudLoadingOverlay.DalamudUpdateStep step) { }
        public void SetVisible() { } public void SetInvisible() { }
        public void ReportProgress(long? size, long downloaded, double? progress) { }
    }

    private sealed class BodyHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }

    private sealed class StalledStream : Stream
    {
        public bool HasCancellation;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { HasCancellation = token.CanBeCanceled; await Task.Delay(Timeout.Infinite, token); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
