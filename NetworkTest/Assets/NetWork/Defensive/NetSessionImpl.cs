using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using UnityEngine;

namespace DefensiveNet
{
    class NetByteArray
    {
        public byte[] Data;
        public int EntityLength;

        public void CopyWholePacket(byte[] buffer, int length)
        {
            if (Data == null || Data.Length < length)
            {
                Data = new byte[Math.Max(1024, length)];
            }

            Array.Copy(buffer, Data, length);
            EntityLength = length;
        }
    }

    class NetData
    {
        public Socket socket;
        public byte[] recvBuffer;
        public MemoryStream msgStream;
        public bool isEstablished;

        public Queue<byte> sendQueue;
        public bool isSending;

        public List<NetByteArray> cacheProtocolDataList;
        public int cacheProtocolValidLength;

        private byte[] netDataBuffer_;
        private int netDataBufferLength_;

        public NetData()
        {
            socket = null;
            recvBuffer = null;
            msgStream = null;
            isEstablished = false;

            sendQueue = new Queue<byte>();
            isSending = false;

            cacheProtocolDataList = new List<NetByteArray>();
            cacheProtocolValidLength = 0;

            netDataBuffer_ = new byte[4096];
            netDataBufferLength_ = 0;
        }

        //接受网络原始数据
        public void acceptNetData(byte[] buffer, int length)
        {
            if (netDataBuffer_.Length < length + netDataBufferLength_)
            {
                int l = netDataBuffer_.Length;
                while (l < length + netDataBufferLength_)
                {
                    l *= 2;
                }

                byte[] tmpBuffer = new byte[l];
                Array.Copy(netDataBuffer_, tmpBuffer, netDataBufferLength_);
                netDataBuffer_ = tmpBuffer;
            }
            Array.Copy(buffer, 0, netDataBuffer_, netDataBufferLength_, length);
            netDataBufferLength_ += length;
        }

        //分发数据
        public void splitNetDataToProtocols(Action<byte[], int, int> handler)
        {
            //包头长度
            var iPacketHeadSize = 4;
            while (true)
            {
                //是否包头
                if (netDataBufferLength_ < iPacketHeadSize)
                {
                    break;
                }

                //有效数据
                int len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(netDataBuffer_, 0));

                //完整数据
                if (netDataBufferLength_ < len + iPacketHeadSize)
                {
                    break;
                }

                //缓存个数
                if (cacheProtocolDataList.Count <= cacheProtocolValidLength)
                {
                    cacheProtocolDataList.Add(new NetByteArray());
                }

                //完整数据
                NetByteArray nba = cacheProtocolDataList[cacheProtocolValidLength++];
                nba.CopyWholePacket(netDataBuffer_, iPacketHeadSize + len);

                //缓冲移动
                Array.Copy(netDataBuffer_, iPacketHeadSize + len, netDataBuffer_, 0, netDataBufferLength_ - iPacketHeadSize - len);

                //总长度减去数据+包头
                netDataBufferLength_ -= (iPacketHeadSize + len);
            }

