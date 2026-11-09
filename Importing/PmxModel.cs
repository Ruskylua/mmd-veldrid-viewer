using System.Numerics;

namespace SimplePmxVeldrid.Importing;

public sealed record PmxModel(
    string Name,
    string SourcePath,
    int VertexCount,
    int TriangleCount,
    IReadOnlyList<string> Textures,
    IReadOnlyList<PmxMaterial> Materials,
    IReadOnlyList<PmxBone> Bones,
    IReadOnlyList<PmxVertex> Vertices,
    IReadOnlyList<int> Indices);

public sealed record PmxMaterial(
    string Name,
    Vector4 Diffuse,
    byte DrawingFlags,
    int TextureIndex,
    int IndexCount)
{
    public bool IsDoubleSided => (DrawingFlags & 0x01) != 0;
    public bool HasEdge => (DrawingFlags & 0x10) != 0;
}

public readonly record struct PmxVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 UV,
    PmxVertexWeight Weight);

public readonly record struct PmxVertexWeight(
    PmxWeightType Type,
    int Bone0,
    int Bone1,
    int Bone2,
    int Bone3,
    float Weight0,
    float Weight1,
    float Weight2,
    float Weight3);

public enum PmxWeightType
{
    Bdef1,
    Bdef2,
    Bdef4,
    Sdef,
    Qdef
}

public sealed record PmxBone(
    string Name,
    Vector3 Position,
    int ParentIndex,
    int TransformLayer,
    ushort Flags,
    int AppendParentIndex,
    float AppendWeight,
    PmxIkInfo? Ik)
{
    public bool HasIk => (Flags & 0x0020) != 0;
    public bool InheritsRotation => (Flags & 0x0100) != 0;
    public bool InheritsTranslation => (Flags & 0x0200) != 0;

    // PMX bones are position-based in this viewer and default to identity
    // local rotation. FBX nodes, however, carry rest local rotation and often
    // rest local translation in the hierarchy; preserving them prevents
    // external motion from being applied onto a flattened/short-circuited rig.
    public Quaternion RestLocalRotation { get; init; } = Quaternion.Identity;

    public Vector3? RestLocalTranslation { get; init; }

    public Matrix4x4? SkinInverseBindMatrix { get; init; }
}

public sealed record PmxIkInfo(
    int TargetBoneIndex,
    int IterationCount,
    float AngleLimit,
    IReadOnlyList<PmxIkLink> Links);

public sealed record PmxIkLink(
    int BoneIndex,
    bool HasLimit,
    Vector3 MinLimit,
    Vector3 MaxLimit);
