using OpenReflect.Core;
using OpenReflect.Core.Abstractions;
using OpenReflect.Restore;
using OpenReflect.Schedule;
using OpenReflect.Verify;

namespace OpenReflect.GUI;

/// <summary>The service layer the GUI talks to — deliberately separated so the exact code path the
/// UI runs is unit-testable (S47).</summary>
public sealed class GuiBackupService
{
    public ImageEngine Engine { get; } = new();
    public Verifier Verifier { get; } = new();
    public Restorer Restorer { get; } = new();
    public SchtasksScheduler Scheduler { get; } = new();

    public Task<Result<ImageHeaderInfo>> BackupAsync(string source, string destDir, string prefix, string kind,
        string? parentImage, ImageOptions options, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.orf");
        return kind switch
        {
            "incremental" => Engine.CreateIncrementalAsync(source, parentImage ?? throw new ArgumentException("parent required"), dest, options, progress, ct),
            "differential" => Engine.CreateDifferentialAsync(source, parentImage ?? throw new ArgumentException("parent required"), dest, options, progress, ct),
            _ => Engine.CreateFullAsync(source, dest, options, progress, ct),
        };
    }

    public Task<VerifyResult> VerifyAsync(string image, string? password, CancellationToken ct = default)
        => Verifier.VerifyAsync(image, VerifyDepth.Full, null, password is null ? null : () => password, ct);

    public Task<Result<RestorePlan>> PlanRestoreAsync(string image, string target, string? password, CancellationToken ct = default)
        => Restorer.PlanAsync(image, target, password is null ? null : () => password, ct);

    public Task<Result> RestoreAsync(RestorePlan plan, bool confirmed, string? password, CancellationToken ct = default)
        => Restorer.ExecuteAsync(plan, new RestoreOptions { DryRun = false, Confirmed = confirmed, PasswordProvider = password is null ? null : () => password }, null, ct);
}
