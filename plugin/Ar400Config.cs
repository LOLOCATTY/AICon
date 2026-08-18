using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AICon
{
    // Per-package automation settings for AR400, edited via PackageSettingsWindow and stored at
    // %APPDATA%\AICon\ar400profiles.json (same location/encoding convention as aiconagent.json —
    // see shared\Config.cs). A package missing from the file behaves as "everything off" except
    // ViewTemplateId, whose null means "fall back to AR400's name-substring auto-match".
    public sealed class Ar400Settings
    {
        [JsonPropertyName("packages")]
        public Dictionary<string, Ar400PackageProfile> Packages { get; set; } =
            new Dictionary<string, Ar400PackageProfile>(StringComparer.OrdinalIgnoreCase);

        // Manual override for the width (mm) of a title block's right-hand data strip ("خرطوشة"),
        // keyed by title block TYPE id. AR400 detects this from the title block's geometry on its own;
        // this is only for when that guess is wrong. 0 / absent = auto-detect.
        [JsonPropertyName("titleBlockStripMm")]
        public Dictionary<string, double> TitleBlockStripMm { get; set; } =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // How each Revit level is written in the drawing register, when the two use different naming
        // (very common: the model says "-01-BF04-PVL", the register says "BS04"). Key = Revit level
        // name, value = the text to look for in the register. Set once in AR400 Settings → Levels.
        [JsonPropertyName("levelAliases")]
        public Dictionary<string, string> LevelAliases { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What to search the register for when looking up this level — the user's alias if
        /// one is set, otherwise the level's own name.</summary>
        public string RegisterLabelForLevel(string levelName)
        {
            string alias;
            if (LevelAliases != null && levelName != null &&
                LevelAliases.TryGetValue(levelName, out alias) && !string.IsNullOrWhiteSpace(alias))
                return alias;
            return levelName;
        }

        /// <summary>User's override in mm for this title block type id, or 0 to auto-detect.
        /// Takes a plain int so this settings model stays free of Revit API types.</summary>
        public double StripOverrideMm(int titleBlockTypeId)
        {
            double mm;
            if (TitleBlockStripMm != null &&
                TitleBlockStripMm.TryGetValue(titleBlockTypeId.ToString(), out mm))
                return mm;
            return 0;
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AICon", "ar400profiles.json");

        /// <summary>Loads the settings file, filling in a default (all-off) profile for any of
        /// Ar400Command.Packages that the file doesn't mention yet, so callers always see a full set.</summary>
        public static Ar400Settings Load()
        {
            Ar400Settings settings = null;
            string path = FilePath;
            if (File.Exists(path))
            {
                try { settings = JsonSerializer.Deserialize<Ar400Settings>(File.ReadAllText(path), JsonOpts); }
                catch { /* corrupt/hand-edited file — fall back to defaults below */ }
            }
            settings = settings ?? new Ar400Settings();
            settings.Packages = settings.Packages ?? new Dictionary<string, Ar400PackageProfile>(StringComparer.OrdinalIgnoreCase);

            foreach (string pkg in Ar400Command.Packages)
                if (!settings.Packages.ContainsKey(pkg))
                    settings.Packages[pkg] = new Ar400PackageProfile();

            return settings;
        }

        public void Save()
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts), new UTF8Encoding(false));
        }

        public Ar400PackageProfile ForPackage(string package)
        {
            Ar400PackageProfile p;
            if (Packages != null && Packages.TryGetValue(package, out p) && p != null) return p;
            return new Ar400PackageProfile();
        }
    }

    public sealed class Ar400PackageProfile
    {
        // null = keep AR400 v1's "apply a template whose name contains the package name" behaviour.
        [JsonPropertyName("viewTemplateId")] public int? ViewTemplateId { get; set; }

        // Which categories to tag, which loaded tag type to use for each, and whether they get a
        // leader — configured via TagCategoriesWindow (mirrors Revit's own "Tag All Not Tagged"
        // dialog). Replaces the old fixed "roomTags"/"wallTags" bools; any taggable category works now.
        [JsonPropertyName("tagCategories")] public List<Ar400TagCategoryProfile> TagCategories { get; set; } = new List<Ar400TagCategoryProfile>();

        // Global settings applied to every tag this package places — matches Revit's own "Tag All Not
        // Tagged" dialog, which also has ONE Leader checkbox and ONE Tag Orientation, not per-category.
        [JsonPropertyName("tagLeader")] public bool TagLeader { get; set; }
        [JsonPropertyName("tagOrientationVertical")] public bool TagOrientationVertical { get; set; }

        // What AR400 dimensions automatically inside each view of this package.
        // "none" | "grids" | "walls" | "grids+walls".
        [JsonPropertyName("dimensions")] public string Dimensions { get; set; } = "none";

        [JsonPropertyName("schedule")] public Ar400ScheduleProfile Schedule { get; set; } = new Ar400ScheduleProfile();

        public bool DimensionGrids
        {
            get { string d = (Dimensions ?? "").ToLowerInvariant(); return d.Contains("grid"); }
        }
        public bool DimensionWalls
        {
            get { string d = (Dimensions ?? "").ToLowerInvariant(); return d.Contains("wall"); }
        }
    }

    public sealed class Ar400TagCategoryProfile
    {
        // BuiltInCategory name of the MODEL category being tagged, e.g. "OST_Doors".
        [JsonPropertyName("category")] public string Category { get; set; }
        // null = use whatever tag type is currently the project default for this category (Revit's
        // own TagMode.TM_ADDBY_CATEGORY behaviour) — set only when the user picked a SPECIFIC loaded
        // tag type/family in TagCategoriesWindow.
        [JsonPropertyName("tagTypeId")] public int? TagTypeId { get; set; }
    }

    public sealed class Ar400ScheduleProfile
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; } = "Doors";
        [JsonPropertyName("name")] public string Name { get; set; } = "Schedule";
        [JsonPropertyName("fields")] public List<string> Fields { get; set; }
    }
}
