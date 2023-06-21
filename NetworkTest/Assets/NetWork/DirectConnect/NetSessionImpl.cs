using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using UnityEngine;

namespace DirectNet
{
    enum NetConnectState
    {
        None = 0,
        Connecting,
        Established,
    }

    class NetByteArray
    {
        public byte[] Data;
        public int EntityLength;

        public void AcceptData(byte[] buffer, int length)
        {
            if (Data == null || Data.Length < length)
                Data = new byte[Math.Max(1024, length)];

            Array.Copy(buffer, Data, length);
            EntityLength = length;
        }
    }

    class NetData
    {
        public NetConnectState state;
        public Socket socket;
        public byte[] recvBuffer;
        public MemoryStream msgStream;
        public bool isConnectError;
        public bool isDisconnected;
        public string disconnectMessage;
        public bool isEstablished;

        public Queue<byte> sendQueue;
        public bool isSending;
        public object sendLock_;

        public List<NetByteArray> cacheProtocolDataList;
        public int cacheProtocolValidLength;

        private object netDataLock_;
        private byte[] netDataBuffer_;
        private int netDataBufferLength_;

        public NetData()
        {
            state = NetConnectState.None;
            socket = null;
            recvBuffer = null;
            msgStream = null;
            isConnectError = false;
            isDisconnected = false;
            isEstablished = false;

            sendQueue = new Queue<byte>();
            isSending = false;
            sendLock_ = new object();

            cacheProtocolDataList = new List<NetByteArray>();
            cacheProtocolValidLength = 0;

            netDataLock_ = new object();
            netDataBuffer_ = new byte[4096];
            netDataBufferLength_ = 0;
        }

        //接受完整的协议包数据
        public void acceptProtocolData(byte[] buffer, int length)
        {
            if (cacheProtocolDataList.Count <= cacheProtocolValidLength)
            {
                cacheProtocolDataList.Add(new NetByteArray());
                if (cacheProtocolDataList.Count == 100)
                {
                    UnityEngine.Debug.LogWarning("数据包池大小过大");
                }
                if (cacheProtocolDataList.Count >= 300)
                {
                    UnityEngine.Debug.LogError("数据包池大小过大");
                    return;
                }
            }

            NetByteArray nba = cacheProtocolDataList[cacheProtocolValidLength++];
            nba.AcceptData(buffer, length);
        }

        //派发缓存的协议包数据
        public void dispatchProtocols(Action<byte[], int, int> handler)
        {
            while (cacheProtocolValidLength > 0)
            {
                NetByteArray nba = cacheProtocolDataList[0];
                cacheProtocolDataList.RemoveAt(0);
                cacheProtocolValidLength--;

                if (nba.EntityLength > 0)
                {
                    handler?.Invoke(nba.Data, 4, nba.EntityLength - 4);
                    nba.EntityLength = 4;
                }

                cacheProtocolDataList.Add(nba);
            }
        }

        //接受网络原始数据
        public void acceptNetData(byte[] buffer, int length)
        {
            lock (netDataLock_)
            {
                if (netDataBuffer_.Length < length + netDataBufferLength_)
                {
                    int l = netDataBuffer_.Length;
                    while (l < length + netDataBufferLength_)
                        l *= 2;

                    byte[] tmpBuffer = new byte[l];
                    Array.Copy(netDataBuffer_, tmpBuffer, netDataBufferLength_);
                    netDataBuffer_ = tmpBuffer;
                }
                Array.Copy(buffer, 0, netDataBuffer_, netDataBufferLength_, length);
                netDataBufferLength_ += length;
            }
        }

