﻿// -----------------------------------------------------------------------
// <copyright file="FakeMqttTcpServer.cs" company="Petabridge, LLC">
//      Copyright (C) 2024 - 2024 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Akka.Event;
using TurboMqtt.Protocol;

namespace TurboMqtt.IO.Tcp;

internal sealed class MqttTcpServerOptions
{
    public MqttTcpServerOptions(string host, int port)
    {
        Host = host;
        Port = port;
    }

    /// <summary>
    /// Would love to just do IPV6, but that still meets resistance everywhere
    /// </summary>
    public AddressFamily AddressFamily { get; set; } = AddressFamily.Unspecified;

    /// <summary>
    /// Frames are limited to this size in bytes. A frame can contain multiple packets.
    /// </summary>
    public int MaxFrameSize { get; set; } = 128 * 1024; // 128kb

    public string Host { get; }

    public int Port { get; }
}

/// <summary>
/// A fake TCP server that can be used to simulate the behavior of a real MQTT broker.
/// </summary>
internal sealed class FakeMqttTcpServer
{
    private readonly MqttProtocolVersion _version;
    private readonly MqttTcpServerOptions _options;
    private readonly CancellationTokenSource _shutdownTcs = new();
    private readonly ILoggingAdapter _log;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _clientCts = new();
    private readonly TimeSpan _heatBeatDelay;
    private readonly IFakeServerHandleFactory _handleFactory;
    private Socket? _bindSocket;
    
    public int BoundPort { get; private set; }

    public FakeMqttTcpServer(MqttTcpServerOptions options, MqttProtocolVersion version, ILoggingAdapter log, TimeSpan heartbeatDelay, IFakeServerHandleFactory handleFactory)
    {
        _options = options;
        _version = version;
        _log = log;
        _heatBeatDelay = heartbeatDelay;
        _handleFactory = handleFactory;

        if (_version == MqttProtocolVersion.V5_0)
            throw new NotSupportedException("V5.0 not supported.");
    }

    public void Bind()
    {
        if (_bindSocket != null)
            throw new InvalidOperationException("Cannot bind the same server twice.");

        if (_options.AddressFamily == AddressFamily.Unspecified) // allows use of dual mode IPv4 / IPv6
        {
            _bindSocket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveBufferSize = _options.MaxFrameSize * 2,
                SendBufferSize = _options.MaxFrameSize * 2,
                DualMode = true,
                NoDelay = true,
                LingerState = new LingerOption(false, 0)
            };
        }
        else
        {
            _bindSocket = new Socket(_options.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveBufferSize = _options.MaxFrameSize * 2,
                SendBufferSize = _options.MaxFrameSize * 2,
                DualMode = true,
                NoDelay = true,
                LingerState = new LingerOption(false, 0)
            };
        }

        var hostAddress = Dns.GetHostAddresses(_options.Host).First();

        _bindSocket.Bind(new IPEndPoint(hostAddress, _options.Port));
        _bindSocket.Listen(100);
        
        BoundPort = _bindSocket!.LocalEndPoint is IPEndPoint ipEndPoint ? ipEndPoint.Port : 0;

        // begin the accept loop
        _ = BeginAcceptAsync();
    }

    public bool TryKickClient(string clientId)
    {
        if (_clientCts.TryRemove(clientId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        return false;
    }

    public void Shutdown()
    {
        _log.Info("Shutting down server.");
        try
        {
            _shutdownTcs.Cancel();
            _bindSocket?.Close();
        }
        catch (Exception)
        {
            // do nothing - this method is idempotent
        }
    }

    private async Task BeginAcceptAsync()
    {
        while (!_shutdownTcs.IsCancellationRequested)
        {
            var socket = await _bindSocket!.AcceptAsync();
            _ = ProcessClientAsync(socket);
        }
    }

    private async Task ProcessClientAsync(Socket socket)
    {
        using (socket)
        {
            Memory<byte> buffer = new byte[_options.MaxFrameSize];
            var closed = false;
            var handle = _handleFactory.CreateServerHandle(PushMessage, ClosingAction, _log, _version, _heatBeatDelay);
            var clientShutdownCts = new CancellationTokenSource();
            _ = handle.WhenClientIdAssigned.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    _clientCts.TryAdd(t.Result, clientShutdownCts);
                }
            }, clientShutdownCts.Token);

            var linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(clientShutdownCts.Token, _shutdownTcs.Token);
            while (!linkedCts.IsCancellationRequested)
            {
                if (closed)
                    break;
                try
                {
                    var bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None, linkedCts.Token);
                    if (bytesRead == 0)
                    {
                        _log.Info("Client {0} disconnected from server.",
                            handle.WhenClientIdAssigned.IsCompletedSuccessfully
                                ? handle.WhenClientIdAssigned.Result
                                : "unknown");
                        socket.Close();
                        return;
                    }

                    Memory<byte> newBuffer = new byte[bytesRead];
                    buffer.Slice(0, bytesRead).CopyTo(newBuffer);
                    // process the incoming message, send any necessary replies back
                    handle.HandleBytes(newBuffer);
                }
                catch (OperationCanceledException)
                {
                    _log.Warning("Client {0} is being disconnected from server.",
                        handle.WhenClientIdAssigned.IsCompletedSuccessfully
                            ? handle.WhenClientIdAssigned.Result
                            : "unknown");
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Error processing message from client {0}.",
                        handle.WhenClientIdAssigned.IsCompletedSuccessfully
                            ? handle.WhenClientIdAssigned.Result
                            : "unknown");
                    return;
                }
            }

            // send a disconnect message
            if(!closed)
                // send a disconnect message
                handle.DisconnectFromServer();

            return;

            bool PushMessage((IMemoryOwner<byte> buffer, int estimatedSize) msg)
            {
                try
                {
                    if (socket.Connected)
                    {
                        var sent = socket.Send(msg.buffer.Memory.Span.Slice(0, msg.estimatedSize));
                        while (sent < msg.estimatedSize)
                        {
                            if (sent == 0) return false; // we are shutting down

                            var remaining = msg.buffer.Memory.Slice(sent);
                            var sent2 = socket.Send(remaining.Span);
                            if (sent2 == remaining.Length)
                                sent += sent2;
                            else
                                return false;
                        }

                        return true;
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Error writing to client.");
                    return false;
                }
                finally
                {
                    msg.buffer.Dispose(); // release any shared memory
                }
            }

            Task ClosingAction()
            {
                if (socket.Connected) socket.Close();
                closed = true;
                return Task.CompletedTask;
            }
        }
    }
}