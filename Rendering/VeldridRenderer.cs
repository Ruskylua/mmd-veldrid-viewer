using SimplePmxVeldrid.Importing;
using SkiaSharp;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.SPIRV;
using Veldrid.StartupUtilities;

namespace SimplePmxVeldrid.Rendering;

public sealed class VeldridRenderer
{
    private readonly object _gate = new();
    private PmxModel? _model;
    private VmdMotion? _motion;
    private string? _audioPath;
    private int _audioVersion;
    private Thread? _renderThread;
    private Action<string>? _statusSink;

    public GraphicsBackend PreferredBackend { get; } = GraphicsBackend.OpenGL;

    public void SetStatusSink(Action<string> statusSink)
    {
        _statusSink = statusSink;
    }

    public void SetModel(PmxModel model)
    {
        lock (_gate)
        {
            _model = model;
        }
    }

    public void SetMotion(VmdMotion motion)
    {
        VmdMotion merged;
        var isFbxModel = false;
        lock (_gate)
        {
            _motion = _motion is null ? motion : _motion.MergeWith(motion);
            merged = _motion;
            isFbxModel = _model is not null &&
                Path.GetExtension(_model.SourcePath).Equals(".fbx", StringComparison.OrdinalIgnoreCase);
        }

        _statusSink?.Invoke(isFbxModel && merged.HasBoneMotion
            ? $"VMD assigned: {merged.BoneFrameCount:N0} bone frames, {merged.CameraFrameCount:N0} camera frames. FBX retarget uses fbx-retarget-profile-active.json; default has all bones disabled."
            : $"VMD assigned: {merged.BoneFrameCount:N0} bone frames, {merged.CameraFrameCount:N0} camera frames.");
    }

    public void SetAudio(string path)
    {
        lock (_gate)
        {
            _audioPath = path;
            _audioVersion++;
        }

        _statusSink?.Invoke($"Audio assigned: {Path.GetFileName(path)}. Press Space in the render window to play.");
    }

    public void ResetView()
    {
        _statusSink?.Invoke("Press R in the render window to reset the camera.");
    }

    public void OpenPreviewWindow()
    {
        PmxModel model;
        VmdMotion? motion;
        lock (_gate)
        {
            if (_model is null) return;
            model = _model;
            motion = _motion;
        }

        if (_renderThread?.IsAlive == true)
        {
            _statusSink?.Invoke("Render window is already open.");
            return;
        }

        _renderThread = new Thread(() =>
        {
            try
            {
                RunPreviewWindow(model, motion);
            }
            catch (Exception ex)
            {
                _statusSink?.Invoke($"Veldrid render error: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "Veldrid PMX Preview"
        };
        _renderThread.Start();
    }

    private void RunPreviewWindow(PmxModel model, VmdMotion? initialMotion)
    {
        var errors = new List<string>();
        foreach (var backend in new[] { PreferredBackend, GraphicsBackend.Direct3D11, GraphicsBackend.Vulkan })
        {
            try
            {
                RunPreviewWindow(model, initialMotion, backend);
                return;
            }
            catch (Exception ex)
            {
                errors.Add($"{backend}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    private void RunPreviewWindow(PmxModel model, VmdMotion? initialMotion, GraphicsBackend backend)
    {
        var windowInfo = new WindowCreateInfo(
            100,
            100,
            960,
            720,
            WindowState.Normal,
            $"Simple PMX Viewer - {model.Name}");

        var options = new GraphicsDeviceOptions(
            debug: false,
            swapchainDepthFormat: PixelFormat.D24_UNorm_S8_UInt,
            syncToVerticalBlank: true,
            resourceBindingModel: ResourceBindingModel.Improved,
            preferStandardClipSpaceYDirection: true,
            preferDepthRangeZeroToOne: true);

        VeldridStartup.CreateWindowAndGraphicsDevice(
            windowInfo,
            options,
            backend,
            out Sdl2Window window,
            out GraphicsDevice graphicsDevice);

        _statusSink?.Invoke($"Veldrid render started: {graphicsDevice.BackendType}");

        var factory = graphicsDevice.ResourceFactory;
        var mesh = BuildPreviewMesh(model);
        var sourceVertices = mesh.Vertices;
        var animatedSourceVertices = sourceVertices.ToArray();
        var drawVertices = sourceVertices.ToArray();
        var camera = new OrbitCamera();
        var playback = new MotionPlayback(model, initialMotion, mesh, animatedSourceVertices, _statusSink);
        var displayMode = DisplayMode.Texture;
        var transparencyEnabled = true;
        var shadingEnabled = true;
        var backfaceCullingEnabled = false;
        var outlineLevel = 0;
        var gridEnabled = true;
        var backgroundMode = 0;
        var transparencySortMode = TransparencySortMode.BackToFront;
        var vmdCameraEnabled = initialMotion?.CameraFrames.Count > 0;
        var vmdCameraUserToggled = false;
        var placementMode = PlacementMode.Foot;
        var ikMode = PmxIkMode.Full;
        var ikProfile = PmxIkProfile.Normal;
        var appendEnabled = true;
        var isFbxModel = Path.GetExtension(model.SourcePath).Equals(".fbx", StringComparison.OrdinalIgnoreCase);
        var vmdMotionEnabled = !isFbxModel;
        var fbxProfileRetargetEnabled = true;
        var gpuSkinningPrepared = false;
        var gpuCameraTest = false;
        UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
        if (isFbxModel && initialMotion?.HasBoneMotion == true)
            _statusSink?.Invoke("FBX + external VMD: profile retarget is available but default profile disables all bones. Camera/audio still work.");

        var vertexBuffer = factory.CreateBuffer(new BufferDescription(
            (uint)(PreviewVertex.SizeInBytes * drawVertices.Length),
            BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        graphicsDevice.UpdateBuffer(vertexBuffer, 0, drawVertices);
        var gpuVertexBuffer = factory.CreateBuffer(new BufferDescription(
            (uint)(GpuPreviewVertex.SizeInBytes * mesh.GpuVertices.Length),
            BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        graphicsDevice.UpdateBuffer(gpuVertexBuffer, 0, mesh.GpuVertices);
        var gpuDrawVertices = mesh.GpuVertices.ToArray();
        var outlineVertices = sourceVertices.ToArray();
        var outlineBuffer = factory.CreateBuffer(new BufferDescription(
            (uint)(PreviewVertex.SizeInBytes * outlineVertices.Length),
            BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        graphicsDevice.UpdateBuffer(outlineBuffer, 0, outlineVertices);
        var gridSourceVertices = CreateGridVertices();
        var gridDrawVertices = gridSourceVertices.ToArray();
        var gridBuffer = factory.CreateBuffer(new BufferDescription(
            (uint)(PreviewVertex.SizeInBytes * gridDrawVertices.Length),
            BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        graphicsDevice.UpdateBuffer(gridBuffer, 0, gridDrawVertices);
        using var gpuSkinningPalette = new GpuSkinningPalette(graphicsDevice, factory, model, mesh.Origin, mesh.Scale);
        var cameraBuffer = factory.CreateBuffer(new BufferDescription(GpuCameraUniform.SizeInBytes, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
        graphicsDevice.UpdateBuffer(cameraBuffer, 0, GpuCameraUniform.Identity);
        var shaders = factory.CreateFromSpirv(
            new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(VertexShader), "main"),
            new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(FragmentShader), "main"));
        var gpuShaders = factory.CreateFromSpirv(
            new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(GpuSkinningVertexShader), "main"),
            new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(FragmentShader), "main"));

        var layout = new VertexLayoutDescription(
            new VertexElementDescription("Position", VertexElementSemantic.Position, VertexElementFormat.Float3),
            new VertexElementDescription("Color", VertexElementSemantic.Color, VertexElementFormat.Float4),
            new VertexElementDescription("DebugColor", VertexElementSemantic.Color, VertexElementFormat.Float4),
            new VertexElementDescription("SkinDebugColor", VertexElementSemantic.Color, VertexElementFormat.Float4),
            new VertexElementDescription("Normal", VertexElementSemantic.Normal, VertexElementFormat.Float3),
            new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));
        var gpuLayout = new VertexLayoutDescription(
            new VertexElementDescription("Position", VertexElementSemantic.Position, VertexElementFormat.Float3),
            new VertexElementDescription("Color", VertexElementSemantic.Color, VertexElementFormat.Float4),
            new VertexElementDescription("DebugColor", VertexElementSemantic.Color, VertexElementFormat.Float4),
            new VertexElementDescription("SkinDebugColor", VertexElementSemantic.Color, VertexElementFormat.Float4),
            new VertexElementDescription("Normal", VertexElementSemantic.Normal, VertexElementFormat.Float3),
            new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
            new VertexElementDescription("BoneIndices", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
            new VertexElementDescription("BoneWeights", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));

        var resourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("SurfaceTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("SurfaceSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
        var gpuSkinningLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("Camera", ResourceKind.UniformBuffer, ShaderStages.Vertex),
            new ResourceLayoutElementDescription("Bones", ResourceKind.StructuredBufferReadOnly, ShaderStages.Vertex)));
        var gpuSkinningSet = factory.CreateResourceSet(new ResourceSetDescription(gpuSkinningLayout, cameraBuffer, gpuSkinningPalette.Buffer));
        var textureResources = CreateTextureResources(graphicsDevice, factory, resourceLayout, mesh.Batches, _statusSink);

        var opaqueDoubleSidedPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            BlendState = BlendStateDescription.SingleOverrideBlend,
            DepthStencilState = DepthStencilStateDescription.DepthOnlyLessEqual,
            RasterizerState = new RasterizerStateDescription(
                FaceCullMode.None,
                PolygonFillMode.Solid,
                FrontFace.Clockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false),
            PrimitiveTopology = PrimitiveTopology.TriangleList,
            ResourceLayouts = [resourceLayout],
            ShaderSet = new ShaderSetDescription([layout], shaders),
            Outputs = graphicsDevice.SwapchainFramebuffer.OutputDescription
        });
        var opaqueSingleSidedPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            BlendState = BlendStateDescription.SingleOverrideBlend,
            DepthStencilState = DepthStencilStateDescription.DepthOnlyLessEqual,
            RasterizerState = new RasterizerStateDescription(
                FaceCullMode.Back,
                PolygonFillMode.Solid,
                FrontFace.Clockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false),
            PrimitiveTopology = PrimitiveTopology.TriangleList,
            ResourceLayouts = [resourceLayout],
            ShaderSet = new ShaderSetDescription([layout], shaders),
            Outputs = graphicsDevice.SwapchainFramebuffer.OutputDescription
        });
        var transparentDoubleSidedPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            BlendState = BlendStateDescription.SingleAlphaBlend,
            DepthStencilState = new DepthStencilStateDescription(
                depthTestEnabled: true,
                depthWriteEnabled: false,
                comparisonKind: ComparisonKind.LessEqual),
            RasterizerState = new RasterizerStateDescription(
                FaceCullMode.None,
                PolygonFillMode.Solid,
                FrontFace.Clockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false),
            PrimitiveTopology = PrimitiveTopology.TriangleList,
            ResourceLayouts = [resourceLayout],
            ShaderSet = new ShaderSetDescription([layout], shaders),
            Outputs = graphicsDevice.SwapchainFramebuffer.OutputDescription
        });
        var transparentSingleSidedPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            BlendState = BlendStateDescription.SingleAlphaBlend,
            DepthStencilState = new DepthStencilStateDescription(
                depthTestEnabled: true,
                depthWriteEnabled: false,
                comparisonKind: ComparisonKind.LessEqual),
            RasterizerState = new RasterizerStateDescription(
                FaceCullMode.Back,
                PolygonFillMode.Solid,
                FrontFace.Clockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false),
            PrimitiveTopology = PrimitiveTopology.TriangleList,
            ResourceLayouts = [resourceLayout],
            ShaderSet = new ShaderSetDescription([layout], shaders),
            Outputs = graphicsDevice.SwapchainFramebuffer.OutputDescription
        });
        var outlinePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            BlendState = BlendStateDescription.SingleOverrideBlend,
            DepthStencilState = DepthStencilStateDescription.DepthOnlyLessEqual,
            RasterizerState = new RasterizerStateDescription(
                FaceCullMode.Front,
                PolygonFillMode.Solid,
                FrontFace.Clockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false),
            PrimitiveTopology = PrimitiveTopology.TriangleList,
            ResourceLayouts = [resourceLayout],
            ShaderSet = new ShaderSetDescription([layout], shaders),
            Outputs = graphicsDevice.SwapchainFramebuffer.OutputDescription
        });
        var gridPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            BlendState = BlendStateDescription.SingleAlphaBlend,
            DepthStencilState = DepthStencilStateDescription.DepthOnlyLessEqual,
            RasterizerState = new RasterizerStateDescription(
                FaceCullMode.None,
                PolygonFillMode.Solid,
                FrontFace.Clockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false),
            PrimitiveTopology = PrimitiveTopology.LineList,
            ResourceLayouts = [resourceLayout],
            ShaderSet = new ShaderSetDescription([layout], shaders),
            Outputs = graphicsDevice.SwapchainFramebuffer.OutputDescription
        });
        var gpuOpaqueDoubleSidedPipeline = CreateGpuSkinningPipeline(BlendStateDescription.SingleOverrideBlend, DepthStencilStateDescription.DepthOnlyLessEqual, FaceCullMode.None);
        var gpuOpaqueSingleSidedPipeline = CreateGpuSkinningPipeline(BlendStateDescription.SingleOverrideBlend, DepthStencilStateDescription.DepthOnlyLessEqual, FaceCullMode.Back);
        var gpuTransparentDoubleSidedPipeline = CreateGpuSkinningPipeline(
            BlendStateDescription.SingleAlphaBlend,
            new DepthStencilStateDescription(true, false, ComparisonKind.LessEqual),
            FaceCullMode.None);
        var gpuTransparentSingleSidedPipeline = CreateGpuSkinningPipeline(
            BlendStateDescription.SingleAlphaBlend,
            new DepthStencilStateDescription(true, false, ComparisonKind.LessEqual),
            FaceCullMode.Back);

        var commandList = factory.CreateCommandList();
        using var audioPlayer = new MciAudioPlayer();
        string? loadedAudioPath = null;
        var loadedAudioVersion = -1;
        var fpsClock = Stopwatch.StartNew();
        var fpsFrameCount = 0;
        var currentFps = 0.0;
        var renderStatsLabel = string.Empty;
        window.Resized += () =>
        {
            if (window.Width > 0 && window.Height > 0)
                graphicsDevice.MainSwapchain.Resize((uint)window.Width, (uint)window.Height);
        };

        Pipeline CreateGpuSkinningPipeline(BlendStateDescription blend, DepthStencilStateDescription depth, FaceCullMode cullMode)
        {
            return factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = blend,
                DepthStencilState = depth,
                RasterizerState = new RasterizerStateDescription(
                    cullMode,
                    PolygonFillMode.Solid,
                    FrontFace.Clockwise,
                    depthClipEnabled: true,
                    scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = [resourceLayout, gpuSkinningLayout],
                ShaderSet = new ShaderSetDescription([gpuLayout], gpuShaders),
                Outputs = graphicsDevice.SwapchainFramebuffer.OutputDescription
            });
        }

