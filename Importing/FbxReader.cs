using Assimp;
using SkiaSharp;
using System.Numerics;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsQuaternion = System.Numerics.Quaternion;

namespace SimplePmxVeldrid.Importing;

public static class FbxReader
{
    public sealed record ImportResult(PmxModel Model, VmdMotion? EmbeddedMotion);

    private readonly record struct VertexInfluence(int BoneIndex, float Weight);

    public static PmxModel Load(string path)
    {
        return LoadWithMotion(path).Model;
    }

    public static ImportResult LoadWithMotion(string path)
    {
        using var context = new AssimpContext();
        var scene = context.ImportFile(
            path,
            PostProcessSteps.Triangulate |
            PostProcessSteps.GenerateSmoothNormals |
            PostProcessSteps.FlipUVs |
            PostProcessSteps.CalculateTangentSpace |
            PostProcessSteps.JoinIdenticalVertices |
            PostProcessSteps.SortByPrimitiveType);

        if (scene is null || scene.MeshCount == 0)
            throw new InvalidDataException("FBX has no readable meshes.");

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var textures = new List<string>();
        var materials = new List<PmxMaterial>();
        var diagnostics = new List<string>
        {
            $"FBX: {path}",
            $"Meshes: {scene.MeshCount:N0}",
            $"Materials: {scene.MaterialCount:N0}",
            $"Embedded textures: {scene.TextureCount:N0}"
        };
        for (var i = 0; i < Math.Max(1, scene.MaterialCount); i++)
        {
            var material = i < scene.MaterialCount ? scene.Materials[i] : null;
            var textureIndex = ResolveDiffuseTextureIndex(scene, material, directory, path, textures, out var rawTexturePath, out var resolvedTexturePath);
            var diffuse = ResolveDiffuseColor(material, textureIndex >= 0, i);
            diagnostics.Add(
                $"Material[{i}] name='{material?.Name ?? "-"}' diffuse={FormatColor(diffuse)} " +
                $"hasDiffuse={material?.HasColorDiffuse.ToString() ?? "false"} " +
                $"hasTex={textureIndex >= 0} rawTex='{rawTexturePath ?? "-"}' resolvedTex='{resolvedTexturePath ?? "-"}'");
            materials.Add(new PmxMaterial(
                string.IsNullOrWhiteSpace(material?.Name) ? $"FBX Material {i + 1}" : material!.Name,
                diffuse,
                0x01,
                textureIndex,
                0));
        }

        var vertices = new List<PmxVertex>();
        var materialIndices = Enumerable.Range(0, materials.Count)
            .Select(_ => new List<int>())
            .ToArray();
        var boneIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var bones = BuildBones(scene, diagnostics, boneIndexByName);
        var fbxInverseBindCount = 0;
        AddNodeMeshes(scene, scene.RootNode, NumericsMatrix4x4.Identity, vertices, materialIndices, boneIndexByName, bones, ref fbxInverseBindCount);
        diagnostics.Add($"FBX inverse bind matrices loaded but disabled: {fbxInverseBindCount:N0}");

        var indices = new List<int>();
        var finalMaterials = new List<PmxMaterial>();
        for (var i = 0; i < materials.Count; i++)
        {
            if (materialIndices[i].Count == 0)
                continue;

            indices.AddRange(materialIndices[i]);
            finalMaterials.Add(materials[i] with { IndexCount = materialIndices[i].Count });
        }

        if (finalMaterials.Count == 0)
            finalMaterials.Add(new PmxMaterial("FBX Material", Vector4.One, 0x01, -1, indices.Count));

        var model = new PmxModel(
            string.IsNullOrWhiteSpace(scene.RootNode?.Name) ? Path.GetFileNameWithoutExtension(path) : scene.RootNode!.Name,
            path,
            vertices.Count,
            indices.Count / 3,
            textures,
            finalMaterials,
            bones,
            vertices,
            indices);
        var embeddedMotion = BuildEmbeddedMotion(scene, model, path, diagnostics);
        WriteDiagnostics(path, diagnostics, textures, vertices.Count, indices.Count);
        return new ImportResult(model, embeddedMotion);
    }

