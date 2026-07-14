using AiMentor.Application;

namespace AiMentor.Infrastructure;

/// <summary>生产和普通开发路径使用的空执行屏障，不改变工具调用时序。</summary>
public sealed class NoOpToolExecutionBarrier : IToolExecutionBarrier
{
    public static NoOpToolExecutionBarrier Instance { get; } = new();

    private NoOpToolExecutionBarrier() { }

    public Task WaitAfterExecutingAsync(string executionKey, string toolName,
        CancellationToken cancellationToken = default)
    {
        _ = executionKey;
        _ = toolName;
        return Task.CompletedTask;
    }
}

/// <summary>配置受控故障验收使用的信号文件、释放文件和轮询间隔。</summary>
public sealed class FileToolExecutionBarrierOptions
{
    public required string SignalPath { get; init; }
    public required string ReleasePath { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(25);
}

/// <summary>
/// 在账本已进入 Executing 时原子发布文件信号，并等待测试驱动释放。
/// 此类型不自行注入故障，只为外部验收脚本提供确定、可观察的强杀窗口。
/// </summary>
public sealed class FileToolExecutionBarrier : IToolExecutionBarrier
{
    private readonly FileToolExecutionBarrierOptions _options;

    public FileToolExecutionBarrier(FileToolExecutionBarrierOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SignalPath) || string.IsNullOrWhiteSpace(options.ReleasePath))
            throw new ArgumentException("执行屏障的信号文件和释放文件路径不能为空。", nameof(options));
        if (options.PollInterval <= TimeSpan.Zero || options.PollInterval > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(options), "执行屏障轮询间隔必须大于零且不超过一秒。");

        _options = options;
    }

    public async Task WaitAfterExecutingAsync(string executionKey, string toolName,
        CancellationToken cancellationToken = default)
    {
        var signalPath = Path.GetFullPath(_options.SignalPath);
        var releasePath = Path.GetFullPath(_options.ReleasePath);
        var signalDirectory = Path.GetDirectoryName(signalPath)
            ?? throw new InvalidOperationException("执行屏障信号路径没有有效目录。");
        Directory.CreateDirectory(signalDirectory);

        // 临时文件和原子替换避免验收脚本读到只写入一半的执行标识。
        var temporaryPath = signalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, $"{executionKey}|{toolName}", cancellationToken);
            File.Move(temporaryPath, signalPath, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        while (!File.Exists(releasePath))
            await Task.Delay(_options.PollInterval, cancellationToken);
    }
}
