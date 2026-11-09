using System.Text.Json;

namespace SimplePmxVeldrid.Importing;

public sealed record FbxRetargetProfileDocument(
    int Version,
    string ModelPath,
    string MotionPath,
    string ModelName,
    string MotionModelName,
    string Mode,
    string Note,
    IReadOnlyList<FbxRetargetProfileEntry> Entries);

public sealed record FbxRetargetProfileEntry(
    string VmdBoneName,
    int? FbxBoneIndex,
    string? FbxBoneName,
    bool Matched,
    bool Enabled,
    string Group,
    string Risk,
    string DefaultMode,
    float RotationScale,
    float TranslationScale,
    string Note,
    string RotationMode = "PmxToFbxPreview",
    string HumanBone = "None",
    FbxAxisFix? AxisFix = null);

public sealed record FbxAxisFix(float X, float Y, float Z, float W);

public static class FbxRetargetProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public const string ActiveFileName = "fbx-retarget-profile-active.json";
    public const string HeadTestFileName = "fbx-retarget-profile-head-test.json";
    public const string HeadInvertTestFileName = "fbx-retarget-profile-head-invert-test.json";
    public const string HeadRawTestFileName = "fbx-retarget-profile-head-raw-test.json";
    public const string NeckHeadTestFileName = "fbx-retarget-profile-neck-head-test.json";
    public const string UpperBodyTestFileName = "fbx-retarget-profile-upper-body-test.json";
    public const string SpineTestFileName = "fbx-retarget-profile-spine-test.json";
    public const string LowerBodyTestFileName = "fbx-retarget-profile-lower-body-test.json";
    public const string FullBodyTestFileName = "fbx-retarget-profile-full-body-test.json";

    public static string GetActivePath(string diagnosticsDirectory)
    {
        return Path.Combine(diagnosticsDirectory, ActiveFileName);
    }

    public static string GetHeadTestPath(string diagnosticsDirectory)
    {
        return Path.Combine(diagnosticsDirectory, HeadTestFileName);
    }

    public static string GetHeadInvertTestPath(string diagnosticsDirectory)
    {
        return Path.Combine(diagnosticsDirectory, HeadInvertTestFileName);
    }

    public static string GetHeadRawTestPath(string diagnosticsDirectory)
    {
        return Path.Combine(diagnosticsDirectory, HeadRawTestFileName);
    }

    public static string GetNeckHeadTestPath(string diagnosticsDirectory)
    {
        return Path.Combine(diagnosticsDirectory, NeckHeadTestFileName);
    }

    public static string GetUpperBodyTestPath(string diagnosticsDirectory)
    {
        return Path.Combine(diagnosticsDirectory, UpperBodyTestFileName);
    }

    public static string GetSpineTestPath(string diagnosticsDirectory)
    {
        return Path.Combine(diagnosticsDirectory, SpineTestFileName);
    }

    public static string GetLowerBodyTestPath(string diagnosticsDirectory)
    {
        return Path.Combine(diagnosticsDirectory, LowerBodyTestFileName);
    }

    public static string GetFullBodyTestPath(string diagnosticsDirectory)
    {
        return Path.Combine(diagnosticsDirectory, FullBodyTestFileName);
    }

    public static FbxRetargetProfileDocument? LoadActive(PmxModel model, VmdMotion motion)
    {
        var path = GetActivePath(Path.Combine(AppContext.BaseDirectory, "diagnostics"));
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<FbxRetargetProfileDocument>(json);
            if (profile is null)
                return null;

            if (!Matches(profile, model, motion))
                return null;

            var upgraded = NormalizeKnownPreset(UpgradeForCurrentSchema(profile));
            if (!ReferenceEquals(upgraded, profile))
                SaveProfile(path, upgraded);

            return upgraded;
        }
        catch
        {
            return null;
        }
    }

    public static string ActivateHeadTest()
    {
        return ActivatePreset(GetHeadTestPath, "head-test");
    }

    public static string ActivateHeadInvertTest()
    {
        return ActivatePreset(GetHeadInvertTestPath, "head-invert-test");
    }

    public static string ActivateHeadRawTest()
    {
        return ActivatePreset(GetHeadRawTestPath, "head-raw-test");
    }

    public static string ActivateNeckHeadTest()
    {
        return ActivatePreset(GetNeckHeadTestPath, "neck+head-test");
    }

    public static string ActivateUpperBodyTest()
    {
        return ActivatePreset(GetUpperBodyTestPath, "upper-body-test");
    }

    public static string ActivateSpineTest()
    {
        return ActivatePreset(GetSpineTestPath, "spine-test");
    }

    public static string ActivateLowerBodyTest()
    {
        return ActivatePreset(GetLowerBodyTestPath, "lower-body-test");
    }

    public static string ActivateFullBodyTest()
    {
        return ActivatePreset(GetFullBodyTestPath, "full-body-test");
    }

    private static string ActivatePreset(Func<string, string> sourcePathFactory, string label)
    {
        var diagnosticsDirectory = Path.Combine(AppContext.BaseDirectory, "diagnostics");
        var sourcePath = sourcePathFactory(diagnosticsDirectory);
        var targetPath = GetActivePath(diagnosticsDirectory);
        if (!File.Exists(sourcePath))
            return $"{label} profile not found. Load FBX + VMD once to generate diagnostics.";

        try
        {
            var json = File.ReadAllText(sourcePath);
            var profile = JsonSerializer.Deserialize<FbxRetargetProfileDocument>(json);
            if (profile is not null)
            {
                var upgraded = NormalizeKnownPreset(UpgradeForCurrentSchema(profile));
                SaveProfile(targetPath, upgraded);
                if (!ReferenceEquals(upgraded, profile))
                    SaveProfile(sourcePath, upgraded);
            }
            else
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
            }

            return $"{label} profile activated";
        }
        catch (Exception ex)
        {
            return $"failed to activate {label} profile: {ex.Message}";
        }
    }

    public static bool Matches(FbxRetargetProfileDocument profile, PmxModel model, VmdMotion motion)
    {
        return SamePath(profile.ModelPath, model.SourcePath) &&
               SamePath(profile.MotionPath, motion.SourcePath);
    }

    public static string GetDefaultHumanBone(string vmdBoneName)
    {
        return vmdBoneName switch
        {
            "\u30bb\u30f3\u30bf\u30fc" => "Hips",
            "\u4e0b\u534a\u8eab" => "Hips",
            "\u4e0a\u534a\u8eab" => "Spine",
            "\u4e0a\u534a\u8eab2" => "Chest",
            "\u9996" => "Neck",
            "\u982d" => "Head",
            "\u5de6\u80a9" => "LeftShoulder",
            "\u53f3\u80a9" => "RightShoulder",
            "\u5de6\u8155" => "LeftUpperArm",
            "\u53f3\u8155" => "RightUpperArm",
            "\u5de6\u3072\u3058" => "LeftLowerArm",
            "\u53f3\u3072\u3058" => "RightLowerArm",
            "\u5de6\u624b\u9996" => "LeftHand",
            "\u53f3\u624b\u9996" => "RightHand",
            "\u5de6\u8db3" => "LeftUpperLeg",
            "\u53f3\u8db3" => "RightUpperLeg",
            "\u5de6\u3072\u3056" => "LeftLowerLeg",
            "\u53f3\u3072\u3056" => "RightLowerLeg",
            "\u5de6\u8db3\u9996" => "LeftFoot",
            "\u53f3\u8db3\u9996" => "RightFoot",
            _ => "None"
        };
    }

    private static FbxRetargetProfileDocument UpgradeForCurrentSchema(FbxRetargetProfileDocument profile)
    {
        var changed = profile.Version < 2;
        var entries = profile.Entries
            .Select(entry =>
            {
                var humanBone = string.IsNullOrWhiteSpace(entry.HumanBone) || entry.HumanBone == "None"
                    ? GetDefaultHumanBone(entry.VmdBoneName)
                    : entry.HumanBone;
                var axisFix = IsValidAxisFix(entry.AxisFix)
                    ? entry.AxisFix
                    : new FbxAxisFix(0f, 0f, 0f, 1f);

                if (!string.Equals(humanBone, entry.HumanBone, StringComparison.Ordinal) ||
                    !Equals(axisFix, entry.AxisFix))
                {
                    changed = true;
                }

                return entry with
                {
                    HumanBone = humanBone,
                    AxisFix = axisFix
                };
            })
            .ToArray();

        return changed
            ? profile with
            {
                Version = 2,
                Note = EnsureSchemaNote(profile.Note),
                Entries = entries
            }
            : profile;
    }

    private static FbxRetargetProfileDocument NormalizeKnownPreset(FbxRetargetProfileDocument profile)
    {
        if (!string.Equals(profile.Mode, "upper-body-test", StringComparison.Ordinal))
            return profile;

        var changed = false;
        var entries = profile.Entries
            .Select(entry =>
            {
                if (entry.HumanBone != "Neck" && entry.VmdBoneName != "\u9996")
                    return entry;

                if (!entry.Enabled && entry.RotationScale == 0f && entry.TranslationScale == 0f)
                    return entry;

                changed = true;
                return entry with
                {
                    Enabled = false,
                    RotationScale = 0f,
                    TranslationScale = 0f,
                    Note = "Neck is disabled in upper-body test preset. Use N for neck+head testing."
                };
            })
            .ToArray();

        return changed
            ? profile with
            {
                Note = profile.Note.Contains("Neck is disabled", StringComparison.Ordinal)
                    ? profile.Note
                    : profile.Note + " Neck is disabled here; use N for neck+head testing.",
                Entries = entries
            }
            : profile;
    }

    private static bool IsValidAxisFix(FbxAxisFix? axisFix)
    {
        if (axisFix is null)
            return false;

        return float.IsFinite(axisFix.X) &&
               float.IsFinite(axisFix.Y) &&
               float.IsFinite(axisFix.Z) &&
               float.IsFinite(axisFix.W) &&
               axisFix.X * axisFix.X + axisFix.Y * axisFix.Y + axisFix.Z * axisFix.Z + axisFix.W * axisFix.W > 0.000001f;
    }

    private static string EnsureSchemaNote(string note)
    {
        const string schemaNote = " AxisFix is applied as inverse(axisFix) * rotation * axisFix.";
        return note.Contains("AxisFix", StringComparison.Ordinal)
            ? note
            : note + schemaNote;
    }

    private static void SaveProfile(string path, FbxRetargetProfileDocument profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
