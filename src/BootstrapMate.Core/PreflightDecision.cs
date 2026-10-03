using System.Text.Json;

namespace BootstrapMate.Core;

/// <summary>
/// What the rest of the run does, chosen by a preflight script's exit code.
/// Names and values match the macOS build.
/// </summary>
public enum PreflightDecision
{
    /// <summary>Exit 0: the machine needs nothing; skip every later stage.</summary>
    Skip,
    /// <summary>
    /// Exit 2: the machine is already provisioned; bring its tooling back to the
    /// manifest's baseline without provisioning it again.
    /// </summary>
    Baseline,
    /// <summary>Any other positive exit: run the full bootstrap.</summary>
    Provision,
    /// <summary>Negative exit: the script itself failed.</summary>
    Failed
}

public static class Preflight
{
    /// <summary>Exit code a preflight script returns to request baseline mode.</summary>
    public const int BaselineExitCode = 2;

    /// <summary>
    /// Set for every process BootstrapMate starts, so a preflight can tell this
    /// build understands baseline mode before it returns <see cref="BaselineExitCode"/>.
    /// A build without baseline mode reads 2 as Provision.
    /// </summary>
    public const string BaselineExitCodeVariable = "BOOTSTRAPMATE_BASELINE_EXIT_CODE";

    public static PreflightDecision Decide(int exitCode)
    {
        if (exitCode == 0) return PreflightDecision.Skip;
        if (exitCode == BaselineExitCode) return PreflightDecision.Baseline;
        return exitCode > 0 ? PreflightDecision.Provision : PreflightDecision.Failed;
    }

    /// <summary>
    /// Whether a manifest item runs in baseline mode. Items opt out with
    /// <c>"baseline": false</c>; everything else in setupassistant is included.
    /// </summary>
    public static bool RunsInBaseline(JsonElement item)
    {
        return !(item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("baseline", out var flag)
            && flag.ValueKind == JsonValueKind.False);
    }
}
