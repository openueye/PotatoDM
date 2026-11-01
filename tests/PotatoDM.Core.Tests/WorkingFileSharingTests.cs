using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Storage;

namespace PotatoDM.Core.Tests;

/// <summary>
/// Regression tests for the Windows sharing modes of the working files.
///
/// Background: every working file is created inside the per-task process
/// directory, which the app removes with <c>Directory.Delete(recursive: true)</c>.
/// On Windows a recursive delete marks each file delete-pending and fails with an
/// IOException while any open handle lacks <c>FileShare.Delete</c>. The handles
/// used to be opened with <c>FileShare.Read</c> only, so a delete that raced a
/// live transfer could never succeed.
///
/// These tests fail on the old sharing mode and pass on the current one, so a
/// future edit that drops <c>FileShare.Delete</c> cannot go unnoticed.
/// </summary>
public class WorkingFileSharingTests
{
    [Fact]
    public async Task NewlyCreatedWorkingFile_AllowsRecursiveDelete_WhileHandleOpen()
    {
        using var temp = new TempDirectory();
        var processDirectory = Path.Combine(temp.Path, StatePaths.ProcessDirectoryName);
        Directory.CreateDirectory(processDirectory);
        var working = Path.Combine(processDirectory, "payload" + StatePaths.WorkingSuffix);

        await using (var file = RandomAccessOutputFile.Create(working, length: 1024))
        {
            await file.WriteAsync(new byte[1024], 0, CancellationToken.None);

            // The handle is still open here; the delete must still succeed.
            Directory.Delete(temp.Path, recursive: true);

            Assert.False(Directory.Exists(temp.Path));
        }
    }

    [Fact]
    public async Task ResumedWorkingFile_AllowsRecursiveDelete_WhileHandleOpen()
    {
        using var temp = new TempDirectory();
        var processDirectory = Path.Combine(temp.Path, StatePaths.ProcessDirectoryName);
        Directory.CreateDirectory(processDirectory);
        var working = Path.Combine(processDirectory, "payload" + StatePaths.WorkingSuffix);

        // Leave a finished-but-not-renamed working file behind, i.e. a job that
        // was resumed: this is the OpenExisting code path.
        await using (var created = RandomAccessOutputFile.Create(working, length: 2048))
        {
            await created.WriteAsync(new byte[2048], 0, CancellationToken.None);
        }

        await using (var resumed = RandomAccessOutputFile.OpenExisting(working, expectedLength: 2048))
        {
            Directory.Delete(temp.Path, recursive: true);

            Assert.False(Directory.Exists(temp.Path));
        }
    }

    /// <summary>
    /// The lock is the mutex itself, not a data file: a second process must never
    /// be able to acquire it. Pinned here because the working files deliberately
    /// share delete and it would be easy to "unify" the lock by mistake.
    /// </summary>
    [Fact]
    public void StateLock_StillExcludesASecondAcquirer()
    {
        using var temp = new TempDirectory();
        var statePath = Path.Combine(temp.Path, "payload" + StatePaths.StateSuffix);

        using var first = StateLock.Acquire(statePath);

        var error = Assert.Throws<JobAlreadyRunningException>(() => StateLock.Acquire(statePath));
        Assert.Contains("already working on", error.Message);
    }
}
