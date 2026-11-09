using System.Numerics;

namespace SimplePmxVeldrid.Importing;

public sealed record PmxPose(
    uint Frame,
    IReadOnlyList<PmxBoneTransform> BoneTransforms,
    int AnimatedBoneCount,
    int AnimatedIkBoneCount,
    int SolvedIkCount,
    int IkFallbackCount);

public readonly record struct PmxBoneTransform(
    int BoneIndex,
    string BoneName,
    Matrix4x4 LocalMatrix,
    Matrix4x4 WorldMatrix,
    bool HasVmdPose);

public enum PmxIkMode
{
    Full,
    Off
}

public enum PmxIkProfile
{
    Stable,
    Normal,
    Strong
}

public static class PmxPoseBuilder
{
    public static PmxPose BuildRestPose(PmxModel model)
    {
        return Build(model, new VmdPoseSample(0, []));
    }

    public static PmxPose Build(PmxModel model, VmdPoseSample sample, PmxIkMode ikMode = PmxIkMode.Full, bool enableAppend = true, PmxIkProfile ikProfile = PmxIkProfile.Normal)
    {
        var count = model.Bones.Count;
        var poseByBoneIndex = BuildPoseLookup(sample);
        var localTranslations = new Vector3[count];
        var localRotations = new Quaternion[count];
        var hasVmdPose = new bool[count];
        var localMatrices = new Matrix4x4[count];
        var worldMatrices = new Matrix4x4[count];
        var restLocalTranslations = new Vector3[count];
        var transformOrder = GetTransformOrder(model);
        var animatedBoneCount = 0;
        var animatedIkBoneCount = 0;

        for (var i = 0; i < count; i++)
        {
            var bone = model.Bones[i];
            var parentPosition = IsValidBoneIndex(model, bone.ParentIndex)
                ? model.Bones[bone.ParentIndex].Position
                : Vector3.Zero;
            // FBX rest local rotation/translation is stored on PmxBone for
            // diagnostics and future retargeting, but do not feed it into this
            // PMX-style pose builder yet. The vertices are already imported in
            // preview/world space, and mixing FBX node-local transforms here can
            // double-apply hierarchy transforms and make external VMD poses
            // collapse or twist badly.
            var restLocalPosition = bone.Position - parentPosition;
            var restLocalRotation = Quaternion.Identity;
            restLocalTranslations[i] = restLocalPosition;

            if (poseByBoneIndex.TryGetValue(i, out var pose))
            {
                hasVmdPose[i] = true;
                animatedBoneCount++;
                if (bone.Ik is not null)
                    animatedIkBoneCount++;
                localTranslations[i] = restLocalPosition + pose.Translation;
                localRotations[i] = NormalizeOrIdentity(restLocalRotation * NormalizeOrIdentity(pose.Rotation));
            }
            else
            {
                localTranslations[i] = restLocalPosition;
                localRotations[i] = restLocalRotation;
            }
        }

        RebuildAllWorldMatrices(model, localTranslations, localRotations, localMatrices, worldMatrices);
        var ikFallbackCount = 0;
        var solvedIkCount = ikMode != PmxIkMode.Off
            ? SolveIk(model, transformOrder, hasVmdPose, localTranslations, localRotations, localMatrices, worldMatrices, ikProfile, out ikFallbackCount)
            : 0;

        if (enableAppend)
            ApplyAppendTransforms(model, transformOrder, restLocalTranslations, localTranslations, localRotations);
        RebuildAllWorldMatrices(model, localTranslations, localRotations, localMatrices, worldMatrices);

        var transforms = new PmxBoneTransform[count];
        for (var i = 0; i < count; i++)
            transforms[i] = new PmxBoneTransform(i, model.Bones[i].Name, localMatrices[i], worldMatrices[i], hasVmdPose[i]);

        return new PmxPose(sample.Frame, transforms, animatedBoneCount, animatedIkBoneCount, solvedIkCount, ikFallbackCount);
    }

    private static Dictionary<int, VmdBonePose> BuildPoseLookup(VmdPoseSample sample)
    {
        var poses = new Dictionary<int, VmdBonePose>();
        foreach (var pose in sample.BonePoses)
        {
            if (!poses.TryAdd(pose.PmxBoneIndex, pose))
            {
                var current = poses[pose.PmxBoneIndex];
                if (pose.Translation.LengthSquared() > current.Translation.LengthSquared())
                    poses[pose.PmxBoneIndex] = pose;
            }
        }

        return poses;
    }

