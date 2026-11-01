using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;

namespace PotatoDM.Core.Tests;

public class JsonDownloadStateStoreTests
{
    private static JobState CreateState(string target, string working)
    {
        var created = new DateTimeOffset(2024, 3, 1, 10, 30, 0, TimeSpan.Zero);

        var state = new JobState
        {
            Url = "http://127.0.0.1:5099/dl?token=unit",
            FinalUrl = "http://127.0.0.1:5099/real?token=unit",
            ETag = "\"v1\"",
            LastModified = "2024-03-01T10:00:00.0000000+00:00",
            TotalLength = 1000,
            Mode = DownloadMode.Segmented,
            TargetFile = target,
            WorkingFile = working,
            Status = JobStatus.Downloading,
            CreatedUtc = created,
            UpdatedUtc = created,
        };

        state.Segments.Add(new DownloadSegment
        {
            Index = 0, Start = 0, EndExclusive = 400, BytesDone = 25,
        });
        state.Segments.Add(new DownloadSegment
        {
            Index = 1, Start = 400, EndExclusive = 1000, BytesDone = 0,
        });

        return state;
    }

    [Fact]
    public async Task Save_and_load_round_trip()
    {
        using var temp = new TempDirectory();
        var store = new JsonDownloadStateStore();
        var path = temp.FileFor("file.bin.download.json");
        var state = CreateState(temp.FileFor("file.bin"), temp.FileFor("file.bin.potatodm-part"));

        await store.SaveAsync(path, state, default);
        var loaded = await store.LoadAsync(path, default);

        Assert.NotNull(loaded);
        Assert.Equal(state.Url, loaded!.Url);
        Assert.Equal(state.FinalUrl, loaded.FinalUrl);
        Assert.Equal(state.ETag, loaded.ETag);
        Assert.Equal(state.LastModified, loaded.LastModified);
        Assert.Equal(1000, loaded.TotalLength);
        Assert.Equal(DownloadMode.Segmented, loaded.Mode);
        Assert.Equal(JobStatus.Downloading, loaded.Status);
        Assert.Equal(state.CreatedUtc, loaded.CreatedUtc);
        Assert.Equal(2, loaded.Segments.Count);
        Assert.Equal(25, loaded.Segments[0].BytesDone);
        Assert.Equal(400, loaded.Segments[1].Start);
        Assert.Equal(25, loaded.BytesDownloaded);
    }

