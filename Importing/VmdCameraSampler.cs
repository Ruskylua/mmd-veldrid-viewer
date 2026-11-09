using System.Numerics;

namespace SimplePmxVeldrid.Importing;

public static class VmdCameraSampler
{
    public static VmdCameraPose Sample(IReadOnlyList<VmdCameraFrame> frames, double frame)
    {
        if (frames.Count == 0)
            return new VmdCameraPose(0, 0, Vector3.Zero, Vector3.Zero, 45, true);
        if (frame <= frames[0].Frame)
            return ToPose(frames[0], (uint)Math.Max(0.0, Math.Round(frame)));
        if (frame >= frames[^1].Frame)
            return ToPose(frames[^1], (uint)Math.Max(0.0, Math.Round(frame)));

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
        var span = Math.Max(1u, b.Frame - a.Frame);
        var t = Math.Clamp((float)((frame - a.Frame) / span), 0f, 1f);
        var tx = EvaluateCameraCurve(b, 0, t);
        var ty = EvaluateCameraCurve(b, 1, t);
        var tz = EvaluateCameraCurve(b, 2, t);
        var tr = EvaluateCameraCurve(b, 3, t);
        var td = EvaluateCameraCurve(b, 4, t);
        var tf = EvaluateCameraCurve(b, 5, t);
        return new VmdCameraPose(
            (uint)Math.Max(0.0, Math.Round(frame)),
            float.Lerp(a.Distance, b.Distance, td),
            new Vector3(
                float.Lerp(a.Position.X, b.Position.X, tx),
                float.Lerp(a.Position.Y, b.Position.Y, ty),
                float.Lerp(a.Position.Z, b.Position.Z, tz)),
            Vector3.Lerp(a.Rotation, b.Rotation, tr),
            float.Lerp(a.FieldOfView, b.FieldOfView, tf),
            t < 0.5f ? a.IsPerspective : b.IsPerspective);
    }

    private static float EvaluateCameraCurve(VmdCameraFrame frame, int channel, float time)
    {
        if (frame.Interpolation.Length < 24 || channel < 0 || channel >= 6)
            return time;

        var x1 = frame.Interpolation[channel] / 127f;
        var y1 = frame.Interpolation[channel + 6] / 127f;
        var x2 = frame.Interpolation[channel + 12] / 127f;
        var y2 = frame.Interpolation[channel + 18] / 127f;
        return CubicBezierYAtX(x1, y1, x2, y2, time);
    }

    private static float CubicBezierYAtX(float x1, float y1, float x2, float y2, float x)
    {
        var t = Math.Clamp(x, 0f, 1f);
        for (var i = 0; i < 6; i++)
        {
            var currentX = Cubic(0f, x1, x2, 1f, t);
            var derivative = CubicDerivative(0f, x1, x2, 1f, t);
            if (MathF.Abs(derivative) < 0.00001f)
                break;
            t = Math.Clamp(t - (currentX - x) / derivative, 0f, 1f);
        }

        return Math.Clamp(Cubic(0f, y1, y2, 1f, t), 0f, 1f);
    }

    private static float Cubic(float p0, float p1, float p2, float p3, float t)
    {
        var u = 1f - t;
        return u * u * u * p0 +
               3f * u * u * t * p1 +
               3f * u * t * t * p2 +
               t * t * t * p3;
    }

    private static float CubicDerivative(float p0, float p1, float p2, float p3, float t)
    {
        var u = 1f - t;
        return 3f * u * u * (p1 - p0) +
               6f * u * t * (p2 - p1) +
               3f * t * t * (p3 - p2);
    }

    private static VmdCameraPose ToPose(VmdCameraFrame frame, uint sampledFrame)
    {
        return new VmdCameraPose(
            sampledFrame,
            frame.Distance,
            frame.Position,
            frame.Rotation,
            frame.FieldOfView,
            frame.IsPerspective);
    }
}
