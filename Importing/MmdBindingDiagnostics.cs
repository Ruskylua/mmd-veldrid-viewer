using System.Text;
using System.Text.Json;
using System.Numerics;

namespace SimplePmxVeldrid.Importing;

public static class MmdBindingDiagnostics
{
    public static string Write(PmxModel model, VmdMotion motion, VmdBoneBinding binding, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var fileName = $"mmd-binding-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        var path = Path.Combine(outputDirectory, fileName);
        File.WriteAllText(path, Build(model, motion, binding), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (IsFbxModel(model))
        {
            WriteFbxRetargetMap(model, motion, binding, outputDirectory, Path.GetFileNameWithoutExtension(fileName));
            WriteFbxRetargetProfile(model, motion, binding, outputDirectory, Path.GetFileNameWithoutExtension(fileName));
        }
        return path;
    }

    private static string Build(PmxModel model, VmdMotion motion, VmdBoneBinding binding)
    {
        var sb = new StringBuilder();
        sb.AppendLine("MMD binding diagnostics");
        sb.AppendLine($"PMX: {model.SourcePath}");
        sb.AppendLine($"PMX model: {model.Name}");
        sb.AppendLine($"VMD: {motion.SourcePath}");
        sb.AppendLine($"VMD model: {motion.ModelName}");
        sb.AppendLine($"Matched: {binding.MatchedBoneNameCount}/{binding.TotalAnimatedBoneNames} names, {binding.MatchedBoneFrames}/{binding.TotalBoneFrames} frames");
        if (IsFbxModel(model))
            sb.AppendLine("FBX external VMD retarget is controlled by fbx-retarget-profile-active.json. The generated default profile disables all bones; VMD camera/audio still work.");
        sb.AppendLine();

        sb.AppendLine("[PMX lower-body candidate bones]");
        foreach (var bone in model.Bones.Select((bone, index) => new { bone, index }).Where(x => IsLowerBodyName(x.bone.Name)))
        {
            var append = bone.bone.AppendParentIndex < 0
                ? "-"
                : $"parent={bone.bone.AppendParentIndex} weight={bone.bone.AppendWeight:0.###} rot={bone.bone.InheritsRotation} move={bone.bone.InheritsTranslation}";
            sb.AppendLine($"{bone.index,4}: {bone.bone.Name} layer={bone.bone.TransformLayer} parent={bone.bone.ParentIndex} append={append} ik={(bone.bone.Ik is null ? "-" : $"target={bone.bone.Ik.TargetBoneIndex} links={string.Join(",", bone.bone.Ik.Links.Select(FormatIkLink))}")}");
        }

        sb.AppendLine();
        sb.AppendLine("[VMD lower-body animated bones]");
        foreach (var pair in motion.BoneFramesByName.Where(x => IsLowerBodyName(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var bound = binding.BoundBones.FirstOrDefault(x => x.BoneName == pair.Key);
            var boundText = bound is null
                ? "UNMATCHED"
                : $"PMX[{bound.PmxBoneIndex}] {model.Bones[bound.PmxBoneIndex].Name}";
            var maxTranslation = pair.Value.Count == 0 ? 0f : pair.Value.Max(x => x.Translation.Length());
            sb.AppendLine($"{pair.Key} frames={pair.Value.Count} maxT={maxTranslation:0.000} -> {boundText}");
        }

        sb.AppendLine();
        sb.AppendLine("[Unmatched lower-body VMD bones]");
        foreach (var name in binding.UnmatchedVmdBoneNames.Where(IsLowerBodyName).OrderBy(x => x, StringComparer.Ordinal))
            sb.AppendLine(name);

        sb.AppendLine();
        sb.AppendLine("[Duplicate PMX bone bindings]");
        foreach (var duplicate in binding.DuplicatePmxBoneBindings)
            sb.AppendLine(duplicate);

        sb.AppendLine();
        sb.AppendLine("[Top unmatched VMD bones]");
        foreach (var name in binding.UnmatchedVmdBoneNames.Take(50))
            sb.AppendLine(name);

        if (IsFbxModel(model))
            AppendFbxRetargetDiagnostics(sb, model, motion, binding);

        return sb.ToString();
    }

    private static void AppendFbxRetargetDiagnostics(StringBuilder sb, PmxModel model, VmdMotion motion, VmdBoneBinding binding)
    {
        AppendFbxPlaybackRetargetDiagnostics(sb, model, motion);

        sb.AppendLine();
        sb.AppendLine("[FBX retarget candidates - not applied]");
        sb.AppendLine("This section shows VMD bone names that can be matched to FBX bones. It is a map-building aid, not active motion.");

        var important = GetEssentialFbxRetargetBoneNames();

        var boundByVmdName = binding.BoundBones
            .GroupBy(x => x.BoneName, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var covered = 0;
        foreach (var vmdName in important)
        {
            if (boundByVmdName.TryGetValue(vmdName, out var bound) &&
                (uint)bound.PmxBoneIndex < (uint)model.Bones.Count)
            {
                covered++;
                var maxTranslation = motion.BoneFramesByName.TryGetValue(vmdName, out var frames) && frames.Count > 0
                    ? frames.Max(x => x.Translation.Length())
                    : 0f;
                var maxRotationDegrees = bound.Frames.Count > 0
                    ? bound.Frames.Max(x => GetRotationAngleDegrees(x.Rotation))
                    : 0f;
                sb.AppendLine($"{vmdName} -> FBX[{bound.PmxBoneIndex}] {model.Bones[bound.PmxBoneIndex].Name} group={GetRetargetGroup(vmdName)} frames={bound.Frames.Count} maxT={maxTranslation:0.000} maxR={maxRotationDegrees:0.0}deg risk={GetRetargetRisk(vmdName, maxTranslation, maxRotationDegrees)}");
            }
            else
            {
                sb.AppendLine($"{vmdName} -> UNMATCHED");
            }
        }

        sb.AppendLine($"Essential coverage: {covered}/{important.Length}");
        sb.AppendLine();
        sb.AppendLine("[FBX bone inventory sample]");
        foreach (var bone in model.Bones.Select((bone, index) => new { bone, index }).Take(120))
        {
            var parent = bone.bone.ParentIndex >= 0 && bone.bone.ParentIndex < model.Bones.Count
                ? model.Bones[bone.bone.ParentIndex].Name
                : "-";
            sb.AppendLine($"{bone.index,4}: {bone.bone.Name} parent={parent}");
        }
    }

    private static void AppendFbxPlaybackRetargetDiagnostics(StringBuilder sb, PmxModel model, VmdMotion motion)
    {
        var playbackBinding = VmdBoneBinder.BindForPlayback(model, motion);

        sb.AppendLine();
        sb.AppendLine("[FBX playback retarget - active profile]");
        sb.AppendLine("Only entries explicitly enabled in fbx-retarget-profile-active.json are applied. The generated default profile sets Enabled=false and scales=0 for every bone.");
        sb.AppendLine($"Active-profile matched: {playbackBinding.MatchedBoneNameCount}/{playbackBinding.TotalAnimatedBoneNames} names, {playbackBinding.MatchedBoneFrames}/{playbackBinding.TotalBoneFrames} frames");

        foreach (var bound in playbackBinding.BoundBones.OrderBy(x => x.BoneName, StringComparer.Ordinal))
        {
            if ((uint)bound.PmxBoneIndex >= (uint)model.Bones.Count)
                continue;

            var maxRotationDegrees = bound.Frames.Count > 0
                ? bound.Frames.Max(x => GetRotationAngleDegrees(x.Rotation))
                : 0f;
            var maxTranslation = bound.Frames.Count > 0
                ? bound.Frames.Max(x => x.Translation.Length())
                : 0f;
            sb.AppendLine($"{bound.BoneName} -> FBX[{bound.PmxBoneIndex}] {model.Bones[bound.PmxBoneIndex].Name} group={GetRetargetGroup(bound.BoneName)} frames={bound.Frames.Count} scaledMaxT={maxTranslation:0.000} scaledMaxR={maxRotationDegrees:0.0}deg");
        }
    }

    private static void WriteFbxRetargetMap(PmxModel model, VmdMotion motion, VmdBoneBinding binding, string outputDirectory, string baseFileName)
    {
        try
        {
            var important = GetEssentialFbxRetargetBoneNames();
            var boundByVmdName = binding.BoundBones
                .GroupBy(x => x.BoneName, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

            var entries = important.Select(vmdName =>
            {
                if (!boundByVmdName.TryGetValue(vmdName, out var bound) ||
                    (uint)bound.PmxBoneIndex >= (uint)model.Bones.Count)
                {
                    return new FbxRetargetMapEntry(
                        vmdName,
                        null,
                        null,
                        false,
                        GetRetargetGroup(vmdName),
                        "Unmatched",
                        "Disabled",
                        0,
                        0f,
                        0f);
                }

                var maxTranslation = motion.BoneFramesByName.TryGetValue(vmdName, out var frames) && frames.Count > 0
                    ? frames.Max(x => x.Translation.Length())
                    : 0f;
                var maxRotationDegrees = bound.Frames.Count > 0
                    ? bound.Frames.Max(x => GetRotationAngleDegrees(x.Rotation))
                    : 0f;
                return new FbxRetargetMapEntry(
                    vmdName,
                    bound.PmxBoneIndex,
                    model.Bones[bound.PmxBoneIndex].Name,
                    true,
                    GetRetargetGroup(vmdName),
                    GetRetargetRisk(vmdName, maxTranslation, maxRotationDegrees),
                    GetRetargetRecommendation(vmdName, maxTranslation, maxRotationDegrees),
                    bound.Frames.Count,
                    maxTranslation,
                    maxRotationDegrees);
            }).ToArray();

            var map = new FbxRetargetMapDocument(
                model.SourcePath,
                motion.SourcePath,
                model.Name,
                motion.ModelName,
                "diagnostic-candidates",
                "FBX external VMD retarget is controlled by fbx-retarget-profile-active.json. This map is diagnostic-only.",
                entries.Count(x => x.Matched),
                entries.Length,
                entries);

            var jsonPath = Path.Combine(outputDirectory, baseFileName.Replace("mmd-binding", "fbx-retarget-map", StringComparison.Ordinal) + ".json");
            var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Retarget map export is diagnostic-only; never block VMD loading.
        }
    }

    private static void WriteFbxRetargetProfile(PmxModel model, VmdMotion motion, VmdBoneBinding binding, string outputDirectory, string baseFileName)
    {
        try
        {
            var important = GetEssentialFbxRetargetBoneNames();
            var boundByVmdName = binding.BoundBones
                .GroupBy(x => x.BoneName, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

            var entries = important.Select(vmdName =>
            {
                boundByVmdName.TryGetValue(vmdName, out var bound);
                var matched = bound is not null && (uint)bound.PmxBoneIndex < (uint)model.Bones.Count;
                var maxTranslation = motion.BoneFramesByName.TryGetValue(vmdName, out var frames) && frames.Count > 0
                    ? frames.Max(x => x.Translation.Length())
                    : 0f;
                var maxRotationDegrees = matched && bound!.Frames.Count > 0
                    ? bound.Frames.Max(x => GetRotationAngleDegrees(x.Rotation))
                    : 0f;
                var group = GetRetargetGroup(vmdName);
                var risk = matched ? GetRetargetRisk(vmdName, maxTranslation, maxRotationDegrees) : "Unmatched";

                return new FbxRetargetProfileEntry(
                    vmdName,
                    matched ? bound!.PmxBoneIndex : null,
                    matched ? model.Bones[bound!.PmxBoneIndex].Name : null,
                    matched,
                    false,
                    group,
                    risk,
                    GetDefaultRetargetMode(group, risk),
                    0f,
                    0f,
                    "Disabled by default. Enable manually only after verifying this FBX bone's rest axis and scale.",
                    "PmxToFbxPreview",
                    GetHumanBone(vmdName),
                    GetIdentityAxisFix());
            }).ToArray();

            var profile = new FbxRetargetProfileDocument(
                2,
                model.SourcePath,
                motion.SourcePath,
                model.Name,
                motion.ModelName,
                "disabled-by-default",
                "Manual retarget profile scaffold. HumanBone is a Unity-Humanoid-style target hint; AxisFix is a manual quaternion basis correction applied as inverse(axisFix) * rotation * axisFix. The runtime applies only entries with Enabled=true from fbx-retarget-profile-active.json. Generated entries are disabled by default.",
                entries);

            var jsonPath = Path.Combine(outputDirectory, baseFileName.Replace("mmd-binding", "fbx-retarget-profile", StringComparison.Ordinal) + ".json");
            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var activePath = FbxRetargetProfileStore.GetActivePath(outputDirectory);
            if (!File.Exists(activePath))
                File.WriteAllText(activePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var headTestProfile = BuildFbxHeadTestProfile(profile);
            var headTestJson = JsonSerializer.Serialize(headTestProfile, new JsonSerializerOptions { WriteIndented = true });
            var datedHeadTestPath = Path.Combine(outputDirectory, baseFileName.Replace("mmd-binding", "fbx-retarget-profile-head-test", StringComparison.Ordinal) + ".json");
            File.WriteAllText(datedHeadTestPath, headTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(FbxRetargetProfileStore.GetHeadTestPath(outputDirectory), headTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var headInvertTestProfile = BuildFbxHeadTestProfile(profile, "head-invert-test", "InvertPmxToFbxPreview", "Head inverse-conversion FBX retarget test preset. Press Q in the render window to activate this preset.");
            var headInvertTestJson = JsonSerializer.Serialize(headInvertTestProfile, new JsonSerializerOptions { WriteIndented = true });
            var datedHeadInvertTestPath = Path.Combine(outputDirectory, baseFileName.Replace("mmd-binding", "fbx-retarget-profile-head-invert-test", StringComparison.Ordinal) + ".json");
            File.WriteAllText(datedHeadInvertTestPath, headInvertTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(FbxRetargetProfileStore.GetHeadInvertTestPath(outputDirectory), headInvertTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var headRawTestProfile = BuildFbxHeadTestProfile(profile, "head-raw-test", "RawPmxPreview", "Head raw-PMX-preview FBX retarget test preset. Press E in the render window to activate this preset.");
            var headRawTestJson = JsonSerializer.Serialize(headRawTestProfile, new JsonSerializerOptions { WriteIndented = true });
            var datedHeadRawTestPath = Path.Combine(outputDirectory, baseFileName.Replace("mmd-binding", "fbx-retarget-profile-head-raw-test", StringComparison.Ordinal) + ".json");
            File.WriteAllText(datedHeadRawTestPath, headRawTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(FbxRetargetProfileStore.GetHeadRawTestPath(outputDirectory), headRawTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var neckHeadTestProfile = BuildFbxNeckHeadTestProfile(profile);
            var neckHeadTestJson = JsonSerializer.Serialize(neckHeadTestProfile, new JsonSerializerOptions { WriteIndented = true });
            var datedNeckHeadTestPath = Path.Combine(outputDirectory, baseFileName.Replace("mmd-binding", "fbx-retarget-profile-neck-head-test", StringComparison.Ordinal) + ".json");
            File.WriteAllText(datedNeckHeadTestPath, neckHeadTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(FbxRetargetProfileStore.GetNeckHeadTestPath(outputDirectory), neckHeadTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var upperBodyTestProfile = BuildFbxUpperBodyTestProfile(profile);
            var upperBodyTestJson = JsonSerializer.Serialize(upperBodyTestProfile, new JsonSerializerOptions { WriteIndented = true });
            var datedUpperBodyTestPath = Path.Combine(outputDirectory, baseFileName.Replace("mmd-binding", "fbx-retarget-profile-upper-body-test", StringComparison.Ordinal) + ".json");
            File.WriteAllText(datedUpperBodyTestPath, upperBodyTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(FbxRetargetProfileStore.GetUpperBodyTestPath(outputDirectory), upperBodyTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var spineTestProfile = BuildFbxSpineTestProfile(profile);
            var spineTestJson = JsonSerializer.Serialize(spineTestProfile, new JsonSerializerOptions { WriteIndented = true });
            var datedSpineTestPath = Path.Combine(outputDirectory, baseFileName.Replace("mmd-binding", "fbx-retarget-profile-spine-test", StringComparison.Ordinal) + ".json");
            File.WriteAllText(datedSpineTestPath, spineTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(FbxRetargetProfileStore.GetSpineTestPath(outputDirectory), spineTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var lowerBodyTestProfile = BuildFbxLowerBodyTestProfile(profile);
            var lowerBodyTestJson = JsonSerializer.Serialize(lowerBodyTestProfile, new JsonSerializerOptions { WriteIndented = true });
            var datedLowerBodyTestPath = Path.Combine(outputDirectory, baseFileName.Replace("mmd-binding", "fbx-retarget-profile-lower-body-test", StringComparison.Ordinal) + ".json");
            File.WriteAllText(datedLowerBodyTestPath, lowerBodyTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(FbxRetargetProfileStore.GetLowerBodyTestPath(outputDirectory), lowerBodyTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var fullBodyTestProfile = BuildFbxFullBodyTestProfile(profile);
            var fullBodyTestJson = JsonSerializer.Serialize(fullBodyTestProfile, new JsonSerializerOptions { WriteIndented = true });
            var datedFullBodyTestPath = Path.Combine(outputDirectory, baseFileName.Replace("mmd-binding", "fbx-retarget-profile-full-body-test", StringComparison.Ordinal) + ".json");
            File.WriteAllText(datedFullBodyTestPath, fullBodyTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(FbxRetargetProfileStore.GetFullBodyTestPath(outputDirectory), fullBodyTestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Retarget profile export is diagnostic-only; never block VMD loading.
        }
    }

    private static FbxRetargetProfileDocument BuildFbxHeadTestProfile(
        FbxRetargetProfileDocument source,
        string mode = "head-test",
        string rotationMode = "PmxToFbxPreview",
        string note = "Head-only FBX retarget test preset. Copy this file to fbx-retarget-profile-active.json, then press P in the render window to reload.")
    {
        var entries = source.Entries
            .Select(entry =>
            {
                var isHead =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u982d", StringComparison.Ordinal) &&
                    (entry.FbxBoneName?.Contains("Head", StringComparison.OrdinalIgnoreCase) ?? false);
                return entry with
                {
                    Enabled = isHead,
                    RotationScale = isHead ? 0.05f : 0f,
                    TranslationScale = 0f,
                    RotationMode = isHead ? rotationMode : "PmxToFbxPreview",
                    Note = isHead
                        ? $"Head-only test preset. RotationMode={rotationMode}. Keep scale tiny."
                        : "Disabled in head-only test preset."
                };
            })
            .ToArray();

        return source with
        {
            Mode = mode,
            Note = note,
            Entries = entries
        };
    }

    private static FbxRetargetProfileDocument BuildFbxUpperBodyTestProfile(FbxRetargetProfileDocument source)
    {
        var entries = source.Entries
            .Select(entry =>
            {
                var isHead =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u982d", StringComparison.Ordinal) &&
                    (entry.FbxBoneName?.Contains("Head", StringComparison.OrdinalIgnoreCase) ?? false);
                var isNeck =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u9996", StringComparison.Ordinal) &&
                    (entry.FbxBoneName?.Contains("Neck", StringComparison.OrdinalIgnoreCase) ?? false);
                var isSpine =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u4e0a\u534a\u8eab", StringComparison.Ordinal) &&
                    string.Equals(entry.HumanBone, "Spine", StringComparison.Ordinal);
                var isChest =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u4e0a\u534a\u8eab2", StringComparison.Ordinal) &&
                    string.Equals(entry.HumanBone, "Chest", StringComparison.Ordinal);
                var isShoulder =
                    entry.Matched &&
                    IsSafeFbxDeformBone(entry.FbxBoneName) &&
                    (entry.VmdBoneName.Contains("\u80a9", StringComparison.Ordinal) ||
                     entry.FbxBoneName?.Contains("Shoulder", StringComparison.OrdinalIgnoreCase) == true ||
                     entry.FbxBoneName?.Contains("Clavicle", StringComparison.OrdinalIgnoreCase) == true);
                var isUpperArm =
                    entry.Matched &&
                    IsSafeFbxDeformBone(entry.FbxBoneName) &&
                    entry.VmdBoneName.Contains("\u8155", StringComparison.Ordinal) &&
                    !entry.VmdBoneName.Contains("\u3072\u3058", StringComparison.Ordinal) &&
                    !entry.VmdBoneName.Contains("\u624b\u9996", StringComparison.Ordinal);
                var isElbowOrWrist =
                    entry.Matched &&
                    IsSafeFbxDeformBone(entry.FbxBoneName) &&
                    (entry.VmdBoneName.Contains("\u3072\u3058", StringComparison.Ordinal) ||
                     entry.VmdBoneName.Contains("\u624b\u9996", StringComparison.Ordinal));
                var enabled = isHead || isSpine || isChest || isShoulder || isUpperArm || isElbowOrWrist;
                var scale =
                    isHead ? 0.05f :
                    isSpine ? 0.008f :
                    isChest ? 0.008f :
                    isShoulder ? 0.008f :
                    isUpperArm ? 0.008f :
                    isElbowOrWrist ? 0.006f :
                    0f;
                return entry with
                {
                    Enabled = enabled,
                    RotationScale = scale,
                    TranslationScale = 0f,
                    RotationMode = isHead || isNeck ? "InvertPmxToFbxPreview" : "PmxToFbxPreview",
                    Note = enabled
                        ? "Upper-body safe test preset: torso/head plus main arm deform bones only; intentionally tiny rotation-only scale."
                        : isNeck
                            ? "Neck is disabled in upper-body test preset. Use N for neck+head testing."
                        : "Disabled in upper-body test preset."
                };
            })
            .ToArray();

        return source with
        {
            Mode = "upper-body-test",
            Note = "Upper-body FBX retarget test preset. Press A in the render window to activate this preset. Rotation-only, very small scales, support/twist bones disabled. Neck is disabled here; use N for neck+head testing.",
            Entries = entries
        };
    }

    private static bool IsSafeFbxDeformBone(string? fbxBoneName)
    {
        return !string.IsNullOrWhiteSpace(fbxBoneName) &&
               !fbxBoneName.Contains("Support", StringComparison.OrdinalIgnoreCase) &&
               !fbxBoneName.Contains("Twist", StringComparison.OrdinalIgnoreCase) &&
               !fbxBoneName.Contains("Dummy", StringComparison.OrdinalIgnoreCase);
    }

    private static FbxRetargetProfileDocument BuildFbxSpineTestProfile(FbxRetargetProfileDocument source)
    {
        var entries = source.Entries
            .Select(entry =>
            {
                var isHead =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u982d", StringComparison.Ordinal) &&
                    (entry.FbxBoneName?.Contains("Head", StringComparison.OrdinalIgnoreCase) ?? false);
                var isNeck =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u9996", StringComparison.Ordinal) &&
                    (entry.FbxBoneName?.Contains("Neck", StringComparison.OrdinalIgnoreCase) ?? false);
                var isSpine =
                    entry.Matched &&
                    (entry.VmdBoneName.Equals("\u4e0a\u534a\u8eab", StringComparison.Ordinal) ||
                     entry.VmdBoneName.Equals("\u4e0a\u534a\u8eab2", StringComparison.Ordinal)) &&
                    (entry.FbxBoneName?.Contains("Spine", StringComparison.OrdinalIgnoreCase) == true ||
                     entry.FbxBoneName?.Contains("Chest", StringComparison.OrdinalIgnoreCase) == true);
                var enabled = isHead || isNeck || isSpine;
                var scale =
                    isHead ? 0.05f :
                    isNeck ? 0.025f :
                    isSpine ? 0.01f :
                    0f;
                return entry with
                {
                    Enabled = enabled,
                    RotationScale = scale,
                    TranslationScale = 0f,
                    RotationMode = isHead || isNeck ? "InvertPmxToFbxPreview" : "PmxToFbxPreview",
                    Note = enabled
                        ? "Spine test preset: head/neck plus very tiny spine rotation-only scale."
                        : "Disabled in spine test preset."
                };
            })
            .ToArray();

        return source with
        {
            Mode = "spine-test",
            Note = "Spine FBX retarget test preset. Press S in the render window to activate this preset. Legs, hip and root remain disabled.",
            Entries = entries
        };
    }

    private static FbxRetargetProfileDocument BuildFbxLowerBodyTestProfile(FbxRetargetProfileDocument source)
    {
        var entries = source.Entries
            .Select(entry =>
            {
                var humanBone = entry.HumanBone;
                var isUpperLeg =
                    entry.Matched &&
                    IsSafeFbxDeformBone(entry.FbxBoneName) &&
                    (humanBone is "LeftUpperLeg" or "RightUpperLeg");
                var isLowerLeg =
                    entry.Matched &&
                    IsSafeFbxDeformBone(entry.FbxBoneName) &&
                    (humanBone is "LeftLowerLeg" or "RightLowerLeg");
                var isFoot =
                    entry.Matched &&
                    IsSafeFbxDeformBone(entry.FbxBoneName) &&
                    (humanBone is "LeftFoot" or "RightFoot");
                var enabled = isUpperLeg || isLowerLeg || isFoot;
                var scale =
                    isUpperLeg ? 0.0035f :
                    isLowerLeg ? 0.0025f :
                    isFoot ? 0.0025f :
                    0f;

                return entry with
                {
                    Enabled = enabled,
                    RotationScale = scale,
                    TranslationScale = 0f,
                    RotationMode = "PmxToFbxPreview",
                    Note = enabled
                        ? "Lower-body safe test preset: FK leg deform bones only. Center, hips translation, IK and toe controls stay disabled."
                        : "Disabled in lower-body test preset."
                };
            })
            .ToArray();

        return source with
        {
            Mode = "lower-body-test",
            Note = "Lower-body FBX retarget test preset. Press L in the render window to activate this preset. Tiny rotation-only FK legs; center/hips/IK remain disabled.",
            Entries = entries
        };
    }

    private static FbxRetargetProfileDocument BuildFbxFullBodyTestProfile(FbxRetargetProfileDocument source)
    {
        var entries = source.Entries
            .Select(entry =>
            {
                var humanBone = entry.HumanBone;
                var isHead =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u982d", StringComparison.Ordinal) &&
                    (entry.FbxBoneName?.Contains("Head", StringComparison.OrdinalIgnoreCase) ?? false);
                var isNeck =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u9996", StringComparison.Ordinal);
                var isSpine =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u4e0a\u534a\u8eab", StringComparison.Ordinal) &&
                    humanBone == "Spine";
                var isChest =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u4e0a\u534a\u8eab2", StringComparison.Ordinal) &&
                    humanBone == "Chest";
                var isShoulder =
                    entry.Matched &&
                    IsSafeFbxDeformBone(entry.FbxBoneName) &&
                    (humanBone is "LeftShoulder" or "RightShoulder");
                var isUpperArm =
                    entry.Matched &&
                    IsSafeFbxDeformBone(entry.FbxBoneName) &&
                    (humanBone is "LeftUpperArm" or "RightUpperArm");
                var isLowerArmOrHand =
                    entry.Matched &&
                    IsSafeFbxDeformBone(entry.FbxBoneName) &&
                    (humanBone is "LeftLowerArm" or "RightLowerArm" or "LeftHand" or "RightHand");
                var isUpperLeg =
                    entry.Matched &&
                    IsSafeFbxDeformBone(entry.FbxBoneName) &&
                    (humanBone is "LeftUpperLeg" or "RightUpperLeg");
                var isLowerLegOrFoot =
                    entry.Matched &&
                    IsSafeFbxDeformBone(entry.FbxBoneName) &&
                    (humanBone is "LeftLowerLeg" or "RightLowerLeg" or "LeftFoot" or "RightFoot");
                var enabled = isHead || isSpine || isChest || isShoulder || isUpperArm || isLowerArmOrHand || isUpperLeg || isLowerLegOrFoot;
                var scale =
                    isHead ? 0.05f :
                    isSpine ? 0.008f :
                    isChest ? 0.008f :
                    isShoulder ? 0.008f :
                    isUpperArm ? 0.008f :
                    isLowerArmOrHand ? 0.006f :
                    isUpperLeg ? 0.0035f :
                    isLowerLegOrFoot ? 0.0025f :
                    0f;

                return entry with
                {
                    Enabled = enabled,
                    RotationScale = scale,
                    TranslationScale = 0f,
                    RotationMode = isHead ? "InvertPmxToFbxPreview" : "PmxToFbxPreview",
                    Note = enabled
                        ? "Full-body safe test preset: upper-body baseline plus tiny FK legs. Neck, center, hips translation and IK stay disabled."
                        : isNeck
                            ? "Neck is disabled in full-body test preset. Use N for neck+head testing."
                            : "Disabled in full-body test preset."
                };
            })
            .ToArray();

        return source with
        {
            Mode = "full-body-test",
            Note = "Full-body FBX retarget test preset. Press F in the render window. Uses the stable upper-body baseline plus tiny FK legs; neck, center, hips translation and IK remain disabled.",
            Entries = entries
        };
    }

    private static FbxRetargetProfileDocument BuildFbxNeckHeadTestProfile(FbxRetargetProfileDocument source)
    {
        var entries = source.Entries
            .Select(entry =>
            {
                var isHead =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u982d", StringComparison.Ordinal) &&
                    (entry.FbxBoneName?.Contains("Head", StringComparison.OrdinalIgnoreCase) ?? false);
                var isNeck =
                    entry.Matched &&
                    entry.VmdBoneName.Equals("\u9996", StringComparison.Ordinal) &&
                    (entry.FbxBoneName?.Contains("Neck", StringComparison.OrdinalIgnoreCase) ?? false);
                return entry with
                {
                    Enabled = isHead || isNeck,
                    RotationScale = isHead ? 0.05f : isNeck ? 0.025f : 0f,
                    TranslationScale = 0f,
                    RotationMode = isHead || isNeck ? "InvertPmxToFbxPreview" : "PmxToFbxPreview",
                    Note = isHead
                        ? "Neck+head test preset: head enabled with tiny rotation scale. RotationMode=InvertPmxToFbxPreview."
                        : isNeck
                            ? "Neck+head test preset: neck enabled with extra tiny rotation scale. RotationMode=InvertPmxToFbxPreview."
                            : "Disabled in neck+head test preset."
                };
            })
            .ToArray();

        return source with
        {
            Mode = "neck-head-test",
            Note = "Neck+head FBX retarget test preset. Press N in the render window to activate this preset.",
            Entries = entries
        };
    }

    private static string GetDefaultRetargetMode(string group, string risk)
    {
        if (risk is "Unmatched" || risk.StartsWith("High", StringComparison.Ordinal))
            return "Disabled";

        return group is "Head" or "Arm"
            ? "RotationOnlyCandidate"
            : "DiagnosticOnly";
    }

    private static string GetHumanBone(string vmdBoneName)
    {
        return vmdBoneName switch
        {
            "\u30bb\u30f3\u30bf\u30fc" => "Hips",
            "\u4e0b\u534a\u8eab" => "Hips",
            "\u4e0a\u534a\u8eab" => "Spine",
            "\u4e0a\u534a\u8eab2" => "Chest",
            "\u9996" => "Neck",
            "\u982d" => "Head",
            "\u5de6\u80a9" => "LeftShoulder",
            "\u53f3\u80a9" => "RightShoulder",
            "\u5de6\u8155" => "LeftUpperArm",
            "\u53f3\u8155" => "RightUpperArm",
            "\u5de6\u3072\u3058" => "LeftLowerArm",
            "\u53f3\u3072\u3058" => "RightLowerArm",
            "\u5de6\u624b\u9996" => "LeftHand",
            "\u53f3\u624b\u9996" => "RightHand",
            "\u5de6\u8db3" => "LeftUpperLeg",
            "\u53f3\u8db3" => "RightUpperLeg",
            "\u5de6\u3072\u3056" => "LeftLowerLeg",
            "\u53f3\u3072\u3056" => "RightLowerLeg",
            "\u5de6\u8db3\u9996" => "LeftFoot",
            "\u53f3\u8db3\u9996" => "RightFoot",
            _ => "None"
        };
    }

    private static FbxAxisFix GetIdentityAxisFix()
    {
        return new FbxAxisFix(0f, 0f, 0f, 1f);
    }

    private static string[] GetEssentialFbxRetargetBoneNames()
    {
        return
        [
            "\u30bb\u30f3\u30bf\u30fc",
            "\u4e0b\u534a\u8eab",
            "\u4e0a\u534a\u8eab",
            "\u4e0a\u534a\u8eab2",
            "\u9996",
            "\u982d",
            "\u5de6\u80a9",
            "\u53f3\u80a9",
            "\u5de6\u8155",
            "\u53f3\u8155",
            "\u5de6\u3072\u3058",
            "\u53f3\u3072\u3058",
            "\u5de6\u624b\u9996",
            "\u53f3\u624b\u9996",
            "\u5de6\u8db3",
            "\u53f3\u8db3",
            "\u5de6\u3072\u3056",
            "\u53f3\u3072\u3056",
            "\u5de6\u8db3\u9996",
            "\u53f3\u8db3\u9996",
            "\u5de6\u8db3IK",
            "\u53f3\u8db3IK"
        ];
    }

    private sealed record FbxRetargetMapDocument(
        string ModelPath,
        string MotionPath,
        string ModelName,
        string MotionModelName,
        string Mode,
        string Note,
        int EssentialMatched,
        int EssentialTotal,
        IReadOnlyList<FbxRetargetMapEntry> Entries);

    private sealed record FbxRetargetMapEntry(
        string VmdBoneName,
        int? FbxBoneIndex,
        string? FbxBoneName,
        bool Matched,
        string Group,
        string Risk,
        string Recommendation,
        int FrameCount,
        float MaxTranslation,
        float MaxRotationDegrees);

    private sealed record FbxRetargetProfileDocument(
        int Version,
        string ModelPath,
        string MotionPath,
        string ModelName,
        string MotionModelName,
        string Mode,
        string Note,
        IReadOnlyList<FbxRetargetProfileEntry> Entries);

    private sealed record FbxRetargetProfileEntry(
        string VmdBoneName,
        int? FbxBoneIndex,
        string? FbxBoneName,
        bool Matched,
        bool Enabled,
        string Group,
        string Risk,
        string DefaultMode,
        float RotationScale,
        float TranslationScale,
        string Note,
        string RotationMode = "PmxToFbxPreview",
        string HumanBone = "None",
        FbxAxisFix? AxisFix = null);

    private sealed record FbxAxisFix(float X, float Y, float Z, float W);

    private static string GetRetargetGroup(string vmdBoneName)
    {
        return ContainsAny(vmdBoneName, "\u30bb\u30f3\u30bf\u30fc", "Center") ? "Root" :
            ContainsAny(vmdBoneName, "\u4e0b\u534a\u8eab", "\u8170", "LowerBody", "Hips", "Pelvis") ? "Hip" :
            ContainsAny(vmdBoneName, "\u4e0a\u534a\u8eab", "\u4e0a\u534a\u8eab2", "UpperBody", "Spine", "Chest") ? "Spine" :
            ContainsAny(vmdBoneName, "\u80a9", "\u8155", "\u3072\u3058", "\u624b\u9996", "\u624b", "Shoulder", "Arm", "Elbow", "Wrist", "Hand") ? "Arm" :
            ContainsAny(vmdBoneName, "\u8db3IK", "\u8db3\uff29\uff2b", "\u3064\u307e\u5148IK", "\u722a\u5148IK", "LegIK", "FootIK", "ToeIK") ? "LegIk" :
            ContainsAny(vmdBoneName, "\u8db3", "\u3072\u3056", "\u819d", "\u8db3\u9996", "\u3064\u307e\u5148", "\u722a\u5148", "Leg", "Knee", "Foot", "Toe") ? "Leg" :
            ContainsAny(vmdBoneName, "\u9996", "\u982d", "Neck", "Head") ? "Head" :
            "Other";
    }

    private static string GetRetargetRisk(string vmdBoneName, float maxTranslation, float maxRotationDegrees)
    {
        var group = GetRetargetGroup(vmdBoneName);
        if (group is "Root" or "LegIk" && maxTranslation > 0.001f)
            return "HighTranslation";
        if (group is "Hip" or "Spine" or "Leg" && maxRotationDegrees > 25f)
            return "HighAxisMismatch";
        if (maxRotationDegrees > 65f)
            return "HighRotation";
        if (maxRotationDegrees > 25f)
            return "MediumRotation";
        return "Low";
    }

    private static string GetRetargetRecommendation(string vmdBoneName, float maxTranslation, float maxRotationDegrees)
    {
        var group = GetRetargetGroup(vmdBoneName);
        var risk = GetRetargetRisk(vmdBoneName, maxTranslation, maxRotationDegrees);
        if (risk.StartsWith("High", StringComparison.Ordinal))
            return "Disabled";
        if (group is "Head" or "Arm")
            return "ExperimentalRotationOnly";
        return "DiagnosticOnly";
    }

    private static float GetRotationAngleDegrees(Quaternion value)
    {
        if (value.LengthSquared() <= 0.000001f)
            return 0f;

        var q = Quaternion.Normalize(value);
        var angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f));
        return angle * 180f / MathF.PI;
    }

    private static bool IsFbxModel(PmxModel model)
    {
        return Path.GetExtension(model.SourcePath).Equals(".fbx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLowerBodyName(string name)
    {
        return name.Contains("\u30bb\u30f3\u30bf\u30fc", StringComparison.Ordinal) ||
               name.Contains("\u4e0b\u534a\u8eab", StringComparison.Ordinal) ||
               name.Contains('\u8db3', StringComparison.Ordinal) ||
               name.Contains("\u3072\u3056", StringComparison.Ordinal) ||
               name.Contains("\u819d", StringComparison.Ordinal) ||
               name.Contains("IK", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("\uff29\uff2b", StringComparison.Ordinal);
    }

    private static bool ContainsAny(string value, params string[] parts)
    {
        foreach (var part in parts)
        {
            if (value.Contains(part, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string FormatIkLink(PmxIkLink link)
    {
        if (!link.HasLimit)
            return link.BoneIndex.ToString();

        return $"{link.BoneIndex}[min=({link.MinLimit.X:0.###},{link.MinLimit.Y:0.###},{link.MinLimit.Z:0.###}) max=({link.MaxLimit.X:0.###},{link.MaxLimit.Y:0.###},{link.MaxLimit.Z:0.###})]";
    }
}
