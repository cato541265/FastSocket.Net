﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Sodao.FastSocket.Client
{
    /// <summary>
    /// socket client
    /// </summary>
    /// <typeparam name="TMessage"></typeparam>
    public class SocketClient<TMessage> : SocketBase.BaseHost
        where TMessage : class, Messaging.IMessage
    {
        #region Event
        /// <summary>
        /// received unknow message
        /// </summary>
        public event Action<SocketBase.IConnection, TMessage> UnknowMessageReceived;
        #endregion

        #region Private Members
        private int _seqId = 0;
        private readonly Protocol.IProtocol<TMessage> _protocol = null;

        private readonly int _millisecondsSendTimeout;
        private readonly int _millisecondsReceiveTimeout;

        private readonly PendingSendQueue _pendingQueue = null;
        private readonly ReceivingQueue _receivingQueue = null;

        private readonly EndPointManager _endPointManager = null;
        private readonly IConnectionPool _connectionPool = null;
        #endregion

        #region Constructors
        /// <summary>
        /// new
        /// </summary>
        /// <param name="protocol"></param>
        public SocketClient(Protocol.IProtocol<TMessage> protocol)
            : this(protocol, 8192, 8192, 3000, 3000)
        {
        }
        /// <summary>
        /// new
        /// </summary>
        /// <param name="protocol"></param>
        /// <param name="socketBufferSize"></param>
        /// <param name="messageBufferSize"></param>
        /// <param name="millisecondsSendTimeout"></param>
        /// <param name="millisecondsReceiveTimeout"></param>
        /// <exception cref="ArgumentNullException">protocol is null</exception>
        public SocketClient(Protocol.IProtocol<TMessage> protocol,
            int socketBufferSize,
            int messageBufferSize,
            int millisecondsSendTimeout,
            int millisecondsReceiveTimeout)
            : base(socketBufferSize, messageBufferSize)
        {
            if (protocol == null) throw new ArgumentNullException("protocol");
            this._protocol = protocol;

            if (protocol.IsAsync) this._connectionPool = new AsyncPool();
            else this._connectionPool = new SyncPool();

            this._millisecondsSendTimeout = millisecondsSendTimeout;
            this._millisecondsReceiveTimeout = millisecondsReceiveTimeout;

            this._pendingQueue = new PendingSendQueue(this);
            this._receivingQueue = new ReceivingQueue(this);

            this._endPointManager = new EndPointManager(this);
            this._endPointManager.NodeConnected += this.EndPoint_Connected;
            this._endPointManager.NodeAlreadyAvailable += this.EndPoint_AlreadyAvailable;
        }
        #endregion

        #region Public Properties
        /// <summary>
        /// 发送超时毫秒数
        /// </summary>
        public int MillisecondsSendTimeout
        {
            get { return this._millisecondsSendTimeout; }
        }
        /// <summary>
        /// 接收超时毫秒数
        /// </summary>
        public int MillisecondsReceiveTimeout
        {
            get { return this._millisecondsReceiveTimeout; }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// try register endPoint
        /// </summary>
        /// <param name="name"></param>
        /// <param name="remoteEP"></param>
        /// <param name="initFunc"></param>
        /// <returns></returns>
        public bool TryRegisterEndPoint(string name, EndPoint remoteEP, Func<SendContext, Task> initFunc = null)
        {
            return this._endPointManager.TryRegister(name, remoteEP, initFunc);
        }
        /// <summary>
        /// un register endPoint
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public bool UnRegisterEndPoint(string name)
        {
            return this._endPointManager.UnRegister(name);
        }
        /// <summary>
        /// get all registered endPoint
        /// </summary>
        /// <returns></returns>
        public KeyValuePair<string, EndPoint>[] GetAllRegisteredEndPoint()
        {
            return this._endPointManager.ToArray();
        }
        /// <summary>
        /// send request
        /// </summary>
        /// <param name="request"></param>
        public void Send(Request<TMessage> request)
        {
            SocketBase.IConnection connection = null;
            if (!this._connectionPool.TryAcquire(out connection))
            {
                this._pendingQueue.Enqueue(request);
                return;
            }
            connection.BeginSend(request);
        }
        /// <summary>
        /// 产生不重复的seqId
        /// </summary>
        /// <returns></returns>
        public int NextRequestSeqId()
        {
            return Interlocked.Increment(ref this._seqId) & 0x7fffffff;
        }
        /// <summary>
        /// new request
        /// </summary>
        /// <param name="name"></param>
        /// <param name="payload"></param>
        /// <param name="millisecondsReceiveTimeout"></param>
        /// <param name="onException"></param>
        /// <param name="onResult"></param>
        /// <returns></returns>
        public Request<TMessage> NewRequest(string name,
            byte[] payload,
            int millisecondsReceiveTimeout,
            Action<Exception> onException,
            Action<TMessage> onResult)
        {
            return new Request<TMessage>(this.NextRequestSeqId(),
                name,
                payload,
                millisecondsReceiveTimeout,
                onException,
                onResult);
        }
        #endregion

        #region Private Methods
        /// <summary>
        /// endPoint connected
        /// </summary>
        /// <param name="node"></param>
        /// <param name="connection"></param>
        private void EndPoint_Connected(Node node, SocketBase.IConnection connection)
        {
            this.RegisterConnection(connection);
        }
        /// <summary>
        /// endPoint already available
        /// </summary>
        /// <param name="node"></param>
        /// <param name="connection"></param>
        private void EndPoint_AlreadyAvailable(Node node, SocketBase.IConnection connection)
        {
            this._connectionPool.Register(connection);
        }
        #endregion

        #region Protected Methods
        /// <summary>
        /// 处理未知的message
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="message"></param>
        protected virtual void HandleUnknowMessage(SocketBase.IConnection connection, TMessage message)
        {
            if (this.UnknowMessageReceived != null)
                this.UnknowMessageReceived(connection, message);
        }
        /// <summary>
        /// on pending send timeout
        /// </summary>
        /// <param name="request"></param>
        protected virtual void OnPendingSendTimeout(Request<TMessage> request)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { request.SetException(new RequestException(RequestException.Errors.PendingSendTimeout, request.Name)); }
                catch (Exception ex) { SocketBase.Log.Trace.Error(ex.Message, ex); }
            });
        }
        /// <summary>
        /// on receive timeout
        /// </summary>
        /// <param name="request"></param>
        protected virtual void OnReceiveTimeout(Request<TMessage> request)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { request.SetException(new RequestException(RequestException.Errors.ReceiveTimeout, request.Name)); }
                catch (Exception ex) { SocketBase.Log.Trace.Error(ex.Message, ex); }
            });
        }
        #endregion

        #region Override Methods
        /// <summary>
        /// OnConnected
        /// </summary>
        /// <param name="connection"></param>
        protected override void OnConnected(SocketBase.IConnection connection)
        {
            base.OnConnected(connection);
            connection.BeginReceive();//异步开始接收数据
        }
        /// <summary>
        /// on disconnected
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="ex"></param>
        protected override void OnDisconnected(SocketBase.IConnection connection, Exception ex)
        {
            base.OnDisconnected(connection, ex);
            this._connectionPool.Destroy(connection);
        }
        /// <summary>
        /// OnStartSending
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="packet"></param>
        protected override void OnStartSending(SocketBase.IConnection connection, SocketBase.Packet packet)
        {
            base.OnStartSending(connection, packet);

            var request = packet as Request<TMessage>;
            request.SendConnection = connection;
            this._receivingQueue.TryAdd(request);
        }
        /// <summary>
        /// OnSendCallback
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="packet"></param>
        /// <param name="isSuccess"></param>
        protected override void OnSendCallback(SocketBase.IConnection connection, SocketBase.Packet packet, bool isSuccess)
        {
            base.OnSendCallback(connection, packet, isSuccess);

            var request = packet as Request<TMessage>;
            if (isSuccess)
            {
                request.SentTime = SocketBase.Utils.Date.UtcNow;
                return;
            }

            Request<TMessage> removed;
            if (this._receivingQueue.TryRemove(request.SeqId, out removed))
                removed.SendConnection = null;

            if (!request.AllowRetry)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { request.SetException(new RequestException(RequestException.Errors.SendFaild, request.Name)); }
                    catch (Exception ex) { SocketBase.Log.Trace.Error(ex.Message, ex); }
                });
                return;
            }

            if (DateTime.UtcNow.Subtract(request.CreatedTime).TotalMilliseconds > this._millisecondsSendTimeout)
            {
                //send time out
                this.OnPendingSendTimeout(request);
                return;
            }

            //retry send
            this.Send(request);
        }
        /// <summary>
        /// OnMessageReceived
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="e"></param>
        protected override void OnMessageReceived(SocketBase.IConnection connection, SocketBase.MessageReceivedEventArgs e)
        {
            base.OnMessageReceived(connection, e);

            int readlength;
            TMessage message = null;
            try { message = this._protocol.Parse(connection, e.Buffer, out readlength); }
            catch (Exception ex)
            {
                base.OnConnectionError(connection, ex);
                connection.BeginDisconnect(ex);
                e.SetReadlength(e.Buffer.Count);
                return;
            }

            if (message != null)
            {
                Request<TMessage> r = null;
                if (this._receivingQueue.TryRemove(message.SeqId, out r))
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { r.SetResult(message); }
                        catch (Exception ex) { SocketBase.Log.Trace.Error(ex.Message, ex); }
                    });
                }
                else
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { this.HandleUnknowMessage(connection, message); }
                        catch (Exception ex) { SocketBase.Log.Trace.Error(ex.Message, ex); }
                    });
                }
            }

            //continue receiveing..
            e.SetReadlength(readlength);
        }
        #endregion

        /// <summary>
        /// send queue
        /// </summary>
        private class PendingSendQueue
        {
            #region Private Members
            private readonly SocketClient<TMessage> _client = null;
            private readonly ConcurrentQueue<Request<TMessage>> _queue =
                new ConcurrentQueue<Request<TMessage>>();
            private readonly Timer _timer = null;
            #endregion

            #region Constructors
            /// <summary>
            /// new
            /// </summary>
            /// <param name="client"></param>
            public PendingSendQueue(SocketClient<TMessage> client)
            {
                this._client = client;

                this._timer = new Timer(state =>
                {
                    var count = this._queue.Count;
                    if (count == 0) return;

                    this._timer.Change(Timeout.Infinite, Timeout.Infinite);

                    var dtNow = SocketBase.Utils.Date.UtcNow;
                    var timeOut = this._client.MillisecondsSendTimeout;
                    while (count-- > 0)
                    {
                        Request<TMessage> request;
                        if (!this._queue.TryDequeue(out request)) break;

                        if (dtNow.Subtract(request.CreatedTime).TotalMilliseconds < timeOut)
                        {
                            //try send...
                            this._client.Send(request);
                            continue;
                        }

                        //fire send time out
                        this._client.OnPendingSendTimeout(request);
                    }

                    this._timer.Change(50, 0);
                }, null, 50, 0);
            }
            #endregion

            #region Public Methods
            /// <summary>
            /// 入列
            /// </summary>
            /// <param name="request"></param>
            /// <exception cref="ArgumentNullException">request is null</exception>
            public void Enqueue(Request<TMessage> request)
            {
                if (request == null) throw new ArgumentNullException("request");
                this._queue.Enqueue(request);
            }
            #endregion
        }

        /// <summary>
        /// receiving queue
        /// </summary>
        private class ReceivingQueue
        {
            #region Private Members
            private readonly SocketClient<TMessage> _client = null;

            private readonly ConcurrentDictionary<int, Request<TMessage>> _dic =
                new ConcurrentDictionary<int, Request<TMessage>>();
            private readonly Timer _timer = null;
            #endregion

            #region Constructors
            /// <summary>
            /// new
            /// </summary>
            /// <param name="client"></param>
            public ReceivingQueue(SocketClient<TMessage> client)
            {
                this._client = client;

                this._timer = new Timer(_ =>
                {
                    if (this._dic.Count == 0) return;
                    this._timer.Change(Timeout.Infinite, Timeout.Infinite);

                    var dtNow = SocketBase.Utils.Date.UtcNow;
                    var arr = this._dic.ToArray().Where(c =>
                        dtNow.Subtract(c.Value.SentTime).TotalMilliseconds > c.Value.MillisecondsReceiveTimeout).ToArray();

                    for (int i = 0, l = arr.Length; i < l; i++)
                    {
                        Request<TMessage> request;
                        if (this._dic.TryRemove(arr[i].Key, out request))
                            this._client.OnReceiveTimeout(request);
                    }

                    this._timer.Change(500, 0);
                }, null, 500, 0);
            }
            #endregion

            #region Public Methods
            /// <summary>
            /// try add
            /// </summary>
            /// <param name="request"></param>
            /// <returns></returns>
            public bool TryAdd(Request<TMessage> request)
            {
                return this._dic.TryAdd(request.SeqId, request);
            }
            /// <summary>
            /// try remove
            /// </summary>
            /// <param name="seqID"></param>
            /// <param name="request"></param>
            /// <returns></returns>
            public bool TryRemove(int seqID, out Request<TMessage> request)
            {
                return this._dic.TryRemove(seqID, out request);
            }
            #endregion
        }

        /// <summary>
        /// server node
        /// </summary>
        private class Node
        {
            #region Members
            static private int NODE_ID = 0;

            /// <summary>
            /// id
            /// </summary>
            public readonly int Id;
            /// <summary>
            /// name
            /// </summary>
            public readonly string Name;
            /// <summary>
            /// remote endPoint
            /// </summary>
            public readonly EndPoint RemoteEP;
            /// <summary>
            /// init function
            /// </summary>
            public readonly Func<SendContext, Task> InitFunc;
            #endregion

            #region Constructors
            /// <summary>
            /// new
            /// </summary>
            /// <param name="name"></param>
            /// <param name="remoteEP"></param>
            /// <param name="initFunc"></param>
            /// <exception cref="ArgumentNullException">name is null or empty</exception>
            /// <exception cref="ArgumentNullException">remoteEP</exception>
            public Node(string name, EndPoint remoteEP, Func<SendContext, Task> initFunc)
            {
                if (string.IsNullOrEmpty(name)) throw new ArgumentNullException("name");
                if (remoteEP == null) throw new ArgumentNullException("remoteEP");

                this.Id = Interlocked.Increment(ref NODE_ID);
                this.Name = name;
                this.RemoteEP = remoteEP;
                this.InitFunc = initFunc;
            }
            #endregion
        }

        /// <summary>
        /// endPoint manager
        /// </summary>
        private class EndPointManager
        {
            #region Events
            /// <summary>
            /// node connected event
            /// </summary>
            public event Action<Node, SocketBase.IConnection> NodeConnected;
            /// <summary>
            /// node already event
            /// </summary>
            public event Action<Node, SocketBase.IConnection> NodeAlreadyAvailable;
            #endregion

            #region Members
            /// <summary>
            /// host
            /// </summary>
            private readonly SocketBase.IHost _host = null;
            /// <summary>
            /// key:node id
            /// </summary>
            private readonly Dictionary<int, Node> _dicNodes =
                new Dictionary<int, Node>();
            /// <summary>
            /// key:node id
            /// </summary>
            private readonly Dictionary<int, SocketBase.IConnection> _dicConnections =
                new Dictionary<int, SocketBase.IConnection>();
            #endregion

            #region Constructors
            /// <summary>
            /// new
            /// </summary>
            /// <param name="host"></param>
            public EndPointManager(SocketBase.IHost host)
            {
                this._host = host;
            }
            #endregion

            #region Public Methods
            /// <summary>
            /// try register
            /// </summary>
            /// <param name="name"></param>
            /// <param name="remoteEP"></param>
            /// <param name="initFunc"></param>
            /// <returns></returns>
            public bool TryRegister(string name, EndPoint remoteEP, Func<SendContext, Task> initFunc)
            {
                Node node = null;
                lock (this)
                {
                    if (this._dicNodes.Values.FirstOrDefault(c => c.Name == name) != null) return false;
                    node = new Node(name, remoteEP, initFunc);
                    this._dicNodes[node.Id] = node;
                }

                this.Connect(node);
                return true;
            }
            /// <summary>
            /// un register
            /// </summary>
            /// <param name="name"></param>
            /// <returns></returns>
            public bool UnRegister(string name)
            {
                SocketBase.IConnection connection = null;
                lock (this)
                {
                    var node = this._dicNodes.Values.FirstOrDefault(c => c.Name == name);
                    if (node == null) return false;

                    this._dicNodes.Remove(node.Id);
                    this._dicConnections.TryGetValue(node.Id, out connection);
                }

                if (connection != null) connection.BeginDisconnect();
                return true;
            }
            /// <summary>
            /// to array
            /// </summary>
            /// <returns></returns>
            public KeyValuePair<string, EndPoint>[] ToArray()
            {
                lock (this)
                {
                    return this._dicNodes.Values
                        .Select(c => new KeyValuePair<string, EndPoint>(c.Name, c.RemoteEP)).ToArray();
                }
            }
            #endregion

            #region Private Methods
            /// <summary>
            /// connect
            /// </summary>
            /// <param name="node"></param>
            private void Connect(Node node)
            {
                SocketConnector.Connect(node.RemoteEP).ContinueWith(task => this.ConnectCallback(node, task));
            }
            /// <summary>
            /// connect callback
            /// </summary>
            /// <param name="node"></param>
            /// <param name="task"></param>
            private void ConnectCallback(Node node, Task<Socket> task)
            {
                bool isActive;
                lock (this) isActive = this._dicNodes.ContainsKey(node.Id);

                if (task.IsFaulted)
                {
                    if (isActive) //after 1000~3000ms retry connect
                        SocketBase.Utils.TaskEx.Delay(new Random().Next(1000, 3000)).ContinueWith(_ => this.Connect(node));

                    return;
                }

                var socket = task.Result;
                if (!isActive)
                {
                    try { socket.Close(); }
                    catch (Exception ex) { SocketBase.Log.Trace.Error(ex.Message, ex); }
                    return;
                }

                socket.NoDelay = true;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);
                socket.ReceiveBufferSize = this._host.SocketBufferSize;
                socket.SendBufferSize = this._host.SocketBufferSize;
                var connection = this._host.NewConnection(socket);

                connection.Disconnected += (conn, ex) =>
                {
                    bool isExists;
                    lock (this)
                    {
                        isExists = this._dicNodes.ContainsKey(node.Id);
                        this._dicConnections.Remove(node.Id);
                    }

                    if (isExists) //after 100~1500ms retry connect
                        SocketBase.Utils.TaskEx.Delay(new Random().Next(100, 1500)).ContinueWith(_ => this.Connect(node));
                };

                //fire node connected event.
                if (this.NodeConnected != null)
                    this.NodeConnected(node, connection);

                if (node.InitFunc == null)
                {
                    bool isExists;
                    lock (this)
                    {
                        if (isExists = this._dicNodes.ContainsKey(node.Id))
                            this._dicConnections[node.Id] = connection;
                    }

                    if (isExists)
                    {
                        //fire node already event.
                        if (this.NodeAlreadyAvailable != null)
                            this.NodeAlreadyAvailable(node, connection);
                    }
                    else connection.BeginDisconnect();

                    return;
                }

                node.InitFunc(new SendContext(connection))
                    .ContinueWith(c =>
                    {
                        if (c.IsFaulted)
                        {
                            connection.BeginDisconnect(c.Exception.InnerException);
                            return;
                        }

                        bool isExists;
                        lock (this)
                        {
                            if (isExists = this._dicNodes.ContainsKey(node.Id))
                                this._dicConnections[node.Id] = connection;
                        }

                        if (isExists)
                        {
                            //fire node already event.
                            if (this.NodeAlreadyAvailable != null)
                                this.NodeAlreadyAvailable(node, connection);
                        }
                        else connection.BeginDisconnect();
                    });
            }
            #endregion
        }

        /// <summary>
        /// connection pool interface
        /// </summary>
        private interface IConnectionPool
        {
            #region Public Methods
            /// <summary>
            /// register
            /// </summary>
            /// <param name="connection"></param>
            void Register(SocketBase.IConnection connection);
            /// <summary>
            /// try acquire <see cref="SocketBase.IConnection"/>
            /// </summary>
            /// <param name="connection"></param>
            /// <returns></returns>
            bool TryAcquire(out SocketBase.IConnection connection);
            /// <summary>
            /// release
            /// </summary>
            /// <param name="connection"></param>
            void Release(SocketBase.IConnection connection);
            /// <summary>
            /// destroy
            /// </summary>
            /// <param name="connection"></param>
            void Destroy(SocketBase.IConnection connection);
            #endregion
        }

        /// <summary>
        /// async connection pool
        /// </summary>
        public sealed class AsyncPool : IConnectionPool
        {
            #region Private Members
            private readonly List<SocketBase.IConnection> _list = new List<SocketBase.IConnection>();
            private SocketBase.IConnection[] _arr = null;
            private int _acquireNumber = 0;
            #endregion

            #region Public Methods
            /// <summary>
            /// register
            /// </summary>
            /// <param name="connection"></param>
            public void Register(SocketBase.IConnection connection)
            {
                if (connection == null) throw new ArgumentNullException("connection");

                lock (this)
                {
                    if (this._list.Contains(connection)) return;

                    this._list.Add(connection);
                    this._arr = this._list.ToArray();
                }
            }
            /// <summary>
            /// try acquire
            /// </summary>
            /// <param name="connection"></param>
            /// <returns></returns>
            public bool TryAcquire(out SocketBase.IConnection connection)
            {
                var arr = this._arr;
                if (arr == null || arr.Length == 0)
                {
                    connection = null;
                    return false;
                }

                if (arr.Length == 1) connection = arr[0];
                else connection = arr[(Interlocked.Increment(ref this._acquireNumber) & 0x7fffffff) % arr.Length];
                return true;
            }
            /// <summary>
            /// release
            /// </summary>
            /// <param name="connection"></param>
            public void Release(SocketBase.IConnection connection)
            {
            }
            /// <summary>
            /// destroy
            /// </summary>
            /// <param name="connection"></param>
            public void Destroy(SocketBase.IConnection connection)
            {
                if (connection == null) throw new ArgumentNullException("connection");

                lock (this)
                {
                    if (this._list.Remove(connection)) this._arr = this._list.ToArray();
                }
            }
            #endregion
        }

        /// <summary>
        /// sync connection pool
        /// </summary>
        public sealed class SyncPool : IConnectionPool
        {
            #region Private Members
            private readonly ConcurrentDictionary<long, SocketBase.IConnection> _dic =
                new ConcurrentDictionary<long, SocketBase.IConnection>();
            private readonly ConcurrentStack<SocketBase.IConnection> _stack =
                new ConcurrentStack<SocketBase.IConnection>();
            #endregion

            #region Public Methods
            /// <summary>
            /// register
            /// </summary>
            /// <param name="connection"></param>
            public void Register(SocketBase.IConnection connection)
            {
                if (this._dic.TryAdd(connection.ConnectionID, connection))
                    this._stack.Push(connection);
            }
            /// <summary>
            /// try acquire
            /// </summary>
            /// <param name="connection"></param>
            /// <returns></returns>
            public bool TryAcquire(out SocketBase.IConnection connection)
            {
                return this._stack.TryPop(out connection);
            }
            /// <summary>
            /// release
            /// </summary>
            /// <param name="connection"></param>
            public void Release(SocketBase.IConnection connection)
            {
                if (this._dic.ContainsKey(connection.ConnectionID))
                    this._stack.Push(connection);
            }
            /// <summary>
            /// destroy
            /// </summary>
            /// <param name="connection"></param>
            public void Destroy(SocketBase.IConnection connection)
            {
                SocketBase.IConnection exists = null;
                this._dic.TryRemove(connection.ConnectionID, out exists);
            }
            #endregion
        }
    }
}