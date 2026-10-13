using System.Diagnostics;
using OpenReflect.Core;
using OpenReflect.Core.Abstractions;
using OpenReflect.Schedule;
using OpenReflect.GUI;
using Xunit;

/// <summary>Schedule (S31–S33), CLI (S43–S44) and GUI service-path (S47) gates.</summary>
public class OpsTests
{
    private static void AssertSuccess<T>(Result<T> r) where T : notnull
    {
        if (!r.IsSuccess) throw new Exception($"expected success but got: {r.Error}");
    }

    private static void AssertSuccess(Result r)
    {
        if (!r.IsSuccess) throw new Exception($"expected success but got: {r.Error}");
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "ops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static byte[] RandomBuf(int len, int seed) { var b = new byte[len]; new Random(seed).NextBytes(b); return b; }

    private static (string repoRoot, string cli) LocateCli()
    {
        var root = FindRepoRoot();
        var cfg = "Debug";
        return (root, Path.Combine(root, "src", "CLI", "bin", cfg, "net10.0", "OpenReflect.CLI.exe"));
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "OpenReflect.slnx"))) d = d.Parent!;
        return d!.FullName;
    }

    [Fact]
    public async Task S32_TaskScheduler_CreateQueryDelete()
    {
        var dir = TempDir();
        try
        {
            var sched = new SchtasksScheduler();
            var name = $"OpenReflect-Test-{Guid.NewGuid():N}".AsSpan(0, 40).ToString();
            var spec = new JobScheduleSpec(name, "cmd.exe /c exit", dir, ScheduleKind.Daily, "23:59");

            var created = await sched.RegisterAsync(spec);
            AssertSuccess(created);
            try
            {
                var listed = await sched.ListAsync();
                Assert.True(listed.IsSuccess);
                Assert.Contains(listed.Value, s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                var deleted = await sched.DeleteAsync(name);
                AssertSuccess(deleted);
            }
            var after = await sched.ListAsync();
            Assert.True(after.IsSuccess);
            Assert.DoesNotContain(after.Value, s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            // deleting again → typed JobNotFound
            var second = await sched.DeleteAsync(name);
            Assert.False(second.IsSuccess);
            Assert.Equal(ErrorCode.JobNotFound, second.Error.Code);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task S33_JobRunner_DryRun_ValidatesWithoutWriting()
    {
        var dir = TempDir();
        try
        {
            var fixture = Path.Combine(dir, "f.img");
            await File.WriteAllBytesAsync(fixture, RandomBuf(5000, 7));
            var jobJson = Path.Combine(dir, "job.json");
            await File.WriteAllTextAsync(jobJson, $$"""
            {
              "schemaVersion": 1,
              "name": "dry",
              "source": { "path": {{System.Text.Json.JsonSerializer.Serialize(fixture)}} },
              "destination": { "directory": {{System.Text.Json.JsonSerializer.Serialize(dir)}}, "fileNamePrefix": "dryjob" }
            }
            """);
            var runner = new JobRunner();
            var result = await runner.RunAsync(jobJson, dryRun: true);
            AssertSuccess(result);
            Assert.Equal("dry-run", result.Value.SourceLabel);
            Assert.Empty(Directory.GetFiles(dir, "dryjob-*.orf"));   // nothing written

            // missing source → typed failure
            var badJson = Path.Combine(dir, "bad.json");
            await File.WriteAllTextAsync(badJson, $$"""
            { "schemaVersion": 1, "name": "bad", "source": { "path": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir, "nope.img"))}} }, "destination": { "directory": {{System.Text.Json.JsonSerializer.Serialize(dir)}} } }
            """);
            var failed = await runner.RunAsync(badJson, dryRun: true);
            Assert.False(failed.IsSuccess);
            Assert.Equal(ErrorCode.SourceNotFound, failed.Error.Code);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task S43_CLI_Help_Version_Parse()
    {
        var (_, cli) = LocateCli();
        Assert.True(File.Exists(cli), $"CLI not built at {cli}");
        var (code, output) = await RunAsync(cli, "--help");
        Assert.Equal(0, code);
        foreach (var cmd in new[] { "create", "verify", "restore", "schedule", "jobs" })
            Assert.Contains(cmd, output);
        (code, output) = await RunAsync(cli, "--version");
        Assert.Equal(0, code);
        Assert.Contains("OpenReflect", output);
        (code, output) = await RunAsync(cli, "bogus-command");
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task S44_CLI_E2E_CreateVerifyRestore()
    {
        var dir = TempDir();
        try
        {
            var fixture = Path.Combine(dir, "f.img");
            var data = new byte[300 * 1024];
            RandomBuf(150 * 1024, 5).CopyTo(data, 0);          // chunk 1 zero → sparse
            await File.WriteAllBytesAsync(fixture, data);
            var (_, cli) = LocateCli();

            var (code, output) = await RunAsync(cli, $"create --source \"{fixture}\" --dest \"{dir}\" --prefix cli --chunk-size 131072");
            Assert.True(code == 0, $"create exit={code}: {output}");
            Assert.Contains("created", output);
            var image = Directory.GetFiles(dir, "cli-*.orf").Single();

            (code, output) = await RunAsync(cli, $"verify \"{image}\"");
            Assert.True(code == 0, $"verify exit={code}: {output}");
            Assert.Contains("OK", output);

            var target = Path.Combine(dir, "restored.img");
            // safe default: unconfirmed restore must REFUSE (exit 2)
            (code, output) = await RunAsync(cli, $"restore \"{image}\" --target \"{target}\"");
            Assert.True(code == 2, $"unconfirmed restore should refuse, got exit={code}: {output}");
            Assert.False(File.Exists(target));
            // explicit dry-run succeeds and writes nothing
            (code, output) = await RunAsync(cli, $"restore \"{image}\" --target \"{target}\" --dry-run");
            Assert.True(code == 0, $"restore dry exit={code}: {output}");
            Assert.False(File.Exists(target));

            (code, output) = await RunAsync(cli, $"restore \"{image}\" --target \"{target}\" --confirm");
            Assert.True(code == 0, $"restore exit={code}: {output}");
            var restored = await File.ReadAllBytesAsync(target);
            Assert.Equal(data.Length, restored.Length);
            Assert.True(data.AsSpan().SequenceEqual(restored), "CLI restore bytes differ");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output);
    }

    [Fact]
    public void S47_GUI_ServicePath_RunsRealBackup()
    {
        // GUI thread model: STA — the service is what the UI calls; prove it works end-to-end
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dir = TempDir();
                try
                {
                    var fixture = Path.Combine(dir, "f.img");
                    File.WriteAllBytes(fixture, RandomBuf(40000, 9));
                    var service = new GuiBackupService();
                    var r = service.BackupAsync(fixture, dir, "gui", "full", null,
                        new ImageOptions { ChunkSizeBytes = 16 * 1024, Compression = CompressionKind.Deflate }, null).GetAwaiter().GetResult();
                    AssertSuccess(r);
                    var image = Directory.GetFiles(dir, "gui-*.orf").Single();
                    var v = service.VerifyAsync(image, null).GetAwaiter().GetResult();
                    Assert.True(v.Success, string.Join("; ", v.FailedChunks.Select(f => f.Reason)));
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(120_000);
        Assert.Null(failure);
    }

    [Fact]
    public void S45_GUI_MainForm_Instantiates()
    {
        Exception? failure = null;
        MainForm? form = null;
        var thread = new Thread(() =>
        {
            try
            {
                form = new MainForm();
                Assert.Equal("OpenReflect 0.1 — open disk imaging", form.Text);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(30_000);
        Assert.Null(failure);
        Assert.NotNull(form);
        form!.Dispose();
    }
}
