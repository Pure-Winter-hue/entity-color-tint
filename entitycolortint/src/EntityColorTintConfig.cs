using System.Collections.Generic;

namespace EntityColorTint
{
    public enum Style : int { SoftHue = 0, Gray = 1, Dark = 2, White = 3, Mutant = 4 }

    // ---------------- Config ----------------
    public class RangeF { public float Min { get; set; } = 0.80f; public float Max { get; set; } = 1.02f; }
    public class SoftHueRange
    {
        public float HueJitterDeg { get; set; } = 16f;
        public float SMin { get; set; } = 0.06f;
        public float SMax { get; set; } = 0.22f;
        public float LMin { get; set; } = 0.55f;
        public float LMax { get; set; } = 1.05f;
    }

    /// <summary>
    /// Non-recursive placeholder for rule overrides.
    /// The old version had Rule.Custom typed as Config, which created a recursive schema
    /// (Config -> Rules -> Rule.Custom -> Config -> ...), breaking some config UIs/serializers.
    ///
    /// If you ever wire Rules into gameplay, add override fields here.
    /// </summary>
    public class ConfigOverrides
    {
        // Intentionally empty for now (keeps config UI happy).
    }

    public class Rule
    {
        public string Pattern { get; set; } = "*";
        public string Side { get; set; } = "Both";
        public string Mode { get; set; } = "";
        public ConfigOverrides Custom { get; set; } = null;
    }

    public class Config
    {
        // Spawn weights
        public double WeightSoftHue { get; set; } = 0.45;
        public double WeightGray { get; set; } = 0.30;
        public double WeightDark { get; set; } = 0.15;
        public double WeightWhite { get; set; } = 0.10;

        // Arctic natural spawn
        public double ArcticDarkChance { get; set; } = 0.22;
        public double ArcticWhiteChance { get; set; } = 0.18;

        // Neutral bands
        public RangeF Gray { get; set; } = new RangeF { Min = 0.70f, Max = 1.05f };
        public RangeF Dark { get; set; } = new RangeF { Min = 0.36f, Max = 0.62f };
        public RangeF White { get; set; } = new RangeF { Min = 1.05f, Max = 1.18f };

        public SoftHueRange SoftHue { get; set; } = new SoftHueRange();

        // Genetics (server-authoritative)
        public bool ServerEnableBreedingMutations { get; set; } = true;
        public double BreedingMutationChance { get; set; } = 0.025;

        // Wild juveniles fallback (no parents nearby) — OFF by default
        public bool EnableJuvenileFallbackMutation { get; set; } = false;
        public double JuvenileFallbackMutationChance { get; set; } = 0.001;

        public float BreedMixNoise { get; set; } = 0.20f;
        public float BreedOvershoot { get; set; } = 0.05f;
        public float BreedDrift { get; set; } = 0.035f;
        public float BreedLowClamp { get; set; } = 0.08f;
        public float BreedHighClamp { get; set; } = 1.30f;

        // Mutation vividness & compounding
        public float MutationIntensity { get; set; } = 1.08f;
        public float MutantAmplify { get; set; } = 0.10f;
        public float InheritMutantBias { get; set; } = 0.05f;

        public double[] MutationWeights { get; set; } = new double[] { 1 / 6.0, 1 / 6.0, 1 / 6.0, 1 / 6.0, 1 / 6.0, 1 / 6.0 };
        public bool AllowArcticMutations { get; set; } = true;

        // Kill switches
        public bool ServerDisableAll { get; set; } = false;
        public bool ClientDisableAll { get; set; } = false;

        // Currently unused by the mod logic, but kept for backward-compat with existing config files.
        public List<Rule> Rules { get; set; } = new List<Rule>();

        public static Config Default() => new Config();
    }
}
