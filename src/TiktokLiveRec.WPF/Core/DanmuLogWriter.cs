#nullable disable
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using TiktokLiveRec.Extensions;
using TiktokLiveRec.Models;

namespace TiktokLiveRec.Core;

internal sealed class DanmuLogWriter : IDisposable
{
    public static DanmuLogWriter Instance { get; } = new();

    private readonly Channel<DanmuMessage> _channel = Channel.CreateUnbounded<DanmuMessage>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    private DanmuLogWriter()
    {
        _worker = Task.Run(ProcessAsync);
    }

    public void Enqueue(DanmuMessage message)
    {
        _ = _channel.Writer.TryWrite(message);
    }

    public string GetLogFolder(string? roomNickname = null)
    {
        string root = Configurations.DanmuLogFolder.Get();

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(SaveFolderHelper.GetSaveFolder(Configurations.SaveFolder.Get()), "danmu");
        }

        string fullPath = SaveFolderHelper.GetSaveFolder(root);

        if (!string.IsNullOrWhiteSpace(roomNickname))
        {
            fullPath = Path.Combine(fullPath, SanitizeFolderName(roomNickname));
        }

        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (DanmuMessage message in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                string folder = GetLogFolder(message.RoomNickname);
                string fileName = Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd}_{SanitizeFolderName(message.RoomNickname)}_danmu.log");
                await File.AppendAllTextAsync(fileName, message.FormatForLog() + Environment.NewLine, Encoding.UTF8, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            _worker.GetAwaiter().GetResult();
        }
        catch
        {
        }

        _cts.Dispose();
    }

    private static string SanitizeFolderName(string name)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = string.Concat(name.Select(ch => invalidChars.Contains(ch) ? '_' : ch));

        int i = sanitized.Length - 1;
        while (i >= 0 && sanitized[i] == '.')
        {
            i--;
        }

        return i == sanitized.Length - 1
            ? sanitized
            : string.Concat(sanitized.AsSpan(0, i + 1), new string('_', sanitized.Length - i - 1));
    }
}