            //派发数据
            while (cacheProtocolValidLength > 0)
            {
                NetByteArray nba = cacheProtocolDataList[0];
                cacheProtocolDataList.RemoveAt(0);
                cacheProtocolValidLength--;

                if (nba.EntityLength > 0)
                {
                    handler?.Invoke(nba.Data, 0, nba.EntityLength);
                    nba.EntityLength = 0;
                }

                cacheProtocolDataList.Add(nba);
            }
        }
    }

    public class NetSessionImpl : INetSession
    {
        private NetData netData_ = null;
        private bool isInitialize_ = false;
        public NetConnectState netState_ = NetConnectState.None;
        private bool isQuickConnect_ = false;

        private string ip_ = "";
        private int port_ = 0;
        private int index_ = 0;

        public event Action<INetSession, int, bool> NetConnectEvent;
        public event Action<INetSession, byte[], int, int> NetMessageEvent;
        public event Action<INetSession, string> NetErrorEvent;
        private static readonly object sessionLock_ = new object();

        private System.Timers.Timer socketTimer_ = null;

        public double dbConnDataTickCount_ = 0;
        public double dbRecvDataTickCount_ = 0;

        public NetSessionImpl()
        {
            netData_ = null;
        }

        public void SetSessionInfo(string ip, int port, int index)
        {
            ip_ = ip;
            port_ = port;
            index_ = index;
        }

        public NetConnectState GetSessionState()
        {
            return netState_;
        }

        //开始连接
        public void Connect()
        {
            try
            {
                //连接状态
                if (netState_ != NetConnectState.None)
                {
                    //调试日志
                    _onNetworkError(true, $"连接时发生错误,强制断开重置,等待下次连接");
                    return;
                }

                //调试信息
                if (NetHelper.AllowNetLoger)
                {
                    Debug.LogWarning($"网络日志, 开始连接服务器, 地址:" + ip_);
                }

                //初始状态
                isInitialize_ = true;
                netState_ = NetConnectState.Domain;

                //解析任务
                var connectTask = Task.Run(() =>
                {
                    //域名解析
                    _getHostByName();
                });

                //继续任务
                connectTask.ContinueWith(t =>
                {
                    //异步连接
                    _connectServer();
                });
            }
            catch (Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 网络连接接口出现异常{ex.Message}");
            }
        }

        //超时检测
        protected void onCheckSocketTimeout(object source, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                //对象加锁
                lock (sessionLock_)
                {
                    //对象检测
                    if (netData_ == null)
                    {
                        _onNetworkError(false, $"网络对象为空, ip:{ip_}");
                        return;
                    }

                    //网络对象
                    switch (netState_)
                    {
                        case NetConnectState.Established:
                            {
                                double ulNowTime = NetHelper.GetTickCount();
                                if (dbRecvDataTickCount_ != 0 && ulNowTime - dbRecvDataTickCount_ > 4000)
                                {
                                    //调试信息
                                    if (NetHelper.AllowNetLoger)
                                    {
                                        Debug.LogWarning($"网络日志, 接收数据超时, 地址:" + ip_);
                                    }
                                    //网络错误
                                    _onNetworkError(true, $"接收数据超时");
                                    return;
                                }
                                break;
                            }

                        default:
                            {
                                double ulNowTime = NetHelper.GetTickCount();
                                if (dbConnDataTickCount_ != 0 && ulNowTime - dbConnDataTickCount_ > 4000)
                                {
                                    //调试信息
                                    if (NetHelper.AllowNetLoger)
                                    {
                                        Debug.LogWarning($"网络日志, 连接服务超时, 地址:" + ip_);
                                    }
                                    //网络错误
                                    _onNetworkError(true, $"连接服务超时");
                                }
                                break;
                            }
                    }
                }
            }
            catch (Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 网络超时检测出现异常{ex.Message}");
            }
        }

        //设置连接
        public void SetQuickConnect()
        {
            isQuickConnect_ = true;
        }

        //空闲会话
        public bool IsFreeSession()
        {
            if (GetSessionState() == NetConnectState.None && !isQuickConnect_)
            {
                return true;
            }

            return false;
        }

        //是否连接
        public bool IsActiveSession()
        {
            if (GetSessionState() == NetConnectState.Established && isQuickConnect_)
            {
                return true;
            }

            return false;
        }

        //接收回调
        public void _onRecvNetData(byte[] buffer, int index, int length)
        {
            NetMessageEvent?.Invoke(this, buffer, index, length);
        }

        //关闭连接
        public void Disconnect(bool bInvoke, string errorMessage)
        {
            //调试日志
            if (NetHelper.AllowNetLoger)
            {
                Debug.LogWarning($"网络日志, 网络关闭, 地址:{ip_}, 状态:{netState_}, 标记:{isQuickConnect_}, 原因:{errorMessage}");
            }

            //通知错误
            if (bInvoke)
            {
                NetErrorEvent?.Invoke(this, errorMessage);
            }

            //关闭连接
            _disconnect();
        }

        //发送数据
        public bool Send(byte[] buffer, int index, int length)
        {
            try
            {
                //对象加锁
                lock (sessionLock_)
                {
                    //初始判断
                    if (!isInitialize_) return false;

                    //网络对象
                    if (netData_ != null && netData_.isEstablished)
                    {
                        try
                        {
                            for (int i = 0; i < length; ++i)
                            {
                                netData_.sendQueue.Enqueue(buffer[index + i]);
                            }
                            if (!netData_.isSending)
                            {
                                netData_.isSending = true;
                                netData_.socket.BeginSend(netData_.sendQueue.ToArray(), 0, netData_.sendQueue.Count, SocketFlags.None, new AsyncCallback(_onBeginSendCallback), netData_);
                            }
                            return true;
                        }
                        catch (System.Exception ex)
                        {
                            _onNetworkError(true, $"SOCKET发送数据异常：{ex.Message}");
                            return false;
                        }
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 网络发送接口出现异常{ex.Message}");
                return false;
            }
        }

        //连接回调
        protected void _beginConnectCallback(IAsyncResult ar)
        {
            try
            {
                //对象加锁
                lock (sessionLock_)
                {
                    //初始标志
                    if (!isInitialize_) return;

                    //连接回调
                    if (netData_ != null && !netData_.isEstablished)
                    {
                        //调试信息
                        if (NetHelper.AllowNetLoger)
                        {
                            Debug.LogWarning($"网络日志, 连接服务器成功, 地址:" + ip_);
                        }

                        //结束连接
                        netData_.socket.EndConnect(ar);


                        //更新数据
                        netData_.isEstablished = true;
                        netState_ = NetConnectState.Established;

                        //调试信息
                        if (NetHelper.AllowNetLoger)
                        {
                            Debug.LogWarning($"网络日志, 回调服务器成功, 地址:" + ip_);

                        }
                        //连接回调
                        NetConnectEvent?.Invoke(this, index_, true);
                    }
                }

                //准备接受
                _beginReceive();
            }
            catch (Exception ex)
            {
                //调试信息
                if (NetHelper.AllowNetLoger)
                {
                    Debug.LogWarning($"网络日志, 网络异步连接回调异常{ex.Message}");
                }

                //错误处理
                _onNetworkError(true, "网络异步连接回调异常");
            }
        }

        //投递接收
        protected void _beginReceive()
        {
            try
            {
                //对象加锁
                lock (sessionLock_)
                {
                    //初始状态
                    if (!isInitialize_) return;

                    //网络对象
                    if (netData_ != null && netData_.isEstablished)
                    {
                        netData_.socket.BeginReceive(netData_.recvBuffer, 0, netData_.recvBuffer.Length, SocketFlags.None, new AsyncCallback(_beginReceiveCallback), netData_);
                    }
                }
            }
            catch (System.Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 网络准备接收出现异常{ex.Message}");

                //网络断开
                _onNetworkError(true, "网络准备接收异常");
                return;
            }
        }


        //接收回调
        protected void _beginReceiveCallback(IAsyncResult ar)
        {
            try
            {
                //对象加锁
                lock (sessionLock_)
                {
                    //初始标志
                    if (!isInitialize_) return;

                    //网络对象
                    if (netData_ != null && netData_.isEstablished)
                    {
                        //接收时间
                        dbRecvDataTickCount_ = NetHelper.GetTickCount();
                        //接收结果
                        int len = netData_.socket.EndReceive(ar);
                        if (len > 0)
                        {
                            //接收数据
                            netData_.acceptNetData(netData_.recvBuffer, len);
                            //分发数据
                            netData_.splitNetDataToProtocols(_onRecvNetData);
                        }
                    }
                }

                //准备接收
                _beginReceive();
            }
            catch (System.Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 网络数据接收回调出现异常{ex.Message}");

                //网络断开
                _onNetworkError(true, "网络数据接收回调异常");
                return;
            }
        }

        //发送回调
        protected void _onBeginSendCallback(IAsyncResult ar)
        {
            try
            {
                //对象加锁
                lock (sessionLock_)
                {

                    //初始状态
                    if (!isInitialize_) return;

                    //网络对象
                    if (netData_ != null && netData_.isEstablished)
                    {
                        netData_.isSending = false;
                        int sendedCount = netData_.socket.EndSend(ar);
                        while (sendedCount-- > 0)
                        {
                            netData_.sendQueue.Dequeue();
                        }
                        if (ReferenceEquals(netData_, netData_) && netData_.sendQueue.Count > 0)
                        {
                            netData_.isSending = true;
                            netData_.socket.BeginSend(netData_.sendQueue.ToArray(), 0, netData_.sendQueue.Count, SocketFlags.None, new AsyncCallback(_onBeginSendCallback), netData_);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 网络数据发送回调异常{ex.Message}");

                //网络断开
                _onNetworkError(true, "网络数据发送回调出现异常");
            }
        }

        #region 内部接口,无需加锁

        //内部断开
        private void _disconnect()
        {
            //对象加锁
            lock (sessionLock_)
            {
                //初始状态
                isInitialize_ = false;
                isQuickConnect_ = false;

                //时间变量
                dbRecvDataTickCount_ = 0;
                dbConnDataTickCount_ = 0;

                //关闭对象
                if (netData_ != null)
                {
                    try
                    {
                        //关闭对象
                        netData_.socket.Close();
                        netData_.socket = null;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"网络日志, 网络关闭异常{ex.Message}");
                    }
                }

                //停止定时
                if (socketTimer_ != null)
                {
                    socketTimer_.Stop();
                    socketTimer_ = null;
                }

                //停止状态
                netState_ = NetConnectState.None;
                netData_ = null;
            }
        }

        //异步连接
        private void _connectServer()
        {

            //对象加锁
            lock (sessionLock_)
            {
                try
                {


                    //连接时间
                    dbConnDataTickCount_ = NetHelper.GetTickCount();

                    //检测定时
                    if (socketTimer_ == null)
                    {
                        socketTimer_ = new System.Timers.Timer(1000);//实例化Timer类，设置时间间隔
                        socketTimer_.Elapsed += new System.Timers.ElapsedEventHandler(onCheckSocketTimeout);//到达时间的时候执行事件
                        socketTimer_.AutoReset = true;//设置是执行一次（false）还是一直执行(true)
                        socketTimer_.Enabled = true;//是否执行System.Timers.Timer.Elapsed事件
                    }

                    //通信大小
                    var addr = IPAddress.Parse(ip_);
                    var endPoint = new IPEndPoint(addr, port_);

                    //网络属性
                    uint dummy = 0;
                    byte[] inOptionValues = new byte[System.Runtime.InteropServices.Marshal.SizeOf(dummy) * 3];
                    BitConverter.GetBytes((uint)1).CopyTo(inOptionValues, 0);
                    BitConverter.GetBytes((uint)5000).CopyTo(inOptionValues, System.Runtime.InteropServices.Marshal.SizeOf(dummy));//keep-alive间隔
                    BitConverter.GetBytes((uint)1000).CopyTo(inOptionValues, System.Runtime.InteropServices.Marshal.SizeOf(dummy) * 2);//尝试间隔

                    //网络类型
                    if (_isIpv4(ip_))
                    {
                        //构造对象
                        netData_ = new NetData();
                        netData_.socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        netData_.socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
                        netData_.socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
                        netData_.socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                        netData_.recvBuffer = new byte[4096];
                        netData_.msgStream = new MemoryStream();
                        netData_.socket.BeginConnect(endPoint, new AsyncCallback(_beginConnectCallback), netData_);
                    }
                    else
                    {
                        //构造对象
                        netData_ = new NetData();
                        netData_.socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                        netData_.socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
                        netData_.socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
                        netData_.socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                        netData_.recvBuffer = new byte[4096];
                        netData_.msgStream = new MemoryStream();
                        netData_.socket.BeginConnect(endPoint, new AsyncCallback(_beginConnectCallback), netData_);
                    }
                    //连接状态
                    netState_ = NetConnectState.Connecting;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"网络日志, 异步连接异常{ex.Message}");
                }
            }
        }

        //域名解析
        private void _getHostByName()
        {
            try
            {
                if (IPAddress.TryParse(ip_, out IPAddress address)) return;
                var ipAddresses = Dns.GetHostAddresses(ip_);
                foreach (var item in ipAddresses)
                {
                    try
                    {
                        if (item.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            IPHostEntry hostIpv6 = Dns.GetHostEntry(ip_);
                            ip_ = hostIpv6.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6).ToString();
                            if (NetHelper.AllowNetLoger) Debug.LogWarning($"网络日志, 域名解析成功, ipv6地址:" + ip_);
                            return;
                        }
                        else
                        {
                            IPHostEntry hostIpv4 = Dns.GetHostEntry(ip_);
                            ip_ = hostIpv4.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork).ToString();
                            if (NetHelper.AllowNetLoger) Debug.LogWarning($"网络日志, 域名解析成功, ipv4地址:" + ip_);
                            return;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"网络日志, 域名解析异常 {ip_},{ex.Message}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"网络日志, 域名解析异常 {ip_},{ex.Message}");
            }
        }

        //网络类型
        private bool _isIpv4(string ip)
        {
            int freq = ip.Count(f => (f == '.'));
            return freq == 3;
        }

        //网络错误
        private void _onNetworkError(bool bInvoke, string errorMessage)
        {
            //调试日志
            if (NetHelper.AllowNetLoger)
            {
                Debug.LogWarning($"网络日志, 网络关闭, 地址:{ip_}, 状态:{netState_}, 标记:{isQuickConnect_}, 原因:{errorMessage}");
            }

            //通知错误
            if (bInvoke)
            {
                NetErrorEvent?.Invoke(this, errorMessage);
            }

            //关闭连接
            _disconnect();
        }

        #endregion
    }
}
