using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Tests;

[TestClass]
public class DalamudRuntimeUpdateTests
{
    [TestMethod]
    public async Task PreservesOtherVersionsWhileTheyAreInUse()
    {
        using var fixture = new Fixture();
        using var lockedFile = File.Open(fixture.LegacyFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        await fixture.Update();
        Assert.AreEqual("legacy", File.ReadAllText(fixture.LegacyFile));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Runtime.FullName, "shared", "Microsoft.NETCore.App", "10.0.14", "coreclr.dll")));
    }

    [TestMethod]
    public async Task FailedDownloadDoesNotRemoveExistingFiles()
    {
        using var fixture = new Fixture { FailDownload = true };
        await Assert.ThrowsExceptionAsync<HttpRequestException>(fixture.Update);
        Assert.AreEqual("legacy", File.ReadAllText(fixture.LegacyFile));
        Assert.AreEqual("6.0.36", File.ReadAllText(Path.Combine(fixture.Runtime.FullName, "version")));
    }

    [TestMethod]
    public async Task InvalidDownloadedHashesDoNotReplaceExistingFiles()
    {
        using var fixture = new Fixture { InvalidHashes = true };
        await Assert.ThrowsExceptionAsync<DalamudIntegrityException>(fixture.Update);
        Assert.AreEqual("legacy", File.ReadAllText(fixture.LegacyFile));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Runtime.FullName, "dotnet.exe")));
    }

    [TestMethod]
    public async Task ConcurrentUpdatesReuseValidatedRuntime()
    {
        using var fixture = new Fixture();
        await Task.WhenAll(fixture.Update(), fixture.Update());
        Assert.AreEqual(2, fixture.DownloadCount);
        Assert.AreEqual("legacy", File.ReadAllText(fixture.LegacyFile));
    }

    private sealed class Fixture : HttpMessageHandler, IDisposable
    {
        private readonly DirectoryInfo root = new(Path.Combine(Path.GetTempPath(), "estell-runtime-test-" + Guid.NewGuid().ToString("N")));
        private readonly Dictionary<string, byte[]> files = new()
        {
            ["dotnet.exe"] = Encoding.UTF8.GetBytes("new-host"),
            ["host/fxr/10.0.14/hostfxr.dll"] = Encoding.UTF8.GetBytes("new-fxr"),
            ["shared/Microsoft.NETCore.App/10.0.14/coreclr.dll"] = Encoding.UTF8.GetBytes("new-core"),
            ["shared/Microsoft.WindowsDesktop.App/10.0.14/wpf.dll"] = Encoding.UTF8.GetBytes("new-desktop"),
        };
        private readonly HttpClient client;
        private int downloadCount;

        public Fixture()
        {
            Runtime = root.CreateSubdirectory("runtime");
            var old = Runtime.CreateSubdirectory(Path.Combine("shared", "Microsoft.NETCore.App", "6.0.36"));
            LegacyFile = Path.Combine(old.FullName, "coreclr.dll");
            File.WriteAllText(LegacyFile, "legacy");
            File.WriteAllText(Path.Combine(Runtime.FullName, "version"), "6.0.36");
            client = new HttpClient(this, disposeHandler: false);
        }

        public DirectoryInfo Runtime { get; }
        public string LegacyFile { get; }
        public bool FailDownload { get; init; }
        public bool InvalidHashes { get; init; }
        public int DownloadCount => downloadCount;

        public async Task Update()
        {
            // インストール先は検証専用で、実際の利用者のランタイムには触れない。
            var updater = new DalamudUpdater(client, root, Runtime, root, null, "Control") { Overlay = new Overlay() };
            var method = typeof(DalamudUpdater).GetMethod("DownloadRuntime", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(updater, new object[] { Runtime, "10.0.14" })!;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/Hashes/"))
            {
                var hashes = new Dictionary<string, string>();
                foreach (var item in files)
                    hashes[item.Key] = InvalidHashes ? new string('0', 32) : Convert.ToHexString(MD5.HashData(item.Value));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(hashes)) };
            }

            Interlocked.Increment(ref downloadCount);
            await Task.Delay(50, cancellationToken);
            if (FailDownload)
                return new HttpResponseMessage(HttpStatusCode.BadRequest);

            using var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var file in files)
                    using (var stream = archive.CreateEntry(file.Key).Open())
                        stream.Write(file.Value);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                client.Dispose();
                var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (root.FullName.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && root.Exists)
                    root.Delete(true);
            }
            base.Dispose(disposing);
        }
    }

    private sealed class Overlay : IDalamudLoadingOverlay
    {
        public void SetStep(IDalamudLoadingOverlay.DalamudUpdateStep step) { }
        public void SetVisible() { }
        public void SetInvisible() { }
        public void ReportProgress(long? size, long downloaded, double? progress) { }
    }
}