        //拆分网络数据，组装协议包
        public void splitNetDataToProtocols()
        {
            lock (netDataLock_)
            {
                while (true)
                {
                    if (netDataBufferLength_ < 4)
                        break;

                    int len = BitConverter.ToInt32(netDataBuffer_, 0);
                    if (netDataBufferLength_ < len)
                        break;

                    if (len == 0)
                    {
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        for (int i = 0; i < Math.Min(netDataBufferLength_, 100); ++i)
                        {
                            byte b = netDataBuffer_[i];
                            sb.Append(b.ToString("X"));
                            sb.Append(' ');
                        }
                        throw new Exception($"len==0 ms.Length={netDataBufferLength_} content={sb.ToString()}");
                    }

                    //UnityEngine.Debug.Log("len:"+ len);

                    acceptProtocolData(netDataBuffer_, len);
                    if (len < netDataBufferLength_)
                    {

                        Array.Copy(netDataBuffer_, len, netDataBuffer_, 0, netDataBufferLength_ - len);
                        //int count = netDataBufferLength_ - len;
                        //for (int i = 0; i < count; i++)
                        //    netDataBuffer_[i] = netDataBuffer_[len + i];

                    }
                    netDataBufferLength_ -= len;

                }
            }
        }
    }

    internal class NetSessionImpl : INetSession
    {
        private NetData netData_;   //attention: netData_的赋值必须发生在主线程
        private Action<bool> connectNotifyCallback_;

        public event Action<string> NetErrorEvent;
        public event Action<byte[], int, int> NetRecvDataEvent;

        public NetSessionImpl()
        {
            netData_ = null;
            connectNotifyCallback_ = null;
        }

        public void _disconnect()
        {
            connectNotifyCallback_ = null;
            var data = netData_;
            if (data != null)
            {
                netData_ = null;
                if (data.state == NetConnectState.Established)
                {
                    try
                    {
                        //发送未完成数据，最多等待5秒钟关闭socket
                        data.socket.Close(5);
                    }
                    catch (System.Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"socket close error: {ex.Message}");
                    }
                }
            }
        }