    private static void ApplyAppendTransforms(
        PmxModel model,
        int[] transformOrder,
        Vector3[] restLocalTranslations,
        Vector3[] localTranslations,
        Quaternion[] localRotations)
    {
        // PMX "grant"/append bones are common in MMD models. YYB-style lower
        // body meshes can be weighted to D/EX bones that inherit motion from
        // normal leg bones, so ignoring this makes the lower body appear stuck.
        for (var pass = 0; pass < 1; pass++)
        {
            foreach (var i in transformOrder)
            {
                var bone = model.Bones[i];
                if (!IsValidBoneIndex(model, bone.AppendParentIndex) || MathF.Abs(bone.AppendWeight) <= 0.000001f)
                    continue;

                if (bone.InheritsRotation)
                {
                    var appendRotation = Quaternion.Slerp(Quaternion.Identity, localRotations[bone.AppendParentIndex], bone.AppendWeight);
                    localRotations[i] = NormalizeOrIdentity(localRotations[i] * appendRotation);
                }

                if (bone.InheritsTranslation)
                {
                    var appendDelta = (localTranslations[bone.AppendParentIndex] - restLocalTranslations[bone.AppendParentIndex]) * bone.AppendWeight;
                    if (appendDelta.LengthSquared() < 4f)
                        localTranslations[i] += appendDelta;
                }
            }
        }
    }

    private static int SolveIk(
        PmxModel model,
        int[] transformOrder,
        bool[] hasVmdPose,
        Vector3[] localTranslations,
        Quaternion[] localRotations,
        Matrix4x4[] localMatrices,
        Matrix4x4[] worldMatrices,
        PmxIkProfile ikProfile,
        out int fallbackCount)
    {
        var solvedIkCount = 0;
        fallbackCount = 0;
        foreach (var ikBoneIndex in transformOrder)
        {
            var ik = model.Bones[ikBoneIndex].Ik;
            if (ik is null || !IsValidBoneIndex(model, ik.TargetBoneIndex) || ik.Links.Count == 0)
                continue;
            if (!hasVmdPose[ikBoneIndex] && IsFootIkBone(model.Bones[ikBoneIndex].Name))
                continue;

            var iterations = Math.Max(0, ik.IterationCount);
            var maxAngle = ik.AngleLimit;
            var solvedThisIk = false;

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var targetPosition = GetTranslation(worldMatrices[ikBoneIndex]);
                var effectorPosition = GetTranslation(worldMatrices[ik.TargetBoneIndex]);
                if (Vector3.DistanceSquared(targetPosition, effectorPosition) < 0.0000005f)
                    break;

                foreach (var link in ik.Links)
                {
                    if (!IsValidBoneIndex(model, link.BoneIndex))
                        continue;

                    var linkPosition = GetTranslation(worldMatrices[link.BoneIndex]);
                    effectorPosition = GetTranslation(worldMatrices[ik.TargetBoneIndex]);
                    targetPosition = GetTranslation(worldMatrices[ikBoneIndex]);

                    var effectorDirection = SafeNormalize(effectorPosition - linkPosition);
                    var targetDirection = SafeNormalize(targetPosition - linkPosition);
                    if (effectorDirection == Vector3.Zero || targetDirection == Vector3.Zero)
                        continue;

                    var dot = Math.Clamp(Vector3.Dot(effectorDirection, targetDirection), -1f, 1f);
                    var angle = MathF.Acos(dot);
                    if (angle < 0.00001f)
                        continue;

                    var beforeDistanceSquared = Vector3.DistanceSquared(targetPosition, effectorPosition);
                    var isUpperLegLink = IsUpperLegIkLink(model.Bones[link.BoneIndex].Name);
                    var damping = GetIkStepDamping(ikProfile, isUpperLegLink);
                    angle = MathF.Min(angle, maxAngle) * damping;
                    if (isUpperLegLink)
                        angle = MathF.Min(angle, GetUpperLegMaxStepAngle(ikProfile));
                    var axisWorld = SafeNormalize(Vector3.Cross(effectorDirection, targetDirection));
                    if (axisWorld == Vector3.Zero)
                        continue;

                    var oldLocalRotation = localRotations[link.BoneIndex];
                    var candidateDistanceSquared = ApplyIkCandidate(
                        model,
                        link,
                        ik.TargetBoneIndex,
                        ikBoneIndex,
                        axisWorld,
                        angle,
                        oldLocalRotation,
                        localTranslations,
                        localRotations,
                        localMatrices,
                        worldMatrices);
                    var candidateRotation = localRotations[link.BoneIndex];

                    localRotations[link.BoneIndex] = oldLocalRotation;
                    RebuildAllWorldMatrices(model, localTranslations, localRotations, localMatrices, worldMatrices);
                    var reverseDistanceSquared = ApplyIkCandidate(
                        model,
                        link,
                        ik.TargetBoneIndex,
                        ikBoneIndex,
                        -axisWorld,
                        angle,
                        oldLocalRotation,
                        localTranslations,
                        localRotations,
                        localMatrices,
                        worldMatrices);
                    var reverseRotation = localRotations[link.BoneIndex];

                    if (candidateDistanceSquared <= reverseDistanceSquared && candidateDistanceSquared < beforeDistanceSquared)
                    {
                        localRotations[link.BoneIndex] = candidateRotation;
                        solvedThisIk = true;
                    }
                    else if (reverseDistanceSquared < beforeDistanceSquared)
                    {
                        localRotations[link.BoneIndex] = reverseRotation;
                        solvedThisIk = true;
                    }
                    else
                    {
                        localRotations[link.BoneIndex] = oldLocalRotation;
                    }

                    RebuildAllWorldMatrices(model, localTranslations, localRotations, localMatrices, worldMatrices);
                }
            }

            if (solvedThisIk)
                solvedIkCount++;
        }

