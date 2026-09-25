using System;
using System.Collections.Generic;

namespace MorePlayersMod.Logic;

public enum FrontierCategory { Power, Propulsion, LifeSupport, Radiation, Hangar, Control, Weapons }

/// <summary>One Frontier block: a native part cloned from a vanilla donor, renamed and re-tuned.</summary>
public sealed class FrontierBlock
{
    /// <summary>Authoring name; hashed by the game into a new SCPrefab, so it must never change once released.</summary>
    public string Id;
    public string Name;
    public string Description;
    public FrontierCategory Category;
    /// <summary>1..3. Tier N unlocks after <see cref="FrontierCatalog.RunsForTier"/> supply runs.</summary>
    public int Tier;
    /// <summary>Vanilla display names (case-insensitive substrings) tried in order to pick the donor part.</summary>
    public string[] Donors;
    /// <summary>Float/float3 authoring fields whose lowercase name contains the key are multiplied before baking.</summary>
    public Dictionary<string, float> StatScale = new Dictionary<string, float>();
    public float MassScale = 1f;
    /// <summary>Garage stock once unlocked.</summary>
    public int Available = 4;
    public LifeSupportRole Role = LifeSupportRole.None;
    public double RoleValue;
    /// <summary>Preview parts are only registered when the matching config switch is on.</summary>
    public bool Preview;
}

public static class FrontierCatalog
{
    public static int RunsForTier(int tier) => tier <= 1 ? 1 : tier == 2 ? 3 : 6;

    public static bool Unlocked(FrontierBlock block, int supplyRuns, bool unlockAll) =>
        unlockAll || supplyRuns >= RunsForTier(block.Tier);

    private static readonly string[] BatteryDonors = { "Large Battery", "Battery" };
    private static readonly string[] FrameDonors = { "Frame" };
    private static readonly string[] SmallDeviceDonors = { "Small Battery", "Battery", "Light" };
    private static readonly string[] ThrusterDonors = { "Large Electric Thruster", "Medium Electric Thruster", "Electric Thruster" };
    private static readonly string[] GlassDonors = { "Glass", "Window" };
    private static readonly string[] MonitorDonors = { "Datameter", "Display", "Monitor" };

    private static Dictionary<string, float> Power(float x) => new Dictionary<string, float>
        { { "power", x }, { "capacity", x }, { "energy", x }, { "charge", x } };

