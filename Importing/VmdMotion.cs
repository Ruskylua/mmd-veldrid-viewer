using System.Numerics;

namespace SimplePmxVeldrid.Importing;

public sealed record VmdMotion(
    string Header,
    string ModelName,
    string SourcePath,
    int BoneFrameCount,
    int MorphFrameCount,
    int CameraFrameCount,
    int LightFrameCount,
    int SelfShadowFrameCount,
    int IkFrameCount,
    uint LastFrame,
    IReadOnlyList<VmdCameraFrame> CameraFrames,
    IReadOnlyDictionary<string, int> BoneFrameCountsByName,
    IReadOnlyDictionary<string, IReadOnlyList<VmdBoneFrame>> BoneFramesByName)
{
    public int AnimatedBoneNameCount => BoneFrameCountsByName.Count;

    public bool HasBoneMotion => BoneFramesByName.Count > 0 || BoneFrameCount > 0;

    public bool HasCameraMotion => CameraFrames.Count > 0 || CameraFrameCount > 0;

    public VmdMotion MergeWith(VmdMotion next)
    {
        var useNextBones = next.HasBoneMotion;
        var useNextCamera = next.HasCameraMotion;
        var boneFrameCounts = useNextBones ? next.BoneFrameCountsByName : BoneFrameCountsByName;
        var boneFrames = useNextBones ? next.BoneFramesByName : BoneFramesByName;
        var cameraFrames = useNextCamera ? next.CameraFrames : CameraFrames;
        return this with
        {
            Header = next.Header,
            ModelName = string.IsNullOrWhiteSpace(next.ModelName) ? ModelName : next.ModelName,
            SourcePath = next.SourcePath,
            BoneFrameCount = useNextBones ? next.BoneFrameCount : BoneFrameCount,
            MorphFrameCount = useNextBones ? next.MorphFrameCount : MorphFrameCount,
            CameraFrameCount = useNextCamera ? next.CameraFrameCount : CameraFrameCount,
            LightFrameCount = next.LightFrameCount > 0 ? next.LightFrameCount : LightFrameCount,
            SelfShadowFrameCount = next.SelfShadowFrameCount > 0 ? next.SelfShadowFrameCount : SelfShadowFrameCount,
            IkFrameCount = useNextBones ? next.IkFrameCount : IkFrameCount,
            LastFrame = Math.Max(useNextBones ? next.LastFrame : LastFrame, useNextCamera ? next.LastFrame : LastFrame),
            CameraFrames = cameraFrames,
            BoneFrameCountsByName = boneFrameCounts,
            BoneFramesByName = boneFrames
        };
    }
}

public readonly record struct VmdBoneFrame(
    string BoneName,
    uint Frame,
    Vector3 Translation,
    Quaternion Rotation);

public readonly record struct VmdCameraFrame(
    uint Frame,
    float Distance,
    Vector3 Position,
    Vector3 Rotation,
    byte[] Interpolation,
    int FieldOfView,
    bool IsPerspective);

public readonly record struct VmdCameraPose(
    uint Frame,
    float Distance,
    Vector3 Position,
    Vector3 Rotation,
    float FieldOfView,
    bool IsPerspective);
