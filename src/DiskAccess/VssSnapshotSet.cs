using Alphaleonis.Win32.Vss;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.DiskAccess;

/// <summary>One VSS snapshot set over several volumes (crash-consistent-with-writers backup
/// context). Shadow devices stay valid until disposal; DeleteSnapshotSet always runs (VSS leaks are
/// the classic backup-tool failure mode).</summary>
public sealed class VssSnapshotSet : IAsyncDisposable
{
    private readonly IVssBackupComponents _backup;
    private readonly Guid _snapshotSetId;
    private bool _deleted;

    /// <summary>volumeGuidPath (\\?\Volume{guid}\) → shadow device object
    /// (\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN).</summary>
    public IReadOnlyDictionary<string, string> ShadowDevices { get; }

    private VssSnapshotSet(IVssBackupComponents backup, Guid set, Dictionary<string, string> devices)
    {
        _backup = backup; _snapshotSetId = set; ShadowDevices = devices;
    }

    public static Result<VssSnapshotSet> Create(IEnumerable<string> volumeGuidPaths, ILogSink? log = null)
    {
        var volumes = volumeGuidPaths.ToList();
        if (volumes.Count == 0) return Error.Of(ErrorCode.VssFailure, "no VSS-capable volumes to snapshot");
        IVssBackupComponents? backup = null;
        try
        {
            var factory = VssFactoryProvider.Default.GetVssFactory();
            backup = factory.CreateVssBackupComponents();
            backup.InitializeForBackup(null);
            backup.SetContext(VssSnapshotContext.Backup);
            backup.SetBackupState(selectComponents: false, backupBootableSystemState: false, VssBackupType.Full, partialFileSupport: false);
            backup.GatherWriterMetadata();
            var set = backup.StartSnapshotSet();
            var snapshotIds = new List<Guid>();
            foreach (var v in volumes)
                snapshotIds.Add(backup.AddToSnapshotSet(v));
            backup.PrepareForBackup();
            backup.DoSnapshotSet();

            var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in snapshotIds)
            {
                var props = backup.GetSnapshotProperties(id);
                if (string.IsNullOrEmpty(props.SnapshotDeviceObject))
                    throw new InvalidOperationException($"VSS returned no shadow device for {props.OriginalVolumeName}");
                devices[props.OriginalVolumeName] = props.SnapshotDeviceObject;
            }
            log?.Write(LogLevel.Info, "vss", $"snapshot set {set}: {string.Join(", ", devices.Values)}");
            return new VssSnapshotSet(backup, set, devices);
        }
        catch (Exception ex)
        {
            TryCleanup(backup);
            return Error.Of(ErrorCode.VssFailure, "VSS snapshot failed", ex.Message);
        }
    }

    private static void TryCleanup(IVssBackupComponents? backup)
    {
        try { backup?.Dispose(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_deleted) return;
        _deleted = true;
        try
        {
            _backup.DeleteSnapshotSet(_snapshotSetId, forceDelete: true);
            _backup.BackupComplete();
        }
        catch { /* disposal is best-effort; VSS also reaps shadow copies by lifetime */ }
        _backup.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
