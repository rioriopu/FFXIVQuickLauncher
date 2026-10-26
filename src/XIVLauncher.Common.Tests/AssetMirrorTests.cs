using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
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
public class AssetMirrorTests
{
    [TestMethod]
    public async Task HealthyAssetsAreCheckedWithoutDownloadingAgain()
    {
        using var fixture = new Fixture { OfficialUnavailable = true };
        await fixture.Update();
        await fixture.Update();
        Assert.AreEqual(1, fixture.Packages);
        Assert.AreEqual(2, fixture.MetadataRequests);
        Assert.AreEqual(0, fixture.OfficialRequests);
    }

    [TestMethod]
    public async Task UnavailableMirrorUsesOfficialMetadataAndPackage()
    {
        using var fixture = new Fixture { MirrorUnavailable = true };
        await fixture.Update();
        Assert.IsTrue(fixture.OfficialRequests >= 2);
        Assert.AreEqual("verified", File.ReadAllText(Path.Combine(fixture.Root.FullName, "446", "asset.bin")));
    }

    [TestMethod]
    public async Task CorruptMirroredPackageIsVerifiedBeforeOfficialFallback()
    {
        using var fixture = new Fixture { CorruptMirror = true };
        await fixture.Update();
        Assert.AreEqual(2, fixture.Packages);
        Assert.AreEqual("verified", File.ReadAllText(Path.Combine(fixture.Root.FullName, "446", "asset.bin")));
    }

    [TestMethod]
    public async Task BothCorruptPackagesLeaveExistingAssetsUntouched()
    {
        using var fixture = new Fixture { CorruptMirror = true, CorruptOfficial = true };
        var directory = fixture.Root.CreateSubdirectory("446");
        File.WriteAllText(Path.Combine(directory.FullName, "asset.bin"), "existing");
        File.WriteAllText(Path.Combine(fixture.Root.FullName, "asset.ver"), "446");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(fixture.Update);
        Assert.AreEqual("existing", File.ReadAllText(Path.Combine(directory.FullName, "asset.bin")));
        Assert.AreEqual("446", File.ReadAllText(Path.Combine(fixture.Root.FullName, "asset.ver")));
    }

    [TestMethod]
    public async Task StaleMirrorCannotDowngradeInstalledAssets()
    {
        using var fixture = new Fixture { MirrorVersion = 445 };
        var directory = fixture.Root.CreateSubdirectory("446");
        File.WriteAllText(Path.Combine(directory.FullName, "asset.bin"), "verified");
        File.WriteAllText(Path.Combine(fixture.Root.FullName, "asset.ver"), "446");
        await fixture.Update();
        Assert.AreEqual(0, fixture.Packages);
        Assert.AreEqual(1, fixture.OfficialRequests);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly HttpClient client;
        private readonly DalamudUpdater updater;
        public DirectoryInfo Root { get; } = new(Path.Combine(Path.GetTempPath(), "estell-asset-test-" + Guid.NewGuid().ToString("N")));
        public bool MirrorUnavailable { get; init; }
        public bool OfficialUnavailable { get; init; }
        public bool CorruptMirror { get; init; }
        public bool CorruptOfficial { get; init; }
        public int MirrorVersion { get; init; } = 446;
        public int OfficialRequests { get; private set; }
        public int MetadataRequests { get; private set; }
        public int Packages { get; private set; }

        public Fixture()
        {
            Root.Create();
            client = new HttpClient(this, disposeHandler: false);
            updater = new DalamudUpdater(client, Root, Root, Root, null, "Control") { Overlay = new Overlay() };
        }

        public async Task Update() => await AssetManager.EnsureAssets(client, updater, Root);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var mirror = request.RequestUri!.Host == new Uri(DistributionConfig.MirrorBase).Host;
            if (!mirror)
                OfficialRequests++;
            if (mirror ? MirrorUnavailable : OfficialUnavailable)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var expected = Encoding.UTF8.GetBytes("verified");
            if (request.RequestUri.AbsolutePath.EndsWith("meta.json") || request.RequestUri.AbsolutePath.EndsWith("/Meta"))
            {
                MetadataRequests++;
                var metadata = new
                {
                    version = mirror ? MirrorVersion : 446,
                    packageUrl = mirror ? DistributionConfig.MirrorAssetPackageUrl(MirrorVersion) : "https://kamori.goats.dev/File/Get/test-assets",
                    originalPackageUrl = "https://kamori.goats.dev/File/Get/test-assets",
                    assets = new[] { new { fileName = "asset.bin", hash = Convert.ToHexString(SHA1.HashData(expected)), url = "https://kamori.goats.dev/File/Get/test-asset" } },
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(metadata)) });
            }
            Packages++;
            using var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            using (var file = archive.CreateEntry("asset.bin").Open())
                file.Write((mirror ? CorruptMirror : CorruptOfficial) ? Encoding.UTF8.GetBytes("bad") : expected);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                client.Dispose();
                var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Root.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Root.Name.StartsWith("estell-asset-test-"))
                    throw new InvalidOperationException("検証用フォルダ以外は削除できません。");
                Root.Delete(recursive: true);
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
