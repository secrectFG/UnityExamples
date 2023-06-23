using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using UnityEngine;
using UnityEngine.Profiling;

namespace DefensiveNet
{
    class NetData
    {
        public NetworkStream stream;
        public Socket socket;
        // public byte[] recvBuffer;
        // public MemoryStream msgStream;
        public bool isEstablished;

        // public Queue<byte> sendQueue;
        public bool isSending;

        // public List<NetByteArray> cacheProtocolDataList;
        // public int cacheProtocolValidLength;

        // private byte[] netDataBuffer_;
        // private int netDataBufferLength_;

        public NetData()
        {
            socket = null;
            // recvBuffer = null;
            // msgStream = null;
            isEstablished = false;

            // sendQueue = new Queue<byte>();
            isSending = false;

            // cacheProtocolDataList = new List<NetByteArray>();
            // cacheProtocolValidLength = 0;

            // netDataBuffer_ = new byte[4096];
            // netDataBufferLength_ = 0;
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
                        if(netData_.stream==null){
                            Debug.LogError("网络日志：逻辑有错误netData_.stream==null");
                            return false;
                        }
                        netData_.stream.Write(buffer, index, length);
                        //GC.Collect();

                        //Debug.Log("Send len:" + length);
                    }

                    return true;
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
                        NetworkStream stream = new NetworkStream(netData_.socket, false);
                        netData_.stream = stream;
                        NetConnectEvent?.Invoke(this, index_, true);
                        //准备接受
                        _beginReceive();
                    }
                }


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
        protected async void _beginReceive()
        {
            try
            {

                byte[] dataBuffer = new byte[1024 * 512 + 4];
                var stream = netData_.stream;
                while (netData_ != null && netData_.isEstablished)
                {
                    //GC.Collect();

                    int bytesRead = await stream.ReadAsync(dataBuffer, 0, 4);
                    // Debug.Log("bytesRead:" + bytesRead);
                    if(bytesRead==0){
                        _onNetworkError(true, "连接已关闭1");
                        break;
                    }
                    if (bytesRead != 4)
                    {
                        _onNetworkError(true,"接收包头长度错误:" + bytesRead);
                        break;
                    }
                    int packetLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(dataBuffer, 0));
                    if (packetLength > dataBuffer.Length)
                    {
                        _onNetworkError(true,"数据包过大！长度:" + packetLength + "最大缓冲:" + dataBuffer.Length);
                        break;
                    }
                    // 读取整个数据包
                    int totalBytesRead = 0;
                    while (totalBytesRead < packetLength)
                    {
                        bytesRead = await stream.ReadAsync(dataBuffer, totalBytesRead + 4, packetLength - totalBytesRead);
                        if (bytesRead == 0)
                        {
                            // 连接已关闭
                            _onNetworkError(true,"连接已关闭2");
                            break;
                        }
                        totalBytesRead += bytesRead;
                    }

                    // Debug.Log("_onRecvNetData packetLength:" + packetLength);
                    //GC.Collect();


                    NetMessageEvent?.Invoke(this, dataBuffer, 0, packetLength + 4);
                }
            }
            catch (System.Exception ex)
            {
                if (isInitialize_)
                {
                    //调试信息
                    Debug.LogWarning($"网络日志, 网络准备接收出现异常:{ex.Message}\n{ex.StackTrace}");

                    //网络断开
                    _onNetworkError(true, "网络准备接收异常");
                }

                return;
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
                        //netData_.recvBuffer = new byte[4096];
                        //netData_.msgStream = new MemoryStream();
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
                        //netData_.recvBuffer = new byte[4096];
                        //netData_.msgStream = new MemoryStream();
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
