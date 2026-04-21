#nullable disable
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TiktokLiveRec.Core;

internal enum DouyinWebSocketOpcode : byte
{
    Continuation = 0x0,
    Text = 0x1,
    Binary = 0x2,
    Close = 0x8,
    Ping = 0x9,
    Pong = 0xA,
}

internal sealed class DouyinWebSocketFrame
{
    public DouyinWebSocketOpcode Opcode { get; init; }

    public byte[] Payload { get; init; } = [];

    public int? CloseStatus { get; init; }

    public string CloseDescription { get; init; } = string.Empty;
}

internal sealed class DouyinWebSocketTransport : IDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient _tcpClient;
    private Stream _stream;
    private bool _isDisposed;

    public bool IsOpen { get; private set; }

    public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        _tcpClient = new TcpClient();
        int port = uri.Port > 0 ? uri.Port : uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        await _tcpClient.ConnectAsync(uri.Host, port, cancellationToken);

        Stream stream = _tcpClient.GetStream();
        if (uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            SslStream sslStream = new(stream, false, static (_, _, _, _) => true);
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.Host,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            }, cancellationToken);
            stream = sslStream;
        }

        _stream = stream;

        string secKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        string requestPath = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        StringBuilder request = new();
        request.Append($"GET {requestPath} HTTP/1.1\r\n");
        request.Append($"Host: {uri.Host}:{port}\r\n");
        request.Append("Upgrade: websocket\r\n");
        request.Append("Connection: Upgrade\r\n");
        request.Append($"Sec-WebSocket-Key: {secKey}\r\n");
        request.Append("Sec-WebSocket-Version: 13\r\n");
        foreach ((string key, string value) in headers)
        {
            request.Append($"{key}: {value}\r\n");
        }

        request.Append("\r\n");
        byte[] requestBytes = Encoding.ASCII.GetBytes(request.ToString());
        await _stream.WriteAsync(requestBytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);

        string responseHeader = await ReadHttpHeaderAsync(cancellationToken);
        ValidateHandshake(responseHeader, secKey);
        IsOpen = true;
    }

    public Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken) =>
        SendFrameAsync(DouyinWebSocketOpcode.Binary, payload, cancellationToken);

    public Task SendPingAsync(byte[] payload, CancellationToken cancellationToken) =>
        SendFrameAsync(DouyinWebSocketOpcode.Ping, payload, cancellationToken);

    public Task SendPongAsync(byte[] payload, CancellationToken cancellationToken) =>
        SendFrameAsync(DouyinWebSocketOpcode.Pong, payload, cancellationToken);

    public async Task<DouyinWebSocketFrame> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureConnected();

        byte[] header = await ReadExactlyAsync(2, cancellationToken);
        bool fin = (header[0] & 0x80) != 0;
        DouyinWebSocketOpcode opcode = (DouyinWebSocketOpcode)(header[0] & 0x0F);
        bool masked = (header[1] & 0x80) != 0;
        ulong payloadLength = (ulong)(header[1] & 0x7F);

        if (payloadLength == 126)
        {
            byte[] lengthBytes = await ReadExactlyAsync(2, cancellationToken);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(lengthBytes);
            }

            payloadLength = BitConverter.ToUInt16(lengthBytes, 0);
        }
        else if (payloadLength == 127)
        {
            byte[] lengthBytes = await ReadExactlyAsync(8, cancellationToken);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(lengthBytes);
            }

            payloadLength = BitConverter.ToUInt64(lengthBytes, 0);
        }

        byte[] maskKey = masked ? await ReadExactlyAsync(4, cancellationToken) : null;
        byte[] payload = payloadLength == 0
            ? []
            : await ReadExactlyAsync(checked((int)payloadLength), cancellationToken);
        if (masked && maskKey != null)
        {
            ApplyMask(payload, maskKey);
        }

        if (opcode is DouyinWebSocketOpcode.Binary or DouyinWebSocketOpcode.Text && !fin)
        {
            using MemoryStream combined = new();
            combined.Write(payload, 0, payload.Length);
            while (!fin)
            {
                header = await ReadExactlyAsync(2, cancellationToken);
                fin = (header[0] & 0x80) != 0;
                DouyinWebSocketOpcode continuationOpcode = (DouyinWebSocketOpcode)(header[0] & 0x0F);
                if (continuationOpcode != DouyinWebSocketOpcode.Continuation)
                {
                    throw new InvalidOperationException("Unexpected fragmented websocket frame.");
                }

                masked = (header[1] & 0x80) != 0;
                payloadLength = (ulong)(header[1] & 0x7F);
                if (payloadLength == 126)
                {
                    byte[] lengthBytes = await ReadExactlyAsync(2, cancellationToken);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(lengthBytes);
                    }

                    payloadLength = BitConverter.ToUInt16(lengthBytes, 0);
                }
                else if (payloadLength == 127)
                {
                    byte[] lengthBytes = await ReadExactlyAsync(8, cancellationToken);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(lengthBytes);
                    }

                    payloadLength = BitConverter.ToUInt64(lengthBytes, 0);
                }

                maskKey = masked ? await ReadExactlyAsync(4, cancellationToken) : null;
                byte[] continuationPayload = payloadLength == 0
                    ? []
                    : await ReadExactlyAsync(checked((int)payloadLength), cancellationToken);
                if (masked && maskKey != null)
                {
                    ApplyMask(continuationPayload, maskKey);
                }

                combined.Write(continuationPayload, 0, continuationPayload.Length);
            }

            payload = combined.ToArray();
        }

        return opcode == DouyinWebSocketOpcode.Close
            ? DecodeCloseFrame(payload)
            : new DouyinWebSocketFrame
            {
                Opcode = opcode,
                Payload = payload,
            };
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (!IsOpen || _stream == null)
        {
            return;
        }

        try
        {
            await SendFrameAsync(DouyinWebSocketOpcode.Close, [], cancellationToken);
        }
        catch
        {
        }

        IsOpen = false;
    }

    public void Abort()
    {
        IsOpen = false;
        try
        {
            _stream?.Dispose();
        }
        catch
        {
        }

        try
        {
            _tcpClient?.Close();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Abort();
        _sendLock.Dispose();
    }

    private async Task SendFrameAsync(DouyinWebSocketOpcode opcode, byte[] payload, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureConnected();
        payload ??= [];

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            using MemoryStream frame = new();
            frame.WriteByte((byte)(0x80 | (byte)opcode));

            int payloadLength = payload.Length;
            if (payloadLength <= 125)
            {
                frame.WriteByte((byte)(0x80 | payloadLength));
            }
            else if (payloadLength <= ushort.MaxValue)
            {
                frame.WriteByte(0x80 | 126);
                byte[] lengthBytes = BitConverter.GetBytes((ushort)payloadLength);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(lengthBytes);
                }

                frame.Write(lengthBytes, 0, lengthBytes.Length);
            }
            else
            {
                frame.WriteByte(0x80 | 127);
                byte[] lengthBytes = BitConverter.GetBytes((ulong)payloadLength);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(lengthBytes);
                }

                frame.Write(lengthBytes, 0, lengthBytes.Length);
            }

            byte[] mask = RandomNumberGenerator.GetBytes(4);
            frame.Write(mask, 0, mask.Length);
            byte[] maskedPayload = payload.ToArray();
            ApplyMask(maskedPayload, mask);
            frame.Write(maskedPayload, 0, maskedPayload.Length);

            byte[] bytes = frame.ToArray();
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<string> ReadHttpHeaderAsync(CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] oneByte = new byte[1];
        while (true)
        {
            int bytesRead = await _stream.ReadAsync(oneByte, cancellationToken);
            if (bytesRead <= 0)
            {
                throw new IOException("WebSocket handshake response ended unexpectedly.");
            }

            buffer.WriteByte(oneByte[0]);
            if (buffer.Length >= 4)
            {
                byte[] raw = buffer.GetBuffer();
                int length = (int)buffer.Length;
                if (raw[length - 4] == '\r' && raw[length - 3] == '\n' && raw[length - 2] == '\r' && raw[length - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(buffer.ToArray());
                }
            }
        }
    }

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int bytesRead = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
            if (bytesRead <= 0)
            {
                throw new IOException("WebSocket stream closed unexpectedly.");
            }

            offset += bytesRead;
        }

        return buffer;
    }

    private static void ApplyMask(byte[] payload, byte[] mask)
    {
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] ^= mask[i % 4];
        }
    }

    private static void ValidateHandshake(string responseHeader, string secKey)
    {
        string[] lines = responseHeader.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].Contains("101", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"WebSocket handshake failed: {lines.FirstOrDefault() ?? "empty response"}");
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }

            headers[line[..colonIndex].Trim()] = line[(colonIndex + 1)..].Trim();
        }

        string expectedAccept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(secKey + WebSocketGuid)));
        if (!headers.TryGetValue("Sec-WebSocket-Accept", out string accept) || !string.Equals(accept, expectedAccept, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("WebSocket handshake validation failed.");
        }
    }

    private static DouyinWebSocketFrame DecodeCloseFrame(byte[] payload)
    {
        int? closeStatus = null;
        string description = string.Empty;
        if (payload.Length >= 2)
        {
            closeStatus = (payload[0] << 8) | payload[1];
            if (payload.Length > 2)
            {
                description = Encoding.UTF8.GetString(payload, 2, payload.Length - 2);
            }
        }

        return new DouyinWebSocketFrame
        {
            Opcode = DouyinWebSocketOpcode.Close,
            Payload = payload,
            CloseStatus = closeStatus,
            CloseDescription = description,
        };
    }

    private void EnsureConnected()
    {
        if (!IsOpen || _stream == null)
        {
            throw new InvalidOperationException("WebSocket transport is not connected.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
