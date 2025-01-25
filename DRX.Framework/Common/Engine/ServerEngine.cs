using DRX.Framework.Common.Args;
using DRX.Framework.Common.Base;
using DRX.Framework.Common.Components;
using DRX.Framework.Common.Enums;
using DRX.Framework.Common.Enums.Packet;
using DRX.Framework.Common.Models;
using DRX.Framework.Common.Pool;
using DRX.Framework.Common.Utility;
using DRX.Framework.Extensions;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using DRX.Framework.Common.Interface;

namespace DRX.Framework.Common.Engine;

/// <summary>
/// 抽象服务器类，负责管理客户端连接、事件处理和数据传输。
/// </summary>
public abstract class ServerEngine : DrxBehaviour, IEngine
{
    /* 服务器引擎类型 */
    public EngineType Type => EngineType.Server;

    #region 字段
    /// <summary>
    /// 服务器Socket实例。
    /// </summary>
    protected DRXSocket Socket;

    /// <summary>
    /// 服务器监听端口。
    /// </summary>
    protected int Port;

    /// <summary>
    /// 服务器IP地址。
    /// </summary>
    protected string Ip = string.Empty;

    /// <summary>
    /// 存储连接的客户端Socket。
    /// </summary>
    protected readonly ConcurrentDictionary<DRXSocket, byte> Clients = new();

    /// <summary>
    /// 存储等待响应的请求。
    /// </summary>
    protected readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> PendingRequests = new();

    /// <summary>
    /// 消息队列池。
    /// </summary>
    protected readonly DrxQueuePool MessageQueue;

    /// <summary>
    /// 缓冲区大小常量。
    /// </summary>
    protected const int BufferSize = 8192;

    /// <summary>
    /// 加密密钥（可选）。
    /// </summary>
    protected string? Key;
    #endregion

    #region 事件
    /// <summary>
    /// 发生错误时触发的事件。
    /// </summary>
    public event EventHandler<NetworkEventArgs>? OnError;

    /// <summary>
    /// 服务器启动时触发的事件。
    /// </summary>
    public event EventHandler<NetworkEventArgs>? OnServerStarted;

    /// <summary>
    /// 客户端连接时触发的事件。
    /// </summary>
    public event EventHandler<NetworkEventArgs>? OnClientConnected;

    /// <summary>
    /// 客户端断开连接时触发的事件。
    /// </summary>
    public event EventHandler<NetworkEventArgs>? OnClientDisconnected;

    /// <summary>
    /// 接收到数据时触发的事件。
    /// </summary>
    public event EventHandler<NetworkEventArgs>? OnDataReceived;

    /// <summary>
    /// 验证客户端时触发的事件。
    /// </summary>
    public event EventHandler<NetworkEventArgs>? OnVerifyClient;

    /// <summary>
    /// 数据发送完成时触发的事件。
    /// </summary>
    public event EventHandler<NetworkEventArgs>? OnDataSent;

    /// <summary>
    /// 命令执行完成时触发的事件。
    /// </summary>
    public event EventHandler<NetworkEventArgs>? OnCommandExecuted;
    /// <summary>
    /// 当客户端被封禁时触发的事件。
    /// </summary>
    public EventHandler<NetworkEventArgs>? OnClientBlocked;
    /// <summary>
    /// 当客户端被解封时触发的事件。
    /// </summary>
    public EventHandler<NetworkEventArgs>? OnUnBlockedClient;
    #endregion

    #region 构造函数
    /// <summary>
    /// 初始化DRXServer的新实例。
    /// </summary>
    protected ServerEngine()
    {
        Socket = new DRXSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // 初始化消息队列
        MessageQueue = new DrxQueuePool(
            maxChannels: Environment.ProcessorCount,
            maxQueueSize: 10000,
            defaultDelay: 500
        );

        Initialize();
    }

    /// <summary>
    /// 使用指定参数初始化DRXServer的新实例。
    /// </summary>
    /// <param name="maxChannels">最大通道数。</param>
    /// <param name="maxQueueSize">最大队列大小。</param>
    /// <param name="defaultDelay">默认延迟时间。</param>
    protected ServerEngine(int maxChannels, int maxQueueSize, int defaultDelay)
    {
        Socket = new DRXSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // 初始化消息队列
        MessageQueue = new DrxQueuePool(
            maxChannels: maxChannels,
            maxQueueSize: maxQueueSize,
            defaultDelay: defaultDelay
        );

        Initialize();
    }

