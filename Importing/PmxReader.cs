using System.Numerics;
using System.Text;

namespace SimplePmxVeldrid.Importing;

public static class PmxReader
{
    public static PmxModel Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var r = new BinaryReader(stream);

        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "PMX ")
            throw new InvalidDataException("PMX header is invalid.");

        var version = r.ReadSingle();
        if (version < 2 || version >= 3)
            throw new InvalidDataException($"Unsupported PMX version: {version}");

        var headerLength = r.ReadByte();
        var globals = r.ReadBytes(headerLength);
        if (globals.Length < 8)
            throw new InvalidDataException("PMX global settings are invalid.");

        var textEncoding = globals[0] == 0 ? Encoding.Unicode : Encoding.UTF8;
        int extraUvCount = globals[1];
        int vertexIndexSize = globals[2];
        int textureIndexSize = globals[3];
        int boneIndexSize = globals[5];

        string ReadText()
        {
            var length = r.ReadInt32();
            if (length < 0 || length > 128 * 1024 * 1024)
                throw new InvalidDataException("PMX text length is invalid.");
            return length == 0 ? string.Empty : textEncoding.GetString(r.ReadBytes(length));
        }

        long ReadIndex(int size, bool signed = true) => size switch
        {
            1 => signed ? r.ReadSByte() : r.ReadByte(),
            2 => signed ? r.ReadInt16() : r.ReadUInt16(),
            4 => signed ? r.ReadInt32() : r.ReadUInt32(),
            _ => throw new InvalidDataException($"Invalid PMX index size: {size}")
        };

        var modelName = ReadText();
        _ = ReadText();
        _ = ReadText();
        _ = ReadText();

        int vertexCount = r.ReadInt32();
        if (vertexCount < 0 || vertexCount > 20_000_000)
            throw new InvalidDataException("PMX vertex count is invalid.");

        var vertices = new List<PmxVertex>(vertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            var position = new Vector3(r.ReadSingle(), r.ReadSingle(), -r.ReadSingle());
            var normal = new Vector3(r.ReadSingle(), r.ReadSingle(), -r.ReadSingle());
            var uv = new Vector2(r.ReadSingle(), r.ReadSingle());

            r.BaseStream.Seek(extraUvCount * 16L, SeekOrigin.Current);
            var weight = ReadVertexWeight(r, boneIndexSize, ReadIndex);
            vertices.Add(new PmxVertex(position, normal, uv, weight));
            r.ReadSingle(); // edge scale
        }

        int indexCount = r.ReadInt32();
        if (indexCount < 0 || indexCount > 100_000_000)
            throw new InvalidDataException("PMX index count is invalid.");

        var indices = new List<int>(indexCount);
        for (int i = 0; i < indexCount; i++)
            indices.Add(checked((int)ReadIndex(vertexIndexSize, signed: false)));

        int textureCount = r.ReadInt32();
        if (textureCount < 0 || textureCount > 1_000_000)
            throw new InvalidDataException("PMX texture count is invalid.");

        var modelDirectory = Path.GetDirectoryName(path)!;
        var textures = new List<string>(textureCount);
        for (int i = 0; i < textureCount; i++)
        {
            var texture = ReadText();
            textures.Add(string.IsNullOrWhiteSpace(texture)
                ? string.Empty
                : Path.GetFullPath(Path.Combine(modelDirectory, texture.Replace('/', Path.DirectorySeparatorChar))));
        }

        int materialCount = r.ReadInt32();
        if (materialCount < 0 || materialCount > 1_000_000)
            throw new InvalidDataException("PMX material count is invalid.");

        var materials = new List<PmxMaterial>(materialCount);
        for (int i = 0; i < materialCount; i++)
        {
            var materialName = ReadText();
            _ = ReadText();
            var diffuse = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            r.BaseStream.Seek(12 + 4 + 12, SeekOrigin.Current);
            var drawingFlags = r.ReadByte();
            r.BaseStream.Seek(16 + 4, SeekOrigin.Current);

            int textureIndex = checked((int)ReadIndex(textureIndexSize));
            ReadIndex(textureIndexSize);
            _ = r.ReadByte();

            byte toonFlag = r.ReadByte();
            if (toonFlag == 0) ReadIndex(textureIndexSize);
            else r.ReadByte();

            _ = ReadText();
            int materialIndexCount = r.ReadInt32();
            if (materialIndexCount < 0)
                throw new InvalidDataException("PMX material index count is invalid.");

            materials.Add(new PmxMaterial(materialName, diffuse, drawingFlags, textureIndex, materialIndexCount));
        }

        int boneCount = r.ReadInt32();
        if (boneCount < 0 || boneCount > 1_000_000)
            throw new InvalidDataException("PMX bone count is invalid.");

        var bones = new List<PmxBone>(boneCount);
        for (int i = 0; i < boneCount; i++)
        {
            var boneName = ReadText();
            _ = ReadText();
            var position = new Vector3(r.ReadSingle(), r.ReadSingle(), -r.ReadSingle());
            var parentIndex = checked((int)ReadIndex(boneIndexSize));
            var transformLayer = r.ReadInt32();
            var flags = r.ReadUInt16();
            var appendParentIndex = -1;
            var appendWeight = 0f;

            if ((flags & 0x0001) != 0)
                ReadIndex(boneIndexSize);
            else
                r.BaseStream.Seek(12, SeekOrigin.Current);

            if ((flags & 0x0100) != 0 || (flags & 0x0200) != 0)
            {
                appendParentIndex = checked((int)ReadIndex(boneIndexSize));
                appendWeight = r.ReadSingle();
            }

            if ((flags & 0x0400) != 0)
                r.BaseStream.Seek(12, SeekOrigin.Current);

            if ((flags & 0x0800) != 0)
                r.BaseStream.Seek(24, SeekOrigin.Current);

            if ((flags & 0x2000) != 0)
                r.ReadInt32();

            PmxIkInfo? ik = null;
            if ((flags & 0x0020) != 0)
            {
                var targetBoneIndex = checked((int)ReadIndex(boneIndexSize));
                var iterationCount = r.ReadInt32();
                var angleLimit = r.ReadSingle();
                var linkCount = r.ReadInt32();
                if (linkCount < 0 || linkCount > 1_000_000)
                    throw new InvalidDataException("PMX IK link count is invalid.");

                var links = new List<PmxIkLink>(linkCount);
                for (int link = 0; link < linkCount; link++)
                {
                    var linkBoneIndex = checked((int)ReadIndex(boneIndexSize));
                    var hasLimit = r.ReadByte() != 0;
                    var minLimit = Vector3.Zero;
                    var maxLimit = Vector3.Zero;
                    if (hasLimit)
                    {
                        minLimit = ConvertMmdRotationEuler(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                        maxLimit = ConvertMmdRotationEuler(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                    }

                    links.Add(new PmxIkLink(linkBoneIndex, hasLimit, minLimit, maxLimit));
                }

                ik = new PmxIkInfo(targetBoneIndex, iterationCount, angleLimit, links);
            }

            bones.Add(new PmxBone(boneName, position, parentIndex, transformLayer, flags, appendParentIndex, appendWeight, ik));
        }

        return new PmxModel(
            string.IsNullOrWhiteSpace(modelName) ? Path.GetFileNameWithoutExtension(path) : modelName,
            path,
            vertexCount,
            indexCount / 3,
            textures,
            materials,
            bones,
            vertices,
            indices);
    }

    private static PmxVertexWeight ReadVertexWeight(BinaryReader r, int boneIndexSize, Func<int, bool, long> readIndex)
    {
        var weightType = r.ReadByte();
        return weightType switch
        {
            0 => ReadBdef1(r, boneIndexSize, readIndex),
            1 => ReadBdef2(r, boneIndexSize, readIndex, PmxWeightType.Bdef2),
            2 => ReadFourBoneWeight(r, boneIndexSize, readIndex, PmxWeightType.Bdef4),
            3 => ReadSdef(r, boneIndexSize, readIndex),
            4 => ReadFourBoneWeight(r, boneIndexSize, readIndex, PmxWeightType.Qdef),
            _ => throw new InvalidDataException($"Unsupported PMX vertex weight type: {weightType}")
        };
    }

    private static PmxVertexWeight ReadBdef1(BinaryReader r, int boneIndexSize, Func<int, bool, long> readIndex)
    {
        var bone0 = checked((int)readIndex(boneIndexSize, true));
        return new PmxVertexWeight(PmxWeightType.Bdef1, bone0, -1, -1, -1, 1f, 0f, 0f, 0f);
    }

    private static PmxVertexWeight ReadBdef2(BinaryReader r, int boneIndexSize, Func<int, bool, long> readIndex, PmxWeightType type)
    {
        var bone0 = checked((int)readIndex(boneIndexSize, true));
        var bone1 = checked((int)readIndex(boneIndexSize, true));
        var weight0 = r.ReadSingle();
        return new PmxVertexWeight(type, bone0, bone1, -1, -1, weight0, 1f - weight0, 0f, 0f);
    }

    private static PmxVertexWeight ReadFourBoneWeight(BinaryReader r, int boneIndexSize, Func<int, bool, long> readIndex, PmxWeightType type)
    {
        var bone0 = checked((int)readIndex(boneIndexSize, true));
        var bone1 = checked((int)readIndex(boneIndexSize, true));
        var bone2 = checked((int)readIndex(boneIndexSize, true));
        var bone3 = checked((int)readIndex(boneIndexSize, true));
        return new PmxVertexWeight(type, bone0, bone1, bone2, bone3, r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    private static PmxVertexWeight ReadSdef(BinaryReader r, int boneIndexSize, Func<int, bool, long> readIndex)
    {
        var weight = ReadBdef2(r, boneIndexSize, readIndex, PmxWeightType.Sdef);
        r.BaseStream.Seek(36, SeekOrigin.Current); // C, R0, R1
        return weight;
    }

    private static Vector3 ConvertMmdRotationEuler(Vector3 value)
    {
        // Positions/normals are converted by mirroring Z. Rotations need the
        // corresponding axial-vector conversion: (-X, -Y, Z). IK link limits
        // are local rotation angles, not positions, so mirroring only Z makes
        // knee/ankle limits point the wrong way and can cause violent IK.
        return new Vector3(-value.X, -value.Y, value.Z);
    }
}