        while (window.Exists)
        {
            var input = window.PumpEvents();
            if (!window.Exists) break;

            RefreshAudio(audioPlayer, ref loadedAudioPath, ref loadedAudioVersion);

            foreach (var keyEvent in input.KeyEvents)
            {
                if (!keyEvent.Down) continue;
                var nextMode = keyEvent.Key switch
                {
                    Key.Number1 => DisplayMode.Texture,
                    Key.Number2 => DisplayMode.MaterialColor,
                    Key.Number3 => DisplayMode.NormalDebug,
                    Key.Number4 => DisplayMode.SolidWhite,
                    _ => displayMode
                };

                if (nextMode != displayMode)
                {
                    displayMode = nextMode;
                    _statusSink?.Invoke($"Display mode: {displayMode}  (1 Texture / 2 Material / 3 Normal / 4 White)");
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                }

                if (keyEvent.Key == Key.Number5)
                {
                    transparencyEnabled = !transparencyEnabled;
                    _statusSink?.Invoke($"Transparency: {(transparencyEnabled ? "On" : "Off")}  (5 toggles)");
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                }

                if (keyEvent.Key == Key.Number6)
                {
                    shadingEnabled = !shadingEnabled;
                    _statusSink?.Invoke($"Shading: {(shadingEnabled ? "On" : "Flat")}  (6 toggles)");
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                }

                if (keyEvent.Key == Key.Number7)
                {
                    backfaceCullingEnabled = !backfaceCullingEnabled;
                    _statusSink?.Invoke($"Backface culling: {(backfaceCullingEnabled ? "On for single-sided PMX materials" : "Off")}  (7 toggles)");
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                }

                if (keyEvent.Key == Key.Number8)
                {
                    outlineLevel = (outlineLevel + 1) % 4;
                    _statusSink?.Invoke($"Outline: {GetOutlineLabel(outlineLevel)}  (8 cycles Off / Thin / Normal / Thick)");
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                }

                if (keyEvent.Key == Key.Number9)
                {
                    placementMode = placementMode == PlacementMode.Foot ? PlacementMode.Center : PlacementMode.Foot;
                    _statusSink?.Invoke($"Placement: {placementMode}  (9 toggles Foot / Center)");
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                }

                if (keyEvent.Key == Key.Number0)
                {
                    gridEnabled = !gridEnabled;
                    _statusSink?.Invoke($"Grid: {OnOff(gridEnabled)}  (0 toggles)");
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                }

                if (keyEvent.Key == Key.B)
                {
                    backgroundMode = (backgroundMode + 1) % 4;
                    _statusSink?.Invoke($"Background: {GetBackgroundLabel(backgroundMode)}  (B cycles)");
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                }

                if (keyEvent.Key == Key.M)
                {
                    displayMode = displayMode == DisplayMode.MaterialIdColor
                        ? DisplayMode.Texture
                        : DisplayMode.MaterialIdColor;
                    _statusSink?.Invoke($"Display mode: {displayMode}  (M toggles material ID color)");
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                }

                if (keyEvent.Key == Key.T)
                {
                    transparencySortMode = transparencySortMode == TransparencySortMode.BackToFront
                        ? TransparencySortMode.MaterialOrder
                        : TransparencySortMode.BackToFront;
                    _statusSink?.Invoke($"Transparency sort: {transparencySortMode}  (T toggles)");
                }

                if (keyEvent.Key == Key.I)
                {
                    ikMode = ikMode switch
                    {
                        PmxIkMode.Full => PmxIkMode.Off,
                        _ => PmxIkMode.Full
                    };
                    playback.ForceRefresh();
                    _statusSink?.Invoke($"IK: {ikMode}  (I toggles Full / Off)");
                }

                if (keyEvent.Key == Key.K)
                {
                    ikProfile = ikProfile switch
                    {
                        PmxIkProfile.Stable => PmxIkProfile.Normal,
                        PmxIkProfile.Normal => PmxIkProfile.Strong,
                        _ => PmxIkProfile.Stable
                    };
                    playback.ForceRefresh();
                    _statusSink?.Invoke($"IK profile: {ikProfile}  (K cycles Stable / Normal / Strong)");
                }

                if (keyEvent.Key == Key.Space)
                {
                    playback.TogglePaused();
                    if (playback.IsPaused)
                        audioPlayer.Pause();
                    else
                        audioPlayer.PlayFrom(playback.CurrentSeconds, playback.Speed);
                    _statusSink?.Invoke($"Playback: {(playback.IsPaused ? "Paused" : "Playing")}  (Space toggles)");
                }

                if (keyEvent.Key == Key.Comma)
                {
                    playback.ChangeSpeedStep(-1);
                    if (!playback.IsPaused)
                        audioPlayer.SetSpeed(playback.Speed);
                    _statusSink?.Invoke($"Playback speed: {playback.Speed:0.00}x  (, slower)");
                }

                if (keyEvent.Key == Key.Period)
                {
                    playback.ChangeSpeedStep(1);
                    if (!playback.IsPaused)
                        audioPlayer.SetSpeed(playback.Speed);
                    _statusSink?.Invoke($"Playback speed: {playback.Speed:0.00}x  (. faster)");
                }

                if (keyEvent.Key == Key.Left)
                {
                    playback.StepFrames(-1);
                    audioPlayer.Pause();
                    audioPlayer.Seek(playback.CurrentSeconds);
                    _statusSink?.Invoke($"Playback frame: {playback.CurrentFrame}/{playback.LastFrame}  (Left/Right ±1)");
                }

                if (keyEvent.Key == Key.Right)
                {
                    playback.StepFrames(1);
                    audioPlayer.Pause();
                    audioPlayer.Seek(playback.CurrentSeconds);
                    _statusSink?.Invoke($"Playback frame: {playback.CurrentFrame}/{playback.LastFrame}  (Left/Right ±1)");
                }

                if (keyEvent.Key == Key.Down)
                {
                    playback.StepFrames(-10);
                    audioPlayer.Pause();
                    audioPlayer.Seek(playback.CurrentSeconds);
                    _statusSink?.Invoke($"Playback frame: {playback.CurrentFrame}/{playback.LastFrame}  (Up/Down ±10)");
                }

                if (keyEvent.Key == Key.Up)
                {
                    playback.StepFrames(10);
                    audioPlayer.Pause();
                    audioPlayer.Seek(playback.CurrentSeconds);
                    _statusSink?.Invoke($"Playback frame: {playback.CurrentFrame}/{playback.LastFrame}  (Up/Down ±10)");
                }

                if (keyEvent.Key == Key.Home)
                {
                    playback.SeekStart();
                    audioPlayer.Pause();
                    audioPlayer.Seek(playback.CurrentSeconds);
                    _statusSink?.Invoke($"Playback frame: {playback.CurrentFrame}/{playback.LastFrame}  (Home start)");
                }

                if (keyEvent.Key == Key.End)
                {
                    playback.SeekEnd();
                    audioPlayer.Pause();
                    audioPlayer.Seek(playback.CurrentSeconds);
                    _statusSink?.Invoke($"Playback frame: {playback.CurrentFrame}/{playback.LastFrame}  (End last)");
                }

                if (keyEvent.Key == Key.L && !isFbxModel)
                {
                    playback.ToggleLoop();
                    _statusSink?.Invoke($"Playback loop: {OnOff(playback.LoopEnabled)}  (L toggles)");
                }

                if (keyEvent.Key == Key.C)
                {
                    vmdCameraEnabled = !vmdCameraEnabled;
                    vmdCameraUserToggled = true;
                    _statusSink?.Invoke($"VMD camera: {OnOff(vmdCameraEnabled)}  (C toggles)");
                }

                if (keyEvent.Key == Key.V)
                {
                    if (isFbxModel)
                    {
                        fbxProfileRetargetEnabled = !fbxProfileRetargetEnabled;
                        var bindingSummary = playback.ReloadBinding();
                        _statusSink?.Invoke($"FBX profile retarget: {OnOff(fbxProfileRetargetEnabled)}  {bindingSummary}");
                    }
                    else
                    {
                        vmdMotionEnabled = !vmdMotionEnabled;
                        playback.ForceRefresh();
                        _statusSink?.Invoke($"VMD bone motion: {OnOff(vmdMotionEnabled)}  (V toggles)");
                    }
                }

                if (keyEvent.Key == Key.P && isFbxModel)
                {
                    var bindingSummary = playback.ReloadBinding();
                    _statusSink?.Invoke($"FBX profile reloaded: {bindingSummary}");
                }

                if (keyEvent.Key == Key.J && isFbxModel)
                {
                    var activateMessage = FbxRetargetProfileStore.ActivateHeadTest();
                    fbxProfileRetargetEnabled = true;
                    var bindingSummary = playback.ReloadBinding();
                    _statusSink?.Invoke($"FBX head-test: {activateMessage}. Retarget On {bindingSummary}");
                }

                if (keyEvent.Key == Key.Q && isFbxModel)
                {
                    var activateMessage = FbxRetargetProfileStore.ActivateHeadInvertTest();
                    fbxProfileRetargetEnabled = true;
                    var bindingSummary = playback.ReloadBinding();
                    _statusSink?.Invoke($"FBX head recommended-test: {activateMessage}. Retarget On {bindingSummary}");
                }

                if (keyEvent.Key == Key.E && isFbxModel)
                {
                    var activateMessage = FbxRetargetProfileStore.ActivateHeadRawTest();
                    fbxProfileRetargetEnabled = true;
                    var bindingSummary = playback.ReloadBinding();
                    _statusSink?.Invoke($"FBX head-raw-test: {activateMessage}. Retarget On {bindingSummary}");
                }

                if (keyEvent.Key == Key.N && isFbxModel)
                {
                    var activateMessage = FbxRetargetProfileStore.ActivateNeckHeadTest();
                    fbxProfileRetargetEnabled = true;
                    var bindingSummary = playback.ReloadBinding();
                    _statusSink?.Invoke($"FBX neck+head-test: {activateMessage}. Retarget On {bindingSummary}");
                }

                if (keyEvent.Key == Key.A && isFbxModel)
                {
                    var activateMessage = FbxRetargetProfileStore.ActivateUpperBodyTest();
                    fbxProfileRetargetEnabled = true;
                    var bindingSummary = playback.ReloadBinding();
                    _statusSink?.Invoke($"FBX upper-body-test: {activateMessage}. Retarget On {bindingSummary}");
                }

                if (keyEvent.Key == Key.S && isFbxModel)
                {
                    var activateMessage = FbxRetargetProfileStore.ActivateSpineTest();
                    fbxProfileRetargetEnabled = true;
                    var bindingSummary = playback.ReloadBinding();
                    _statusSink?.Invoke($"FBX spine-test: {activateMessage}. Retarget On {bindingSummary}");
                }

                if (keyEvent.Key == Key.L && isFbxModel)
                {
                    var activateMessage = FbxRetargetProfileStore.ActivateLowerBodyTest();
                    fbxProfileRetargetEnabled = true;
                    var bindingSummary = playback.ReloadBinding();
                    _statusSink?.Invoke($"FBX lower-body-test: {activateMessage}. Retarget On {bindingSummary}");
                }

                if (keyEvent.Key == Key.F && isFbxModel)
                {
                    var activateMessage = FbxRetargetProfileStore.ActivateFullBodyTest();
                    fbxProfileRetargetEnabled = true;
                    var bindingSummary = playback.ReloadBinding();
                    _statusSink?.Invoke($"FBX full-body-test: {activateMessage}. Retarget On {bindingSummary}");
                }

                if (keyEvent.Key == Key.G)
                {
                    appendEnabled = !appendEnabled;
                    playback.ForceRefresh();
                    _statusSink?.Invoke($"Append/Grant bones: {OnOff(appendEnabled)}  (G toggles)");
                }

                if (keyEvent.Key == Key.Y)
                {
                    gpuSkinningPrepared = !gpuSkinningPrepared;
                    if (gpuSkinningPrepared)
                        gpuCameraTest = false;
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                    _statusSink?.Invoke($"GPU LBS skinning: {OnOff(gpuSkinningPrepared)}  (Y toggles)");
                }

                if (keyEvent.Key == Key.U)
                {
                    displayMode = displayMode == DisplayMode.Bdef1Debug
                        ? DisplayMode.Texture
                        : DisplayMode.Bdef1Debug;
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                    _statusSink?.Invoke($"Display mode: {displayMode}  (U toggles skin weight debug)");
                }

                if (keyEvent.Key == Key.H)
                {
                    gpuCameraTest = !gpuCameraTest;
                    if (gpuCameraTest)
                        gpuSkinningPrepared = false;
                    UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest);
                    _statusSink?.Invoke($"GPU LBS debug: {OnOff(gpuCameraTest)}  (H toggles)");
                }
            }