    private static void AddNodeMeshes(
        Scene scene,
        Node node,
        NumericsMatrix4x4 parentTransform,
        List<PmxVertex> vertices,
        List<int>[] materialIndices,
        IReadOnlyDictionary<string, int> boneIndexByName,
        List<PmxBone> bones,
        ref int fbxInverseBindCount)
    {
        var transform = ToNumerics(node.Transform) * parentTransform;
        foreach (var meshIndex in node.MeshIndices)
        {
            if ((uint)meshIndex >= (uint)scene.MeshCount) continue;
            AddMesh(scene.Meshes[meshIndex], transform, vertices, materialIndices, boneIndexByName, bones, ref fbxInverseBindCount);
        }

        foreach (var child in node.Children)
            AddNodeMeshes(scene, child, transform, vertices, materialIndices, boneIndexByName, bones, ref fbxInverseBindCount);
    }

    private static void AddMesh(
        Mesh mesh,
        NumericsMatrix4x4 transform,
        List<PmxVertex> vertices,
        List<int>[] materialIndices,
        IReadOnlyDictionary<string, int> boneIndexByName,
        List<PmxBone> bones,
        ref int fbxInverseBindCount)
    {
        var baseVertex = vertices.Count;
        var normalTransform = NumericsMatrix4x4.Transpose(NumericsMatrix4x4.Invert(transform, out var inverse)
            ? inverse
            : NumericsMatrix4x4.Identity);

        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var position = ToVector3(mesh.Vertices[i]);
            var normal = mesh.HasNormals && i < mesh.Normals.Count
                ? ToVector3(mesh.Normals[i])
                : Vector3.UnitY;
            var uv = mesh.HasTextureCoords(0) && i < mesh.TextureCoordinateChannels[0].Count
                ? new Vector2(mesh.TextureCoordinateChannels[0][i].X, mesh.TextureCoordinateChannels[0][i].Y)
                : Vector2.Zero;

            position = ConvertToPreviewCoordinate(Vector3.Transform(position, transform));
            normal = SafeNormalize(ConvertToPreviewCoordinate(Vector3.TransformNormal(normal, normalTransform)), Vector3.UnitY);
            vertices.Add(new PmxVertex(
                position,
                normal,
                uv,
                new PmxVertexWeight(PmxWeightType.Bdef1, 0, -1, -1, -1, 1f, 0f, 0f, 0f)));
        }

        ApplyMeshBoneWeights(mesh, baseVertex, vertices, boneIndexByName, bones, transform, ref fbxInverseBindCount);