        public void _connectWithTimeout(string ip, int port, int timeoutInMillionSeconds, Action<bool> notifyCallback)
        {
            _disconnect();
            connectNotifyCallback_ = notifyCallback;

            var addr = IPAddress.Parse(ip);
            var endPoint = new IPEndPoint(addr, port);

            var data = new NetData();
            data.socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            data.socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
            //Socket设置KeepAlive
            {
                uint dummy = 0;
                byte[] inOptionValues = new byte[System.Runtime.InteropServices.Marshal.SizeOf(dummy) * 3];
                BitConverter.GetBytes((uint)1).CopyTo(inOptionValues, 0);
                BitConverter.GetBytes((uint)5000).CopyTo(inOptionValues, System.Runtime.InteropServices.Marshal.SizeOf(dummy));//keep-alive间隔
                BitConverter.GetBytes((uint)1000).CopyTo(inOptionValues, System.Runtime.InteropServices.Marshal.SizeOf(dummy) * 2);//尝试间隔
                data.socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
                data.socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            data.recvBuffer = new byte[4096];
            data.msgStream = new MemoryStream();

            data.state = NetConnectState.Connecting;
            netData_ = data;
            //FIX: 2021-11-18
            //BeginConnect可能会同步执行_beginConnectCallback建立连接成功
            data.socket.BeginConnect(endPoint, new AsyncCallback(_beginConnectCallback), data);

            if (!data.isEstablished && timeoutInMillionSeconds > 0)
                _checkConnectWithTimeout(data, timeoutInMillionSeconds);
        }

        private void _beginConnectCallback(IAsyncResult ar)
        {
            var data = ar.AsyncState as NetData;
            if (netData_ == data && !data.isConnectError && !data.isEstablished)
            {
                try
                {
                    data.socket.EndConnect(ar);
                }
                catch (SocketException ex)
                {
                    Console.WriteLine("net connect error: {0}", ex.Message);
                    data.isConnectError = true;
                    return;
                }

                data.isEstablished = true;
                _beginReceive(data);
            }
        }

        private async void _checkConnectWithTimeout(NetData data, int timeoutInMillionSeconds)
        {
            await Task.Delay(timeoutInMillionSeconds);

            if (data.state == NetConnectState.Connecting)
            {
                data.isConnectError = true;
            }
        }

        private void _beginReceive(NetData data)
        {
            if (netData_ == data)
            {
                data.socket.BeginReceive(data.recvBuffer, 0, data.recvBuffer.Length, SocketFlags.None, _beginReceiveCallback, data);
            }
        }

        private void _beginReceiveCallback(IAsyncResult ar)
        {
            var data = ar.AsyncState as NetData;
            if (netData_ == data && !data.isDisconnected)
            {
                try
                {
                    int len = data.socket.EndReceive(ar);
                    if (len > 0)
                    {
                        // Debug.Log($"_beginReceiveCallback len={len}");
                        data.acceptNetData(data.recvBuffer, len);
                    }
                }
                catch (System.Exception ex)
                {
                    if (!data.socket.Connected)
                    {
                        Console.WriteLine("net receive error1: {0}", ex.Message);
                        data.disconnectMessage = $"Socket接收数据异常1：{ex.Message}";
                        data.isDisconnected = true;
                        return;
                    }
                }

                try
                {
                    _beginReceive(data);
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine("net receive error2: {0}", ex.Message);
                    data.disconnectMessage = $"Socket接收数据异常2：{ex.Message}";
                    data.isDisconnected = true;
                    return;
                }
            }
        }

        public void Connect(string ip, int port, int timeoutInMillionSeconds, Action<bool> notifyCallback)
        {
            _connectWithTimeout(ip, port, timeoutInMillionSeconds, notifyCallback);
        }

        public void Disconnect()
        {
            //断开连接时主动先把消息缓冲池内的缓存触发一下
            netData_?.dispatchProtocols(_onRecvNetData);
            _disconnect();
        }

        public void Update()
        {
            var data = netData_;
            if (data != null)
            {
                if (data.isConnectError)
                {
                    netData_ = null;
                    var t = connectNotifyCallback_;
                    connectNotifyCallback_ = null;
                    t?.Invoke(false);
                    return;
                }

                switch (data.state)
                {
                    case NetConnectState.Connecting:
                        if (data.isEstablished)
                        {
                            data.isEstablished = false;
                            data.state = NetConnectState.Established;
                            var t = connectNotifyCallback_;
                            connectNotifyCallback_ = null;
                            t?.Invoke(true);
                        }
                        break;

                    case NetConnectState.Established:
                        data.splitNetDataToProtocols();
                        break;
                }

                // 派发协议
                data.dispatchProtocols(_onRecvNetData);

                if (data.isDisconnected)
                {
                    netData_ = null;
                    NetErrorEvent?.Invoke(data.disconnectMessage);
                    return;
                }
            }
        }

        public void _onRecvNetData(byte[] buffer, int index, int length)
        {
            NetRecvDataEvent?.Invoke(buffer, index, length);
        }

        public bool Send(byte[] buffer, int index, int length)
        {
            var data = netData_;
            if (data != null && data.state == NetConnectState.Established)
            {
                try
                {
                    lock (data.sendLock_)
                    {
                        for (int i = 0; i < length; ++i)
                            data.sendQueue.Enqueue(buffer[index + i]);
                        if (!data.isSending)
                        {
                            data.isSending = true;
                            data.socket.BeginSend(data.sendQueue.ToArray(), 0, data.sendQueue.Count, SocketFlags.None, _onBeginSendCallback, data);
                        }
                    }
                    return true;
                }
                catch (System.Exception ex)
                {
                    data.isDisconnected = true;
                    data.disconnectMessage = $"SOCKET发送数据异常：{ex.Message}";
                    UnityEngine.Debug.LogError($"异步发送数据失败：网络可能已断开，套接字无效了。\n{ex.Message}");
                }
            }
            return false;
        }

        private void _onBeginSendCallback(IAsyncResult ar)
        {
            int sendedCount = 0;
            var data = ar.AsyncState as NetData;
            try
            {
                sendedCount = data.socket.EndSend(ar);
            }
            catch (System.Exception ex)
            {
                data.isDisconnected = true;
                data.disconnectMessage = $"SOCKET[异步]发送数据异常：{ex.Message}";
                return;
            }

            lock (data.sendLock_)
            {
                data.isSending = false;
                while (sendedCount-- > 0)
                    data.sendQueue.Dequeue();
                if (ReferenceEquals(netData_, data) && data.sendQueue.Count > 0)
                {
                    data.isSending = true;
                    data.socket.BeginSend(data.sendQueue.ToArray(), 0, data.sendQueue.Count, SocketFlags.None, _onBeginSendCallback, data);
                }
            }
        }
    }
}
