using System.Numerics;

namespace SimplePmxVeldrid.Importing;

public sealed record VmdPoseSample(
    uint Frame,
    IReadOnlyList<VmdBonePose> BonePoses);

public readonly record struct VmdBonePose(
    int PmxBoneIndex,
    string BoneName,
    Vector3 Translation,
    Quaternion Rotation);

public static class VmdPoseSampler
{
    public static VmdPoseSample Sample(VmdBoneBinding binding, uint frame)
    {
        var poses = new List<VmdBonePose>(binding.BoundBones.Count);
        foreach (var bone in binding.BoundBones)
        {
            if (bone.Frames.Count == 0) continue;
            var sampled = SampleBoneFrame(bone.Frames, frame);
            poses.Add(new VmdBonePose(
                bone.PmxBoneIndex,
                bone.BoneName,
                sampled.Translation,
                sampled.Rotation));
        }

        return new VmdPoseSample(frame, poses);
    }

    private static VmdBoneFrame SampleBoneFrame(IReadOnlyList<VmdBoneFrame> frames, uint frame)
    {
        if (frame <= frames[0].Frame) return frames[0];
        if (frame >= frames[^1].Frame) return frames[^1];

        var left = 0;
        var right = frames.Count - 1;
        while (right - left > 1)
        {
            var mid = (left + right) / 2;
            if (frames[mid].Frame <= frame)
                left = mid;
            else
                right = mid;
        }

        var a = frames[left];
        var b = frames[right];
        var frameSpan = Math.Max(1u, b.Frame - a.Frame);
        var t = Math.Clamp((frame - a.Frame) / (float)frameSpan, 0f, 1f);
        return new VmdBoneFrame(
            a.BoneName,
            frame,
            Vector3.Lerp(a.Translation, b.Translation, t),
            Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, t)));
    }
}