        var materialIndex = Math.Clamp(mesh.MaterialIndex, 0, materialIndices.Length - 1);
        var indices = materialIndices[materialIndex];
        foreach (var face in mesh.Faces)
        {
            if (face.IndexCount != 3) continue;
            indices.Add(baseVertex + face.Indices[0]);
            indices.Add(baseVertex + face.Indices[2]);
            indices.Add(baseVertex + face.Indices[1]);
        }
    }

    private static List<PmxBone> BuildBones(Scene scene, List<string> diagnostics, Dictionary<string, int> boneIndexByName)
    {
        var usedBoneNames = scene.Meshes
            .Where(mesh => mesh.HasBones)
            .SelectMany(mesh => mesh.Bones)
            .Select(bone => bone.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var bones = new List<PmxBone>
        {
            new("FBX_Root", Vector3.Zero, -1, 0, 0, -1, 0f, null)
        };
        boneIndexByName["FBX_Root"] = 0;

        if (usedBoneNames.Count == 0)
        {
            diagnostics.Add("Bones: 0 skin bones");
            return bones;
        }

        var hierarchyBoneNames = CollectSkinBoneHierarchyNames(scene.RootNode, usedBoneNames);
        TraverseBoneNodes(scene.RootNode, NumericsMatrix4x4.Identity, 0);

        foreach (var missingName in usedBoneNames.Where(name => !boneIndexByName.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal))
        {
            boneIndexByName[missingName] = bones.Count;
            bones.Add(new PmxBone(missingName, Vector3.Zero, 0, 0, 0, -1, 0f, null));
        }

        diagnostics.Add($"Bones: {usedBoneNames.Count:N0} skin bones, hierarchy nodes {hierarchyBoneNames.Count:N0}, imported {bones.Count - 1:N0}");
        diagnostics.Add($"Added hierarchy-only bones: {Math.Max(0, hierarchyBoneNames.Count - usedBoneNames.Count):N0}");
        diagnostics.Add("Top bones: " + string.Join(", ", usedBoneNames.Take(12)));
        return bones;

        void TraverseBoneNodes(Node node, NumericsMatrix4x4 parentTransform, int currentBoneParentIndex)
        {
            var local = ToNumerics(node.Transform);
            var previewLocal = ConvertToPreviewTransform(local);
            var restLocalTranslation = new Vector3(previewLocal.M41, previewLocal.M42, previewLocal.M43);
            var restLocalRotation = ExtractRotation(previewLocal);
            var world = local * parentTransform;
            var nextBoneParentIndex = currentBoneParentIndex;
            if (hierarchyBoneNames.Contains(node.Name) && !boneIndexByName.ContainsKey(node.Name))
            {
                var position = ConvertToPreviewCoordinate(new Vector3(world.M41, world.M42, world.M43));
                var boneIndex = bones.Count;
                boneIndexByName[node.Name] = boneIndex;
                bones.Add(new PmxBone(node.Name, position, currentBoneParentIndex, 0, 0, -1, 0f, null)
                {
                    RestLocalRotation = restLocalRotation,
                    RestLocalTranslation = restLocalTranslation
                });
                nextBoneParentIndex = boneIndex;
            }

            foreach (var child in node.Children)
                TraverseBoneNodes(child, world, nextBoneParentIndex);
        }
    }

    private static HashSet<string> CollectSkinBoneHierarchyNames(Node root, IReadOnlySet<string> skinBoneNames)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<string>();

        Visit(root);
        return result;

        bool Visit(Node node)
        {
            var hasName = !string.IsNullOrWhiteSpace(node.Name);
            if (hasName)
                stack.Add(node.Name);

            var containsSkinBone = hasName && skinBoneNames.Contains(node.Name);
            foreach (var child in node.Children)
                containsSkinBone |= Visit(child);

            if (containsSkinBone)
            {
                foreach (var name in stack)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        result.Add(name);
                }
            }

            if (hasName)
                stack.RemoveAt(stack.Count - 1);

            return containsSkinBone;
        }
    }

    private static void ApplyMeshBoneWeights(
        Mesh mesh,
        int baseVertex,
        List<PmxVertex> vertices,
        IReadOnlyDictionary<string, int> boneIndexByName,
        List<PmxBone> bones,
        NumericsMatrix4x4 meshWorldTransform,
        ref int fbxInverseBindCount)
    {
        if (!mesh.HasBones)
            return;

        var influences = new List<VertexInfluence>[mesh.VertexCount];
        for (var i = 0; i < influences.Length; i++)
            influences[i] = new List<VertexInfluence>(4);

        foreach (var bone in mesh.Bones)
        {
            if (!boneIndexByName.TryGetValue(bone.Name, out var boneIndex))
                continue;

            AssignFbxInverseBindMatrix(bones, boneIndex, bone, meshWorldTransform, ref fbxInverseBindCount);

            foreach (var weight in bone.VertexWeights)
            {
                if ((uint)weight.VertexID >= (uint)influences.Length || weight.Weight <= 0f)
                    continue;

                influences[weight.VertexID].Add(new VertexInfluence(boneIndex, weight.Weight));
            }
        }

        for (var i = 0; i < influences.Length; i++)
        {
            var top = influences[i]
                .OrderByDescending(x => x.Weight)
                .Take(4)
                .ToArray();
            if (top.Length == 0)
                continue;

            var sum = top.Sum(x => MathF.Max(0f, x.Weight));
            if (sum <= 0.000001f)
                continue;

            var bone0 = top.ElementAtOrDefault(0).BoneIndex;
            var bone1 = top.ElementAtOrDefault(1).BoneIndex;
            var bone2 = top.ElementAtOrDefault(2).BoneIndex;
            var bone3 = top.ElementAtOrDefault(3).BoneIndex;
            var weight0 = top.Length > 0 ? top[0].Weight / sum : 0f;
            var weight1 = top.Length > 1 ? top[1].Weight / sum : 0f;
            var weight2 = top.Length > 2 ? top[2].Weight / sum : 0f;
            var weight3 = top.Length > 3 ? top[3].Weight / sum : 0f;
            var type = top.Length switch
            {
                1 => PmxWeightType.Bdef1,
                2 => PmxWeightType.Bdef2,
                _ => PmxWeightType.Bdef4
            };

            var vertexIndex = baseVertex + i;
            vertices[vertexIndex] = vertices[vertexIndex] with
            {
                Weight = new PmxVertexWeight(type, bone0, bone1, bone2, bone3, weight0, weight1, weight2, weight3)
            };
        }
    }

    private static void AssignFbxInverseBindMatrix(
        List<PmxBone> bones,
        int boneIndex,
        Bone bone,
        NumericsMatrix4x4 meshWorldTransform,
        ref int fbxInverseBindCount)
    {
        if ((uint)boneIndex >= (uint)bones.Count || bones[boneIndex].SkinInverseBindMatrix is not null)
            return;

        var inverseMeshWorld = NumericsMatrix4x4.Invert(meshWorldTransform, out var inv)
            ? inv
            : NumericsMatrix4x4.Identity;
        var offset = ToNumerics(bone.OffsetMatrix);
        var inverseBindRawModel = inverseMeshWorld * offset;
        var inverseBindPreview = ConvertToPreviewTransform(inverseBindRawModel);
        bones[boneIndex] = bones[boneIndex] with
        {
            SkinInverseBindMatrix = inverseBindPreview
        };
        fbxInverseBindCount++;
    }

    private static VmdMotion? BuildEmbeddedMotion(Scene scene, PmxModel model, string sourcePath, List<string> diagnostics)
    {
        if (!scene.HasAnimations || scene.AnimationCount == 0)
        {
            diagnostics.Add("Animations: 0");
            return null;
        }

        var animation = scene.Animations[0];
        if (!animation.HasNodeAnimations || animation.NodeAnimationChannelCount == 0)
        {
            diagnostics.Add($"Animations: {scene.AnimationCount:N0}, first has no node channels");
            return null;
        }

        var boneByName = model.Bones
            .Select((bone, index) => (bone, index))
            .ToDictionary(x => x.bone.Name, x => (x.bone, x.index), StringComparer.Ordinal);
        var restLocalByName = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        for (var i = 0; i < model.Bones.Count; i++)
        {
            var bone = model.Bones[i];
            var parentPosition = (uint)bone.ParentIndex < (uint)model.Bones.Count
                ? model.Bones[bone.ParentIndex].Position
                : Vector3.Zero;
            restLocalByName[bone.Name] = bone.Position - parentPosition;
        }

        var ticksPerSecond = animation.TicksPerSecond <= 0.000001 ? 30.0 : animation.TicksPerSecond;
        var framesByName = new Dictionary<string, IReadOnlyList<VmdBoneFrame>>(StringComparer.Ordinal);
        var frameCount = 0;
        uint lastFrame = 0;

        foreach (var channel in animation.NodeAnimationChannels)
        {
            if (!boneByName.ContainsKey(channel.NodeName))
                continue;

            var keyTimes = channel.PositionKeys.Select(x => x.Time)
                .Concat(channel.RotationKeys.Select(x => x.Time))
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            if (keyTimes.Length == 0)
                continue;

            var restLocal = restLocalByName.TryGetValue(channel.NodeName, out var rest)
                ? rest
                : Vector3.Zero;
            var frames = new List<VmdBoneFrame>(keyTimes.Length);
            foreach (var time in keyTimes)
            {
                var frame = (uint)Math.Clamp(Math.Round(time / ticksPerSecond * 30.0), 0, uint.MaxValue);
                var position = SampleVectorKey(channel.PositionKeys, time);
                var rotation = SampleQuaternionKey(channel.RotationKeys, time);
                var translation = ConvertToPreviewCoordinate(position) - restLocal;
                var convertedRotation = ConvertToPreviewRotation(rotation);
                frames.Add(new VmdBoneFrame(channel.NodeName, frame, translation, convertedRotation));
                lastFrame = Math.Max(lastFrame, frame);
            }

            var compactFrames = frames
                .GroupBy(x => x.Frame)
                .Select(x => x.Last())
                .OrderBy(x => x.Frame)
                .ToArray();
            if (compactFrames.Length == 0)
                continue;

            framesByName[channel.NodeName] = compactFrames;
            frameCount += compactFrames.Length;
        }

        diagnostics.Add($"Animations: {scene.AnimationCount:N0}, imported='{animation.Name}', channels={animation.NodeAnimationChannelCount:N0}, boundChannels={framesByName.Count:N0}, frames={frameCount:N0}, lastFrame={lastFrame:N0}");
        if (frameCount == 0)
            return null;

        return new VmdMotion(
            "FBX Embedded Animation",
            model.Name,
            sourcePath,
            frameCount,
            0,
            0,
            0,
            0,
            0,
            lastFrame,
            [],
            framesByName.ToDictionary(x => x.Key, x => x.Value.Count, StringComparer.Ordinal),
            framesByName);
    }

    private static Vector3 SampleVectorKey(IReadOnlyList<VectorKey> keys, double time)
    {
        if (keys.Count == 0)
            return Vector3.Zero;
        if (keys.Count == 1 || time <= keys[0].Time)
            return ToVector3(keys[0].Value);
        if (time >= keys[^1].Time)
            return ToVector3(keys[^1].Value);

        var left = 0;
        var right = keys.Count - 1;
        while (right - left > 1)
        {
            var mid = (left + right) / 2;
            if (keys[mid].Time <= time)
                left = mid;
            else
                right = mid;
        }

        var a = keys[left];
        var b = keys[right];
        var t = Math.Clamp((time - a.Time) / Math.Max(0.000001, b.Time - a.Time), 0.0, 1.0);
        return Vector3.Lerp(ToVector3(a.Value), ToVector3(b.Value), (float)t);
    }

    private static NumericsQuaternion SampleQuaternionKey(IReadOnlyList<QuaternionKey> keys, double time)
    {
        if (keys.Count == 0)
            return NumericsQuaternion.Identity;
        if (keys.Count == 1 || time <= keys[0].Time)
            return ToQuaternion(keys[0].Value);
        if (time >= keys[^1].Time)
            return ToQuaternion(keys[^1].Value);

        var left = 0;
        var right = keys.Count - 1;
        while (right - left > 1)
        {
            var mid = (left + right) / 2;
            if (keys[mid].Time <= time)
                left = mid;
            else
                right = mid;
        }

        var a = keys[left];
        var b = keys[right];
        var t = Math.Clamp((time - a.Time) / Math.Max(0.000001, b.Time - a.Time), 0.0, 1.0);
        return NumericsQuaternion.Normalize(NumericsQuaternion.Slerp(ToQuaternion(a.Value), ToQuaternion(b.Value), (float)t));
    }

    private static Vector4 ResolveDiffuseColor(Material? material, bool hasTexture, int materialIndex)
    {
        if (material is null)
            return CreateFallbackMaterialColor(materialIndex, 1f);

        var hasAnyColor = material.HasColorDiffuse || material.HasColorAmbient || material.HasColorEmissive || material.HasColorSpecular;
        var color = material.HasColorDiffuse
            ? ToVector4(material.ColorDiffuse)
            : material.HasColorAmbient
                ? ToVector4(material.ColorAmbient)
                : material.HasColorEmissive
                    ? ToVector4(material.ColorEmissive)
                    : material.HasColorSpecular
                        ? ToVector4(material.ColorSpecular)
                        : CreateFallbackMaterialColor(materialIndex, 1f);

        // Many FBX files store the visible color in textures/properties while
        // reporting a black diffuse color. Multiplying a valid texture by black
        // makes it look like "no color/no texture", so keep the texture visible.
        if (hasTexture && color.X + color.Y + color.Z < 0.03f)
            return new Vector4(1f, 1f, 1f, color.W);

        if (!hasTexture && (!hasAnyColor || color.X + color.Y + color.Z < 0.03f))
            return CreateFallbackMaterialColor(materialIndex, color.W);

        return color;
    }

    private static Vector4 CreateFallbackMaterialColor(int materialIndex, float alpha)
    {
        var hue = (materialIndex * 0.61803398875f) % 1f;
        var c = HsvToRgb(hue, 0.55f, 0.95f);
        return new Vector4(c.X, c.Y, c.Z, Math.Clamp(alpha, 0.35f, 1f));
    }

    private static Vector3 HsvToRgb(float hue, float saturation, float value)
    {
        var h = hue * 6f;
        var i = (int)MathF.Floor(h);
        var f = h - i;
        var p = value * (1f - saturation);
        var q = value * (1f - saturation * f);
        var t = value * (1f - saturation * (1f - f));
        return (i % 6) switch
        {
            0 => new Vector3(value, t, p),
            1 => new Vector3(q, value, p),
            2 => new Vector3(p, value, t),
            3 => new Vector3(p, q, value),
            4 => new Vector3(t, p, value),
            _ => new Vector3(value, p, q)
        };
    }

    private static Vector4 ToVector4(Color4D color)
    {
        return new Vector4(
            Math.Clamp(color.R, 0f, 1f),
            Math.Clamp(color.G, 0f, 1f),
            Math.Clamp(color.B, 0f, 1f),
            Math.Clamp(color.A <= 0f ? 1f : color.A, 0f, 1f));
    }

    private static int ResolveDiffuseTextureIndex(
        Scene scene,
        Material? material,
        string directory,
        string sourcePath,
        List<string> textures,
        out string? rawTexturePath,
        out string? resolvedTexturePath)
    {
        rawTexturePath = null;
        resolvedTexturePath = null;
        if (material is null)
            return -1;

        var texturePath = material.HasTextureDiffuse
            ? material.TextureDiffuse.FilePath
            : material.GetAllMaterialTextures()
                .Select(x => x.FilePath)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        rawTexturePath = string.IsNullOrWhiteSpace(texturePath) ? null : texturePath;
        resolvedTexturePath = string.IsNullOrWhiteSpace(texturePath)
            ? null
            : ResolveTexturePath(scene, texturePath, directory, sourcePath);
        resolvedTexturePath ??= ResolveTexturePathByMaterialName(material.Name, directory, sourcePath);
        if (string.IsNullOrWhiteSpace(resolvedTexturePath))
            return -1;

        var resolved = resolvedTexturePath;
        var existing = textures.FindIndex(x => string.Equals(x, resolved, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            return existing;

        textures.Add(resolvedTexturePath);
        return textures.Count - 1;
    }

    private static string? ResolveTexturePath(Scene scene, string texturePath, string directory, string sourcePath)
    {
        if (texturePath.StartsWith("*", StringComparison.Ordinal))
            return ExtractEmbeddedTexture(scene, texturePath, sourcePath);

        texturePath = texturePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(texturePath) && File.Exists(texturePath) && IsSupportedTexturePath(texturePath))
            return Path.GetFullPath(texturePath);

        var combined = Path.GetFullPath(Path.Combine(directory, texturePath));
        if (File.Exists(combined) && IsSupportedTexturePath(combined))
            return combined;

        return FindTextureNearSource(Path.GetFileNameWithoutExtension(texturePath), directory, sourcePath);
    }

    private static string? ResolveTexturePathByMaterialName(string? materialName, string directory, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(materialName))
            return null;

        var key = NormalizeMaterialTextureKey(materialName);
        return string.IsNullOrWhiteSpace(key)
            ? null
            : FindTextureNearSource(key, directory, sourcePath);
    }

    private static string? FindTextureNearSource(string? key, string directory, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var roots = BuildTextureSearchRoots(directory, sourcePath).ToArray();
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds" };

        foreach (var root in roots)
        {
            foreach (var preferred in EnumeratePreferredTextureNames(key))
            {
                foreach (var extension in supported)
                {
                    var candidate = Path.Combine(root, preferred + extension);
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
            }
        }

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var match = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(path => supported.Contains(Path.GetExtension(path)))
                    .Where(path => IsTextureNameMatch(Path.GetFileNameWithoutExtension(path), key))
                    .OrderByDescending(path => path.Contains($"{Path.DirectorySeparatorChar}Default{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(path => path.Length)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(match))
                    return Path.GetFullPath(match);
            }
            catch
            {
                // Keep looking in other roots.
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildTextureSearchRoots(string directory, string sourcePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void YieldIfExists(string? path, List<string> result)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var full = Path.GetFullPath(path);
            if (seen.Add(full))
                result.Add(full);
        }

        var roots = new List<string>();
        YieldIfExists(directory, roots);
        var current = new DirectoryInfo(directory);
        for (var i = 0; i < 4 && current is not null; i++, current = current.Parent)
        {
            YieldIfExists(current.FullName, roots);
            YieldIfExists(Path.Combine(current.FullName, "Textures"), roots);
            YieldIfExists(Path.Combine(current.FullName, "Textures", "Default"), roots);
        }

        var sourceDirectory = Path.GetDirectoryName(sourcePath);
        YieldIfExists(sourceDirectory, roots);
        return roots;
    }

    private static IEnumerable<string> EnumeratePreferredTextureNames(string key)
    {
        yield return key;
        yield return key + "_default";
        yield return key + "_Default";
        yield return key.ToLowerInvariant();
    }

    private static string NormalizeMaterialTextureKey(string value)
    {
        var key = value;
        foreach (var suffix in new[] { "_transparent", "_Transparent", "_default", "_Default", " default", " Default" })
            key = key.Replace(suffix, string.Empty, StringComparison.OrdinalIgnoreCase);

        key = key.Trim();
        return key.Contains("hair", StringComparison.OrdinalIgnoreCase) ? "Hair" :
            key.Contains("costume", StringComparison.OrdinalIgnoreCase) ? "Costume" :
            key.Contains("face", StringComparison.OrdinalIgnoreCase) ? "Face" :
            key.Contains("body", StringComparison.OrdinalIgnoreCase) ? "Body" :
            key.Contains("smartphone", StringComparison.OrdinalIgnoreCase) ? "Smartphone" :
            key;
    }

    private static bool IsTextureNameMatch(string fileNameWithoutExtension, string key)
    {
        var file = fileNameWithoutExtension.ToLowerInvariant();
        var normalizedKey = NormalizeMaterialTextureKey(key).ToLowerInvariant();
        return file.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase) ||
               file.Equals(normalizedKey + "_default", StringComparison.OrdinalIgnoreCase) ||
               file.StartsWith(normalizedKey + "_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedTexturePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tga", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dds", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveLegacyTexturePath(string texturePath, string directory)
    {
        var fileName = Path.GetFileName(texturePath);
        if (string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory))
            return null;

        try
        {
            return Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractEmbeddedTexture(Scene scene, string texturePath, string sourcePath)
    {
        if (!int.TryParse(texturePath.AsSpan(1), out var textureIndex) ||
            (uint)textureIndex >= (uint)scene.TextureCount)
            return null;

        var texture = scene.Textures[textureIndex];
        var cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "SimplePmxVeldrid",
            "fbx-textures",
            SanitizeFileName(Path.GetFileNameWithoutExtension(sourcePath)));
        Directory.CreateDirectory(cacheDirectory);

        if (texture.HasCompressedData)
        {
            var extension = string.IsNullOrWhiteSpace(texture.CompressedFormatHint)
                ? ".bin"
                : "." + texture.CompressedFormatHint.Trim().TrimStart('.').ToLowerInvariant();
            var outputPath = Path.Combine(cacheDirectory, $"embedded-{textureIndex}{extension}");
            File.WriteAllBytes(outputPath, texture.CompressedData);
            return outputPath;
        }

        if (!texture.HasNonCompressedData || texture.Width <= 0 || texture.Height <= 0)
            return null;

        var bitmap = new SKBitmap(texture.Width, texture.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = texture.NonCompressedData;
        for (var y = 0; y < texture.Height; y++)
        {
            for (var x = 0; x < texture.Width; x++)
            {
                var texel = pixels[y * texture.Width + x];
                bitmap.SetPixel(x, y, new SKColor(texel.R, texel.G, texel.B, texel.A));
            }
        }

        var pngPath = Path.Combine(cacheDirectory, $"embedded-{textureIndex}.png");
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        using var stream = File.Open(pngPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        data.SaveTo(stream);
        return pngPath;
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "model" : value;
    }

    private static NumericsMatrix4x4 ToNumerics(Assimp.Matrix4x4 matrix)
    {
        return new NumericsMatrix4x4(
            matrix.A1, matrix.A2, matrix.A3, matrix.A4,
            matrix.B1, matrix.B2, matrix.B3, matrix.B4,
            matrix.C1, matrix.C2, matrix.C3, matrix.C4,
            matrix.D1, matrix.D2, matrix.D3, matrix.D4);
    }

    private static Vector3 ToVector3(Vector3D value)
    {
        return new Vector3(value.X, value.Y, value.Z);
    }

    private static NumericsQuaternion ToQuaternion(Assimp.Quaternion value)
    {
        return NumericsQuaternion.Normalize(new NumericsQuaternion(value.X, value.Y, value.Z, value.W));
    }

    private static NumericsMatrix4x4 ConvertToPreviewTransform(NumericsMatrix4x4 value)
    {
        // Row-vector convention: preview = raw * Basis, so a transform becomes
        // Basis * Raw * Basis. This preserves FBX local rest rotations instead
        // of flattening them into position-only PMX-style bones.
        var basis = NumericsMatrix4x4.CreateScale(1f, -1f, -1f);
        return basis * value * basis;
    }

    private static NumericsQuaternion ExtractRotation(NumericsMatrix4x4 value)
    {
        if (NumericsMatrix4x4.Decompose(value, out _, out var rotation, out _))
            return NormalizeOrIdentity(rotation);

        value.M41 = 0f;
        value.M42 = 0f;
        value.M43 = 0f;
        value.M44 = 1f;
        return NormalizeOrIdentity(NumericsQuaternion.CreateFromRotationMatrix(value));
    }

    private static NumericsQuaternion ConvertToPreviewRotation(NumericsQuaternion value)
    {
        // Same basis conversion as ConvertToPreviewCoordinate for the vector
        // part of the rotation: X stays, Y/Z are mirrored.
        return NumericsQuaternion.Normalize(new NumericsQuaternion(value.X, -value.Y, -value.Z, value.W));
    }

    private static Vector3 ConvertToPreviewCoordinate(Vector3 value)
    {
        // Keep FBX Y-up by default and mirror depth to match the PMX-oriented
        // renderer. Flip Y as well because this Assimp FBX path currently
        // imports the user's model upside down in the preview space.
        return new Vector3(value.X, -value.Y, -value.Z);
    }

    private static string FormatColor(Vector4 color)
    {
        return $"{color.X:0.###},{color.Y:0.###},{color.Z:0.###},{color.W:0.###}";
    }

    private static void WriteDiagnostics(string sourcePath, IReadOnlyList<string> diagnostics, IReadOnlyList<string> textures, int vertexCount, int indexCount)
    {
        try
        {
            var outputDirectory = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, $"fbx-import-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var lines = new List<string>(diagnostics)
            {
                $"Vertices: {vertexCount:N0}",
                $"Triangles: {indexCount / 3:N0}",
                $"Resolved textures: {textures.Count:N0}"
            };
            for (var i = 0; i < textures.Count; i++)
                lines.Add($"Texture[{i}]: {textures[i]} exists={File.Exists(textures[i])}");
            File.WriteAllLines(outputPath, lines);
        }
        catch
        {
            // Diagnostics are best-effort only.
        }
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        return value.LengthSquared() <= 0.000001f ? fallback : Vector3.Normalize(value);
    }

    private static NumericsQuaternion NormalizeOrIdentity(NumericsQuaternion value)
    {
        return value.LengthSquared() <= 0.000001f
            ? NumericsQuaternion.Identity
            : NumericsQuaternion.Normalize(value);
    }
}
