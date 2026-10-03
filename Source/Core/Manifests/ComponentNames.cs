namespace Unvault.Core.Manifests;

public enum ComponentGroup { Content, Debugging, TargetPlatform, Other }

/// <summary>Human names for install tags, matching the Epic Games Launcher's install options.</summary>
public static class ComponentNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["starter_content"] = "Starter Content",
        ["templates"] = "Templates and Feature Packs",
        ["engine_source"] = "Engine Source",
        ["metahuman_content"] = "MetaHuman Content",
        ["editor_symbols"] = "Editor symbols for debugging",
        ["platform_Android"] = "Android",
        ["platform_IOS"] = "iOS",
        ["platform_Linux"] = "Linux",
        ["platform_LinuxArm64"] = "Linux Arm64",
        ["platform_Mac"] = "macOS",
        ["platform_TVOS"] = "tvOS",
        ["platform_VOS"] = "visionOS",
        ["platform_WinARM64"] = "Windows on Arm",
        ["platform_HoloLens"] = "HoloLens 2",
        ["platform_Lumin"] = "Magic Leap",
    };

    public static string GetDisplayName(string tag) =>
        Names.TryGetValue(tag, out var name) ? name
        : tag.StartsWith("platform_", StringComparison.Ordinal) ? tag["platform_".Length..]
        : tag.Replace('_', ' ');

    public static ComponentGroup GetGroup(string tag) => tag switch
    {
        "editor_symbols" => ComponentGroup.Debugging,
        _ when tag.StartsWith("platform_", StringComparison.Ordinal) => ComponentGroup.TargetPlatform,
        "starter_content" or "templates" or "engine_source" or "metahuman_content" => ComponentGroup.Content,
        _ => ComponentGroup.Other,
    };
}
