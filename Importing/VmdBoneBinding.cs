using System.Numerics;
using System.Text;

namespace SimplePmxVeldrid.Importing;

public sealed record VmdBoneBinding(
    IReadOnlyList<VmdBoundBone> BoundBones,
    IReadOnlyList<string> UnmatchedVmdBoneNames,
    IReadOnlyList<string> DuplicatePmxBoneBindings,
    int TotalAnimatedBoneNames,
    int TotalBoneFrames,
    int MatchedBoneFrames)
{
    public int MatchedBoneNameCount => BoundBones.Count;
}

public sealed record VmdBoundBone(
    int PmxBoneIndex,
    string BoneName,
    IReadOnlyList<VmdBoneFrame> Frames);

public static class VmdBoneBinder
{
    public static VmdBoneBinding Bind(PmxModel model, VmdMotion motion)
    {
        return BindCore(model, motion, VmdBindingPurpose.Diagnostics);
    }

    public static VmdBoneBinding BindForPlayback(PmxModel model, VmdMotion motion)
    {
        if (Path.GetExtension(model.SourcePath).Equals(".fbx", StringComparison.OrdinalIgnoreCase))
        {
            if (IsSameSourcePath(model.SourcePath, motion.SourcePath))
                return BindCore(model, motion, VmdBindingPurpose.Playback);

            var profile = FbxRetargetProfileStore.LoadActive(model, motion);
            return profile is null
                ? CreateDisabledFbxPlaybackBinding(motion)
                : BindFbxProfileForPlayback(model, motion, profile);
        }

        return BindCore(model, motion, VmdBindingPurpose.Playback);
    }

    private static VmdBoneBinding BindFbxProfileForPlayback(PmxModel model, VmdMotion motion, FbxRetargetProfileDocument profile)
    {
        var enabledByName = profile.Entries
            .Where(x => x.Enabled && x.Matched && x.FbxBoneIndex is not null)
            .GroupBy(x => x.VmdBoneName, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        if (enabledByName.Count == 0)
            return CreateDisabledFbxPlaybackBinding(motion);

        var diagnosticBinding = BindCore(model, motion, VmdBindingPurpose.Diagnostics);
        var candidates = new List<BindingCandidate>();
        var unmatched = new List<string>();
        var order = 0;

        foreach (var bound in diagnosticBinding.BoundBones.OrderBy(x => x.BoneName, StringComparer.Ordinal))
        {
            if (!enabledByName.TryGetValue(bound.BoneName, out var entry) ||
                entry.FbxBoneIndex != bound.PmxBoneIndex ||
                (uint)bound.PmxBoneIndex >= (uint)model.Bones.Count)
            {
                unmatched.Add(bound.BoneName);
                continue;
            }

            var frames = BuildFbxProfileFrames(
                bound.Frames,
                ClampProfileRotationScale(entry.RotationScale),
                ClampProfileTranslationScale(entry.TranslationScale),
                entry.RotationMode,
                entry.AxisFix);
            candidates.Add(new BindingCandidate(
                new VmdBoundBone(bound.PmxBoneIndex, bound.BoneName, frames),
                0,
                order++));
        }

        foreach (var name in motion.BoneFramesByName.Keys)
        {
            if (!diagnosticBinding.BoundBones.Any(x => x.BoneName == name))
                unmatched.Add(name);
        }

        var duplicateBindings = new List<string>();
        var boundBones = new List<VmdBoundBone>();
        foreach (var group in candidates.GroupBy(x => x.Bone.PmxBoneIndex).OrderBy(x => x.Min(y => y.Order)))
        {
            var selected = group
                .OrderByDescending(x => x.Bone.Frames.Count)
                .ThenBy(x => x.Order)
                .First();
            boundBones.Add(selected.Bone);

            foreach (var duplicate in group.Where(x => x.Order != selected.Order).OrderBy(x => x.Order))
            {
                duplicateBindings.Add(
                    $"{duplicate.Bone.BoneName} -> FBX[{duplicate.Bone.PmxBoneIndex}] skipped; selected {selected.Bone.BoneName}");
            }
        }

        return new VmdBoneBinding(
            boundBones,
            unmatched.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            duplicateBindings,
            motion.AnimatedBoneNameCount,
            motion.BoneFrameCount,
            boundBones.Sum(x => x.Frames.Count));
    }

    private static VmdBoneBinding CreateDisabledFbxPlaybackBinding(VmdMotion motion)
    {
        // External MMD VMD bone tracks are authored for PMX/MMD axes and IK
        // controls. The diagnostic binder can still report possible FBX bone
        // candidates, but playback must not apply them until a real retarget
        // solver exists. This keeps FBX models stable while preserving VMD
        // camera/audio playback.
        return new VmdBoneBinding(
            [],
            motion.BoneFramesByName.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            [],
            motion.AnimatedBoneNameCount,
            motion.BoneFrameCount,
            0);
    }

    private static bool IsSameSourcePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static VmdBoneBinding BindCore(PmxModel model, VmdMotion motion, VmdBindingPurpose purpose)
    {
        var isFbxModel = Path.GetExtension(model.SourcePath).Equals(".fbx", StringComparison.OrdinalIgnoreCase);
        var pmxBoneByName = model.Bones
            .Select((bone, index) => new { bone.Name, Index = index })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().Index, StringComparer.Ordinal);
        var pmxBoneByNormalizedName = model.Bones
            .Select((bone, index) => new { Name = NormalizeBoneName(bone.Name), Index = index })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().Index, StringComparer.Ordinal);

