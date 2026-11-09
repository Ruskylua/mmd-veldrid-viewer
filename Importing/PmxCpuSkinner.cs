using System.Numerics;

namespace SimplePmxVeldrid.Importing;

public sealed record PmxSkinningResult(
    uint Frame,
    IReadOnlyList<PmxSkinnedVertex> Vertices,
    int SkinnedVertexCount,
    int FallbackVertexCount,
    int InvalidInfluenceCount);

public readonly record struct PmxSkinnedVertex(
    Vector3 Position,
    Vector3 Normal);

public static class PmxCpuSkinner
{
    public static PmxSkinningResult Skin(PmxModel model, PmxPose pose)
    {
        if (model.Vertices.Count == 0)
            return new PmxSkinningResult(pose.Frame, [], 0, 0, 0);

        var restPose = PmxPoseBuilder.BuildRestPose(model);
        var skinMatrices = BuildSkinMatrices(model, restPose, pose);
        var skinnedVertices = new PmxSkinnedVertex[model.Vertices.Count];
        var skinnedVertexCount = 0;
        var fallbackVertexCount = 0;
        var invalidInfluenceCount = 0;

        for (var i = 0; i < model.Vertices.Count; i++)
        {
            var vertex = model.Vertices[i];
            var accumulator = new SkinAccumulator(vertex.Position, vertex.Normal);
            AddInfluence(vertex.Weight.Bone0, vertex.Weight.Weight0);
            AddInfluence(vertex.Weight.Bone1, vertex.Weight.Weight1);
            AddInfluence(vertex.Weight.Bone2, vertex.Weight.Weight2);
            AddInfluence(vertex.Weight.Bone3, vertex.Weight.Weight3);

            if (accumulator.TotalWeight <= 0f)
            {
                skinnedVertices[i] = new PmxSkinnedVertex(vertex.Position, NormalizeOrDefault(vertex.Normal));
                fallbackVertexCount++;
                continue;
            }

            skinnedVertices[i] = accumulator.Build();
            skinnedVertexCount++;

            void AddInfluence(int boneIndex, float weight)
            {
                if (weight <= 0f) return;
                if ((uint)boneIndex >= (uint)skinMatrices.Length)
                {
                    invalidInfluenceCount++;
                    return;
                }

                accumulator.Add(vertex.Position, vertex.Normal, skinMatrices[boneIndex], weight);
            }
        }

        return new PmxSkinningResult(
            pose.Frame,
            skinnedVertices,
            skinnedVertexCount,
            fallbackVertexCount,
            invalidInfluenceCount);
    }

    public static Matrix4x4[] BuildSkinMatrices(PmxModel model, PmxPose restPose, PmxPose pose)
    {
        var count = Math.Min(Math.Min(model.Bones.Count, restPose.BoneTransforms.Count), pose.BoneTransforms.Count);
        var skinMatrices = new Matrix4x4[count];

        for (var i = 0; i < count; i++)
        {
            var restWorld = restPose.BoneTransforms[i].WorldMatrix;
            var poseWorld = pose.BoneTransforms[i].WorldMatrix;
            // FBX OffsetMatrix support is intentionally not used yet. Assimp's
            // offset matrix is in the mesh/node space of the original FBX, and
            // combining it with this viewer's already-preview-converted
            // vertices can double-apply basis/mesh transforms on some models.
            // Keep the stable PMX-style rest-pose inverse path until the FBX
            // mesh space is represented explicitly.
            skinMatrices[i] = Matrix4x4.Invert(restWorld, out var inverseRestWorld)
                ? inverseRestWorld * poseWorld
                : Matrix4x4.Identity;
        }

        return skinMatrices;
    }

    private static Vector3 NormalizeOrDefault(Vector3 normal)
    {
        return normal.LengthSquared() > 0.000001f
            ? Vector3.Normalize(normal)
            : Vector3.UnitY;
    }

    private struct SkinAccumulator(Vector3 fallbackPosition, Vector3 fallbackNormal)
    {
        private Vector3 _position;
        private Vector3 _normal;

        public float TotalWeight { get; private set; }

        public void Add(Vector3 position, Vector3 normal, Matrix4x4 skinMatrix, float weight)
        {
            _position += Vector3.Transform(position, skinMatrix) * weight;
            _normal += Vector3.TransformNormal(normal, skinMatrix) * weight;
            TotalWeight += weight;
        }

        public readonly PmxSkinnedVertex Build()
        {
            if (TotalWeight <= 0f)
                return new PmxSkinnedVertex(fallbackPosition, NormalizeOrDefault(fallbackNormal));

            var invWeight = 1f / TotalWeight;
            return new PmxSkinnedVertex(
                _position * invWeight,
                NormalizeOrDefault(_normal));
        }
    }
}