    /// <summary>
    /// 初始化服务器组件和事件订阅。
    /// </summary>
    protected void Initialize()
    {
        // 订阅队列事件
        MessageQueue.ItemFailed += (_, args) =>
        {
            OnError?.Invoke(this, new NetworkEventArgs(
                socket: null!,
                eventType: NetworkEventType.HandlerEvent,
                message: $"消息处理失败: {args.Exception.Message}"
            ));
        };

        var pg = AddComponent<PermissionGroup>();
        pg.SetPermissionGroup(PermissionGroupType.Terminal);
    }
    #endregion

    #region 核心方法
    /// <summary>
    /// 启动服务器。
    /// </summary>
    /// <param name="ip">服务器IP地址。</param>
    /// <param name="port">服务器端口。</param>
    public virtual void Start(string ip, int port)
    {
        try
        {
            InitializeServer(ip, port);
            // InitializeEvent();
            StartListening();
            NotifyServerStarted();
            BeginReceiveCommand();
        }
        catch (Exception ex)
        {
            HandleStartupError(ex);
        }
    }

    /// <summary>
    /// 初始化服务器配置。
    /// </summary>
    /// <param name="ip">服务器IP地址。</param>
    /// <param name="port">服务器端口。</param>
    protected virtual void InitializeServer(string ip, int port)
    {
        Ip = ip;
        Port = port;
        Socket.Bind(new IPEndPoint(IPAddress.Parse(Ip), Port));
    }

    /// <summary>
    /// 开始监听客户端连接。
    /// </summary>
    protected virtual void StartListening()
    {
        Socket.Listen(10);
        _ = Socket.BeginAccept(AcceptCallback, null);
    }

    /// <summary>
    /// 通知服务器已启动。
    /// </summary>
    protected virtual void NotifyServerStarted()
    {
        OnServerStarted?.Invoke(this, new NetworkEventArgs(
            socket: Socket,
            eventType: NetworkEventType.HandlerEvent,
            message: $"服务器已启动，监听 {Ip}:{Port}"
        ));
    }

    /// <summary>
    /// 处理服务器启动时的错误。
    /// </summary>
    /// <param name="ex">异常信息。</param>
    protected virtual void HandleStartupError(Exception ex)
    {
        OnError?.Invoke(this, new NetworkEventArgs(
            socket: Socket,
            eventType: NetworkEventType.HandlerEvent,
            message: $"启动服务器时发生错误: {ex.Message}(若能尝试连接服务器成功，则无视该错误)"
        ));
    }

    /// <summary>
    /// 停止服务器。
    /// </summary>
    public virtual void Stop()
    {
        if (Socket?.IsBound != true) return;

        try
        {
            StopMessageQueue();
            CloseServerSocket();
            ClearConnections();
        }
        catch (Exception ex)
        {
            HandleStopError(ex);
        }
    }

    /// <summary>
    /// 停止消息队列。
    /// </summary>
    protected virtual void StopMessageQueue()
    {
        MessageQueue.Stop();
        MessageQueue.Dispose();
    }

    /// <summary>
    /// 关闭服务器Socket。
    /// </summary>
    protected virtual void CloseServerSocket()
    {
        Socket.Close();
    }

    /// <summary>
    /// 清理所有客户端连接。
    /// </summary>
    protected virtual void ClearConnections()
    {
        foreach (var client in Clients.Keys)
        {
            _ = HandleDisconnectAsync(client);
        }
        Clients.Clear();
    }

    /// <summary>
    /// 处理服务器停止时的错误。
    /// </summary>
    /// <param name="ex">异常信息。</param>
    protected virtual void HandleStopError(Exception ex)
    {
        OnError?.Invoke(this, new NetworkEventArgs(
            socket: Socket,
            eventType: NetworkEventType.HandlerEvent,
            message: $"停止服务器时发生错误: {ex.Message}"
        ));
    }
    #endregion

    #region 客户端连接管理