        var normalizedModelBones = model.Bones
            .Select((bone, index) => new NormalizedBone(index, NormalizeBoneName(bone.Name)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .ToArray();

        var candidates = new List<BindingCandidate>();
        var unmatched = new List<string>();

        var order = 0;
        foreach (var pair in motion.BoneFramesByName.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var normalizedVmdName = NormalizeBoneName(pair.Key);
            if (TryFindBone(
                    pmxBoneByName,
                    pmxBoneByNormalizedName,
                    normalizedModelBones,
                    isFbxModel,
                    pair.Key,
                    normalizedVmdName,
                    out var pmxBoneIndex,
                    out var matchPriority))
            {
                var frames = pair.Value;
                if (isFbxModel && matchPriority >= 2 && purpose == VmdBindingPurpose.Playback)
                {
                    if (!IsFbxPlayableAliasName(pair.Key))
                    {
                        unmatched.Add(pair.Key);
                        continue;
                    }

                    frames = BuildFbxSafeAliasFrames(model, pair.Key, pair.Value);
                }

                candidates.Add(new BindingCandidate(
                    new VmdBoundBone(pmxBoneIndex, pair.Key, frames),
                    matchPriority,
                    order++));
            }
            else
            {
                unmatched.Add(pair.Key);
            }
        }

        var duplicateBindings = new List<string>();
        var boundBones = new List<VmdBoundBone>();
        foreach (var group in candidates.GroupBy(x => x.Bone.PmxBoneIndex).OrderBy(x => x.Min(y => y.Order)))
        {
            var selected = group
                .OrderBy(x => x.MatchPriority)
                .ThenByDescending(x => x.Bone.Frames.Count)
                .ThenBy(x => x.Order)
                .First();
            boundBones.Add(selected.Bone);

            foreach (var duplicate in group.Where(x => x.Order != selected.Order).OrderBy(x => x.Order))
            {
                duplicateBindings.Add(
                    $"{duplicate.Bone.BoneName} -> PMX[{duplicate.Bone.PmxBoneIndex}] skipped; selected {selected.Bone.BoneName}");
            }
        }

        var matchedFrames = boundBones.Sum(x => x.Frames.Count);

        return new VmdBoneBinding(
            boundBones,
            unmatched,
            duplicateBindings,
            motion.AnimatedBoneNameCount,
            motion.BoneFrameCount,
            matchedFrames);
    }

    private enum VmdBindingPurpose
    {
        Diagnostics,
        Playback
    }

    private static IReadOnlyList<VmdBoneFrame> BuildFbxSafeAliasFrames(PmxModel model, string vmdBoneName, IReadOnlyList<VmdBoneFrame> frames)
    {
        // VMD rotations are authored for MMD/PMX local bone axes. Humanoid FBX
        // bones often have different local axes, so applying those rotations
        // directly can explode the model. For FBX alias matches, keep the
        // retarget intentionally small and rotational-only so "missing" motion
        // is visible without returning to the earlier exploding-retarget
        // behavior.
        var rotationScale = GetFbxAliasRotationScale(vmdBoneName);
        var translationScale = GetFbxAliasTranslationScale(model, vmdBoneName);
        return frames
            .Select(frame => frame with
            {
                Translation = ConvertPmxPreviewTranslationToFbxPreview(frame.Translation) * translationScale,
                Rotation = ScaleRotation(ConvertPmxPreviewRotationToFbxPreview(frame.Rotation), rotationScale)
            })
            .ToArray();
    }

    private static IReadOnlyList<VmdBoneFrame> BuildFbxProfileFrames(IReadOnlyList<VmdBoneFrame> frames, float rotationScale, float translationScale, string? rotationMode, FbxAxisFix? axisFix)
    {
        var axisFixRotation = ToAxisFixQuaternion(axisFix);
        return frames
            .Select(frame => frame with
            {
                Translation = ConvertPmxPreviewTranslationToFbxPreview(frame.Translation) * translationScale,
                Rotation = ApplyAxisFix(ScaleRotation(ConvertProfileRotation(frame.Rotation, rotationMode), rotationScale), axisFixRotation)
            })
            .ToArray();
    }

    private static Quaternion ConvertProfileRotation(Quaternion value, string? mode)
    {
        if (value.LengthSquared() <= 0.000001f)
            return Quaternion.Identity;

        return mode switch
        {
            "RawPmxPreview" => Quaternion.Normalize(value),
            "InvertRawPmxPreview" => Quaternion.Conjugate(Quaternion.Normalize(value)),
            "InvertPmxToFbxPreview" => Quaternion.Conjugate(ConvertPmxPreviewRotationToFbxPreview(value)),
            _ => ConvertPmxPreviewRotationToFbxPreview(value)
        };
    }

    private static float ClampProfileRotationScale(float value)
    {
        return Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, 0.25f);
    }

