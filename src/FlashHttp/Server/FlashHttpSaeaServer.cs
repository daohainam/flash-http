
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FlashHttp.Abstractions;
using Microsoft.Extensions.Logging;

namespace FlashHttp.Server
{
    /// <summary>
    /// High-performance HTTP/1.1 server using SocketAsyncEventArgs for non-TLS scenarios.
    /// Reuses HandlerSet + FlashHttpRequest/Response and the shared FlashHttpParser.
    /// </summary>
    public sealed class FlashHttpSaeaServer : IDisposable
    {
        private readonly FlashHttpServerOptions _options;
        private readonly HandlerSet _handlerSet;
        private readonly ILogger _logger;

        private Socket? _listenSocket;
        private bool _disposed;

        private const int Backlog = 1024;
        private const int BufferSize = 16 * 1024; // 16KB / connection

        private static readonly Encoding Ascii = Encoding.ASCII;

        public FlashHttpSaeaServer(FlashHttpServerOptions options, HandlerSet handlerSet, ILogger logger)
        {
            _options = options;
            _handlerSet = handlerSet;
            _logger = logger;
        }

        public void Start()
        {
            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.Bind(new IPEndPoint(_options.Address, _options.Port));
            _listenSocket.Listen(Backlog);

            var acceptEventArgs = new SocketAsyncEventArgs();
            acceptEventArgs.Completed += AcceptCompleted;

            StartAccept(acceptEventArgs);

            _logger.LogInformation("FlashHttp SAEA HTTP server listening on {Address}:{Port}",
                _options.Address, _options.Port);
        }

        public void Stop()
        {
            if (_listenSocket == null) return;

            try
            {
                _listenSocket.Close();
            }
            catch
            {
                // ignore
            }
            finally
            {
                _listenSocket.Dispose();
                _listenSocket = null;
            }
        }

        private void StartAccept(SocketAsyncEventArgs acceptEventArgs)
        {
            acceptEventArgs.AcceptSocket = null;

            bool pending;
            try
            {
                pending = _listenSocket!.AcceptAsync(acceptEventArgs);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!pending)
            {
                ProcessAccept(acceptEventArgs);
            }
        }

        private void AcceptCompleted(object? sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }

        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            if (e.SocketError != SocketError.Success)
            {
                _logger.LogWarning("Accept failed: {Error}", e.SocketError);
                StartAccept(e);
                return;
            }

            var socket = e.AcceptSocket!;
            socket.NoDelay = true;

            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

            var receiveArgs = new SocketAsyncEventArgs();
            var sendArgs = new SocketAsyncEventArgs();

            var state = new ConnectionState(socket, _handlerSet, buffer, receiveArgs, sendArgs);

            receiveArgs.UserToken = state;
            sendArgs.UserToken = state;

            receiveArgs.SetBuffer(0, buffer.Length);
            receiveArgs.Completed += IOCompleted;
            sendArgs.Completed += IOCompleted;

            StartReceive(receiveArgs);

            // Accept next connection
            StartAccept(e);
        }