    /// <summary>
    /// 封禁客户端。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <param name="timeH">封禁时长（小时）。</param>
    public virtual void BlockClient(DRXSocket clientSocket, int timeH)
    {
        var client = clientSocket.GetComponent<ClientComponent>();
        if (client == null) return;

        var path = DrxFile.BanPath;

        // 设置封禁时间
        client.Ban(DateTime.Now.AddHours(timeH));

        // 写入封禁信息到文件
        _ = DrxFile.WriteJsonKeyAsync(path, client.UID, client.UnBandedDate).ConfigureAwait(false);

        client.SaveToFile(DrxFile.UserPath);

        _ = HandleDisconnectAsync(clientSocket);
        OnClientBlocked?.Invoke(this, new NetworkEventArgs(clientSocket));
    }

    /// <summary>
    /// 解封客户端。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    public virtual void UnBlockClient(DRXSocket clientSocket)
    {
        var client = clientSocket.GetComponent<ClientComponent>();
        if (client == null) return;

        var path = DrxFile.BanPath;

        // 移除封禁信息
        _ = DrxFile.RemoveJsonKeyAsync(path, client.UID).ConfigureAwait(false);

        client.UnBan();
        OnUnBlockedClient?.Invoke(this, new NetworkEventArgs(clientSocket));
    }

    /// <summary>
    /// 开始检查被封禁的客户端。
    /// </summary>
    public virtual void BeginCheckBlockClient()
    {
        _ = AddTask(CheckBlockClient, 1000 * 60, "check_block_client");
    }

    /// <summary>
    /// 检查被封禁的客户端。
    /// </summary>
    protected virtual void CheckBlockClient()
    {
        var sockets = GetConnectedSockets();
        foreach (var socket in sockets)
        {
            var client = socket.GetComponent<ClientComponent>();
            if (client == null)
            {
                // 断开没有ClientComponent的连接
                socket.Disconnect(false);
                continue;
            }
            if (client.IsBannded)
            {
                if (DateTime.Now > client.UnBandedDate)
                {
                    client.UnBan();
                }
                else
                {
                    _ = HandleDisconnectAsync(socket);
                }
            }
        }
    }

    /// <summary>
    /// 处理客户端连接回调。
    /// </summary>
    /// <param name="ar">异步操作结果。</param>
    protected virtual async void AcceptCallback(IAsyncResult ar)
    {
        DRXSocket? clientSocket = null;
        clientSocket = AcceptClientSocket(ar);
        try
        {
            if (clientSocket != null)
            {
                await HandleNewClientAsync(clientSocket);
            }

        }
        catch (Exception ex)
        {
            await HandleAcceptErrorAsync(clientSocket, ex);
        }
        finally
        {
            ContinueAccepting();
        }
    }

    /// <summary>
    /// 接受客户端Socket连接。
    /// </summary>
    /// <param name="ar">异步操作结果。</param>
    /// <returns>转换后的DRXSocket对象，如果失败返回null。</returns>
    protected virtual DRXSocket? AcceptClientSocket(IAsyncResult ar)
    {
        var baseSocket = Socket.EndAccept(ar);
        return baseSocket.TakeOver<DRXSocket>();
    }

    /// <summary>
    /// 处理新的客户端连接。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    protected virtual async Task HandleNewClientAsync(DRXSocket clientSocket)
    {
        if (Clients.TryAdd(clientSocket, 1))
        {
            await InitializeClientSocket(clientSocket);
            BeginReceive(clientSocket);
        }
        else
        {
            clientSocket.Close();
        }
    }

