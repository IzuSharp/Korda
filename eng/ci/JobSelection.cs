namespace NativeWeave.CI;

internal sealed record JobSelection(bool Build, bool Modules, bool Focused)
{
    public static JobSelection For(Plan plan, bool isPush, bool isMerge, bool reuse)
    {
        bool rebuild = isMerge && !reuse;
        return new(
            plan.BuildScopes.Length > 0 && (!isPush || rebuild && plan.Packages.Length > 0),
            (plan.Modules.Length > 0 || plan.ModulePackages.Length > 0) && (!isPush || rebuild && plan.Candidates.Length > 0),
            !isPush && plan.Checks.Length > 0);
    }
}