            var useGpuPipeline = gpuSkinningPrepared || gpuCameraTest;
            var needsDepthSortedTransparentVertices =
                transparencyEnabled &&
                transparencySortMode == TransparencySortMode.BackToFront &&
                mesh.Batches.Any(batch => batch.IsTransparent);
            var needsCpuSkinnedVertices =
                !useGpuPipeline ||
                outlineLevel > 0 ||
                needsDepthSortedTransparentVertices ||
                mesh.GpuFallbackVertexCount > 0;
            var effectiveVmdMotionEnabled = isFbxModel
                ? fbxProfileRetargetEnabled
                : vmdMotionEnabled;
            playback.Update(this, ikMode, appendEnabled, ikProfile, effectiveVmdMotionEnabled, needsCpuSkinnedVertices);
            if (!vmdCameraUserToggled && playback.HasCameraFrames)
                vmdCameraEnabled = true;
            var cameraPose = vmdCameraEnabled ? playback.CurrentCameraPose : null;
            camera.Update(input, cameraPose is null);
            if (cameraPose is not null)
                camera.ApplyVmdCamera(cameraPose.Value, mesh.Origin, mesh.Scale);
            var aspectCorrection = window.Width > 0 && window.Height > 0
                ? (float)window.Height / window.Width
                : 1f;
            var placementYOffset = placementMode == PlacementMode.Center ? mesh.CenterPlacementYOffset : 0f;
            if (useGpuPipeline)
            {
                if (playback.CurrentPose is not null)
                    gpuSkinningPalette.Update(playback.CurrentPose, camera.CreateGpuMatrix(aspectCorrection, placementYOffset), camera.CreateGpuNormalMatrix());

                if (needsCpuSkinnedVertices)
                    camera.Transform(animatedSourceVertices, drawVertices, displayMode, aspectCorrection, transparencyEnabled, shadingEnabled, placementYOffset);
                FillGpuLinearSkinningVertices(drawVertices, mesh.GpuVertices, gpuDrawVertices);
                graphicsDevice.UpdateBuffer(gpuVertexBuffer, 0, gpuDrawVertices);
                graphicsDevice.UpdateBuffer(cameraBuffer, 0, GpuCameraUniform.Identity);
            }
            else
            {
                camera.Transform(animatedSourceVertices, drawVertices, displayMode, aspectCorrection, transparencyEnabled, shadingEnabled, placementYOffset);
                graphicsDevice.UpdateBuffer(vertexBuffer, 0, drawVertices);
            }
            if (gridEnabled)
            {
                camera.TransformGrid(gridSourceVertices, gridDrawVertices, aspectCorrection, placementYOffset);
                graphicsDevice.UpdateBuffer(gridBuffer, 0, gridDrawVertices);
            }
            if (outlineLevel > 0)
            {
                camera.TransformOutline(animatedSourceVertices, outlineVertices, aspectCorrection, outlineLevel, placementYOffset);
                graphicsDevice.UpdateBuffer(outlineBuffer, 0, outlineVertices);
            }