    /// <summary>
    /// 初始化客户端Socket。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    protected virtual Task InitializeClientSocket(DRXSocket clientSocket)
    {
        _ = clientSocket.AddComponent<Verify>();
        _ = clientSocket.AddComponent<PermissionGroup>();

        OnClientConnected?.Invoke(this, new NetworkEventArgs(
            socket: clientSocket,
            eventType: NetworkEventType.HandlerEvent
        ));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理接受连接时的错误。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <param name="ex">异常信息。</param>
    protected virtual Task HandleAcceptErrorAsync(DRXSocket? clientSocket, Exception ex)
    {
        OnError?.Invoke(this, new NetworkEventArgs(
            socket: clientSocket,
            eventType: NetworkEventType.HandlerEvent,
            message: $"处理客户端连接时发生错误: {ex.Message}"
        ));
        clientSocket?.Close();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 继续接受新的客户端连接。
    /// </summary>
    protected virtual void ContinueAccepting()
    {
        if (Socket?.IsBound != true) return;
        try
        {
            Socket.BeginAccept(AcceptCallback, null);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, new NetworkEventArgs(
                socket: Socket,
                eventType: NetworkEventType.HandlerEvent,
                message: $"继续接受连接时发生错误: {ex.Message}"
            ));
        }
    }

    /// <summary>
    /// 断开指定客户端连接。
    /// </summary>
    /// <param name="clientSocket">要断开的客户端Socket。</param>
    /// <returns>断开操作是否成功。</returns>
    public virtual bool DisconnectClient(DRXSocket clientSocket)
    {
        try
        {
            _ = HandleDisconnectAsync(clientSocket);
            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, new NetworkEventArgs(
                socket: clientSocket,
                eventType: NetworkEventType.HandlerEvent,
                message: $"断开客户端连接时发生错误: {ex.Message}"
            ));
            return false;
        }
    }

    /// <summary>
    /// 处理客户端断开连接。
    /// </summary>
    /// <param name="clientSocket">断开连接的客户端Socket。</param>
    protected virtual async Task HandleDisconnectAsync(DRXSocket clientSocket)
    {
        try
        {
            await HandleClientDisconnection(clientSocket);
        }
        catch (Exception ex)
        {
            await HandleDisconnectErrorAsync(clientSocket, ex);
        }
    }