    [Fact]
    public async Task Save_leaves_no_temporary_file_behind()
    {
        using var temp = new TempDirectory();
        var store = new JsonDownloadStateStore();
        var path = temp.FileFor("file.bin.download.json");

        await store.SaveAsync(
            path,
            CreateState(temp.FileFor("file.bin"), temp.FileFor("file.bin.potatodm-part")),
            default);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Second_save_keeps_the_previous_snapshot_as_backup()
    {
        using var temp = new TempDirectory();
        var store = new JsonDownloadStateStore();
        var path = temp.FileFor("file.bin.download.json");
        var state = CreateState(temp.FileFor("file.bin"), temp.FileFor("file.bin.potatodm-part"));

        await store.SaveAsync(path, state, default);
        state.Segments[0].BytesDone = 50;
        await store.SaveAsync(path, state, default);

        Assert.True(File.Exists(path + ".bak"));

        var current = await store.LoadAsync(path, default);
        var backup = await store.LoadAsync(path + ".bak", default);

        Assert.Equal(50, current!.Segments[0].BytesDone);
        Assert.Equal(25, backup!.Segments[0].BytesDone);
    }

    [Fact]
    public async Task Corrupt_primary_falls_back_to_backup()
    {
        using var temp = new TempDirectory();
        var store = new JsonDownloadStateStore();
        var path = temp.FileFor("file.bin.download.json");
        var state = CreateState(temp.FileFor("file.bin"), temp.FileFor("file.bin.potatodm-part"));

        await store.SaveAsync(path, state, default);
        state.Segments[0].BytesDone = 75;
        await store.SaveAsync(path, state, default);
        await File.WriteAllTextAsync(path, "{ this is not json");

        var loaded = await store.LoadAsync(path, default);

        Assert.NotNull(loaded);
        Assert.Equal(25, loaded!.Segments[0].BytesDone);
    }

    [Fact]
    public async Task Corrupt_primary_and_backup_is_a_state_error()
    {
        using var temp = new TempDirectory();
        var store = new JsonDownloadStateStore();
        var path = temp.FileFor("file.bin.download.json");
        await File.WriteAllTextAsync(path, "garbage");
        await File.WriteAllTextAsync(path + ".bak", "garbage");

        await Assert.ThrowsAsync<StateException>(() => store.LoadAsync(path, default));
    }

    [Fact]
    public async Task Empty_state_file_is_a_state_error()
    {
        using var temp = new TempDirectory();
        var store = new JsonDownloadStateStore();
        var path = temp.FileFor("file.bin.download.json");
        await File.WriteAllTextAsync(path, string.Empty);

        await Assert.ThrowsAsync<StateException>(() => store.LoadAsync(path, default));
    }

    [Fact]
    public async Task Missing_state_returns_null()
    {
        using var temp = new TempDirectory();
        var store = new JsonDownloadStateStore();

        Assert.Null(await store.LoadAsync(temp.FileFor("nope.download.json"), default));
    }

    [Fact]
    public async Task Orphan_backup_is_loaded_when_the_primary_is_missing()
    {
        using var temp = new TempDirectory();
        var store = new JsonDownloadStateStore();
        var path = temp.FileFor("file.bin.download.json");

        await store.SaveAsync(
            path,
            CreateState(temp.FileFor("file.bin"), temp.FileFor("file.bin.potatodm-part")),
            default);
        File.Move(path, path + ".bak");

        var loaded = await store.LoadAsync(path, default);

        Assert.NotNull(loaded);
        Assert.Equal(1000, loaded!.TotalLength);
    }

    [Fact]
    public async Task Unsupported_schema_version_is_rejected()
    {
        using var temp = new TempDirectory();
        var store = new JsonDownloadStateStore();
        var path = temp.FileFor("file.bin.download.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":999}");

        await Assert.ThrowsAsync<StateException>(() => store.LoadAsync(path, default));
    }

    [Fact]
    public void Snapshot_freezes_progress_without_aliasing_the_live_state()
    {
        var state = CreateState(
            "C:\\downloads\\file.bin",
            "C:\\downloads\\file.bin.potatodm-part");
        state.Segments[1].BytesDone = 10;

        var snapshot = state.Snapshot();

        state.Segments[0].BytesDone = 300;
        state.Segments[1].BytesDone = 600;
        state.Segments.Add(new DownloadSegment
        {
            Index = 2, Start = 1000, EndExclusive = 1200,
        });
        state.Status = JobStatus.Completed;

        Assert.NotSame(state.Segments, snapshot.Segments);
        Assert.NotSame(state.Segments[0], snapshot.Segments[0]);
        Assert.Equal(2, snapshot.Segments.Count);
        Assert.Equal(25, snapshot.Segments[0].BytesDone);
        Assert.Equal(10, snapshot.Segments[1].BytesDone);
        Assert.Equal(35, snapshot.BytesDownloaded);
        Assert.Equal(JobStatus.Downloading, snapshot.Status);
        Assert.Equal(state.Url, snapshot.Url);
        Assert.Equal(state.CreatedUtc, snapshot.CreatedUtc);
    }

    [Fact]
    public async Task Save_creates_missing_directories()
    {
        using var temp = new TempDirectory();
        var store = new JsonDownloadStateStore();
        var path = System.IO.Path.Combine(temp.Path, "nested", "dir", "file.download.json");

        await store.SaveAsync(
            path,
            CreateState(temp.FileFor("file.bin"), temp.FileFor("file.bin.potatodm-part")),
            default);

        Assert.True(File.Exists(path));
    }
}

public class RandomAccessOutputFileTests
{
    [Fact]
    public async Task Writes_at_absolute_offsets_and_renames_on_completion()
    {
        using var temp = new TempDirectory();
        var working = temp.FileFor("data.potatodm-part");
        var target = temp.FileFor("data.bin");

        await using (var file = RandomAccessOutputFile.Create(working, 100))
        {
            Assert.Equal(100, file.Length);
            await file.WriteAsync(new byte[] { 1, 2, 3 }, 10, default);
            await file.WriteAsync(new byte[] { 9 }, 99, default);
            await file.CompleteAsync(target, default);
        }

        Assert.False(File.Exists(working));
        Assert.True(File.Exists(target));

        var bytes = await File.ReadAllBytesAsync(target);
        Assert.Equal(100, bytes.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes[10..13]);
        Assert.Equal(9, bytes[99]);
        Assert.Equal(0, bytes[0]);
    }

    [Fact]
    public async Task Flush_to_disk_is_safe_before_and_after_completion()
    {
        using var temp = new TempDirectory();
        var working = temp.FileFor("data.potatodm-part");
        var target = temp.FileFor("data.bin");

        await using (var file = RandomAccessOutputFile.Create(working, 1024))
        {
            await file.WriteAsync(new byte[] { 1, 2, 3 }, 0, default);
            file.FlushToDisk();
            await file.CompleteAsync(target, default);
            file.FlushToDisk();
        }

        Assert.True(File.Exists(target));
        Assert.Equal(new byte[] { 1, 2, 3 }, (await File.ReadAllBytesAsync(target))[..3]);
    }