    public static readonly IReadOnlyList<FrontierBlock> All = new List<FrontierBlock>
    {
        // Tier 1 - first supply run: the ship becomes a place where many people can live.
        new FrontierBlock { Id = "MPM_FissionReactor", Name = "FR-1 Fission Reactor", Tier = 1, Category = FrontierCategory.Power,
            Description = "Compact fission core: 4x the output of a large battery. Leaks radiation - wall it in with Radiation Walls or pipe it out with Radiation Vents.",
            Donors = BatteryDonors, StatScale = Power(4f), MassScale = 3f, Available = 2, Role = LifeSupportRole.Reactor, RoleValue = 1.0 },
        new FrontierBlock { Id = "MPM_RadiationWall", Name = "Lead-Lined Radiation Wall", Tier = 1, Category = FrontierCategory.Radiation,
            Description = "Heavy shielding plate. Each wall within 6 m of a reactor absorbs about a fifth of its radiation; a full reactor room blocks almost all of it.",
            Donors = FrameDonors, MassScale = 4f, Available = 24, Role = LifeSupportRole.Shield },
        new FrontierBlock { Id = "MPM_RadiationVent", Name = "Radiation Vent Pipe", Tier = 1, Category = FrontierCategory.Radiation,
            Description = "Pipes contaminated air out of the hull. Each vent within 6 m of a reactor exhausts about a third of the remaining radiation.",
            Donors = FrameDonors, MassScale = 1.5f, Available = 8, Role = LifeSupportRole.Vent },
        new FrontierBlock { Id = "MPM_OxygenGenerator", Name = "O2 Electrolysis Generator", Tier = 1, Category = FrontierCategory.LifeSupport,
            Description = "Keeps 4 crew breathing. Once a ship carries life-support equipment, every crew member aboard needs oxygen capacity.",
            Donors = SmallDeviceDonors, MassScale = 2f, Available = 6, Role = LifeSupportRole.OxygenGenerator, RoleValue = 4 },
        new FrontierBlock { Id = "MPM_CO2Scrubber", Name = "CO2 Scrubber", Tier = 1, Category = FrontierCategory.LifeSupport,
            Description = "Recycles air for 2 more crew. Cheap backup for large crews.",
            Donors = SmallDeviceDonors, Available = 12, Role = LifeSupportRole.Scrubber, RoleValue = 2 },

        // Tier 2 - three supply runs: serious power and propulsion for 24-seat ships.
        new FrontierBlock { Id = "MPM_FusionReactor", Name = "FX-7 Fusion Reactor", Tier = 2, Category = FrontierCategory.Power,
            Description = "Magnetically confined fusion: 12x large-battery output. Strong radiation - needs its own shielded room.",
            Donors = BatteryDonors, StatScale = Power(12f), MassScale = 6f, Available = 2, Role = LifeSupportRole.Reactor, RoleValue = 3.0 },
        new FrontierBlock { Id = "MPM_PlasmaDrive", Name = "Plasma Drive", Tier = 2, Category = FrontierCategory.Propulsion,
            Description = "Magnetoplasma engine with 6x the thrust of a large electric thruster. Wire it like any thruster.",
            Donors = ThrusterDonors, StatScale = new Dictionary<string, float> { { "force", 6f }, { "thrust", 6f } }, MassScale = 3f, Available = 4 },
        new FrontierBlock { Id = "MPM_EnergyGatePanel", Name = "Energy Gate Panel", Tier = 2, Category = FrontierCategory.Hangar,
            Description = "Transparent hangar panel that keeps air in. Build large hangar walls out of it next to Hangar Blast Doors.",
            Donors = GlassDonors, Available = 32 },
        new FrontierBlock { Id = "MPM_FireControlMonitor", Name = "Fire Control Monitor", Tier = 2, Category = FrontierCategory.Control,
            Description = "Bridge display. Shows reactor, radiation, oxygen and plasma bank status of this ship in the Frontier HUD.",
            Donors = MonitorDonors, Available = 6 },

        // Tier 3 - six supply runs: star-grade power.
        new FrontierBlock { Id = "MPM_StarCore", Name = "Star Core Generator", Tier = 3, Category = FrontierCategory.Power,
            Description = "A contained micro-star: 40x large-battery output. Extreme radiation; needs a fully walled room plus vents.",
            Donors = BatteryDonors, StatScale = Power(40f), MassScale = 12f, Available = 1, Role = LifeSupportRole.Reactor, RoleValue = 8.0 },
        new FrontierBlock { Id = "MPM_ParticleAccelerator", Name = "Particle Accelerator Ring", Tier = 3, Category = FrontierCategory.Power,
            Description = "Accelerates plasma towards light speed and feeds Plasma Condensers. Charges the ship's plasma bank from reactor power.",
            Donors = BatteryDonors, StatScale = Power(2f), MassScale = 4f, Available = 4 },
        new FrontierBlock { Id = "MPM_PlasmaCondenser", Name = "Plasma Condenser", Tier = 3, Category = FrontierCategory.Power,
            Description = "Stores 100 units of accelerated plasma for heavy systems.",
            Donors = BatteryDonors, StatScale = Power(3f), MassScale = 2f, Available = 6 },

        // Weapons preview: parts and plasma charging only (see README: no projectile layer yet).
        new FrontierBlock { Id = "MPM_PlasmaLance", Name = "Plasma Lance (preview)", Tier = 3, Category = FrontierCategory.Weapons, Preview = true,
            Description = "Capital-ship plasma cannon. Needs a full 500-unit plasma bank per shot. PREVIEW: charges and reports status, does not fire yet.",
            Donors = ThrusterDonors, MassScale = 10f, Available = 1 },
        new FrontierBlock { Id = "MPM_Railgun80", Name = "80mm Railgun (preview)", Tier = 3, Category = FrontierCategory.Weapons, Preview = true,
            Description = "Electromagnetic kinetic cannon. PREVIEW: charges and reports status, does not fire yet.",
            Donors = ThrusterDonors, MassScale = 4f, Available = 4 },
        new FrontierBlock { Id = "MPM_ArcProjector", Name = "Tesla Arc Projector (preview)", Tier = 3, Category = FrontierCategory.Weapons, Preview = true,
            Description = "Short-range electric weapon. PREVIEW: charges and reports status, does not fire yet.",
            Donors = SmallDeviceDonors, MassScale = 2f, Available = 4 },
    };