    /// <summary>
    /// 处理客户端断开连接的具体逻辑。
    /// </summary>
    /// <param name="clientSocket">断开连接的客户端Socket。</param>
    protected virtual Task HandleClientDisconnection(DRXSocket clientSocket)
    {
        if (Clients.TryRemove(clientSocket, out _))
        {
            OnClientDisconnected?.Invoke(this, new NetworkEventArgs(
                socket: clientSocket,
                eventType: NetworkEventType.HandlerEvent
            ));
        }
        CloseSocketSafely(clientSocket);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理断开连接时的错误。
    /// </summary>
    /// <param name="clientSocket">客户端Socket。</param>
    /// <param name="ex">异常信息。</param>
    protected virtual Task HandleDisconnectErrorAsync(DRXSocket clientSocket, Exception ex)
    {
        OnError?.Invoke(this, new NetworkEventArgs(
            socket: clientSocket,
            eventType: NetworkEventType.HandlerEvent,
            message: $"处理断开连接时发生错误: {ex.Message}"
        ));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 安全关闭Socket连接。
    /// </summary>
    /// <param name="socket">要关闭的Socket对象。</param>
    protected virtual void CloseSocketSafely(Socket socket)
    {
        try
        {
            if (!socket.Connected) return;
            try { socket.Shutdown(SocketShutdown.Both); }
            catch
            {
                // ignored
            } // 忽略潜在的异常
            finally { socket.Close(); }
        }
        catch
        {
            // ignored
        } // 忽略关闭过程中的异常
    }

    /// <summary>
    /// 启动客户端验证任务。
    /// </summary>
    [Obsolete("这个方法目前已经不应该再使用，因为这是一个臃肿的方法，因此我们将不再对其进行维护。")]
    public virtual void BeginVerifyClient()
    {
        _ = AddTask(VerifyClientTask, 1000 * 60, "verify_client");
        OnDataReceived += VerifyClientHeartBeat;
    }

    /// <summary>
    /// 验证客户端心跳包。
    /// </summary>
    /// <param name="sender">事件发送者。</param>
    /// <param name="args">网络事件参数。</param>
    [Obsolete("这个方法目前已经不应该再使用，因为这是一个臃肿的方法，因此我们将不再对其进行维护。")]
    protected virtual async void VerifyClientHeartBeat(object? sender, NetworkEventArgs args)
    {
        OnVerifyClient?.Invoke(this, args);  // 通知客户端连接验证事件

        if (args.Socket is not { } socket) return;
        var client = socket.GetComponent<Verify>();
        if (client == null) return;
        client.UpdateLastActiveTime();

        /* 这里回应客户端一个心跳包 */
        var responsePacket = new DRXPacket()
        {
            Headers =
            {
                { "type", "heartbeat" }
            },
            Data = { { "message", "pong" } }
        };
        Send(socket, responsePacket, Key);
    }

    /// <summary>
    /// 允许子类重写以实现自定义的客户端验证逻辑。
    /// </summary>
    protected virtual void VerifyClientTask()
    {
        var sockets = GetConnectedSockets();
        foreach (var socket in sockets)
        {
            var clientVerify = socket.GetComponent<Verify>();

            if (clientVerify == null)
            {
                // 断开没有ClientComponent的连接
                socket.Disconnect(false);
                continue;
            }

            /* 检查客户端是否长时间未活动 */
            var lastActiveTime = clientVerify.GetLastActiveTime();

            /* 超过5分钟未活动则断开连接 */
            if (!((DateTime.Now - lastActiveTime).TotalMinutes > 5)) continue;
            Logger.Log(LogLevel.Info, "Server", $"客户端 {socket.RemoteEndPoint} 由于长时间未活动而被断开");
            _ = DisconnectClient(socket);
        }
    }
    #endregion

    // --------------------------------------------------------------------------------- 命令接收系统
    /// <summary>
    /// 开始接收命令。
    /// </summary>
    protected virtual void BeginReceiveCommand()
    {
        OnDataReceived += (sender, args) =>
        {
            if (args.Packet == null) return;
            var packet = DRXPacket.Unpack(args.Packet, Key);

            var type = packet.Headers["type"];

            HandleCommandPacket(packet, args.Socket);
            
        };
    }

    #region 数据处理
    // --------------------------------------------------------------------------------- 数据包接收系统
    /// <summary>
    /// 开始接收数据。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    protected virtual void BeginReceive(DRXSocket clientSocket)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            _ = clientSocket.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None,
                ar =>
                {
                    try
                    {
                        HandleDataReceived(ar, clientSocket, buffer);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }, null);
        }
        catch (Exception)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _ = HandleDisconnectAsync(clientSocket);
        }
    }

    /// <summary>
    /// 处理接收到的数据。
    /// </summary>
    /// <param name="ar">异步操作结果。</param>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <param name="buffer">数据缓冲区。</param>
    protected virtual void HandleDataReceived(IAsyncResult ar, DRXSocket clientSocket, byte[] buffer)
    {
        try
        {
            int bytesRead = clientSocket.EndReceive(ar);
            if (bytesRead > 0)
            {
                // 将数据处理委托给消息队列
                var data = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);

                _ = MessageQueue.PushAsync(
                    () => ProcessReceivedData(clientSocket, data), 0);
                BeginReceive(clientSocket);
            }
            else
            {
                _ = HandleDisconnectAsync(clientSocket);
            }
        }
        catch
        {
            _ = HandleDisconnectAsync(clientSocket);
        }
    }

    /// <summary>
    /// 处理接收到的数据包。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <param name="data">接收到的数据。</param>
    protected virtual void ProcessReceivedData(DRXSocket clientSocket, byte[] data)
    {
        try
        {
            // 解析收到的数据包
            var receivedPacket = DRXPacket.Unpack(data, Key);

            // 获取请求ID
            var requestId = receivedPacket.Headers.TryGetValue(PacketHeaderKey.RequestID, out var header)
                ? header?.ToString()
                : null;

            // 如果存在请求ID且在等待队列中,则完成对应的TaskCompletionSource 
            if (!string.IsNullOrEmpty(requestId) && PendingRequests.TryRemove(requestId, out var tcs))
            {
                tcs.SetResult(data);
            }

            // 触发数据接收事件
            OnDataReceived?.Invoke(this, new NetworkEventArgs(
                socket: clientSocket,
                eventType: NetworkEventType.HandlerEvent,
                packet: data
            ));
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, new NetworkEventArgs(
                socket: clientSocket,
                eventType: NetworkEventType.HandlerEvent,
                message: $"处理数据时发生错误: {ex.Message}"
            ));
        }
    }
    #endregion

    #region 工具方法
    /// <summary>
    /// 获取所有已连接的客户端Socket。
    /// </summary>
    /// <returns>连接的客户端Socket集合。</returns>
    public virtual HashSet<DRXSocket> GetConnectedSockets()
    {
        return [.. Clients.Keys];
    }

    /// <summary>
    /// 根据UID获取客户端Socket。
    /// </summary>
    /// <param name="uid">客户端UID。</param>
    /// <returns>对应的客户端Socket，如果未找到则返回null。</returns>
    public virtual DRXSocket? GetClientByUid(string uid)
    {
        return Clients.Keys.FirstOrDefault(client =>
        {
            var clientComponent = client.GetComponent<ClientComponent>();
            return clientComponent != null && clientComponent.UID == uid;
        });
    }
    #endregion

    /// <summary>
    /// 销毁时停止服务器。
    /// </summary>
    protected override void OnDestroy()
    {
        Stop();
        base.OnDestroy();
    }

    #region 消息发送
    /// <summary>
    /// 预处理待发送的数据。
    /// </summary>
    /// <param name="data">发送的数据。</param>
    /// <returns>处理后的数据缓冲区。</returns>
    protected virtual byte[] PrepareDataForSend(byte[] data)
    {
        var sendBuffer = ArrayPool<byte>.Shared.Rent(data.Length);
        Buffer.BlockCopy(data, 0, sendBuffer, 0, data.Length);
        return sendBuffer;
    }

    /// <summary>
    /// 创建发送回调。
    /// </summary>
    /// <param name="buffer">发送缓冲区。</param>
    /// <returns>发送完成的回调。</returns>
    protected virtual AsyncCallback CreateSendCallback(byte[] buffer)
    {
        return ar =>
        {
            try
            {
                HandleSendCallback(ar);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        };
    }

    /// <summary>
    /// 验证客户端连接状态。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <returns>如果客户端有效则返回true，否则返回false。</returns>
    protected virtual bool ValidateClientForSend(DRXSocket clientSocket)
    {
        if (!Clients.ContainsKey(clientSocket))
        {
            OnError?.Invoke(this, new NetworkEventArgs(
                socket: clientSocket,
                eventType: NetworkEventType.HandlerEvent,
                message: "客户端未连接"
            ));
            return false;
        }
        return true;
    }

    /// <summary>
    /// 执行实际的数据发送。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <param name="buffer">发送缓冲区。</param>
    /// <param name="length">发送的数据长度。</param>
    protected virtual void ExecuteSend(DRXSocket clientSocket, byte[] buffer, int length)
    {
        _ = clientSocket.BeginSend(
            buffer,
            0,
            length,
            SocketFlags.None,
            CreateSendCallback(buffer),
            clientSocket
        );
    }

    /// <summary>
    /// 处理发送完成的回调。
    /// </summary>
    /// <param name="ar">异步操作结果。</param>
    protected virtual void HandleSendCallback(IAsyncResult ar)
    {
        if (ar.AsyncState is not DRXSocket clientSocket) return;

        try
        {
            var bytesSent = clientSocket.EndSend(ar);
            OnDataSent?.Invoke(this, new NetworkEventArgs(
                socket: clientSocket,
                eventType: NetworkEventType.HandlerEvent,
                message: $"Successfully sent {bytesSent} bytes"
            ));
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, new NetworkEventArgs(
                socket: clientSocket,
                eventType: NetworkEventType.HandlerEvent,
                message: $"发送数据时发生错误: {ex.Message}"
            ));
            _ = HandleDisconnectAsync(clientSocket);
        }
    }

    /// <summary>
    /// 发送完成时触发。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <param name="bytesSent">发送的字节数。</param>
    protected virtual void OnSendComplete(DRXSocket clientSocket, int bytesSent)
    {
        OnDataSent?.Invoke(this, new NetworkEventArgs(
            socket: clientSocket,
            eventType: NetworkEventType.HandlerEvent,
            message: $"Successfully sent {bytesSent} bytes"
        ));
    }

    /// <summary>
    /// 发送错误时触发。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <param name="ex">异常信息。</param>
    protected virtual void OnSendError(DRXSocket clientSocket, Exception ex)
    {
        OnError?.Invoke(this, new NetworkEventArgs(
            socket: clientSocket,
            eventType: NetworkEventType.HandlerEvent,
            message: $"发送数据时发生错误: {ex.Message}"
        ));
        _ = HandleDisconnectAsync(clientSocket);
    }

    /// <summary>
    /// 向指定客户端发送数据。
    /// </summary>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <param name="packet">网络数据包。</param>
    /// <param name="key">加密密钥。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual void Send(DRXSocket clientSocket, NetworkPacket packet, string key)
    {
        try
        {
            if (!ValidateClientForSend(clientSocket)) return;
            var data = packet.Serialize(key);

            var sendBuffer = PrepareDataForSend(data);
            ExecuteSend(clientSocket, sendBuffer, data.Length);
        }
        catch (Exception ex)
        {
            OnSendError(clientSocket, ex);
        }
    }

    /// <summary>
    /// 向指定客户端发送泛型数据包。
    /// </summary>
    /// <typeparam name="T">数据包类型，必须继承BasePacket。</typeparam>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <param name="packet">数据包对象。</param>
    /// <param name="key">加密密钥。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual void Send<T>(DRXSocket clientSocket, T packet, string key) where T : BasePacket<T>, new()
    {
        try
        {
            if (!ValidateClientForSend(clientSocket)) return;

            packet.Headers.Add(PacketHeaderKey.RequestID, Guid.NewGuid().ToString());   // 添加请求 ID
            // 将泛型包序列化为字节数组
            var packetData = packet.Pack(key);

            var sendBuffer = PrepareDataForSend(packetData);
            ExecuteSend(clientSocket, sendBuffer, packetData.Length);
        }
        catch (Exception ex)
        {
            OnSendError(clientSocket, ex);
        }
    }

    /// <summary>
    /// 发送带请求ID的响应数据包。
    /// </summary>
    /// <typeparam name="T">数据包类型，必须继承BasePacket。</typeparam>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <param name="originPacket">原始请求的数据包。</param>
    /// <param name="responsePacket">响应的数据包。</param>
    /// <param name="key">加密密钥。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual void Send<T>(DRXSocket clientSocket, T originPacket, T responsePacket, string key) where T : BasePacket<T>, new()
    {
        try
        {
            if (!ValidateClientForSend(clientSocket))
            {
                Logger.Log(LogLevel.Warning, "Server", "客户端未连接");
                return;
            };

            var originPacketRequestId = originPacket.Headers[PacketHeaderKey.RequestID];
            responsePacket.Headers.Add(PacketHeaderKey.RequestID, originPacketRequestId);

            var packetData = responsePacket.Pack(key);
            var sendBuffer = PrepareDataForSend(packetData);
            ExecuteSend(clientSocket, sendBuffer, packetData.Length);
        }
        catch (Exception ex)
        {
            OnSendError(clientSocket, ex);
        }
    }

    /// <summary>
    /// 发送带请求ID的数据包并等待响应。
    /// </summary>
    /// <typeparam name="T">数据包类型，必须继承BasePacket。</typeparam>
    /// <param name="clientSocket">客户端Socket对象。</param>
    /// <param name="packet">发送的数据包。</param>
    /// <param name="key">加密密钥。</param>
    /// <param name="timeout">等待超时时间(毫秒)，0表示无超时。</param>
    /// <returns>客户端响应的数据包字节数组。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual async Task<byte[]> SendAsync<T>(DRXSocket clientSocket, T packet, string key, int timeout = 0) where T : BasePacket<T>, new()
    {
        try
        {
            if (!ValidateClientForSend(clientSocket))
            {
                Logger.Log(LogLevel.Warning, "Server", "客户端未连接");
                throw new InvalidOperationException("客户端未连接");
            }

            // 添加请求ID 
            var requestId = Guid.NewGuid().ToString();
            packet.Headers.Add(PacketHeaderKey.RequestID, requestId);

            // 准备TaskCompletionSource用于等待响应
            var tcs = new TaskCompletionSource<byte[]>();
            if (!PendingRequests.TryAdd(requestId, tcs))
            {
                throw new InvalidOperationException("无法添加待处理请求");
            }

            try
            {
                // 发送数据包
                var packetData = packet.Pack(key);
                var sendBuffer = PrepareDataForSend(packetData);
                ExecuteSend(clientSocket, sendBuffer, packetData.Length);

                // 等待响应或超时
                if (timeout > 0)
                {
                    if (await Task.WhenAny(tcs.Task, Task.Delay(timeout)) != tcs.Task)
                    {
                        PendingRequests.TryRemove(requestId, out _);
                        throw new TimeoutException("等待响应超时");
                    }
                }

                return await tcs.Task;
            }
            finally
            {
                // 确保请求被移除
                PendingRequests.TryRemove(requestId, out _);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, new NetworkEventArgs(
                socket: clientSocket,
                eventType: NetworkEventType.HandlerEvent,
                message: $"发送数据包时发生错误: {ex.Message}"
            ));
            throw;
        }
    }

    public virtual async Task<byte[]> SendAsync<T>(DRXSocket socket, T packet, T originPacket, string key, int timeout = 0) where T : BasePacket<T>, new()
    {
        try
        {
            if (!ValidateClientForSend(socket))
            {
                Logger.Log(LogLevel.Warning, "Server", "客户端未连接");
                throw new InvalidOperationException("客户端未连接");
            }

            var requestId = originPacket.Headers[PacketHeaderKey.RequestID].ToString();
            packet.Headers.Add(PacketHeaderKey.RequestID, requestId);
            var tcs = new TaskCompletionSource<byte[]>();
            if (!PendingRequests.TryAdd(requestId, tcs))
            {
                throw new InvalidOperationException("无法添加待处理请求");
            }
            try
            {
                var packetData = packet.Pack(key);
                var sendBuffer = PrepareDataForSend(packetData);
                ExecuteSend(socket, sendBuffer, packetData.Length);
                if (timeout > 0)
                {
                    if (await Task.WhenAny(tcs.Task, Task.Delay(timeout)) != tcs.Task)
                    {
                        PendingRequests.TryRemove(requestId, out _);
                        throw new TimeoutException("等待响应超时");
                    }
                }
                return await tcs.Task;
            }
            finally
            {
                PendingRequests.TryRemove(requestId, out _);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, new NetworkEventArgs(
                socket: socket,
                eventType: NetworkEventType.HandlerEvent,
                message: $"发送数据包时发生错误: {ex.Message}"
            ));
            throw;
        }
    }

    /// <summary>
    /// 向所有已连接的客户端广播数据（不使用数据包校验系统）。
    /// </summary>
    /// <param name="packet">网络数据包。</param>
    public virtual async Task BroadcastAsync(NetworkPacket packet)
    {
        var tasks = new List<Task>();
        var deadClients = new ConcurrentBag<DRXSocket>();
        var data = packet.Serialize();
        var buffer = PrepareDataForSend(data);

        try
        {
            tasks.AddRange(Clients.Keys.Select(client => Task.Run(() =>
            {
                try
                {
                    ExecuteSend(client, buffer, data.Length);
                }
                catch
                {
                    deadClients.Add(client);
                }
            })));

            await Task.WhenAll(tasks);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            // 清理断开的连接
            foreach (var client in deadClients)
            {
                _ = HandleDisconnectAsync(client);
            }
        }
    }

    /// <summary>
    /// 向所有已连接的客户端广播数据。
    /// </summary>
    /// <param name="packet">网络数据包。</param>
    /// <param name="key">加密密钥。</param>
    public virtual async Task BroadcastAsync(NetworkPacket packet, string key)
    {
        var tasks = new List<Task>();
        var deadClients = new ConcurrentBag<DRXSocket>();
        var data = packet.Serialize(key);
        var buffer = PrepareDataForSend(data);

        try
        {
            tasks.AddRange(Clients.Keys.Select(client => Task.Run(() =>
            {
                try
                {
                    ExecuteSend(client, buffer, data.Length);
                }
                catch
                {
                    deadClients.Add(client);
                }
            })));

            await Task.WhenAll(tasks);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            // 清理断开的连接
            foreach (var client in deadClients)
            {
                _ = HandleDisconnectAsync(client);
            }
        }
    }
    #endregion
}