    [Fact]
    public async Task Open_existing_requires_the_expected_length()
    {
        using var temp = new TempDirectory();
        var working = temp.FileFor("data.potatodm-part");
        await File.WriteAllBytesAsync(working, new byte[10]);

        var error = Assert.Throws<StateException>(
            () => RandomAccessOutputFile.OpenExisting(working, 20));

        Assert.Contains("10", error.Message);
    }

    [Fact]
    public void Open_existing_reports_a_missing_file()
    {
        using var temp = new TempDirectory();

        Assert.Throws<StateException>(
            () => RandomAccessOutputFile.OpenExisting(temp.FileFor("missing.part"), 10));
    }

    [Fact]
    public async Task Sparse_working_file_keeps_unwritten_ranges_unallocated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var working = temp.FileFor("data.potatodm-part");
        const long length = 32L * 1024 * 1024;
        const long writeOffset = 16L * 1024 * 1024;

        await using (var file = RandomAccessOutputFile.Create(working, length, sparse: true))
        {
            Assert.True(file.IsSparse, "the temporary filesystem refused the sparse flag");
            await file.WriteAsync(new byte[64 * 1024], writeOffset, default);
            file.FlushToDisk();
        }

        Assert.Equal(length, new FileInfo(working).Length);

        var allocated = SparseFile.GetAllocatedSize(working);
        Assert.NotNull(allocated);
        Assert.True(
            allocated < 4L * 1024 * 1024,
            $"expected the untouched 32 MiB range to stay sparse, {allocated} bytes allocated");

        using var stream = File.OpenRead(working);
        stream.Seek(writeOffset - 4096, SeekOrigin.Begin);
        Assert.Equal(0, stream.ReadByte());
    }

    [Fact]
    public async Task Non_sparse_working_file_is_not_marked_sparse()
    {
        using var temp = new TempDirectory();
        var working = temp.FileFor("data.potatodm-part");

        await using var file = RandomAccessOutputFile.Create(working, 1024);

        Assert.False(file.IsSparse);
        Assert.Equal(1024, new FileInfo(working).Length);
    }

    [Fact]
    public async Task Open_existing_allows_continuing_at_the_right_length()
    {
        using var temp = new TempDirectory();
        var working = temp.FileFor("data.potatodm-part");
        await File.WriteAllBytesAsync(working, new byte[16]);

        await using (var file = RandomAccessOutputFile.OpenExisting(working, 16))
        {
            await file.WriteAsync(new byte[] { 7 }, 15, default);
        }

        Assert.Equal(7, (await File.ReadAllBytesAsync(working))[15]);
    }
}

public class StatePathTests
{
    [Fact]
    public void Derives_sidecar_and_working_paths()
    {
        var target = "C:\\downloads\\file.bin";

        Assert.Equal(target + ".download.json", StatePaths.StateFor(target));
        Assert.Equal(target + ".potatodm-part", StatePaths.WorkingFor(target));
    }

    [Fact]
    public void Resolves_state_from_a_working_file()
    {
        using var temp = new TempDirectory();
        var working = temp.FileFor("file.bin.potatodm-part");

        Assert.Equal(
            temp.FileFor("file.bin.download.json"),
            StatePaths.ResolveStatePath(working));
    }

    [Fact]
    public void Leaves_state_paths_untouched()
    {
        using var temp = new TempDirectory();
        var state = temp.FileFor("file.bin.download.json");

        Assert.Equal(state, StatePaths.ResolveStatePath(state));
    }

    [Fact]
    public void Rejects_empty_paths()
    {
        Assert.Throws<StateException>(() => StatePaths.ResolveStatePath(""));
    }

    [Fact]
    public void Target_resolver_uses_a_directory_with_the_suggested_name()
    {
        using var temp = new TempDirectory();

        var result = TargetPathResolver.Resolve(temp.Path, "hello.bin");

        Assert.Equal(temp.FileFor("hello.bin"), result);
    }

    [Fact]
    public void Target_resolver_creates_parent_directories()
    {
        using var temp = new TempDirectory();
        var target = System.IO.Path.Combine(temp.Path, "sub", "deep", "file.bin");

        var result = TargetPathResolver.Resolve(target, null);

        Assert.Equal(target, result);
        Assert.True(Directory.Exists(System.IO.Path.Combine(temp.Path, "sub", "deep")));
    }

    [Fact]
    public async Task Target_resolver_avoids_overwriting_an_existing_file()
    {
        using var temp = new TempDirectory();
        var existing = temp.FileFor("file.bin");
        await File.WriteAllTextAsync(existing, "keep me");

        var result = TargetPathResolver.Resolve(existing, null);

        Assert.Equal(temp.FileFor("file (1).bin"), result);
        Assert.Equal("keep me", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task Unique_names_increase_until_free()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(temp.FileFor("file.bin"), "");
        await File.WriteAllTextAsync(temp.FileFor("file (1).bin"), "");

        Assert.Equal(temp.FileFor("file (2).bin"),
            TargetPathResolver.MakeUnique(temp.FileFor("file.bin")));
    }
}
