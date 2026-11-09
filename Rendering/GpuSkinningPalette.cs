using System.Numerics;
using System.Runtime.InteropServices;
using SimplePmxVeldrid.Importing;
using Veldrid;

namespace SimplePmxVeldrid.Rendering;

internal sealed class GpuSkinningPalette : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly DeviceBuffer _buffer;
    private readonly GpuBoneMatrices[] _matrices;
    private readonly PmxModel _model;
    private readonly PmxPose _restPose;

    private readonly Matrix4x4 _previewFromModel;
    private readonly Matrix4x4 _modelFromPreview;

    public GpuSkinningPalette(GraphicsDevice device, ResourceFactory factory, PmxModel model, Vector3 origin, float scale)
    {
        _device = device;
        _model = model;
        _restPose = PmxPoseBuilder.BuildRestPose(model);
        _previewFromModel =
            Matrix4x4.CreateTranslation(-origin) *
            Matrix4x4.CreateScale(scale) *
            Matrix4x4.CreateTranslation(new Vector3(0, -0.82f, 0));
        _modelFromPreview = Matrix4x4.Invert(_previewFromModel, out var inverse)
            ? inverse
            : Matrix4x4.Identity;
        _matrices = new GpuBoneMatrices[Math.Max(1, model.Bones.Count)];
        for (var i = 0; i < _matrices.Length; i++)
            _matrices[i] = GpuBoneMatrices.Identity;

        _buffer = factory.CreateBuffer(new BufferDescription(
            (uint)(_matrices.Length * GpuBoneMatrices.SizeInBytes),
            BufferUsage.StructuredBufferReadOnly | BufferUsage.Dynamic,
            GpuBoneMatrices.SizeInBytes));
        _device.UpdateBuffer(_buffer, 0, _matrices);
    }

    public DeviceBuffer Buffer => _buffer;

    public int Count => _matrices.Length;

    public void Update(PmxPose pose)
    {
        Update(pose, Matrix4x4.Identity, Matrix4x4.Identity);
    }

    public void Update(PmxPose pose, Matrix4x4 postSkinTransform)
    {
        Update(pose, postSkinTransform, postSkinTransform);
    }

    public void Update(PmxPose pose, Matrix4x4 postSkinTransform, Matrix4x4 postNormalTransform)
    {
        var skinMatrices = PmxCpuSkinner.BuildSkinMatrices(_model, _restPose, pose);
        var count = Math.Min(skinMatrices.Length, _matrices.Length);
        for (var i = 0; i < count; i++)
        {
            var skinInPreview = _modelFromPreview * skinMatrices[i] * _previewFromModel;
            _matrices[i] = new GpuBoneMatrices(
                GpuMatrix.FromMatrix(skinInPreview * postSkinTransform),
                GpuMatrix.FromMatrix(skinInPreview * postNormalTransform));
        }
        for (var i = count; i < _matrices.Length; i++)
            _matrices[i] = GpuBoneMatrices.Identity;

        _device.UpdateBuffer(_buffer, 0, _matrices);
    }

    public void Dispose()
    {
        _buffer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GpuBoneMatrices(GpuMatrix Position, GpuMatrix Normal)
    {
        public const uint SizeInBytes = GpuMatrix.SizeInBytes * 2;

        public static GpuBoneMatrices Identity { get; } = new(GpuMatrix.Identity, GpuMatrix.Identity);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GpuMatrix(
        Vector4 Row0,
        Vector4 Row1,
        Vector4 Row2,
        Vector4 Row3)
    {
        public const uint SizeInBytes = 64;

        public static GpuMatrix Identity { get; } = FromMatrix(Matrix4x4.Identity);

        public static GpuMatrix FromMatrix(Matrix4x4 matrix)
        {
            return new GpuMatrix(
                new Vector4(matrix.M11, matrix.M12, matrix.M13, matrix.M14),
                new Vector4(matrix.M21, matrix.M22, matrix.M23, matrix.M24),
                new Vector4(matrix.M31, matrix.M32, matrix.M33, matrix.M34),
                new Vector4(matrix.M41, matrix.M42, matrix.M43, matrix.M44));
        }
    }
}