            commandList.Begin();
            commandList.SetFramebuffer(graphicsDevice.SwapchainFramebuffer);
            commandList.ClearColorTarget(0, GetBackgroundColor(backgroundMode));
            commandList.ClearDepthStencil(1f);
            if (gridEnabled)
            {
                commandList.SetPipeline(gridPipeline);
                commandList.SetVertexBuffer(0, gridBuffer);
                commandList.SetGraphicsResourceSet(0, textureResources.GetResourceSet(null));
                commandList.Draw((uint)gridDrawVertices.Length);
            }

            if (outlineLevel > 0)
            {
                commandList.SetPipeline(outlinePipeline);
                commandList.SetVertexBuffer(0, outlineBuffer);
                commandList.SetGraphicsResourceSet(0, textureResources.GetResourceSet(null));
                foreach (var batch in mesh.Batches.Where(batch => batch.HasEdge))
                    commandList.Draw((uint)batch.Count, 1, (uint)batch.Start, 0);
            }

            commandList.SetVertexBuffer(0, useGpuPipeline ? gpuVertexBuffer : vertexBuffer);
            foreach (var batch in mesh.Batches.Where(batch => !batch.IsTransparent || !transparencyEnabled))
            {
                commandList.SetPipeline(useGpuPipeline
                    ? backfaceCullingEnabled && !batch.IsDoubleSided
                        ? gpuOpaqueSingleSidedPipeline
                        : gpuOpaqueDoubleSidedPipeline
                    : backfaceCullingEnabled && !batch.IsDoubleSided
                        ? opaqueSingleSidedPipeline
                        : opaqueDoubleSidedPipeline);
                var texturePath = displayMode == DisplayMode.Texture ? batch.TexturePath : null;
                commandList.SetGraphicsResourceSet(0, textureResources.GetResourceSet(texturePath));
                if (useGpuPipeline)
                    commandList.SetGraphicsResourceSet(1, gpuSkinningSet);
                commandList.Draw((uint)batch.Count, 1, (uint)batch.Start, 0);
            }

            if (transparencyEnabled)
            {
                foreach (var batch in SortTransparentBatches(mesh.Batches, drawVertices, transparencySortMode))
                {
                    commandList.SetPipeline(useGpuPipeline
                        ? backfaceCullingEnabled && !batch.IsDoubleSided
                            ? gpuTransparentSingleSidedPipeline
                            : gpuTransparentDoubleSidedPipeline
                        : backfaceCullingEnabled && !batch.IsDoubleSided
                            ? transparentSingleSidedPipeline
                            : transparentDoubleSidedPipeline);
                    var texturePath = displayMode == DisplayMode.Texture ? batch.TexturePath : null;
                    commandList.SetGraphicsResourceSet(0, textureResources.GetResourceSet(texturePath));
                    if (useGpuPipeline)
                        commandList.SetGraphicsResourceSet(1, gpuSkinningSet);
                    commandList.Draw((uint)batch.Count, 1, (uint)batch.Start, 0);
                }
            }

            commandList.End();

            graphicsDevice.SubmitCommands(commandList);
            graphicsDevice.SwapBuffers();

