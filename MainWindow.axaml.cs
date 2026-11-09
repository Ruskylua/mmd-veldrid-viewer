using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SimplePmxVeldrid.Importing;
using SimplePmxVeldrid.Rendering;

namespace SimplePmxVeldrid;

public sealed partial class MainWindow : Window
{
    private readonly VeldridRenderer _renderer = new();
    private readonly string? _initialPmxPath;
    private PmxModel? _currentModel;
    private VmdMotion? _currentMotion;
    private string? _currentAudioPath;
    private bool _initialPickerShown;

    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(string? initialPmxPath)
    {
        _initialPmxPath = initialPmxPath;
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);

        _renderer.SetStatusSink(message =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText.Text = message;
                RenderInfoText.Text = message;
            });
        });

        Opened += (_, _) =>
        {
            if (_initialPickerShown) return;
            _initialPickerShown = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (!string.IsNullOrWhiteSpace(_initialPmxPath))
                    LoadPmx(_initialPmxPath);
                else
                    await OpenPmxWorkflowAsync();
            }, DispatcherPriority.Background);
        };

        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                await OpenPmxWorkflowAsync();
            }
        };

        RenderHost.PointerPressed += async (_, _) => await OpenPmxWorkflowAsync();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OpenButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await OpenPmxWorkflowAsync();
    }

    private async void OpenVmdButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await OpenVmdWorkflowAsync();
    }

    private async void OpenAudioButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await OpenAudioWorkflowAsync();
    }

    private async Task OpenPmxWorkflowAsync()
    {
        try
        {
            var path = await PickPmxFileAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText.Text = "File selection canceled";
                return;
            }

            LoadPmx(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = "File selection error";
            RenderInfoText.Text = ex.Message;
        }
    }

    private async Task<string?> PickPmxFileAsync()
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var storage = topLevel?.StorageProvider;
            if (storage?.CanOpen == true)
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open PMX Model",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("PMX / FBX Model")
                        {
                            Patterns = ["*.pmx", "*.fbx"],
                            MimeTypes = ["application/octet-stream"]
                        }
                    ]
                });

                var localPath = files.FirstOrDefault()?.Path.LocalPath;
                if (!string.IsNullOrWhiteSpace(localPath))
                    return localPath;
            }
        }
        catch
        {
            // Fall back to the legacy dialog below.
        }

#pragma warning disable CS0618
        var dialog = new OpenFileDialog
        {
            Title = "Open PMX / FBX Model",
            AllowMultiple = false,
            Filters =
            [
                new FileDialogFilter { Name = "PMX / FBX Model", Extensions = { "pmx", "fbx" } },
                new FileDialogFilter { Name = "All files", Extensions = { "*" } }
            ]
        };
        var selected = await dialog.ShowAsync(this);
#pragma warning restore CS0618
        return selected?.FirstOrDefault();
    }

    private async Task OpenVmdWorkflowAsync()
    {
        try
        {
            var path = await PickVmdFileAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText.Text = "VMD selection canceled";
                return;
            }

            LoadVmd(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = "VMD selection error";
            RenderInfoText.Text = ex.Message;
        }
    }

    private async Task<string?> PickVmdFileAsync()
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var storage = topLevel?.StorageProvider;
            if (storage?.CanOpen == true)
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open VMD Motion",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("VMD Motion")
                        {
                            Patterns = ["*.vmd"],
                            MimeTypes = ["application/octet-stream"]
                        }
                    ]
                });

                var localPath = files.FirstOrDefault()?.Path.LocalPath;
                if (!string.IsNullOrWhiteSpace(localPath))
                    return localPath;
            }
        }
        catch
        {
            // Fall back to the legacy dialog below.
        }

#pragma warning disable CS0618
        var dialog = new OpenFileDialog
        {
            Title = "Open VMD Motion",
            AllowMultiple = false,
            Filters =
            [
                new FileDialogFilter { Name = "VMD Motion", Extensions = { "vmd" } },
                new FileDialogFilter { Name = "All files", Extensions = { "*" } }
            ]
        };
        var selected = await dialog.ShowAsync(this);