    /// <summary>Returns problems with a catalog; empty means valid. Used by tests and at startup.</summary>
    public static List<string> Validate(IReadOnlyList<FrontierBlock> blocks)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in blocks)
        {
            if (string.IsNullOrWhiteSpace(b.Id) || !b.Id.StartsWith("MPM_", StringComparison.Ordinal)) errors.Add($"bad id '{b.Id}'");
            else if (!ids.Add(b.Id)) errors.Add($"duplicate id '{b.Id}'");
            if (string.IsNullOrWhiteSpace(b.Name)) errors.Add($"{b.Id}: missing name");
            if (b.Donors == null || b.Donors.Length == 0) errors.Add($"{b.Id}: no donors");
            if (b.Tier < 1 || b.Tier > 3) errors.Add($"{b.Id}: tier {b.Tier}");
            if (b.MassScale <= 0 || float.IsNaN(b.MassScale)) errors.Add($"{b.Id}: mass scale");
            if (b.Available < 1) errors.Add($"{b.Id}: availability");
            foreach (var kv in b.StatScale)
                if (kv.Value <= 0 || float.IsNaN(kv.Value) || kv.Key != kv.Key.ToLowerInvariant()) errors.Add($"{b.Id}: stat '{kv.Key}'");
            if (b.Role == LifeSupportRole.Reactor && b.RoleValue <= 0) errors.Add($"{b.Id}: reactor without radiation");
        }
        return errors;
    }

    /// <summary>First donor keyword that matches one of <paramref name="vanillaNames"/>; exact names win over substrings.</summary>
    public static string PickDonor(FrontierBlock block, IEnumerable<string> vanillaNames)
    {
        var names = new List<string>(vanillaNames);
        foreach (var keyword in block.Donors)
        {
            foreach (var n in names) if (string.Equals(n, keyword, StringComparison.OrdinalIgnoreCase)) return n;
            string best = null;
            foreach (var n in names)
                if (n != null && n.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 && (best == null || n.Length < best.Length)) best = n;
            if (best != null) return best;
        }
        return null;
    }

    /// <summary>Scale factor for an authoring field name, or 1 when no key matches.</summary>
    public static float ScaleFor(FrontierBlock block, string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return 1f;
        string lower = fieldName.ToLowerInvariant();
        if (lower.Contains("mass") || lower.Contains("temperature") || lower.Contains("bounds") || lower.Contains("sound")) return 1f;
        foreach (var kv in block.StatScale) if (lower.Contains(kv.Key)) return kv.Value;
        return 1f;
    }
}

/// <summary>Plasma bank charged by accelerators, stored by condensers (pure, unit tested).</summary>
public static class PlasmaModel
{
    public const double PerCondenser = 100;
    public const double RatePerAccelerator = 5;

    public static double Capacity(int condensers) => Math.Max(0, condensers) * PerCondenser;

    /// <summary>Accelerators only run when at least one reactor powers them.</summary>
    public static double Step(double bank, int accelerators, int condensers, int reactors, double dt)
    {
        double cap = Capacity(condensers);
        if (reactors <= 0 || accelerators <= 0 || dt <= 0) return Math.Min(bank, cap);
        return Math.Min(cap, bank + accelerators * RatePerAccelerator * dt);
    }
}