        private void IOCompleted(object? sender, SocketAsyncEventArgs e)
        {
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    _ = ProcessReceiveAsync(e);
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend(e);
                    break;
            }
        }

        private void StartReceive(SocketAsyncEventArgs e)
        {
            var state = (ConnectionState)e.UserToken!;
            try
            {
                bool pending = state.Socket.ReceiveAsync(e);
                if (!pending)
                    _ = ProcessReceiveAsync(e);
            }
            catch
            {
                CloseConnection(state);
            }
        }

        private async Task ProcessReceiveAsync(SocketAsyncEventArgs e)
        {
            var state = (ConnectionState)e.UserToken!;

            try
            {
                if (e.SocketError != SocketError.Success || e.BytesTransferred == 0)
                {
                    CloseConnection(state);
                    return;
                }

                int bytesRead = e.BytesTransferred;
                state.BufferCount += bytesRead;

                var seq = new ReadOnlySequence<byte>(state.Buffer, 0, state.BufferCount);
                bool keepAlive = true;

                var localSeq = seq;
                if (!FlashHttpParser.TryReadHttpRequest(
                        ref localSeq,
                        out FlashHttpRequest request,
                        out keepAlive,
                        isHttps: false,
                        remoteEndPoint: state.Socket.RemoteEndPoint as IPEndPoint,
                        localEndPoint: state.Socket.LocalEndPoint as IPEndPoint))
                {
                    e.SetBuffer(state.BufferCount, state.Buffer.Length - state.BufferCount);
                    StartReceive(e);
                    return;
                }

                int consumed = (int)(seq.Length - localSeq.Length);
                seq = localSeq;

                var response = new FlashHttpResponse();

                await state.HandlerSet.HandleAsync(request, response, CancellationToken.None);

                PrepareSend(state, response);

                if (!keepAlive)
                {
                    CloseConnection(state);
                    return;
                }

                int remaining = (int)seq.Length;
                if (remaining > 0 && remaining != state.BufferCount)
                {
                    Buffer.BlockCopy(state.Buffer,
                        state.BufferCount - remaining,
                        state.Buffer,
                        0,
                        remaining);
                }
                state.BufferCount = remaining;

                e.SetBuffer(state.BufferCount, state.Buffer.Length - state.BufferCount);
                StartReceive(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProcessReceiveAsync");
                CloseConnection(state);
            }
        }
        // Optimized response building using a pooled byte[] and ASCII encoding.
        private void PrepareSend(ConnectionState state, FlashHttpResponse response)
        {
            var body = response.Body ?? Array.Empty<byte>();

            bool hasContentLength = false;
            bool hasConnection = false;

            foreach (var h in response.Headers)
            {
                if (h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    hasContentLength = true;
                else if (h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    hasConnection = true;
            }

            if (!hasContentLength)
                response.Headers.Add(new HttpHeader("Content-Length", body.Length.ToString()));

            if (!hasConnection)
                response.Headers.Add(new HttpHeader("Connection", "keep-alive"));

            string reason = response.ReasonPhrase ?? GetReasonPhrase(response.StatusCode);
            string statusCodeText = response.StatusCode.ToString();

            // 1. Compute header byte length (ASCII: 1 char = 1 byte)
            int headerBytesLen = 0;

            headerBytesLen += "HTTP/1.1 ".Length;
            headerBytesLen += statusCodeText.Length;
            headerBytesLen += 1;                // ' '
            headerBytesLen += reason.Length;
            headerBytesLen += 2;                // \r\n

            foreach (var h in response.Headers)
            {
                headerBytesLen += h.Name.Length;
                headerBytesLen += 2;            // ": "
                headerBytesLen += h.Value.Length;
                headerBytesLen += 2;            // \r\n
            }

            headerBytesLen += 2;                // final CRLF

            // 2. Rent buffer for header + body
            int totalLen = headerBytesLen + body.Length;
            state.SendBuffer = ArrayPool<byte>.Shared.Rent(totalLen);

            int pos = 0;

            // 3. Status line
            WriteAscii(state.SendBuffer, ref pos, "HTTP/1.1 ");
            WriteAscii(state.SendBuffer, ref pos, statusCodeText);
            WriteAscii(state.SendBuffer, ref pos, " ");
            WriteAscii(state.SendBuffer, ref pos, reason);
            WriteCRLF(state.SendBuffer, ref pos);

            // 4. Headers
            foreach (var h in response.Headers)
            {
                WriteAscii(state.SendBuffer, ref pos, h.Name);
                WriteAscii(state.SendBuffer, ref pos, ": ");
                WriteAscii(state.SendBuffer, ref pos, h.Value);
                WriteCRLF(state.SendBuffer, ref pos);
            }

            // 5. Blank line
            WriteCRLF(state.SendBuffer, ref pos);

            // 6. Body
            if (body.Length > 0)
            {
                Buffer.BlockCopy(body, 0, state.SendBuffer, pos, body.Length);
                pos += body.Length;
            }

            state.SendOffset = 0;
            state.SendCount = pos;

            var sendArgs = state.SendEventArgs;
            sendArgs.SetBuffer(state.SendBuffer, state.SendOffset, state.SendCount);

            StartSend(sendArgs);
        }

        private static void WriteAscii(byte[] buffer, ref int pos, string text)
        {
            var span = buffer.AsSpan(pos);
            int written = Ascii.GetBytes(text.AsSpan(), span);
            pos += written;
        }

        private static void WriteCRLF(byte[] buffer, ref int pos)
        {
            buffer[pos++] = (byte)'\r';
            buffer[pos++] = (byte)'\n';
        }

        private void StartSend(SocketAsyncEventArgs e)
        {
            var state = (ConnectionState)e.UserToken!;
            try
            {
                bool pending = state.Socket.SendAsync(e);
                if (!pending)
                    ProcessSend(e);
            }
            catch
            {
                CloseConnection(state);
            }
        }

        private void ProcessSend(SocketAsyncEventArgs e)
        {
            var state = (ConnectionState)e.UserToken!;

            if (e.SocketError != SocketError.Success)
            {
                CloseConnection(state);
                return;
            }

            state.SendOffset += e.BytesTransferred;
            state.SendCount -= e.BytesTransferred;

            if (state.SendCount > 0)
            {
                e.SetBuffer(state.SendOffset, state.SendCount);
                StartSend(e);
            }
            else
            {
                // done sending
                ArrayPool<byte>.Shared.Return(state.SendBuffer);
                state.SendBuffer = Array.Empty<byte>();
                // receive loop will continue from ProcessReceive -> StartReceive
            }
        }

        private void CloseConnection(ConnectionState state)
        {
            try { state.Socket.Shutdown(SocketShutdown.Both); } catch { }
            try { state.Socket.Close(); } catch { }
            try { state.Socket.Dispose(); } catch { }

            ArrayPool<byte>.Shared.Return(state.Buffer);
            if (state.SendBuffer.Length > 0)
                ArrayPool<byte>.Shared.Return(state.SendBuffer);
        }

        private static string GetReasonPhrase(int statusCode)
            => statusCode switch
            {
                200 => "OK",
                400 => "Bad Request",
                401 => "Unauthorized",
                404 => "Not Found",
                500 => "Internal Server Error",
                _   => "Unknown"
            };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        // ====== per-connection state ======

        private sealed class ConnectionState
        {
            public Socket Socket { get; }
            public HandlerSet HandlerSet { get; }

            public byte[] Buffer { get; }
            public int BufferCount;

            public SocketAsyncEventArgs ReceiveEventArgs { get; }
            public SocketAsyncEventArgs SendEventArgs { get; }

            public byte[] SendBuffer = Array.Empty<byte>();
            public int SendOffset;
            public int SendCount;

            public ConnectionState(Socket socket,
                                   HandlerSet handlerSet,
                                   byte[] buffer,
                                   SocketAsyncEventArgs receiveArgs,
                                   SocketAsyncEventArgs sendArgs)
            {
                Socket = socket;
                HandlerSet = handlerSet;
                Buffer = buffer;
                ReceiveEventArgs = receiveArgs;
                SendEventArgs = sendArgs;
            }
        }
    }
}