#pragma warning restore CS0618
        return selected?.FirstOrDefault();
    }

    private async Task OpenAudioWorkflowAsync()
    {
        try
        {
            var path = await PickAudioFileAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText.Text = "Audio selection canceled";
                return;
            }

            LoadAudio(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Audio selection error";
            RenderInfoText.Text = ex.Message;
        }
    }

    private async Task<string?> PickAudioFileAsync()
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var storage = topLevel?.StorageProvider;
            if (storage?.CanOpen == true)
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open Audio / Video",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Audio / Video")
                        {
                            Patterns = ["*.wav", "*.mp3", "*.mp4"],
                            MimeTypes = ["audio/wav", "audio/mpeg", "video/mp4"]
                        }
                    ]
                });

                var localPath = files.FirstOrDefault()?.Path.LocalPath;
                if (!string.IsNullOrWhiteSpace(localPath))
                    return localPath;
            }
        }
        catch
        {
            // Fall back to the legacy dialog below.
        }

#pragma warning disable CS0618
        var dialog = new OpenFileDialog
        {
            Title = "Open Audio / Video",
            AllowMultiple = false,
            Filters =
            [
                new FileDialogFilter { Name = "Audio / Video", Extensions = { "wav", "mp3", "mp4" } },
                new FileDialogFilter { Name = "All files", Extensions = { "*" } }
            ]
        };
        var selected = await dialog.ShowAsync(this);
