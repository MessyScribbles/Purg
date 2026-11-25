using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Purg.Platform;

public sealed record FileIdentity(long SizeBytes, DateTime LastWriteTimeUtc, string SampledSha256);

public static class SampledFileHash
{
    private const int SampleLength = 64 * 1024;
    private static readonly BlockingCollection<HashRequest> Requests = new();
    private static readonly Thread Worker = StartWorker();

    public static Task<FileIdentity> ComputeAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _ = Worker;
        var completion = new TaskCompletionSource<FileIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        Requests.Add(new HashRequest(path, completion));
        return completion.Task;
    }

    private static Thread StartWorker()
    {
        var thread = new Thread(ProcessRequests)
        {
            IsBackground = true,
            Name = "purg sampled hash worker",
            Priority = ThreadPriority.Lowest
        };
        thread.Start();
        return thread;
    }

    private static void ProcessRequests()
    {
        foreach (var request in Requests.GetConsumingEnumerable())
        {
            try
            {
                request.Completion.TrySetResult(Compute(request.Path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               CryptographicException or NotSupportedException or ArgumentException or
                                               System.Security.SecurityException)
            {
                request.Completion.TrySetException(exception);
            }
        }
    }

    private static FileIdentity Compute(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            SampleLength,
            FileOptions.RandomAccess);
        var size = stream.Length;
        var lastWrite = File.GetLastWriteTimeUtc(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[SampleLength];

        if (size < SampleLength * 3L)
        {
            AppendRange(stream, hash, buffer, 0, size);
        }
        else
        {
            AppendRange(stream, hash, buffer, 0, SampleLength);
            AppendRange(stream, hash, buffer, size / 2 - SampleLength / 2, SampleLength);
            AppendRange(stream, hash, buffer, size - SampleLength, SampleLength);
        }

        Span<byte> sizeBytes = stackalloc byte[sizeof(long)];
        BitConverter.TryWriteBytes(sizeBytes, size);
        hash.AppendData(sizeBytes);
        if (stream.Length != size || File.GetLastWriteTimeUtc(path) != lastWrite)
        {
            throw new IOException("The file changed while its identity was being checked.");
        }

        return new FileIdentity(size, lastWrite, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void AppendRange(
        FileStream stream,
        IncrementalHash hash,
        byte[] buffer,
        long offset,
        long length)
    {
        stream.Position = offset;
        while (length > 0)
        {
            var requested = (int)Math.Min(buffer.Length, length);
            var read = stream.Read(buffer, 0, requested);
            if (read == 0)
            {
                throw new EndOfStreamException("The file changed while its identity was being checked.");
            }

            hash.AppendData(buffer, 0, read);
            length -= read;
        }
    }

    private sealed record HashRequest(string Path, TaskCompletionSource<FileIdentity> Completion);
}
