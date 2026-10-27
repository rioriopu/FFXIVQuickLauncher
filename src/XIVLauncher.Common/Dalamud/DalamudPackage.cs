using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace XIVLauncher.Common.Dalamud;

internal static class DalamudPackage
{
    // ゲーム用アセンブリをランチャーへロードせず、埋め込まれた版だけを読む。
    public static string? ReadScmVersion(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;
            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference)
                continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(type.Namespace) != "System.Reflection" || metadata.GetString(type.Name) != "AssemblyMetadataAttribute")
                continue;
            var value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() == 1 && value.ReadSerializedString() == "SCMVersion")
                return value.ReadSerializedString();
        }
        return null;
    }

    public static bool MatchesVersion(DirectoryInfo directory, DalamudVersionInfo expected)
        => !DistributionConfig.IsCustomTrack(expected.Track)
           || string.Equals(ReadScmVersion(Path.Combine(directory.FullName, "Dalamud.dll")), expected.AssemblyVersion, StringComparison.Ordinal);
}