#pragma warning restore CS0618
        return selected?.FirstOrDefault();
    }

    private void LoadPmx(string path)
    {
        StatusText.Text = $"Loading: {Path.GetFileName(path)}";
        var loaded = LoadModel(path);
        _currentModel = loaded.Model;
        if (loaded.EmbeddedMotion is not null)
            _currentMotion = loaded.EmbeddedMotion;
        _renderer.SetModel(_currentModel);
        if (_currentMotion is not null)
            _renderer.SetMotion(_currentMotion);

        ModelNameText.Text = _currentModel.Name;
        ModelStatsText.Text =
            $"{_currentModel.VertexCount:N0} vertices\n" +
            $"{_currentModel.TriangleCount:N0} triangles\n" +
            $"{_currentModel.Materials.Count:N0} materials\n" +
            $"{_currentModel.Textures.Count:N0} textures\n" +
            $"{_currentModel.Bones.Count:N0} bones\n" +
            $"{_currentModel.Bones.Count(x => x.HasIk):N0} IK bones\n" +
            BuildWeightSummary(_currentModel);
        RenderInfoText.Text = $"Loaded: {_currentModel.SourcePath}\nOpening Veldrid preview...";
        if (_currentMotion is not null)
            RenderInfoText.Text += "\n\n" + BuildMotionSummary(_currentMotion);
        StatusText.Text = "Loaded. Opening preview";
        _renderer.OpenPreviewWindow();
    }

    private sealed record LoadedModel(PmxModel Model, VmdMotion? EmbeddedMotion);

    private static LoadedModel LoadModel(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase))
        {
            var imported = FbxReader.LoadWithMotion(path);
            return new LoadedModel(imported.Model, imported.EmbeddedMotion);
        }

        return new LoadedModel(PmxReader.Load(path), null);
    }

    private void LoadVmd(string path)
    {
        StatusText.Text = $"Loading VMD: {Path.GetFileName(path)}";
        _currentMotion = VmdReader.Load(path);
        _renderer.SetMotion(_currentMotion);
        var diagnostics = WriteBindingDiagnosticsIfReady();
        RenderInfoText.Text = BuildMotionSummary(_currentMotion) +
            (string.IsNullOrWhiteSpace(diagnostics)
                ? string.Empty
                : $"\nDiagnostics: {diagnostics}" +
                  (IsFbxModel(_currentModel) ? "\nFBX retarget map/profile/head-test JSON files are saved next to the diagnostics file." : string.Empty));
        StatusText.Text = "VMD loaded";
    }

    private void LoadAudio(string path)
    {
        _currentAudioPath = path;
        _renderer.SetAudio(path);
        StatusText.Text = "Audio loaded";
        RenderInfoText.Text =
            $"Loaded audio: {path}\n" +
            "Press Space in the render window to play/pause with the motion.";
    }

    private string? WriteBindingDiagnosticsIfReady()
    {
        if (_currentModel is null || _currentMotion is null)
            return null;

        var binding = VmdBoneBinder.Bind(_currentModel, _currentMotion);
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "diagnostics");
        return MmdBindingDiagnostics.Write(_currentModel, _currentMotion, binding, outputDirectory);
    }

    private string BuildMotionSummary(VmdMotion motion)
    {
        VmdBoneBinding? binding = null;
        VmdBoneBinding? playbackBinding = null;
        if (_currentModel is not null)
        {
            binding = VmdBoneBinder.Bind(_currentModel, motion);
            if (IsFbxModel(_currentModel))
                playbackBinding = VmdBoneBinder.BindForPlayback(_currentModel, motion);
        }
        var isFbxModel = _currentModel is not null &&
            Path.GetExtension(_currentModel.SourcePath).Equals(".fbx", StringComparison.OrdinalIgnoreCase);

        return
            $"Loaded VMD: {motion.SourcePath}\n" +
            $"{motion.BoneFrameCount:N0} bone frames\n" +
            $"{motion.AnimatedBoneNameCount:N0} animated bone names\n" +
            (isFbxModel
                ? "FBX external VMD retarget: controlled by fbx-retarget-profile-active.json. Default profile disables all bones. Press Q recommended head, J old-head, E head-raw, N neck+head, S spine, A upper-body, L lower-body, F full-body, P reload, V toggle.\n"
                : string.Empty) +
            BuildBindingSummary(binding) +
            (isFbxModel ? BuildFbxPlaybackBindingSummary(playbackBinding) : string.Empty) +
            (isFbxModel ? string.Empty : BuildPoseSampleSummary(binding, motion)) +
            (isFbxModel ? string.Empty : BuildPmxPoseSummary(binding, motion)) +
            $"Top bones: {BuildTopAnimatedBoneSummary(motion)}\n" +
            $"{motion.MorphFrameCount:N0} morph frames\n" +
            $"{motion.CameraFrameCount:N0} camera frames\n" +
            $"{motion.LightFrameCount:N0} light frames\n" +
            $"{motion.SelfShadowFrameCount:N0} self shadow frames\n" +
            $"{motion.IkFrameCount:N0} IK frames\n" +
            $"Last frame: {motion.LastFrame:N0}";
    }

    private static string BuildBindingSummary(VmdBoneBinding? binding)
    {
        if (binding is null)
            return "No PMX model loaded for bone binding\n";

        var unmatched = string.Join(", ", binding.UnmatchedVmdBoneNames.Take(5));
        if (string.IsNullOrWhiteSpace(unmatched)) unmatched = "-";
        var unmatchedLower = string.Join(", ", binding.UnmatchedVmdBoneNames.Where(IsLowerBodyBoneName).Take(8));
        if (string.IsNullOrWhiteSpace(unmatchedLower)) unmatchedLower = "-";

        return
            $"{binding.MatchedBoneNameCount:N0}/{binding.TotalAnimatedBoneNames:N0} matched PMX bone names\n" +
            $"{binding.MatchedBoneFrames:N0}/{binding.TotalBoneFrames:N0} bindable bone frames\n" +
            $"{CountLegRelatedBones(binding):N0} leg/IK related bones matched\n" +
            $"{CountKneeRelatedBones(binding):N0} knee related bones matched\n" +
            $"Unmatched lower-body VMD bones: {unmatchedLower}\n" +
            $"Unmatched VMD bones: {unmatched}\n";
    }

    private static string BuildFbxPlaybackBindingSummary(VmdBoneBinding? binding)
    {
        if (binding is null)
            return "FBX playback retarget: disabled\n";

        return
            $"FBX active-profile retarget bones: {binding.MatchedBoneNameCount:N0}/{binding.TotalAnimatedBoneNames:N0}\n" +
            $"FBX active-profile retarget frames: {binding.MatchedBoneFrames:N0}/{binding.TotalBoneFrames:N0}\n";
    }

    private static int CountLegRelatedBones(VmdBoneBinding binding)
    {
        return binding.BoundBones.Count(x => IsLowerBodyBoneName(x.BoneName));
    }

    private static int CountKneeRelatedBones(VmdBoneBinding binding)
    {
        return binding.BoundBones.Count(x =>
            x.BoneName.Contains("\u3072\u3056", StringComparison.Ordinal) ||
            x.BoneName.Contains("\u819d", StringComparison.Ordinal));
    }

    private static bool IsLowerBodyBoneName(string name)
    {
        return name.Contains("\u30bb\u30f3\u30bf\u30fc", StringComparison.Ordinal) ||
               name.Contains("\u4e0b\u534a\u8eab", StringComparison.Ordinal) ||
               name.Contains('\u8db3', StringComparison.Ordinal) ||
               name.Contains("\u3072\u3056", StringComparison.Ordinal) ||
               name.Contains("\u819d", StringComparison.Ordinal) ||
               name.Contains("IK", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("\uff29\uff2b", StringComparison.Ordinal);
    }

    private static string BuildPoseSampleSummary(VmdBoneBinding? binding, VmdMotion motion)
    {
        if (binding is null || binding.BoundBones.Count == 0)
            return string.Empty;

        var middleFrame = motion.LastFrame / 2;
        var startPose = VmdPoseSampler.Sample(binding, 0);
        var middlePose = VmdPoseSampler.Sample(binding, middleFrame);
        var endPose = VmdPoseSampler.Sample(binding, motion.LastFrame);
        return
            $"Sampled poses: F0={startPose.BonePoses.Count:N0}, " +
            $"F{middleFrame:N0}={middlePose.BonePoses.Count:N0}, " +
            $"F{motion.LastFrame:N0}={endPose.BonePoses.Count:N0}\n";
    }

    private string BuildPmxPoseSummary(VmdBoneBinding? binding, VmdMotion motion)
    {
        if (_currentModel is null || binding is null || binding.BoundBones.Count == 0)
            return string.Empty;

        var middleFrame = motion.LastFrame / 2;
        var startPose = PmxPoseBuilder.Build(_currentModel, VmdPoseSampler.Sample(binding, 0));
        var middlePose = PmxPoseBuilder.Build(_currentModel, VmdPoseSampler.Sample(binding, middleFrame));
        var endPose = PmxPoseBuilder.Build(_currentModel, VmdPoseSampler.Sample(binding, motion.LastFrame));
        var skinning = PmxCpuSkinner.Skin(_currentModel, middlePose);

        return
            $"PMX pose matrices: F0={startPose.BoneTransforms.Count:N0}/{startPose.AnimatedBoneCount:N0} animated, " +
            $"F{middleFrame:N0}={middlePose.BoneTransforms.Count:N0}/{middlePose.AnimatedBoneCount:N0}, " +
            $"F{motion.LastFrame:N0}={endPose.BoneTransforms.Count:N0}/{endPose.AnimatedBoneCount:N0}\n" +
            $"IK solve: F{middleFrame:N0}={middlePose.SolvedIkCount:N0} solved, {middlePose.IkFallbackCount:N0} fallback, {middlePose.AnimatedIkBoneCount:N0} animated IK bones\n" +
            BuildLowerBodyMotionDebug(_currentModel, startPose, middlePose) +
            $"CPU skin sample: F{middleFrame:N0}={skinning.SkinnedVertexCount:N0} skinned, " +
            $"{skinning.FallbackVertexCount:N0} fallback, {skinning.InvalidInfluenceCount:N0} invalid influences\n";
    }

    private static string BuildLowerBodyMotionDebug(PmxModel model, PmxPose startPose, PmxPose pose)
    {
        var parts = new List<string>();
        foreach (var keyword in new[] { "\u30bb\u30f3\u30bf\u30fc", "\u4e0b\u534a\u8eab", "\u5de6\u8db3", "\u53f3\u8db3", "\u5de6\u3072\u3056", "\u53f3\u3072\u3056", "\u5de6\u819d", "\u53f3\u819d", "\u5de6\u8db3IK", "\u53f3\u8db3IK", "\u5de6\u8db3\uff29\uff2b", "\u53f3\u8db3\uff29\uff2b" })
        {
            var index = FindBoneIndex(model, keyword);
            if (index < 0 || index >= pose.BoneTransforms.Count || index >= startPose.BoneTransforms.Count)
                continue;

            var startPosition = GetTranslation(startPose.BoneTransforms[index].WorldMatrix);
            var currentPosition = GetTranslation(pose.BoneTransforms[index].WorldMatrix);
            var delta = currentPosition - startPosition;
            if (delta.Length() < 0.0005f && !pose.BoneTransforms[index].HasVmdPose)
                continue;

            parts.Add($"{model.Bones[index].Name}:{delta.Length():0.000}{(pose.BoneTransforms[index].HasVmdPose ? "*" : "")}");
        }

        return parts.Count == 0
            ? "Lower-body motion: none detected\n"
            : "Lower-body motion: " + string.Join(", ", parts.Take(8)) + "\n";
    }

    private static int FindBoneIndex(PmxModel model, string keyword)
    {
        for (var i = 0; i < model.Bones.Count; i++)
        {
            if (model.Bones[i].Name.Contains(keyword, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static System.Numerics.Vector3 GetTranslation(System.Numerics.Matrix4x4 matrix)
    {
        return new System.Numerics.Vector3(matrix.M41, matrix.M42, matrix.M43);
    }

    private static string BuildTopAnimatedBoneSummary(VmdMotion motion)
    {
        var topBones = motion.BoneFrameCountsByName
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(5)
            .Select(x => $"{x.Key}({x.Value:N0})");
        var summary = string.Join(", ", topBones);
        return string.IsNullOrWhiteSpace(summary) ? "-" : summary;
    }

    private static string BuildWeightSummary(PmxModel model)
    {
        var groups = model.Vertices
            .GroupBy(x => x.Weight.Type)
            .OrderBy(x => x.Key)
            .Select(x => $"{x.Key}:{x.Count():N0}");
        return "weights " + string.Join(", ", groups);
    }

    private static bool IsFbxModel(PmxModel? model)
    {
        return model is not null &&
               Path.GetExtension(model.SourcePath).Equals(".fbx", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (TryGetDroppedSupportedPath(e, out _, out _))
            e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (TryGetDroppedSupportedPath(e, out var path, out var extension))
        {
            if (extension.Equals(".pmx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase))
                LoadPmx(path);
            else if (extension.Equals(".vmd", StringComparison.OrdinalIgnoreCase))
                LoadVmd(path);
            else
                LoadAudio(path);
            e.Handled = true;
            return;
        }

        StatusText.Text = "Drop a .pmx, .fbx, .vmd, .wav, .mp3, or .mp4 file";
        e.Handled = true;
    }

    private static bool TryGetDroppedSupportedPath(DragEventArgs e, out string path, out string extension)
    {
        path = string.Empty;
        extension = string.Empty;
#pragma warning disable CS0618
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files is null) return false;

        foreach (var file in files)
        {
            var localPath = file.Path.LocalPath;
            if (string.IsNullOrWhiteSpace(localPath)) continue;
            extension = Path.GetExtension(localPath);
            if (extension.Equals(".pmx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".vmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                path = localPath;
                return true;
            }
        }

        return false;
    }

    private void ResetButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _renderer.ResetView();
        StatusText.Text = _currentModel is null ? "Ready" : "View reset";
    }

    private void RenderButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentModel is null)
        {
            StatusText.Text = "Open a PMX or FBX model first";
            _ = OpenPmxWorkflowAsync();
            return;
        }

        _renderer.OpenPreviewWindow();
        StatusText.Text = "Opening render window";
    }
}
