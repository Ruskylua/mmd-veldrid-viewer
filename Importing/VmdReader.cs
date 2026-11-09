using System.Numerics;
using System.Text;

namespace SimplePmxVeldrid.Importing;

public static class VmdReader
{
    private static readonly Encoding ShiftJis = CreateShiftJisEncoding();

    public static VmdMotion Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var r = new BinaryReader(stream);

        var header = ReadFixedString(r, 30);
        if (!header.StartsWith("Vocaloid Motion Data", StringComparison.Ordinal))
            throw new InvalidDataException("VMD header is invalid.");

        var modelName = ReadFixedString(r, 20);

        var boneFrameCount = ReadCount(r, "VMD bone frame count");
        var mutableBoneFrames = new Dictionary<string, List<VmdBoneFrame>>(StringComparer.Ordinal);
        uint lastFrame = 0;
        for (var i = 0; i < boneFrameCount; i++)
        {
            var boneName = ReadFixedString(r, 15);
            var frame = r.ReadUInt32();
            if (frame > lastFrame) lastFrame = frame;

            var rawTranslation = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var rotation = ConvertMmdRotation(new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            var translation = ConvertMmdPosition(rawTranslation);
            r.BaseStream.Seek(64, SeekOrigin.Current);

            if (string.IsNullOrWhiteSpace(boneName)) continue;
            if (!mutableBoneFrames.TryGetValue(boneName, out var frames))
            {
                frames = [];
                mutableBoneFrames[boneName] = frames;
            }

            frames.Add(new VmdBoneFrame(boneName, frame, translation, rotation));
        }

        var boneFramesByName = mutableBoneFrames.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<VmdBoneFrame>)x.Value.OrderBy(frame => frame.Frame).ToArray(),
            StringComparer.Ordinal);
        var boneFrameCountsByName = boneFramesByName.ToDictionary(
            x => x.Key,
            x => x.Value.Count,
            StringComparer.Ordinal);

        var morphFrameCount = ReadOptionalCount(r);
        for (var i = 0; i < morphFrameCount; i++)
        {
            _ = ReadFixedString(r, 15);
            var frame = r.ReadUInt32();
            if (frame > lastFrame) lastFrame = frame;
            r.BaseStream.Seek(4, SeekOrigin.Current);
        }

        var cameraFrameCount = ReadOptionalCount(r);
        var cameraFrames = new List<VmdCameraFrame>(cameraFrameCount);
        for (var i = 0; i < cameraFrameCount; i++)
        {
            var frame = r.ReadUInt32();
            if (frame > lastFrame) lastFrame = frame;
            var distance = r.ReadSingle();
            var position = ConvertMmdPosition(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            var rotation = ConvertMmdRotationEuler(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            var interpolation = r.ReadBytes(24);
            var fieldOfView = r.ReadInt32();
            var isPerspective = r.ReadByte() == 0;
            cameraFrames.Add(new VmdCameraFrame(frame, distance, position, rotation, interpolation, fieldOfView, isPerspective));
        }

        var lightFrameCount = ReadOptionalCount(r);
        for (var i = 0; i < lightFrameCount; i++)
        {
            var frame = r.ReadUInt32();
            if (frame > lastFrame) lastFrame = frame;
            r.BaseStream.Seek(12 + 12, SeekOrigin.Current);
        }

        var selfShadowFrameCount = ReadOptionalCount(r);
        for (var i = 0; i < selfShadowFrameCount; i++)
        {
            var frame = r.ReadUInt32();
            if (frame > lastFrame) lastFrame = frame;
            r.BaseStream.Seek(1 + 4, SeekOrigin.Current);
        }

        var ikFrameCount = ReadOptionalCount(r);
        for (var i = 0; i < ikFrameCount; i++)
        {
            var frame = r.ReadUInt32();
            if (frame > lastFrame) lastFrame = frame;
            r.ReadByte();
            var ikCount = ReadCount(r, "VMD IK count");
            for (var ik = 0; ik < ikCount; ik++)
                r.BaseStream.Seek(20 + 1, SeekOrigin.Current);
        }

        return new VmdMotion(
            header,
            modelName,
            path,
            boneFrameCount,
            morphFrameCount,
            cameraFrameCount,
            lightFrameCount,
            selfShadowFrameCount,
            ikFrameCount,
            lastFrame,
            cameraFrames.OrderBy(frame => frame.Frame).ToArray(),
            boneFrameCountsByName,
            boneFramesByName);
    }

    private static int ReadOptionalCount(BinaryReader r)
    {
        return r.BaseStream.Position + 4 <= r.BaseStream.Length
            ? ReadCount(r, "VMD frame count")
            : 0;
    }

    private static int ReadCount(BinaryReader r, string label)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > 100_000_000)
            throw new InvalidDataException($"{label} is invalid.");
        return count;
    }

    private static string ReadFixedString(BinaryReader r, int byteCount)
    {
        var bytes = r.ReadBytes(byteCount);
        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return ShiftJis.GetString(bytes, 0, length).Trim();
    }

    private static Encoding CreateShiftJisEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    private static Vector3 ConvertMmdPosition(Vector3 value)
    {
        return new Vector3(value.X, value.Y, -value.Z);
    }

    private static Quaternion ConvertMmdRotation(Quaternion value)
    {
        // PMX positions are imported with Z mirrored. A rotation under the same
        // mirror transform is S * R * S, which maps quaternion (x,y,z,w) to
        // (-x,-y,z,w).
        var converted = new Quaternion(-value.X, -value.Y, value.Z, value.W);
        return converted.LengthSquared() > 0.000001f
            ? Quaternion.Normalize(converted)
            : Quaternion.Identity;
    }

    private static Vector3 ConvertMmdRotationEuler(Vector3 value)
    {
        return new Vector3(-value.X, -value.Y, value.Z);
    }

}