            fpsFrameCount++;
            var fpsElapsed = fpsClock.Elapsed.TotalSeconds;
            if (fpsElapsed >= 0.5)
            {
                currentFps = fpsFrameCount / fpsElapsed;
                fpsFrameCount = 0;
                fpsClock.Restart();
                renderStatsLabel = BuildRenderStatsLabel(currentFps, useGpuPipeline, needsCpuSkinnedVertices, mesh.GpuFallbackVertexCount, mesh.GpuVertices.Length);
                UpdateWindowTitle(window, model, displayMode, transparencyEnabled, shadingEnabled, backfaceCullingEnabled, outlineLevel, placementMode, gridEnabled, backgroundMode, gpuSkinningPrepared, gpuCameraTest, renderStatsLabel);
            }
        }

        graphicsDevice.WaitForIdle();
        commandList.Dispose();
        gpuTransparentSingleSidedPipeline.Dispose();
        gpuTransparentDoubleSidedPipeline.Dispose();
        gpuOpaqueSingleSidedPipeline.Dispose();
        gpuOpaqueDoubleSidedPipeline.Dispose();
        gridPipeline.Dispose();
        outlinePipeline.Dispose();
        transparentSingleSidedPipeline.Dispose();
        transparentDoubleSidedPipeline.Dispose();
        opaqueSingleSidedPipeline.Dispose();
        opaqueDoubleSidedPipeline.Dispose();
        gpuSkinningSet.Dispose();
        gpuSkinningLayout.Dispose();
        foreach (var shader in shaders) shader.Dispose();
        foreach (var shader in gpuShaders) shader.Dispose();
        textureResources.Dispose();
        resourceLayout.Dispose();
        cameraBuffer.Dispose();
        gridBuffer.Dispose();
        outlineBuffer.Dispose();
        gpuVertexBuffer.Dispose();
        vertexBuffer.Dispose();
        graphicsDevice.Dispose();
    }

    private void RefreshAudio(MciAudioPlayer audioPlayer, ref string? loadedAudioPath, ref int loadedAudioVersion)
    {
        string? path;
        int version;
        lock (_gate)
        {
            path = _audioPath;
            version = _audioVersion;
        }

        if (version == loadedAudioVersion) return;
        loadedAudioVersion = version;

        if (string.IsNullOrWhiteSpace(path))
        {
            audioPlayer.Close();
            loadedAudioPath = null;
            return;
        }

        try
        {
            audioPlayer.Open(path);
            loadedAudioPath = path;
            _statusSink?.Invoke($"Audio loaded and paused: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            loadedAudioPath = null;
            _statusSink?.Invoke($"Audio load error: {ex.Message}");
        }
    }

    private static PreviewMesh BuildPreviewMesh(PmxModel model)
    {
        if (model.Vertices.Count == 0 || model.Indices.Count == 0)
        {
            var fallback = new[]
            {
                new PreviewVertex(new(-0.5f, -0.5f, 0), new(1, 0.2f, 0.2f, 1), new(1, 0.2f, 0.2f, 1), new(1, 0.2f, 0.2f, 1), Vector3.UnitZ, Vector2.Zero),
                new PreviewVertex(new(0.5f, -0.5f, 0), new(0.2f, 1, 0.2f, 1), new(0.2f, 1, 0.2f, 1), new(0.2f, 1, 0.2f, 1), Vector3.UnitZ, Vector2.UnitX),
                new PreviewVertex(new(0, 0.5f, 0), new(0.2f, 0.5f, 1, 1), new(0.2f, 0.5f, 1, 1), new(0.2f, 0.5f, 1, 1), Vector3.UnitZ, Vector2.UnitY)
            };
            return new PreviewMesh(
                fallback,
                fallback.Select(ToFallbackGpuVertex).ToArray(),
                [0, 0, 0],
                [new(0, 3, 0, null, false, true, true)],
                Vector3.Zero,
                1f,
                0f,
                fallback.Length);
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in model.Vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        var origin = new Vector3(
            (min.X + max.X) * 0.5f,
            min.Y,
            (min.Z + max.Z) * 0.5f);
        var size = max - min;
        var scale = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        if (scale <= 0.0001f) scale = 1;
        scale = 1.65f / scale;
        var centerPlacementYOffset = 0.82f - size.Y * scale * 0.5f;

        var result = new List<PreviewVertex>(model.Indices.Count);
        var sourceVertexIndices = new List<int>(model.Indices.Count);
        var batches = new List<DrawBatch>();
        var materialCursor = 0;
        var materialIndex = 0;
        foreach (var material in model.Materials)
        {
            var start = result.Count;
            var color = new Vector4(
                Math.Clamp(material.Diffuse.X, 0, 1),
                Math.Clamp(material.Diffuse.Y, 0, 1),
                Math.Clamp(material.Diffuse.Z, 0, 1),
                Math.Clamp(material.Diffuse.W, 0, 1));
            var debugColor = CreateMaterialDebugColor(materialIndex, color.W);

            for (int i = 0; i + 2 < material.IndexCount && materialCursor + i + 2 < model.Indices.Count; i += 3)
            {
                var i0 = model.Indices[materialCursor + i];
                var i1 = model.Indices[materialCursor + i + 1];
                var i2 = model.Indices[materialCursor + i + 2];
                if (!IsValidVertexIndex(i0, model.Vertices.Count) ||
                    !IsValidVertexIndex(i1, model.Vertices.Count) ||
                    !IsValidVertexIndex(i2, model.Vertices.Count))
                    continue;

                AddPreviewVertex(result, sourceVertexIndices, model.Vertices[i0], i0, origin, scale, color, debugColor);
                AddPreviewVertex(result, sourceVertexIndices, model.Vertices[i2], i2, origin, scale, color, debugColor);
                AddPreviewVertex(result, sourceVertexIndices, model.Vertices[i1], i1, origin, scale, color, debugColor);
            }

            var texturePath = ResolveTexturePath(model, material.TextureIndex);
            var count = result.Count - start;
            var isTransparent = color.W < 0.985f;
            if (count > 0 && isTransparent)
            {
                for (var triangleStart = start; triangleStart + 2 < result.Count; triangleStart += 3)
                    batches.Add(new DrawBatch(triangleStart, 3, materialIndex, texturePath, true, material.IsDoubleSided, material.HasEdge));
            }
            else if (count > 0)
            {
                batches.Add(new DrawBatch(start, count, materialIndex, texturePath, false, material.IsDoubleSided, material.HasEdge));
            }

            materialCursor += material.IndexCount;
            materialIndex++;
        }

        if (result.Count == 0)
        {
            var fallback = new[]
            {
                new PreviewVertex(new(-0.5f, -0.5f, 0), new(1, 0, 0, 1), new(1, 0, 0, 1), new(1, 0, 0, 1), Vector3.UnitZ, Vector2.Zero),
                new PreviewVertex(new(0.5f, -0.5f, 0), new(0, 1, 0, 1), new(0, 1, 0, 1), new(0, 1, 0, 1), Vector3.UnitZ, Vector2.UnitX),
                new PreviewVertex(new(0, 0.5f, 0), new(0, 0, 1, 1), new(0, 0, 1, 1), new(0, 0, 1, 1), Vector3.UnitZ, Vector2.UnitY)
            };
            return new PreviewMesh(fallback, fallback.Select(ToFallbackGpuVertex).ToArray(), [0, 0, 0], [new(0, 3, 0, null, false, true, true)], Vector3.Zero, 1f, 0f, fallback.Length);
        }

        var previewVertices = result.ToArray();
        var gpuVertices = BuildGpuVertices(previewVertices, sourceVertexIndices, model);
        var gpuFallbackVertexCount = gpuVertices.Count(vertex => !HasGpuSkinningWeights(vertex));
        return new PreviewMesh(previewVertices, gpuVertices, sourceVertexIndices.ToArray(), batches, origin, scale, centerPlacementYOffset, gpuFallbackVertexCount);
    }

    private static string? ResolveTexturePath(PmxModel model, int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= model.Textures.Count) return null;
        var path = model.Textures[textureIndex];
        if (string.IsNullOrWhiteSpace(path)) return null;
        var extension = Path.GetExtension(path);
        if (extension.Equals(".sph", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".spa", StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(path) ? path : null;
    }

    private static TextureResources CreateTextureResources(GraphicsDevice device, ResourceFactory factory, ResourceLayout layout, IReadOnlyList<DrawBatch> batches, Action<string>? statusSink)
    {
        var sampler = factory.CreateSampler(SamplerDescription.Linear);
        var whiteTexture = CreateSolidTexture(device, factory, 255, 255, 255, 255);
        var resourceSets = new Dictionary<string, ResourceSet>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = factory.CreateResourceSet(new ResourceSetDescription(layout, whiteTexture, sampler))
        };
        var textures = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = whiteTexture
        };

        foreach (var path in batches.Select(x => x.TexturePath).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var texture = LoadTexture(device, factory, path!);
                textures[path!] = texture;
                resourceSets[path!] = factory.CreateResourceSet(new ResourceSetDescription(layout, texture, sampler));
            }
            catch (Exception ex)
            {
                statusSink?.Invoke($"Texture fallback: {Path.GetFileName(path)} ({ex.Message})");
            }
        }

        return new TextureResources(sampler, textures, resourceSets);
    }

    private static Texture CreateSolidTexture(GraphicsDevice device, ResourceFactory factory, byte r, byte g, byte b, byte a)
    {
        var texture = factory.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
        device.UpdateTexture(texture, new[] { r, g, b, a }, 0, 0, 0, 1, 1, 1, 0, 0);
        return texture;
    }

    private static Texture LoadTexture(GraphicsDevice device, ResourceFactory factory, string path)
    {
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException($"Texture decode failed: {path}");
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Texture has invalid size.");
        var pixels = new byte[width * height * 4];
        var offset = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                pixels[offset++] = color.Red;
                pixels[offset++] = color.Green;
                pixels[offset++] = color.Blue;
                pixels[offset++] = color.Alpha;
            }
        }

        var texture = factory.CreateTexture(TextureDescription.Texture2D((uint)width, (uint)height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
        device.UpdateTexture(texture, pixels, 0, 0, 0, (uint)width, (uint)height, 1, 0, 0);
        return texture;
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        return value.LengthSquared() <= 0.000001f ? fallback : Vector3.Normalize(value);
    }

    private static bool IsValidVertexIndex(int index, int vertexCount)
    {
        return index >= 0 && index < vertexCount;
    }

    private static void AddPreviewVertex(List<PreviewVertex> result, List<int> sourceVertexIndices, PmxVertex vertex, int sourceVertexIndex, Vector3 origin, float scale, Vector4 color, Vector4 debugColor)
    {
        var p = (vertex.Position - origin) * scale + new Vector3(0, -0.82f, 0);
        var normal = SafeNormalize(vertex.Normal, Vector3.UnitZ);
        var skinDebugColor = vertex.Weight.Type switch
        {
            PmxWeightType.Bdef1 => new Vector4(1.0f, 0.12f, 0.08f, 1f),
            PmxWeightType.Bdef2 => new Vector4(1.0f, 0.72f, 0.12f, 1f),
            PmxWeightType.Bdef4 => new Vector4(0.2f, 0.9f, 0.35f, 1f),
            PmxWeightType.Sdef => new Vector4(0.12f, 0.78f, 1.0f, 1f),
            PmxWeightType.Qdef => new Vector4(0.8f, 0.35f, 1.0f, 1f),
            _ => new Vector4(0.12f, 0.18f, 0.32f, 1f)
        };
        result.Add(new PreviewVertex(p, color, debugColor, skinDebugColor, normal, vertex.UV));
        sourceVertexIndices.Add(sourceVertexIndex);
    }

    private static PreviewVertex ToPreviewVertex(PmxSkinnedVertex vertex, PreviewVertex template, Vector3 origin, float scale)
    {
        var p = (vertex.Position - origin) * scale + new Vector3(0, -0.82f, 0);
        return template with
        {
            Position = p,
            Normal = SafeNormalize(vertex.Normal, Vector3.UnitZ)
        };
    }

    private static GpuPreviewVertex[] BuildGpuVertices(IReadOnlyList<PreviewVertex> previewVertices, IReadOnlyList<int> sourceVertexIndices, PmxModel model)
    {
        var result = new GpuPreviewVertex[previewVertices.Count];
        for (var i = 0; i < previewVertices.Count; i++)
        {
            var preview = previewVertices[i];
            var sourceIndex = i < sourceVertexIndices.Count ? sourceVertexIndices[i] : -1;
            if ((uint)sourceIndex >= (uint)model.Vertices.Count)
            {
                result[i] = ToFallbackGpuVertex(preview);
                continue;
            }

            var weight = model.Vertices[sourceIndex].Weight;
            if (!IsGpuLinearSkinningWeight(weight.Type) ||
                !TryCreateGpuSkinningWeights(weight, model.Bones.Count, out var boneIndices, out var boneWeights))
            {
                result[i] = ToFallbackGpuVertex(preview);
                continue;
            }

            result[i] = new GpuPreviewVertex(preview.Position, preview.Color, preview.DebugColor, preview.SkinDebugColor, preview.Normal, preview.TexCoord, boneIndices, boneWeights);
        }

        return result;
    }

    private static bool IsGpuLinearSkinningWeight(PmxWeightType type)
    {
        return type is PmxWeightType.Bdef1 or PmxWeightType.Bdef2 or PmxWeightType.Bdef4 or PmxWeightType.Sdef or PmxWeightType.Qdef;
    }

    private static bool TryCreateGpuSkinningWeights(PmxVertexWeight weight, int boneCount, out Vector4 boneIndices, out Vector4 boneWeights)
    {
        var weight0 = IsValidBoneIndex(weight.Bone0, boneCount) ? MathF.Max(weight.Weight0, 0f) : 0f;
        var weight1 = IsValidBoneIndex(weight.Bone1, boneCount) ? MathF.Max(weight.Weight1, 0f) : 0f;
        var weight2 = IsValidBoneIndex(weight.Bone2, boneCount) ? MathF.Max(weight.Weight2, 0f) : 0f;
        var weight3 = IsValidBoneIndex(weight.Bone3, boneCount) ? MathF.Max(weight.Weight3, 0f) : 0f;
        var sum = weight0 + weight1 + weight2 + weight3;
        if (sum <= 0.000001f)
        {
            boneIndices = Vector4.Zero;
            boneWeights = Vector4.Zero;
            return false;
        }

        boneIndices = new Vector4(
            SafeBoneIndex(weight.Bone0, boneCount),
            SafeBoneIndex(weight.Bone1, boneCount),
            SafeBoneIndex(weight.Bone2, boneCount),
            SafeBoneIndex(weight.Bone3, boneCount));
        boneWeights = new Vector4(weight0, weight1, weight2, weight3) / sum;
        return true;
    }

    private static GpuPreviewVertex ToFallbackGpuVertex(PreviewVertex preview)
    {
        return new GpuPreviewVertex(preview.Position, preview.Color, preview.DebugColor, preview.SkinDebugColor, preview.Normal, preview.TexCoord, Vector4.Zero, Vector4.Zero);
    }

    private static void FillGpuPassThroughVertices(IReadOnlyList<PreviewVertex> source, GpuPreviewVertex[] destination)
    {
        var count = Math.Min(source.Count, destination.Length);
        for (var i = 0; i < count; i++)
            destination[i] = ToFallbackGpuVertex(source[i]);
    }

    private static void FillGpuLinearSkinningVertices(IReadOnlyList<PreviewVertex> cpuTransformedVertices, IReadOnlyList<GpuPreviewVertex> gpuSourceVertices, GpuPreviewVertex[] destination)
    {
        var count = Math.Min(Math.Min(cpuTransformedVertices.Count, gpuSourceVertices.Count), destination.Length);
        for (var i = 0; i < count; i++)
        {
            var gpuSource = gpuSourceVertices[i];
            destination[i] = HasGpuSkinningWeights(gpuSource)
                ? gpuSource
                : ToFallbackGpuVertex(cpuTransformedVertices[i]);
        }
    }

    private static bool HasGpuSkinningWeights(GpuPreviewVertex vertex)
    {
        return vertex.BoneWeights.X + vertex.BoneWeights.Y + vertex.BoneWeights.Z + vertex.BoneWeights.W > 0.000001f;
    }

    private static float SafeBoneIndex(int boneIndex, int boneCount)
    {
        return (uint)boneIndex < (uint)boneCount ? boneIndex : 0;
    }

    private static bool IsValidBoneIndex(int boneIndex, int boneCount)
    {
        return (uint)boneIndex < (uint)boneCount;
    }

    private static Vector4 NormalizeWeights(Vector4 weights)
    {
        weights = Vector4.Max(weights, Vector4.Zero);
        var sum = weights.X + weights.Y + weights.Z + weights.W;
        return sum > 0.000001f
            ? weights / sum
            : new Vector4(1, 0, 0, 0);
    }

    private static PreviewVertex[] CreateGridVertices()
    {
        var result = new List<PreviewVertex>();
        const int halfLineCount = 10;
        const float step = 0.18f;
        const float y = -0.82f;
        var minor = new Vector4(0.33f, 0.38f, 0.44f, 0.42f);
        var major = new Vector4(0.52f, 0.58f, 0.66f, 0.62f);
        for (var i = -halfLineCount; i <= halfLineCount; i++)
        {
            var v = i * step;
            var color = i == 0 ? major : minor;
            result.Add(new PreviewVertex(new Vector3(-halfLineCount * step, y, v), color, color, color, Vector3.Zero, Vector2.Zero));
            result.Add(new PreviewVertex(new Vector3(halfLineCount * step, y, v), color, color, color, Vector3.Zero, Vector2.Zero));
            result.Add(new PreviewVertex(new Vector3(v, y, -halfLineCount * step), color, color, color, Vector3.Zero, Vector2.Zero));
            result.Add(new PreviewVertex(new Vector3(v, y, halfLineCount * step), color, color, color, Vector3.Zero, Vector2.Zero));
        }

        return result.ToArray();
    }

    private static float EstimateBatchDepth(PreviewVertex[] vertices, DrawBatch batch)
    {
        if (batch.Count <= 0) return 0;
        var sum = 0f;
        var end = Math.Min(vertices.Length, batch.Start + batch.Count);
        for (var i = batch.Start; i < end; i++)
            sum += vertices[i].Position.Z;
        return sum / Math.Max(1, end - batch.Start);
    }

    private static IEnumerable<DrawBatch> SortTransparentBatches(
        IReadOnlyList<DrawBatch> batches,
        PreviewVertex[] vertices,
        TransparencySortMode sortMode)
    {
        return sortMode switch
        {
            TransparencySortMode.MaterialOrder => batches.Where(batch => batch.IsTransparent).OrderBy(batch => batch.MaterialIndex).ThenBy(batch => batch.Start),
            _ => batches.Where(batch => batch.IsTransparent).OrderByDescending(batch => EstimateBatchDepth(vertices, batch))
        };
    }

    private static Vector4 CreateMaterialDebugColor(int materialIndex, float alpha)
    {
        var hue = (materialIndex * 0.61803398875f) % 1f;
        var color = HsvToRgb(hue, 0.72f, 0.95f);
        return new Vector4(color.X, color.Y, color.Z, Math.Clamp(alpha, 0.35f, 1f));
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

    private static string GetOutlineLabel(int outlineLevel)
    {
        return outlineLevel switch
        {
            1 => "Thin",
            2 => "Normal",
            3 => "Thick",
            _ => "Off"
        };
    }

    private static string GetBackgroundLabel(int backgroundMode)
    {
        return backgroundMode switch
        {
            1 => "Gray",
            2 => "Light",
            3 => "Blue",
            _ => "Dark"
        };
    }

    private static RgbaFloat GetBackgroundColor(int backgroundMode)
    {
        return backgroundMode switch
        {
            1 => new RgbaFloat(0.24f, 0.25f, 0.27f, 1f),
            2 => new RgbaFloat(0.72f, 0.74f, 0.78f, 1f),
            3 => new RgbaFloat(0.08f, 0.12f, 0.20f, 1f),
            _ => new RgbaFloat(0.05f, 0.07f, 0.10f, 1f)
        };
    }

    private static string BuildRenderStatsLabel(double fps, bool useGpuPipeline, bool needsCpuSkinnedVertices, int gpuFallbackVertexCount, int gpuVertexCount)
    {
        var cpuVerticesLabel = useGpuPipeline
            ? needsCpuSkinnedVertices ? "needed" : "skipped"
            : "active";
        var fallbackRate = gpuVertexCount > 0
            ? gpuFallbackVertexCount * 100.0 / gpuVertexCount
            : 0.0;
        return $"FPS:{fps:0.0} | CPUVerts:{cpuVerticesLabel} | Fallback:{fallbackRate:0.0}%";
    }

    private static void UpdateWindowTitle(
        Sdl2Window window,
        PmxModel model,
        DisplayMode displayMode,
        bool transparencyEnabled,
        bool shadingEnabled,
        bool backfaceCullingEnabled,
        int outlineLevel,
        PlacementMode placementMode,
        bool gridEnabled,
        int backgroundMode,
        bool gpuSkinningEnabled,
        bool gpuCameraTest,
        string? renderStatsLabel = null)
    {
        var skinLabel = gpuCameraTest
            ? "GPU-LBSDebug"
            : gpuSkinningEnabled
                ? "GPU-LBS"
                : "CPU";
        var stats = string.IsNullOrWhiteSpace(renderStatsLabel)
            ? string.Empty
            : $" | {renderStatsLabel}";
        window.Title =
            $"Simple PMX Viewer - {model.Name} | {displayMode} | Skin:{skinLabel} | Place:{placementMode} | Alpha:{OnOff(transparencyEnabled)} | Shade:{(shadingEnabled ? "On" : "Flat")} | Cull:{OnOff(backfaceCullingEnabled)} | Edge:{GetOutlineLabel(outlineLevel)} | Grid:{OnOff(gridEnabled)} | Bg:{GetBackgroundLabel(backgroundMode)}{stats}";
    }

    private static string OnOff(bool value) => value ? "On" : "Off";

    private const string VertexShader = """
        #version 450
        layout(location = 0) in vec3 Position;
        layout(location = 1) in vec4 Color;
        layout(location = 2) in vec4 DebugColor;
        layout(location = 3) in vec4 SkinDebugColor;
        layout(location = 4) in vec3 Normal;
        layout(location = 5) in vec2 TexCoord;
        layout(location = 0) out vec4 fsin_Color;
        layout(location = 1) out vec4 fsin_DebugColor;
        layout(location = 2) out vec3 fsin_Normal;
        layout(location = 3) out vec2 fsin_TexCoord;
        void main()
        {
            gl_Position = vec4(Position, 1.0);
            fsin_Color = Color;
            fsin_DebugColor = DebugColor;
            fsin_Normal = Normal;
            fsin_TexCoord = TexCoord;
        }
        """;

    private const string GpuSkinningVertexShader = """
        #version 450
        layout(location = 0) in vec3 Position;
        layout(location = 1) in vec4 Color;
        layout(location = 2) in vec4 DebugColor;
        layout(location = 3) in vec4 SkinDebugColor;
        layout(location = 4) in vec3 Normal;
        layout(location = 5) in vec2 TexCoord;
        layout(location = 6) in vec4 BoneIndices;
        layout(location = 7) in vec4 BoneWeights;

        struct BoneMatrix
        {
            vec4 Row0;
            vec4 Row1;
            vec4 Row2;
            vec4 Row3;
        };

        struct BoneMatrices
        {
            BoneMatrix Position;
            BoneMatrix Normal;
        };

        layout(set = 1, binding = 0) uniform CameraBlock
        {
            vec4 CameraColumn0;
            vec4 CameraColumn1;
            vec4 CameraColumn2;
            vec4 CameraColumn3;
        };

        layout(std430, set = 1, binding = 1) readonly buffer BoneBlock
        {
            BoneMatrices Bones[];
        };

        layout(location = 0) out vec4 fsin_Color;
        layout(location = 1) out vec4 fsin_DebugColor;
        layout(location = 2) out vec3 fsin_Normal;
        layout(location = 3) out vec2 fsin_TexCoord;

        vec4 transformPoint(vec3 p, BoneMatrix m)
        {
            vec4 v = vec4(p, 1.0);
            return vec4(
                dot(v, vec4(m.Row0.x, m.Row1.x, m.Row2.x, m.Row3.x)),
                dot(v, vec4(m.Row0.y, m.Row1.y, m.Row2.y, m.Row3.y)),
                dot(v, vec4(m.Row0.z, m.Row1.z, m.Row2.z, m.Row3.z)),
                dot(v, vec4(m.Row0.w, m.Row1.w, m.Row2.w, m.Row3.w)));
        }

        vec3 transformNormal(vec3 n, BoneMatrix m)
        {
            vec4 v = vec4(n, 0.0);
            return vec3(
                dot(v, vec4(m.Row0.x, m.Row1.x, m.Row2.x, m.Row3.x)),
                dot(v, vec4(m.Row0.y, m.Row1.y, m.Row2.y, m.Row3.y)),
                dot(v, vec4(m.Row0.z, m.Row1.z, m.Row2.z, m.Row3.z)));
        }

        vec4 transformCamera(vec3 p)
        {
            vec4 v = vec4(p, 1.0);
            return vec4(
                dot(v, CameraColumn0),
                dot(v, CameraColumn1),
                dot(v, CameraColumn2),
                dot(v, CameraColumn3));
        }

        void main()
        {
            vec4 weights = max(BoneWeights, vec4(0.0));
            float totalWeight = dot(weights, vec4(1.0));
            if (totalWeight > 0.000001)
            {
                ivec4 indices = ivec4(BoneIndices + vec4(0.5));
                weights /= totalWeight;
                vec4 skinnedPosition =
                    transformPoint(Position, Bones[indices.x].Position) * weights.x +
                    transformPoint(Position, Bones[indices.y].Position) * weights.y +
                    transformPoint(Position, Bones[indices.z].Position) * weights.z +
                    transformPoint(Position, Bones[indices.w].Position) * weights.w;
                vec3 skinnedNormal =
                    transformNormal(Normal, Bones[indices.x].Normal) * weights.x +
                    transformNormal(Normal, Bones[indices.y].Normal) * weights.y +
                    transformNormal(Normal, Bones[indices.z].Normal) * weights.z +
                    transformNormal(Normal, Bones[indices.w].Normal) * weights.w;
                gl_Position = vec4(skinnedPosition.xyz / max(skinnedPosition.w, 0.000001), 1.0);
                fsin_Normal = normalize(skinnedNormal);
            }
            else
            {
                gl_Position = vec4(Position, 1.0);
                fsin_Normal = Normal;
            }
            fsin_Color = Color;
            fsin_DebugColor = DebugColor;
            fsin_TexCoord = TexCoord;
        }
        """;

    private const string FragmentShader = """
        #version 450
        layout(set = 0, binding = 0) uniform texture2D SurfaceTexture;
        layout(set = 0, binding = 1) uniform sampler SurfaceSampler;
        layout(location = 0) in vec4 fsin_Color;
        layout(location = 1) in vec4 fsin_DebugColor;
        layout(location = 2) in vec3 fsin_Normal;
        layout(location = 3) in vec2 fsin_TexCoord;
        layout(location = 0) out vec4 fsout_Color;
        void main()
        {
            vec2 safeTexCoord = fsin_TexCoord;
            vec4 tex = texture(sampler2D(SurfaceTexture, SurfaceSampler), safeTexCoord);
            float alpha = fsin_Color.a * tex.a;
            if (alpha < 0.08)
                discard;
            float shade = 1.0;
            if (dot(fsin_Normal, fsin_Normal) > 0.0001)
            {
                vec3 normal = normalize(fsin_Normal);
                vec3 lightDir = normalize(vec3(-0.35, 0.75, 0.45));
                float diffuse = abs(dot(normal, lightDir));
                shade = 0.66 + diffuse * 0.34;
            }
            fsout_Color = vec4(fsin_Color.rgb * tex.rgb * shade, alpha);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PreviewVertex(Vector3 Position, Vector4 Color, Vector4 DebugColor, Vector4 SkinDebugColor, Vector3 Normal, Vector2 TexCoord)
    {
        public const uint SizeInBytes = 80;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GpuPreviewVertex(Vector3 Position, Vector4 Color, Vector4 DebugColor, Vector4 SkinDebugColor, Vector3 Normal, Vector2 TexCoord, Vector4 BoneIndices, Vector4 BoneWeights)
    {
        public const uint SizeInBytes = 112;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GpuCameraUniform(Vector4 Column0, Vector4 Column1, Vector4 Column2, Vector4 Column3)
    {
        public const uint SizeInBytes = 64;

        public static GpuCameraUniform Identity { get; } = FromMatrix(Matrix4x4.Identity);

        public static GpuCameraUniform FromMatrix(Matrix4x4 matrix)
        {
            return new GpuCameraUniform(
                new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41),
                new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42),
                new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43),
                new Vector4(matrix.M14, matrix.M24, matrix.M34, matrix.M44));
        }
    }

    private sealed record PreviewMesh(
        PreviewVertex[] Vertices,
        GpuPreviewVertex[] GpuVertices,
        int[] SourceVertexIndices,
        IReadOnlyList<DrawBatch> Batches,
        Vector3 Origin,
        float Scale,
        float CenterPlacementYOffset,
        int GpuFallbackVertexCount);
    private sealed record DrawBatch(int Start, int Count, int MaterialIndex, string? TexturePath, bool IsTransparent, bool IsDoubleSided, bool HasEdge);
    private enum DisplayMode
    {
        Texture,
        MaterialColor,
        MaterialIdColor,
        NormalDebug,
        SolidWhite,
        Bdef1Debug
    }

    private enum TransparencySortMode
    {
        BackToFront,
        MaterialOrder
    }

    private enum PlacementMode
    {
        Foot,
        Center
    }

    private sealed class MotionPlayback(
        PmxModel model,
        VmdMotion? initialMotion,
        PreviewMesh mesh,
        PreviewVertex[] animatedVertices,
        Action<string>? statusSink)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private VmdMotion? _motion = initialMotion;
        private VmdBoneBinding? _binding = initialMotion is null ? null : VmdBoneBinder.BindForPlayback(model, initialMotion);
        private uint _lastFrame = uint.MaxValue;
        private readonly double[] _speedSteps = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
        private int _speedStepIndex = 3;
        private double _lastClockSeconds;
        private double _playbackSeconds;
        private bool _paused = true;
        private bool _loopEnabled = true;
        private bool _copiedStaticPose;
        private PmxPose? _currentPose;
        private uint _lastAnimatedFrame = uint.MaxValue;

        public bool IsPaused => _paused;

        public bool LoopEnabled => _loopEnabled;

        public double Speed => _speedSteps[_speedStepIndex];

        public double CurrentSeconds => _playbackSeconds;

        public uint CurrentFrame => GetCurrentFrame();

        public double CurrentCameraFrame => GetCurrentCameraFrame();

        public uint LastFrame => _motion?.LastFrame ?? 0;

        public bool HasCameraFrames => _motion?.CameraFrames.Count > 0;

        public PmxPose? CurrentPose => _currentPose;

        public VmdCameraPose? CurrentCameraPose => _motion is null || _motion.CameraFrames.Count == 0
            ? null
            : VmdCameraSampler.Sample(_motion.CameraFrames, CurrentCameraFrame);

        public void ForceRefresh()
        {
            _lastFrame = uint.MaxValue;
            _lastAnimatedFrame = uint.MaxValue;
            _copiedStaticPose = false;
            _currentPose = null;
        }

        public string ReloadBinding()
        {
            _binding = _motion is null ? null : VmdBoneBinder.BindForPlayback(model, _motion);
            ForceRefresh();
            return _binding is null
                ? "(no VMD loaded)"
                : $"({_binding.MatchedBoneNameCount:N0}/{_binding.TotalAnimatedBoneNames:N0} active-profile bones, {_binding.MatchedBoneFrames:N0}/{_binding.TotalBoneFrames:N0} frames; {FormatActiveBoneNames(_binding)})";
        }

        private static string FormatActiveBoneNames(VmdBoneBinding binding)
        {
            if (binding.BoundBones.Count == 0)
                return "no active bones";

            const int maxNames = 8;
            var names = binding.BoundBones
                .OrderBy(x => x.BoneName, StringComparer.Ordinal)
                .Select(x => x.BoneName)
                .Take(maxNames)
                .ToArray();
            var suffix = binding.BoundBones.Count > maxNames
                ? $", +{binding.BoundBones.Count - maxNames:N0}"
                : string.Empty;
            return string.Join(", ", names) + suffix;
        }

        public void TogglePaused()
        {
            AdvancePlaybackTime();
            _paused = !_paused;
            ForceRefresh();
        }

        public void ChangeSpeedStep(int delta)
        {
            AdvancePlaybackTime();
            _speedStepIndex = Math.Clamp(_speedStepIndex + delta, 0, _speedSteps.Length - 1);
            ForceRefresh();
        }

        public void ToggleLoop()
        {
            AdvancePlaybackTime();
            _loopEnabled = !_loopEnabled;
            ForceRefresh();
        }

        public void StepFrames(int frameDelta)
        {
            AdvancePlaybackTime();
            _paused = true;
            var frameCount = _motion is null ? 1 : Math.Max(1, (int)_motion.LastFrame + 1);
            var currentFrame = (int)CurrentFrame;
            var nextFrame = _loopEnabled
                ? ((currentFrame + frameDelta) % frameCount + frameCount) % frameCount
                : Math.Clamp(currentFrame + frameDelta, 0, frameCount - 1);
            _playbackSeconds = nextFrame / 30.0;
            ForceRefresh();
        }

        public void SeekStart()
        {
            AdvancePlaybackTime();
            _paused = true;
            _playbackSeconds = 0.0;
            ForceRefresh();
        }

        public void SeekEnd()
        {
            AdvancePlaybackTime();
            _paused = true;
            _playbackSeconds = LastFrame / 30.0;
            ForceRefresh();
        }

        public void Update(VeldridRenderer owner, PmxIkMode ikMode, bool appendEnabled, PmxIkProfile ikProfile, bool motionEnabled, bool updateAnimatedVertices)
        {
            RefreshMotion(owner);
            AdvancePlaybackTime();

            if (!motionEnabled || _motion is null || _binding is null || _binding.BoundBones.Count == 0 || _motion.LastFrame == 0)
            {
                _currentPose ??= PmxPoseBuilder.BuildRestPose(model);
                if (updateAnimatedVertices && !_copiedStaticPose)
                {
                    Array.Copy(mesh.Vertices, animatedVertices, mesh.Vertices.Length);
                    _copiedStaticPose = true;
                }

                return;
            }

            var frame = (uint)((_playbackSeconds * 30.0) % Math.Max(1, _motion.LastFrame + 1));
            if (frame != _lastFrame)
            {
                var sample = VmdPoseSampler.Sample(_binding, frame);
                _currentPose = PmxPoseBuilder.Build(model, sample, ikMode, appendEnabled, ikProfile);
                _lastFrame = frame;
            }

            if (!updateAnimatedVertices || frame == _lastAnimatedFrame || _currentPose is null)
                return;

            var skinning = PmxCpuSkinner.Skin(model, _currentPose);

            for (var i = 0; i < animatedVertices.Length; i++)
            {
                var sourceVertexIndex = mesh.SourceVertexIndices[i];
                if ((uint)sourceVertexIndex >= (uint)skinning.Vertices.Count)
                {
                    animatedVertices[i] = mesh.Vertices[i];
                    continue;
                }

                animatedVertices[i] = ToPreviewVertex(
                    skinning.Vertices[sourceVertexIndex],
                    mesh.Vertices[i],
                    mesh.Origin,
                    mesh.Scale);
            }

            _lastAnimatedFrame = frame;
        }

        private void RefreshMotion(VeldridRenderer owner)
        {
            VmdMotion? current;
            lock (owner._gate)
            {
                current = owner._motion;
            }

            if (ReferenceEquals(current, _motion)) return;

            _motion = current;
            _binding = current is null ? null : VmdBoneBinder.BindForPlayback(model, current);
            _lastFrame = uint.MaxValue;
            _lastClockSeconds = 0.0;
            _playbackSeconds = 0.0;
            _paused = true;
            _copiedStaticPose = false;
            _currentPose = null;
            _lastAnimatedFrame = uint.MaxValue;
            _clock.Restart();

            if (current is not null && _binding is not null)
                statusSink?.Invoke($"VMD loaded and paused: {_binding.MatchedBoneNameCount:N0}/{_binding.TotalAnimatedBoneNames:N0} bones matched, {current.CameraFrames.Count:N0} camera frames. Press Space to play.");
        }

        private void AdvancePlaybackTime()
        {
            var now = _clock.Elapsed.TotalSeconds;
            var delta = Math.Max(0.0, now - _lastClockSeconds);
            _lastClockSeconds = now;
            if (!_paused)
                _playbackSeconds += delta * Speed;

            if (!_loopEnabled && _motion is not null && _motion.LastFrame > 0 && CurrentFrame >= _motion.LastFrame)
            {
                _playbackSeconds = _motion.LastFrame / 30.0;
                _paused = true;
            }
        }

        private uint GetCurrentFrame()
        {
            if (_motion is null || _motion.LastFrame == 0)
                return 0;

            var frameCount = Math.Max(1, _motion.LastFrame + 1);
            var frame = (uint)Math.Max(0.0, _playbackSeconds * 30.0);
            return _loopEnabled ? frame % frameCount : Math.Min(frame, _motion.LastFrame);
        }

        private double GetCurrentCameraFrame()
        {
            if (_motion is null || _motion.LastFrame == 0)
                return 0.0;

            var frameCount = Math.Max(1, _motion.LastFrame + 1);
            var frame = Math.Max(0.0, _playbackSeconds * 30.0);
            return _loopEnabled ? frame % frameCount : Math.Min(frame, _motion.LastFrame);
        }
    }

    private sealed class TextureResources(Sampler sampler, Dictionary<string, Texture> textures, Dictionary<string, ResourceSet> resourceSets) : IDisposable
    {
        public ResourceSet GetResourceSet(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && resourceSets.TryGetValue(path, out var set))
                return set;
            return resourceSets[""];
        }

        public void Dispose()
        {
            foreach (var set in resourceSets.Values) set.Dispose();
            foreach (var texture in textures.Values) texture.Dispose();
            sampler.Dispose();
        }
    }

    private sealed class OrbitCamera
    {
        private const float FrontYawOffset = MathF.PI;

        private float _yaw = FrontYawOffset;
        private float _pitch;
        private float _roll;
        private float _zoom = 1.0f;
        private bool _hasSmoothedVmdZoom;
        private Vector2 _pan;
        private Vector3 _target;
        private Vector2 _lastMouse;
        private DragMode _dragMode = DragMode.None;

        public void Update(InputSnapshot input, bool allowManualInput = true)
        {
            foreach (var keyEvent in input.KeyEvents)
            {
                if (keyEvent.Down && keyEvent.Key == Key.R)
                    Reset();
            }

            if (!allowManualInput)
                return;

            if (input.WheelDelta != 0)
                _zoom = Math.Clamp(_zoom + input.WheelDelta * 0.04f, 0.35f, 2.6f);

            var mouse = input.MousePosition;
            var nextDragMode = input.IsMouseDown(MouseButton.Left)
                ? DragMode.Rotate
                : input.IsMouseDown(MouseButton.Right)
                    ? DragMode.Pan
                    : DragMode.None;

            if (nextDragMode != DragMode.None)
            {
                if (_dragMode == nextDragMode)
                {
                    var delta = mouse - _lastMouse;
                    if (nextDragMode == DragMode.Rotate)
                    {
                        _yaw += delta.X * 0.0045f;
                        _pitch += delta.Y * 0.0045f;
                        _pitch = Math.Clamp(_pitch, -1.45f, 1.45f);
                    }
                    else
                    {
                        _pan += new Vector2(delta.X * 0.0024f, -delta.Y * 0.0024f);
                    }
                }

                _dragMode = nextDragMode;
                _lastMouse = mouse;
            }
            else
            {
                _dragMode = DragMode.None;
            }
        }

        public void ApplyVmdCamera(VmdCameraPose pose, Vector3 origin, float scale)
        {
            _target = (pose.Position - origin) * scale + new Vector3(0, -0.82f, 0);
            _pitch = Math.Clamp(pose.Rotation.X, -1.45f, 1.45f);
            _yaw = pose.Rotation.Y + FrontYawOffset;
            _roll = pose.Rotation.Z;
            _pan = Vector2.Zero;

            var fov = Math.Clamp(pose.FieldOfView <= 0 ? 45f : pose.FieldOfView, 10f, 90f);
            var fovZoom = 45f / fov;
            var distanceZoom = 1.25f / (1f + MathF.Abs(pose.Distance) * scale * 0.10f);
            var targetZoom = Math.Clamp(fovZoom * distanceZoom, 0.25f, 3.0f);
            _zoom = SmoothVmdZoom(targetZoom);
        }

        public void Transform(PreviewVertex[] source, PreviewVertex[] destination, DisplayMode displayMode, float aspectCorrection, bool transparencyEnabled, bool shadingEnabled, float placementYOffset)
        {
            var rotation = GetRotationMatrix();
            for (int i = 0; i < source.Length; i++)
            {
                var p = Vector3.Transform(source[i].Position + new Vector3(0, placementYOffset, 0) - _target, rotation) * _zoom;
                var n = Vector3.Normalize(Vector3.TransformNormal(source[i].Normal, rotation));
                var color = displayMode switch
                {
                    DisplayMode.NormalDebug => new Vector4(MathF.Abs(source[i].Normal.X), MathF.Abs(source[i].Normal.Y), MathF.Abs(source[i].Normal.Z), 1f),
                    DisplayMode.SolidWhite => Vector4.One,
                    DisplayMode.MaterialIdColor => source[i].DebugColor,
                    DisplayMode.Bdef1Debug => source[i].SkinDebugColor,
                    _ => new Vector4(source[i].Color.X, source[i].Color.Y, source[i].Color.Z, transparencyEnabled ? source[i].Color.W : 1f)
                };
                destination[i] = source[i] with { Position = new Vector3(p.X * aspectCorrection + _pan.X, p.Y + _pan.Y, p.Z * 0.25f + 0.5f), Normal = shadingEnabled ? n : Vector3.Zero, Color = color };
            }
        }

        public void TransformOutline(PreviewVertex[] source, PreviewVertex[] destination, float aspectCorrection, int outlineLevel, float placementYOffset)
        {
            var rotation = GetRotationMatrix();
            var width = outlineLevel switch
            {
                1 => 0.010f,
                2 => 0.018f,
                3 => 0.030f,
                _ => 0f
            };
            for (int i = 0; i < source.Length; i++)
            {
                var expanded = source[i].Position + new Vector3(0, placementYOffset, 0) - _target + SafeNormalize(source[i].Normal, Vector3.UnitZ) * width;
                var p = Vector3.Transform(expanded, rotation) * _zoom;
                destination[i] = source[i] with
                {
                    Position = new Vector3(p.X * aspectCorrection + _pan.X, p.Y + _pan.Y, p.Z * 0.25f + 0.502f),
                    Normal = Vector3.Zero,
                    Color = new Vector4(0.015f, 0.018f, 0.022f, 1f)
                };
            }
        }

        public void TransformGrid(PreviewVertex[] source, PreviewVertex[] destination, float aspectCorrection, float placementYOffset)
        {
            var rotation = GetRotationMatrix();
            for (int i = 0; i < source.Length; i++)
            {
                var p = Vector3.Transform(source[i].Position + new Vector3(0, placementYOffset, 0) - _target, rotation) * _zoom;
                destination[i] = source[i] with
                {
                    Position = new Vector3(p.X * aspectCorrection + _pan.X, p.Y + _pan.Y, p.Z * 0.25f + 0.5f),
                    Normal = Vector3.Zero
                };
            }
        }

        public GpuCameraUniform CreateGpuUniform(float aspectCorrection, float placementYOffset)
        {
            return GpuCameraUniform.FromMatrix(CreateGpuMatrix(aspectCorrection, placementYOffset));
        }

        public Matrix4x4 CreateGpuMatrix(float aspectCorrection, float placementYOffset)
        {
            var matrix =
                Matrix4x4.CreateTranslation(new Vector3(0, placementYOffset, 0)) *
                Matrix4x4.CreateTranslation(-_target) *
                GetRotationMatrix() *
                Matrix4x4.CreateScale(_zoom) *
                Matrix4x4.CreateScale(aspectCorrection, 1f, 0.25f) *
                Matrix4x4.CreateTranslation(_pan.X, _pan.Y, 0.5f);
            return matrix;
        }

        public Matrix4x4 CreateGpuNormalMatrix()
        {
            return GetRotationMatrix();
        }

        private void Reset()
        {
            _yaw = FrontYawOffset;
            _pitch = 0f;
            _roll = 0f;
            _zoom = 1.0f;
            _hasSmoothedVmdZoom = false;
            _pan = Vector2.Zero;
            _target = Vector3.Zero;
        }

        private float SmoothVmdZoom(float targetZoom)
        {
            if (!_hasSmoothedVmdZoom || MathF.Abs(targetZoom - _zoom) > 0.8f)
            {
                _hasSmoothedVmdZoom = true;
                return targetZoom;
            }

            return float.Lerp(_zoom, targetZoom, 0.28f);
        }

        private Matrix4x4 GetRotationMatrix()
        {
            return Matrix4x4.CreateRotationX(_pitch) *
                   Matrix4x4.CreateRotationY(_yaw) *
                   Matrix4x4.CreateRotationZ(_roll);
        }

        private enum DragMode
        {
            None,
            Rotate,
            Pan
        }
    }
}
