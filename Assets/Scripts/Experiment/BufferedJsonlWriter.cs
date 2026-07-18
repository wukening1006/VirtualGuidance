using System;
using System.Diagnostics;
using System.IO;
using System.Text;

/// <summary>
/// 持有单个 JSONL 文件的会话级 Writer，并按缓冲阈值或时间间隔批量写盘。
/// 文件异常只会使当前日志 Sink 降级，不会向业务调用者抛出异常。
/// </summary>
internal sealed class BufferedJsonlWriter : IDisposable
{
    private const double RetryIntervalSeconds = 10d;

    private readonly string filePath;
    private readonly int bufferSizeBytes;
    private readonly double flushIntervalSeconds;
    private readonly StringBuilder buffer = new StringBuilder(4096);
    private readonly Encoding encoding = new UTF8Encoding(false);

    private StreamWriter writer;
    private long bufferedBytes;
    private int bufferedRecordCount;
    private long lastFlushTimestamp;
    private long nextOpenAttemptTimestamp;
    private bool disposed;

    /// <summary>最近一次打开或写入文件时发生的异常。</summary>
    public Exception LastError { get; private set; }

    /// <summary>由于 Sink 不可用或写盘失败而丢弃的记录数量。</summary>
    public long DroppedRecordCount { get; private set; }

    /// <summary>当前 Writer 对应的 JSONL 绝对路径。</summary>
    public string FilePath => filePath;

    /// <summary>
    /// 创建一个会话级 JSONL Writer。缓冲阈值单位为字节，Flush 间隔单位为秒。
    /// </summary>
    public BufferedJsonlWriter(string filePath, int bufferSizeBytes, double flushIntervalSeconds)
    {
        this.filePath = filePath;
        this.bufferSizeBytes = Math.Max(4096, bufferSizeBytes);
        this.flushIntervalSeconds = Math.Max(0.1d, flushIntervalSeconds);
        lastFlushTimestamp = Stopwatch.GetTimestamp();
        TryOpen();
    }

    /// <summary>
    /// 将一行 JSON 加入内存缓冲；达到阈值、间隔或显式强制时批量写盘。
    /// </summary>
    public bool TryWriteLine(string line, bool autoFlush, bool forceFlush = false)
    {
        if (disposed || !EnsureOpen())
        {
            DroppedRecordCount++;
            return false;
        }

        try
        {
            var value = line ?? string.Empty;
            buffer.AppendLine(value);
            bufferedBytes += encoding.GetByteCount(value) + encoding.GetByteCount(Environment.NewLine);
            bufferedRecordCount++;

            if (forceFlush || autoFlush && (bufferedBytes >= bufferSizeBytes || IsFlushDue()))
            {
                return TryFlush();
            }

            return true;
        }
        catch (Exception exception)
        {
            FailCurrentBatch(exception);
            return false;
        }
    }

    /// <summary>立即将当前内存缓冲写入文件。</summary>
    public bool TryFlush()
    {
        if (disposed)
        {
            return false;
        }

        if (buffer.Length == 0)
        {
            return true;
        }

        if (!EnsureOpen())
        {
            DropBufferedBatch();
            return false;
        }

        try
        {
            writer.Write(buffer.ToString());
            writer.Flush();
            buffer.Clear();
            bufferedBytes = 0;
            bufferedRecordCount = 0;
            lastFlushTimestamp = Stopwatch.GetTimestamp();
            LastError = null;
            return true;
        }
        catch (Exception exception)
        {
            FailCurrentBatch(exception);
            return false;
        }
    }

    /// <summary>Flush 剩余缓冲并释放文件句柄。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        TryFlush();
        disposed = true;
        CloseWriter();
    }

    private bool EnsureOpen()
    {
        if (writer != null)
        {
            return true;
        }

        if (Stopwatch.GetTimestamp() < nextOpenAttemptTimestamp)
        {
            return false;
        }

        return TryOpen();
    }

    private bool TryOpen()
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            writer = new StreamWriter(filePath, true, encoding, 4096)
            {
                AutoFlush = false
            };
            LastError = null;
            lastFlushTimestamp = Stopwatch.GetTimestamp();
            return true;
        }
        catch (Exception exception)
        {
            LastError = exception;
            CloseWriter();
            nextOpenAttemptTimestamp = Stopwatch.GetTimestamp() + SecondsToTimestamp(RetryIntervalSeconds);
            return false;
        }
    }

    private bool IsFlushDue()
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - lastFlushTimestamp;
        return elapsedTicks >= SecondsToTimestamp(flushIntervalSeconds);
    }

    private void FailCurrentBatch(Exception exception)
    {
        LastError = exception;
        DropBufferedBatch();
        CloseWriter();
        nextOpenAttemptTimestamp = Stopwatch.GetTimestamp() + SecondsToTimestamp(RetryIntervalSeconds);
    }

    private void DropBufferedBatch()
    {
        DroppedRecordCount += bufferedRecordCount;
        buffer.Clear();
        bufferedBytes = 0;
        bufferedRecordCount = 0;
    }

    private void CloseWriter()
    {
        if (writer == null)
        {
            return;
        }

        try
        {
            writer.Dispose();
        }
        catch
        {
            // 文件关闭失败不能改变业务流程，最近一次写入异常已由上层限频报告。
        }
        finally
        {
            writer = null;
        }
    }

    private static long SecondsToTimestamp(double seconds)
    {
        return (long)Math.Ceiling(Math.Max(0d, seconds) * Stopwatch.Frequency);
    }
}