        return solvedIkCount;
    }

    private static float GetIkStepDamping(PmxIkProfile profile, bool isUpperLegLink)
    {
        return profile switch
        {
            PmxIkProfile.Stable => isUpperLegLink ? 0.84f : 0.78f,
            PmxIkProfile.Strong => isUpperLegLink ? 0.98f : 0.90f,
            _ => isUpperLegLink ? 0.92f : 0.85f
        };
    }

    private static float GetUpperLegMaxStepAngle(PmxIkProfile profile)
    {
        return profile switch
        {
            PmxIkProfile.Stable => 0.35f,
            PmxIkProfile.Strong => 0.55f,
            _ => 0.45f
        };
    }

    private static float ApplyIkCandidate(
        PmxModel model,
        PmxIkLink link,
        int targetBoneIndex,
        int ikBoneIndex,
        Vector3 axisWorld,
        float angle,
        Quaternion oldLocalRotation,
        Vector3[] localTranslations,
        Quaternion[] localRotations,
        Matrix4x4[] localMatrices,
        Matrix4x4[] worldMatrices)
    {
        var parentWorldRotation = IsValidBoneIndex(model, model.Bones[link.BoneIndex].ParentIndex)
            ? ExtractRotation(worldMatrices[model.Bones[link.BoneIndex].ParentIndex])
            : Quaternion.Identity;
        var axisLocal = SafeNormalize(Vector3.Transform(axisWorld, Quaternion.Inverse(parentWorldRotation)));
        if (axisLocal == Vector3.Zero)
            return float.PositiveInfinity;

        var deltaLocalRotation = Quaternion.CreateFromAxisAngle(axisLocal, angle);
        var nextLocalRotation = NormalizeOrIdentity(oldLocalRotation * deltaLocalRotation);
        if (link.HasLimit)
            nextLocalRotation = ClampIkLinkRotation(nextLocalRotation, link, model.Bones[link.BoneIndex].Name);

        localRotations[link.BoneIndex] = nextLocalRotation;
        RebuildAllWorldMatrices(model, localTranslations, localRotations, localMatrices, worldMatrices);

        return Vector3.DistanceSquared(
            GetTranslation(worldMatrices[ikBoneIndex]),
            GetTranslation(worldMatrices[targetBoneIndex]));
    }

    private static int[] GetTransformOrder(PmxModel model)
    {
        return model.Bones
            .Select((bone, index) => new { bone.TransformLayer, index })
            .OrderBy(x => x.TransformLayer)
            .ThenBy(x => x.index)
            .Select(x => x.index)
            .ToArray();
    }

    private static void RebuildAllWorldMatrices(
        PmxModel model,
        Vector3[] localTranslations,
        Quaternion[] localRotations,
        Matrix4x4[] localMatrices,
        Matrix4x4[] worldMatrices)
    {
        var visited = new bool[model.Bones.Count];
        var visiting = new bool[model.Bones.Count];
        for (var i = 0; i < model.Bones.Count; i++)
            RebuildBoneWorld(model, i, localTranslations, localRotations, localMatrices, worldMatrices, visited, visiting);
    }

    private static Matrix4x4 RebuildBoneWorld(
        PmxModel model,
        int boneIndex,
        Vector3[] localTranslations,
        Quaternion[] localRotations,
        Matrix4x4[] localMatrices,
        Matrix4x4[] worldMatrices,
        bool[] visited,
        bool[] visiting)
    {
        if (!IsValidBoneIndex(model, boneIndex))
            return Matrix4x4.Identity;
        if (visited[boneIndex])
            return worldMatrices[boneIndex];
        if (visiting[boneIndex])
            return Matrix4x4.Identity;

        visiting[boneIndex] = true;
        var parentWorld = IsValidBoneIndex(model, model.Bones[boneIndex].ParentIndex)
            ? RebuildBoneWorld(model, model.Bones[boneIndex].ParentIndex, localTranslations, localRotations, localMatrices, worldMatrices, visited, visiting)
            : Matrix4x4.Identity;

        var local = Matrix4x4.CreateFromQuaternion(localRotations[boneIndex]) * Matrix4x4.CreateTranslation(localTranslations[boneIndex]);
        localMatrices[boneIndex] = local;
        worldMatrices[boneIndex] = local * parentWorld;
        visiting[boneIndex] = false;
        visited[boneIndex] = true;
        return worldMatrices[boneIndex];
    }

    private static bool IsValidBoneIndex(PmxModel model, int index)
    {
        return (uint)index < (uint)model.Bones.Count;
    }

    private static Vector3 GetTranslation(Matrix4x4 matrix)
    {
        return new Vector3(matrix.M41, matrix.M42, matrix.M43);
    }

    private static Quaternion ExtractRotation(Matrix4x4 matrix)
    {
        matrix.M41 = 0f;
        matrix.M42 = 0f;
        matrix.M43 = 0f;
        matrix.M44 = 1f;
        return NormalizeOrIdentity(Quaternion.CreateFromRotationMatrix(matrix));
    }

    private static Vector3 SafeNormalize(Vector3 value)
    {
        return value.LengthSquared() > 0.000001f ? Vector3.Normalize(value) : Vector3.Zero;
    }

    private static Quaternion NormalizeOrIdentity(Quaternion value)
    {
        return value.LengthSquared() > 0.000001f ? Quaternion.Normalize(value) : Quaternion.Identity;
    }

    private static Quaternion ClampIkLinkRotation(Quaternion rotation, PmxIkLink link, string boneName)
    {
        var euler = ToEulerRadians(rotation);
        if (link.HasLimit)
        {
            var min = Vector3.Min(link.MinLimit, link.MaxLimit);
            var max = Vector3.Max(link.MinLimit, link.MaxLimit);
            euler = new Vector3(
                Math.Clamp(euler.X, min.X, max.X),
                Math.Clamp(euler.Y, min.Y, max.Y),
                Math.Clamp(euler.Z, min.Z, max.Z));
        }

        return NormalizeOrIdentity(Quaternion.CreateFromYawPitchRoll(euler.Y, euler.X, euler.Z));
    }

    private static Vector3 ToEulerRadians(Quaternion q)
    {
        q = NormalizeOrIdentity(q);

        var sinrCosp = 2f * (q.W * q.X + q.Y * q.Z);
        var cosrCosp = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        var x = MathF.Atan2(sinrCosp, cosrCosp);

        var sinp = 2f * (q.W * q.Y - q.Z * q.X);
        var y = MathF.Abs(sinp) >= 1f
            ? MathF.CopySign(MathF.PI / 2f, sinp)
            : MathF.Asin(sinp);

        var sinyCosp = 2f * (q.W * q.Z + q.X * q.Y);
        var cosyCosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        var z = MathF.Atan2(sinyCosp, cosyCosp);

        return new Vector3(x, y, z);
    }

    private static bool IsFootIkBone(string name)
    {
        return name.Contains('\u8DB3', StringComparison.Ordinal) ||
               name.Contains("IK", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("\uFF29\uFF2B", StringComparison.Ordinal);
    }

    private static bool IsUpperLegIkLink(string name)
    {
        return ContainsLegName(name) &&
               !ContainsKneeName(name) &&
               !ContainsToeName(name) &&
               !ContainsIkName(name);
    }

    private static bool ContainsLegName(string name)
    {
        return name.Contains('\u8DB3', StringComparison.Ordinal) ||
               name.Contains("Leg", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Foot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsKneeName(string name)
    {
        return name.Contains("\u3072\u3056", StringComparison.Ordinal) ||
               name.Contains("\u819d", StringComparison.Ordinal) ||
               name.Contains("Knee", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsToeName(string name)
    {
        return name.Contains("\u3064\u307e\u5148", StringComparison.Ordinal) ||
               name.Contains("\u722a\u5148", StringComparison.Ordinal) ||
               name.Contains("Toe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIkName(string name)
    {
        return name.Contains("IK", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("\uFF29\uFF2B", StringComparison.Ordinal);
    }

}
