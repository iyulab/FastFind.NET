using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FastFind.Models;
using Microsoft.Extensions.Logging;

namespace FastFind.Windows.Implementation;

/// <summary>
/// .NET 9 최적화된 비동기 파일 열거자 - Memory&lt;T&gt; 활용
/// </summary>
internal sealed class AsyncFileEnumerator : IAsyncDisposable
{
    private readonly ILogger<AsyncFileEnumerator> _logger;
    private readonly MemoryPool<byte> _memoryPool = MemoryPool<byte>.Shared;
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed = false;

    public AsyncFileEnumerator(ILogger<AsyncFileEnumerator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Memory&lt;T&gt;를 활용한 고효율 비동기 파일 열거
    /// </summary>
    public async IAsyncEnumerable<FastFileItem> EnumerateAsync(
        ReadOnlyMemory<string> locations,
        IndexingOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposeCts.Token);
        var token = combinedCts.Token;

        // Channel 생성 - .NET 9 백프레셔 지원
        var channelOptions = new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

        var channel = Channel.CreateBounded<FastFileItem>(channelOptions);
        var writer = channel.Writer;
        var reader = channel.Reader;

        // 비동기 생산자 시작
        var producer = ProduceFileItemsAsync(locations, options, writer, token);

        try
        {
            // 소비자: 메모리 효율적 스트림 처리
            await foreach (var item in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 정상적인 취소
            }
        }
    }

    /// <summary>
    /// Memory Pool을 활용한 비동기 파일 생산자
    /// </summary>
    private async Task ProduceFileItemsAsync(
        ReadOnlyMemory<string> locations,
        IndexingOptions options,
        ChannelWriter<FastFileItem> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 2, locations.Length)
            };

            // Memory<string>을 Span으로 변환하여 병렬 처리
            var locationSpan = locations.Span;
            var locationArray = locationSpan.ToArray(); // 병렬 처리용 배열 변환

            await Parallel.ForEachAsync(locationArray, parallelOptions, async (location, ct) =>
            {
                await ProcessLocationWithMemoryPoolAsync(location, options, writer, ct).ConfigureAwait(false);
            });
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// Memory Pool을 활용한 단일 위치 처리
    /// </summary>
    private async ValueTask ProcessLocationWithMemoryPoolAsync(
        string location,
        IndexingOptions options,
        ChannelWriter<FastFileItem> writer,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(location))
            return;

        // Memory Pool에서 버퍼 임대
        using var memoryOwner = _memoryPool.Rent(4096); // 4KB 버퍼
        var buffer = new List<FastFileItem>(1000);

        try
        {
            var enumerationOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = !options.MaxDepth.HasValue || options.MaxDepth.Value > 1,
                ReturnSpecialDirectories = false,
                AttributesToSkip = GetAttributesToSkip(options),
                MaxRecursionDepth = options.MaxDepth ?? int.MaxValue
            };

            // 비동기 파일 시스템 열거 (가능한 경우)
            await foreach (var entry in EnumerateFileSystemEntriesAsync(location, enumerationOptions, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var fileItem = await CreateFastFileItemAsync(entry, memoryOwner.Memory, cancellationToken).ConfigureAwait(false);
                if (fileItem.HasValue && ShouldIncludeFile(fileItem.Value, options))
                {
                    buffer.Add(fileItem.Value);

                    // 배치 처리 - 백프레셔 지원
                    if (buffer.Count >= 100)
                    {
                        await WriteBufferWithBackpressureAsync(buffer, writer, cancellationToken).ConfigureAwait(false);
                        buffer.Clear();
                    }
                }
            }

            // 남은 항목들 처리
            if (buffer.Count > 0)
            {
                await WriteBufferWithBackpressureAsync(buffer, writer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상적인 취소
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing location: {Location}", location);
        }
    }

    /// <summary>
    /// .NET 9 비동기 파일 시스템 열거 (향후 확장 가능)
    /// </summary>
    private async IAsyncEnumerable<string> EnumerateFileSystemEntriesAsync(
        string location,
        EnumerationOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 현재는 동기 방식을 비동기로 래핑 (향후 진짜 비동기 I/O로 교체 예정)
        var entries = Directory.EnumerateFileSystemEntries(location, "*", options);

        var count = 0;
        foreach (var entry in entries)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            // CPU 양보 (10000개마다)
            if (++count % 10000 == 0)
            {
                await Task.Yield();
            }

            yield return entry;
        }
    }

    /// <summary>
    /// Memory를 활용한 비동기 파일 정보 생성
    /// </summary>
    private async ValueTask<FastFileItem?> CreateFastFileItemAsync(
        string filePath,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            // 비동기 파일 정보 조회 (Task.Run으로 래핑)
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var attributes = File.GetAttributes(filePath);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;

                if (isDirectory)
                {
                    var dirInfo = new DirectoryInfo(filePath);
                    return new FastFileItem(
                        dirInfo.FullName,
                        dirInfo.Name,
                        dirInfo.Parent?.FullName ?? string.Empty,
                        string.Empty,
                        0,
                        dirInfo.CreationTime,
                        dirInfo.LastWriteTime,
                        dirInfo.LastAccessTime,
                        attributes,
                        dirInfo.FullName.Length > 0 ? dirInfo.FullName[0] : '\0'
                    );
                }
                else
                {
                    var fileInfo = new FileInfo(filePath);
                    return new FastFileItem(
                        fileInfo.FullName,
                        fileInfo.Name,
                        fileInfo.DirectoryName ?? string.Empty,
                        fileInfo.Extension,
                        fileInfo.Length,
                        fileInfo.CreationTime,
                        fileInfo.LastWriteTime,
                        fileInfo.LastAccessTime,
                        attributes,
                        fileInfo.FullName.Length > 0 ? fileInfo.FullName[0] : '\0'
                    );
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 백프레셔 지원 버퍼 쓰기
    /// </summary>
    private static async ValueTask WriteBufferWithBackpressureAsync(
        List<FastFileItem> buffer,
        ChannelWriter<FastFileItem> writer,
        CancellationToken cancellationToken)
    {
        foreach (var item in buffer)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // 백프레셔: 채널이 가득 차면 대기
            while (await writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                if (writer.TryWrite(item))
                    break;

                // 짧은 대기 후 재시도
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool ShouldIncludeFile(FastFileItem file, IndexingOptions options)
    {
        if (!options.IncludeHidden && file.IsHidden)
            return false;

        if (!options.IncludeSystem && file.IsSystem)
            return false;

        if (options.MaxFileSize.HasValue && file.Size > options.MaxFileSize.Value)
            return false;

        return true;
    }

    private static FileAttributes GetAttributesToSkip(IndexingOptions options)
    {
        var attributesToSkip = FileAttributes.Normal;

        if (!options.IncludeHidden)
            attributesToSkip |= FileAttributes.Hidden;

        if (!options.IncludeSystem)
            attributesToSkip |= FileAttributes.System;

        return attributesToSkip;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _disposeCts.Cancel();
            _disposeCts.Dispose();
            _memoryPool.Dispose();
        }
    }
}