    private static float ClampProfileTranslationScale(float value)
    {
        return Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, 0.02f);
    }

    private static float GetFbxAliasRotationScale(string vmdBoneName)
    {
        var name = NormalizeBoneName(vmdBoneName);
        if (ContainsAny(name, "\u8db3IK", "\u8db3\uff29\uff2b", "\u3064\u307e\u5148IK", "\u722a\u5148IK", "LegIK", "FootIK", "ToeIK"))
            return 0f;

        if (ContainsAny(name, "\u30bb\u30f3\u30bf\u30fc", "Center", "Root"))
            return 0f;

        if (ContainsAny(name, "\u4e0b\u534a\u8eab", "\u8170", "LowerBody", "Hips", "Pelvis"))
            return 0f;

        if (ContainsAny(name, "\u4e0a\u534a\u8eab", "\u4e0a\u534a\u8eab2", "UpperBody", "Spine", "Chest"))
            return 0f;

        if (ContainsAny(name, "\u80a9", "Shoulder", "Clavicle"))
            return 0.08f;

        if (ContainsAny(name, "\u3072\u3058", "\u624b\u9996", "\u624b", "Elbow", "LowerArm", "ForeArm", "Wrist", "Hand"))
            return 0.06f;

        if (ContainsAny(name, "\u8155", "UpperArm", "Arm"))
            return 0.08f;

        if (ContainsAny(name, "\u89aa\u6307", "\u4eba\u6307", "\u4eba\u5dee", "\u4e2d\u6307", "\u85ac\u6307", "\u5c0f\u6307", "Thumb", "Index", "Middle", "Ring", "Little", "Pinky", "Finger"))
            return 0.10f;

        // These checks must stay before the generic "\u9996" (neck) check.
        // Japanese wrist/ankle names are "\u624b\u9996" and "\u8db3\u9996"; otherwise
        // ankle can be misread as Neck and sneak into FBX playback.
        if (ContainsAny(name, "\u3072\u3056", "\u819d", "Knee", "LowerLeg", "Calf"))
            return 0f;

        if (ContainsAny(name, "\u8db3\u9996", "\u3064\u307e\u5148", "\u722a\u5148", "Ankle", "Foot", "Toe"))
            return 0f;

        if (ContainsAny(name, "\u8db3", "UpperLeg", "Thigh", "Leg"))
            return 0f;

        if (ContainsAny(name, "\u9996", "\u982d", "Neck", "Head"))
            return 0.12f;

        return 0f;
    }

    private static bool IsFbxPlayableAliasName(string vmdBoneName)
    {
        // Keep external MMD VMD -> FBX playback intentionally tiny and
        // conservative. The full diagnostic binding can still show body/leg
        // candidates, but playback only receives the groups that currently
        // survive axis mismatch safely.
        return GetFbxAliasRotationScale(vmdBoneName) > 0f;
    }

    private static float GetFbxAliasTranslationScale(PmxModel model, string vmdBoneName)
    {
        // Keep FBX external-VMD alias retargeting rotational-only for now.
        // Even tiny PMX-authored translations can bend arbitrary FBX body
        // hierarchies because the root/hip scale and rest offsets differ.
        return 0f;
    }

    private static float EstimateFbxExternalVmdTranslationScale(PmxModel model)
    {
        if (model.Vertices.Count == 0)
            return 0.02f;

        var minY = float.PositiveInfinity;
        var maxY = float.NegativeInfinity;
        foreach (var vertex in model.Vertices)
        {
            minY = Math.Min(minY, vertex.Position.Y);
            maxY = Math.Max(maxY, vertex.Position.Y);
        }

        var height = maxY - minY;
        if (!float.IsFinite(height) || height <= 0.0001f)
            return 0.02f;

        // PMX characters are often around 16-22 units tall. Convert VMD
        // translation into the imported FBX model scale, then clamp hard so
        // outlier FBX unit systems do not teleport the avatar.
        return Math.Clamp(height / 20f, 0.005f, 0.08f);
    }

    private static Vector3 ConvertPmxPreviewTranslationToFbxPreview(Vector3 value)
    {
        // VMDReader converts MMD motion for PMX preview space as (X, Y, -Z).
        // The FBX import path currently uses (X, -Y, -Z), so alias-applied
        // external VMD translations need only the Y axis flipped here.
        return new Vector3(value.X, -value.Y, value.Z);
    }

    private static Quaternion ConvertPmxPreviewRotationToFbxPreview(Quaternion value)
    {
        if (value.LengthSquared() <= 0.000001f)
            return Quaternion.Identity;

        // PMX preview rotations are converted from MMD as (-X, -Y, Z, W).
        // FBX preview space is equivalent to a 180-degree X basis rotation
        // from raw MMD axes, which maps to (X, -Y, -Z, W). Convert between
        // those already-preview-space quaternions before applying safety scale.
        var converted = new Quaternion(-value.X, value.Y, -value.Z, value.W);
        return Quaternion.Normalize(converted);
    }

    private static Quaternion ScaleRotation(Quaternion rotation, float scale)
    {
        if (scale <= 0f || rotation.LengthSquared() <= 0.000001f)
            return Quaternion.Identity;

        return Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, Quaternion.Normalize(rotation), Math.Clamp(scale, 0f, 1f)));
    }

    private static Quaternion ApplyAxisFix(Quaternion rotation, Quaternion axisFix)
    {
        if (rotation.LengthSquared() <= 0.000001f || axisFix.LengthSquared() <= 0.000001f)
            return Quaternion.Identity;

        // Manual FBX retarget basis correction:
        // fbxLocalDelta = inverse(axisFix) * vmdDelta * axisFix.
        // The model's original/rest local rotation is still supplied by the
        // pose builder, so this method only changes the incoming VMD delta
        // rotation basis.
        return Quaternion.Normalize(Quaternion.Conjugate(axisFix) * Quaternion.Normalize(rotation) * axisFix);
    }

    private static Quaternion ToAxisFixQuaternion(FbxAxisFix? axisFix)
    {
        if (axisFix is null)
            return Quaternion.Identity;

        var q = new Quaternion(axisFix.X, axisFix.Y, axisFix.Z, axisFix.W);
        if (!float.IsFinite(q.X) ||
            !float.IsFinite(q.Y) ||
            !float.IsFinite(q.Z) ||
            !float.IsFinite(q.W) ||
            q.LengthSquared() <= 0.000001f)
        {
            return Quaternion.Identity;
        }

        return Quaternion.Normalize(q);
    }

    private static bool TryFindBone(
        IReadOnlyDictionary<string, int> pmxBoneByName,
        IReadOnlyDictionary<string, int> pmxBoneByNormalizedName,
        IReadOnlyList<NormalizedBone> normalizedModelBones,
        bool isFbxModel,
        string vmdName,
        string normalizedVmdName,
        out int pmxBoneIndex,
        out int matchPriority)
    {
        if (pmxBoneByName.TryGetValue(vmdName, out pmxBoneIndex))
        {
            matchPriority = 0;
            return true;
        }

        if (pmxBoneByNormalizedName.TryGetValue(normalizedVmdName, out pmxBoneIndex))
        {
            matchPriority = 1;
            return true;
        }

        if (isFbxModel && IsFbxUnsafeAliasName(normalizedVmdName))
        {
            pmxBoneIndex = -1;
            matchPriority = int.MaxValue;
            return false;
        }

        if (TryFindLowerBodyAlias(normalizedModelBones, normalizedVmdName, out pmxBoneIndex))
        {
            matchPriority = 2;
            return true;
        }

        if (TryFindHumanoidAlias(normalizedModelBones, normalizedVmdName, out pmxBoneIndex))
        {
            matchPriority = 3;
            return true;
        }

        matchPriority = int.MaxValue;
        return false;
    }

    private static string NormalizeBoneName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var normalized = name.Normalize(NormalizationForm.FormKC);
        Span<char> buffer = normalized.Length <= 256
            ? stackalloc char[normalized.Length]
            : new char[normalized.Length];

        var length = 0;
        foreach (var c in normalized)
        {
            if (!char.IsWhiteSpace(c))
                buffer[length++] = c;
        }

        return new string(buffer[..length]);
    }

    private static bool TryFindLowerBodyAlias(IReadOnlyList<NormalizedBone> modelBones, string vmdName, out int boneIndex)
    {
        boneIndex = -1;
        if (string.IsNullOrWhiteSpace(vmdName))
            return false;

        const string center = "\u30bb\u30f3\u30bf\u30fc";
        const string lowerBody = "\u4e0b\u534a\u8eab";
        const string waist = "\u8170";
        const string leg = "\u8db3";
        const string kneeHiragana = "\u3072\u3056";
        const string kneeKanji = "\u819d";
        const string toeHiragana = "\u3064\u307e\u5148";
        const string toeKanji = "\u722a\u5148";
        const string fullWidthIk = "\uff29\uff2b";

        if (ContainsAny(vmdName, center, "Center"))
            return TryFind(modelBones, name => ContainsAny(name, center, "Center"), out boneIndex);

        if (ContainsAny(vmdName, lowerBody, "LowerBody", "Lower"))
            return TryFind(modelBones, name => ContainsAny(name, lowerBody, "LowerBody", waist, "Hips", "Pelvis"), out boneIndex);

        var left = IsLeftSideName(vmdName);
        var right = IsRightSideName(vmdName);
        if (!left && !right)
            return false;

        if (ContainsAny(vmdName, leg + "IK", leg + fullWidthIk, "LegIK", "FootIK"))
        {
            return TryFind(modelBones, name =>
                HasSide(name, left, right) &&
                ContainsAny(name, leg + "IK", leg + fullWidthIk, "LegIK", "FootIK", "IK", fullWidthIk), out boneIndex);
        }

        if (ContainsAny(vmdName, toeHiragana, toeKanji, "Toe"))
        {
            return TryFind(modelBones, name =>
                HasSide(name, left, right) &&
                ContainsAny(name, toeHiragana, toeKanji, "Toe"), out boneIndex);
        }

        if (ContainsAny(vmdName, kneeHiragana, kneeKanji, "Knee"))
        {
            return TryFind(modelBones, name =>
                HasSide(name, left, right) &&
                ContainsAny(name, kneeHiragana, kneeKanji, "Knee", "LowerLeg", "Lower_Leg", "Lower.Leg", "Lower-leg", "Lower_leg", "Calf", "Shin"), out boneIndex);
        }

        if (ContainsAny(vmdName, "\u8db3\u9996", "Ankle", "Foot"))
        {
            return TryFind(modelBones, name =>
                HasSide(name, left, right) &&
                ContainsAny(name, "\u8db3\u9996", "Ankle", "Foot") &&
                !ContainsAny(name, "Toe", toeHiragana, toeKanji), out boneIndex);
        }

        if (ContainsAny(vmdName, leg, "Leg", "Foot"))
        {
            return TryFind(modelBones, name =>
                HasSide(name, left, right) &&
                ContainsAny(name, leg, "Leg", "Foot") &&
                !ContainsAny(name, "IK", fullWidthIk, kneeHiragana, kneeKanji, "Knee", "LowerLeg", "Lower_Leg", "Lower.Leg", "Lower-leg", "Lower_leg", "Calf", "Shin", toeHiragana, toeKanji, "Toe", "Ankle", "\u8db3\u9996"), out boneIndex);
        }

        return false;
    }

    private static bool TryFindHumanoidAlias(IReadOnlyList<NormalizedBone> modelBones, string vmdName, out int boneIndex)
    {
        boneIndex = -1;
        if (string.IsNullOrWhiteSpace(vmdName))
            return false;

        const string upperBody = "\u4e0a\u534a\u8eab";
        const string upperBody2 = "\u4e0a\u534a\u8eab2";
        const string neck = "\u9996";
        const string head = "\u982d";
        const string eye = "\u76ee";
        const string shoulder = "\u80a9";
        const string arm = "\u8155";
        const string elbow = "\u3072\u3058";
        const string wrist = "\u624b\u9996";
        const string hand = "\u624b";
        const string thumb = "\u89aa\u6307";
        const string indexFinger = "\u4eba\u6307";
        const string indexFingerAlt = "\u4eba\u5dee";
        const string middleFinger = "\u4e2d\u6307";
        const string ringFinger = "\u85ac\u6307";
        const string littleFinger = "\u5c0f\u6307";
        const string ankle = "\u8db3\u9996";
        const string kneeHiragana = "\u3072\u3056";
        const string kneeKanji = "\u819d";
        const string leg = "\u8db3";

        if (IsFbxUnsafeAliasName(vmdName))
            return false;

        if (ContainsAny(vmdName, "\u30bb\u30f3\u30bf\u30fc", "Center"))
            return TryFindBest(modelBones, [
                name => ContainsAny(name, "Hips", "Pelvis", "Root", "Center")
            ], out boneIndex);

        if (ContainsAny(vmdName, upperBody2, "UpperBody2", "Chest"))
            return TryFindBest(modelBones, [
                name => ContainsAny(name, "UpperChest"),
                name => ContainsAny(name, "Chest"),
                name => ContainsAny(name, "Spine2", "Spine.002")
            ], out boneIndex);

        if (ContainsAny(vmdName, upperBody, "UpperBody", "Spine"))
            return TryFindBest(modelBones, [
                name => ContainsAny(name, "Spine") && !ContainsAny(name, "Spine1", "Spine2", "Spine.001", "Spine.002", "Chest", "UpperChest"),
                name => ContainsAny(name, "Spine1", "Spine.001"),
                name => ContainsAny(name, "Chest")
            ], out boneIndex);

        if (ContainsAny(vmdName, neck, "Neck") && !ContainsAny(vmdName, wrist, ankle))
            return TryFind(modelBones, name => ContainsAny(name, "Neck"), out boneIndex);

        if (ContainsAny(vmdName, head, "Head"))
            return TryFind(modelBones, name => ContainsAny(name, "Head"), out boneIndex);

        if (ContainsAny(vmdName, eye, "Eye"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "Eye"), out boneIndex);

        var left = IsLeftSideName(vmdName);
        var right = IsRightSideName(vmdName);
        if (!left && !right)
            return false;

        if (ContainsAny(vmdName, thumb, "Thumb"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "Thumb"), out boneIndex);

        if (ContainsAny(vmdName, indexFinger, indexFingerAlt, "Index"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "Index"), out boneIndex);

        if (ContainsAny(vmdName, middleFinger, "Middle"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "Middle"), out boneIndex);

        if (ContainsAny(vmdName, ringFinger, "Ring"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "Ring"), out boneIndex);

        if (ContainsAny(vmdName, littleFinger, "Little", "Pinky"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "Little", "Pinky"), out boneIndex);

        if (ContainsAny(vmdName, shoulder, "Shoulder"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "Shoulder", "Clavicle"), out boneIndex);

        if (ContainsAny(vmdName, elbow, "Elbow"))
            return TryFindSideBest(modelBones, vmdName, [
                name => ContainsAny(name, "LowerArm", "Lower_arm", "Lower.Arm", "Lower-arm", "Lower arm", "ForeArm", "Fore_arm", "Fore.Arm", "Fore-arm", "Fore arm") &&
                        !ContainsAny(name, "Twist", "Support", "support"),
                name => ContainsAny(name, "Elbow") &&
                        !ContainsAny(name, "Twist", "Support", "support")
            ], out boneIndex);

        if (ContainsAny(vmdName, wrist, hand + "IK", "Wrist", "Hand"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "Hand", "Wrist") && !ContainsAny(name, "Finger", "Thumb", "Index", "Middle", "Ring", "Little"), out boneIndex);

        if (ContainsAny(vmdName, arm, "Arm"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "UpperArm", "Arm") && !ContainsAny(name, "LowerArm", "ForeArm", "Twist"), out boneIndex);

        if (ContainsAny(vmdName, kneeHiragana, kneeKanji, "Knee"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "LowerLeg", "Lower_Leg", "Lower.Leg", "Lower-leg", "Lower_leg", "Calf", "Shin", "Knee") && !ContainsAny(name, "Twist"), out boneIndex);

        if (ContainsAny(vmdName, ankle, "Ankle", "Foot"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "Foot", "Ankle") && !ContainsAny(name, "Toe"), out boneIndex);

        if (ContainsAny(vmdName, leg, "Leg"))
            return TryFindSide(modelBones, vmdName, name => ContainsAny(name, "UpperLeg", "Upper_Leg", "Upper.Leg", "Upper-leg", "Upper_leg", "Thigh", "Leg") && !ContainsAny(name, "LowerLeg", "Lower_Leg", "Lower.Leg", "Lower-leg", "Lower_leg", "Calf", "Shin", "Foot", "Toe", "Knee", "Twist"), out boneIndex);

        return false;
    }

    private static bool IsFbxUnsafeAliasName(string normalizedVmdName)
    {
        // FBX files generally do not contain MMD's IK, dummy, or endpoint
        // control bones. If we alias those controls to nearby deform bones
        // (Upper_leg, Foot, Hand, etc.), the diagnostic map looks covered but
        // playback becomes noisy or duplicate-bound. Exact-name matches are
        // still accepted before this check, so PMX and FBX models with real
        // matching control bones are not affected.
        const string toeHiragana = "\u3064\u307e\u5148";
        const string toeKanji = "\u722a\u5148";

        if (ContainsAny(normalizedVmdName, "IK", "\uff29\uff2b", "\u30c0\u30df\u30fc", "Dummy"))
            return true;

        if (ContainsAny(normalizedVmdName, "\u8db3\u99962", "Ankle2", "Foot2", "Toe2"))
            return true;

        if (normalizedVmdName.EndsWith("\u5148", StringComparison.Ordinal) &&
            !ContainsAny(normalizedVmdName, toeHiragana, toeKanji))
            return true;

        return false;
    }

    private static bool TryFind(IReadOnlyList<NormalizedBone> bones, Func<string, bool> predicate, out int boneIndex)
    {
        foreach (var bone in bones)
        {
            if (!predicate(bone.Name)) continue;
            boneIndex = bone.Index;
            return true;
        }

        boneIndex = -1;
        return false;
    }

    private static bool TryFindBest(IReadOnlyList<NormalizedBone> bones, IReadOnlyList<Func<string, bool>> predicates, out int boneIndex)
    {
        foreach (var predicate in predicates)
        {
            if (TryFind(bones, predicate, out boneIndex))
                return true;
        }

        boneIndex = -1;
        return false;
    }

    private static bool TryFindSide(IReadOnlyList<NormalizedBone> bones, string vmdName, Func<string, bool> predicate, out int boneIndex)
    {
        var left = IsLeftSideName(vmdName);
        var right = IsRightSideName(vmdName);
        return TryFind(bones, name => HasSide(name, left, right) && predicate(name), out boneIndex);
    }

    private static bool TryFindSideBest(IReadOnlyList<NormalizedBone> bones, string vmdName, IReadOnlyList<Func<string, bool>> predicates, out int boneIndex)
    {
        var left = IsLeftSideName(vmdName);
        var right = IsRightSideName(vmdName);
        return TryFindBest(bones, predicates.Select<Func<string, bool>, Func<string, bool>>(predicate => name => HasSide(name, left, right) && predicate(name)).ToArray(), out boneIndex);
    }

    private static bool HasSide(string name, bool left, bool right)
    {
        return left && IsLeftSideName(name) ||
               right && IsRightSideName(name);
    }

    private static bool IsLeftSideName(string name)
    {
        return ContainsAny(name, "\u5de6", "Left") ||
               name.StartsWith("L_", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_L", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".L", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".L.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRightSideName(string name)
    {
        return ContainsAny(name, "\u53f3", "Right") ||
               name.StartsWith("R_", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_R", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".R", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".R.", StringComparison.OrdinalIgnoreCase);
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

    private readonly record struct NormalizedBone(int Index, string Name);

    private readonly record struct BindingCandidate(VmdBoundBone Bone, int MatchPriority, int Order);
}
