using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace DefensiveNet
{

    public class NetSessionImpl : INetSession
    {
        NetworkStream networkStream = null;
        private bool isInitialize_ = false;
        private NetConnectState netState_ = NetConnectState.None;
        private bool isQuickConnect_ = false;

        private string ip_ = "";
        private int port_ = 0;
        private int index_ = 0;

        public event Action<INetSession, int, bool> NetConnectEvent;
        public event Action<INetSession, byte[], int, int> NetMessageEvent;
        public event Action<INetSession, string> NetErrorEvent;
        private static readonly object sessionLock_ = new object();


        Stopwatch connectCoolDown = Stopwatch.StartNew();
        // bool firstConnect = true;

        public NetConnectState NetState
        {
            get
            {
                return netState_;
            }
            set
            {
                netState_ = value;
                //Debug.Log($"Set NetState:{netState_}");
            }
        }

        public void SetSessionInfo(string ip, int port, int index)
        {
            ip_ = ip;
            port_ = port;
            index_ = index;
        }

        public NetConnectState GetSessionState()
        {
            return NetState;
        }

        long connectCoolTime = 0;
        //开始连接
        public void Connect()
        {
            // Debug.Log("开始连接");
            try
            {
                //防止短时间重连
                if (connectCoolDown.ElapsedMilliseconds < connectCoolTime)
                {
                    return;
                }
                connectCoolTime += 1000;//每连接一次就多加点时间
                // connectCoolTime = 500;
                connectCoolDown.Restart();

                if (NetState == NetConnectState.Connecting)
                {
                    return;
                }
                //连接状态
                if (NetState != NetConnectState.None)
                {
                    //调试日志
                    _onNetworkError(true, $"连接时发生错误,强制断开重置,等待下次连接");
                    return;
                }

                Debug.Log($"网络日志, 开始连接服务器, 地址:" + ip_);

                //初始状态
                isInitialize_ = true;
                NetState = NetConnectState.Domain;

                //异步连接
                startConnect();
            }
            catch (Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 网络连接接口出现异常{ex.Message}");
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
            _onNetworkError(bInvoke, errorMessage);
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
                    if (networkStream != null && NetState == NetConnectState.Established)
                    {
                        if (networkStream == null)
                        {
                            Debug.LogError("网络日志：逻辑有错误netData_.stream==null");
                            return false;
                        }
                        networkStream.Write(buffer, index, length);
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


        //投递接收
        protected async void _beginReceive()
        {
            byte[] dataBuffer = new byte[1024 * 512 + 4];
            int bytesRead = 0;
            var stream = networkStream;

            while (NetState == NetConnectState.Established)
            {
                try
                {
                    bytesRead = await stream.ReadAsync(dataBuffer, 0, 4);
                }
                catch (Exception ex)
                {
                    if (NetState == NetConnectState.Established && stream == networkStream)
                    {
                        _onNetworkError(true, "连接已关闭1 " + ex.Message);
                    }
                    else
                    {
                        Debug.Log("网络日志, 连接被手动关闭了 " + ex.Message);
                    }
                    break;
                }
                if ((NetState != NetConnectState.Established)) break;
                if (bytesRead == 0)
                {
                    _onNetworkError(true, "连接已关闭1");
                    break;
                }
                if (bytesRead != 4)
                {
                    _onNetworkError(true, "接收包头长度错误:" + bytesRead);
                    break;
                }
                int packetLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(dataBuffer, 0));
                if (packetLength > dataBuffer.Length)
                {
                    _onNetworkError(true, "数据包过大！长度:" + packetLength + "最大缓冲:" + dataBuffer.Length);
                    break;
                }
                // 读取整个数据包
                int totalBytesRead = 0;
                while (totalBytesRead < packetLength)
                {
                    try
                    {
                        bytesRead = await stream.ReadAsync(dataBuffer, totalBytesRead + 4, packetLength - totalBytesRead);
                    }
                    catch (Exception ex)
                    {
                        if (NetState == NetConnectState.Established && stream == networkStream)
                        {
                            _onNetworkError(true, "连接已关闭2 " + ex.Message);
                        }
                        else
                        {
                            Debug.Log("网络日志, 连接被手动关闭了2 " + ex.Message);
                        }
                        break;
                    }
                    if (bytesRead == 0)
                    {
                        // 连接已关闭
                        _onNetworkError(true, "连接已关闭2");
                        break;
                    }
                    totalBytesRead += bytesRead;
                }

                // Debug.Log("_onRecvNetData packetLength:" + packetLength);
                //GC.Collect();


                NetMessageEvent?.Invoke(this, dataBuffer, 0, packetLength + 4);
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


                //关闭对象
                if (networkStream != null)
                {
                    try
                    {
                        //关闭对象
                        networkStream.Close();
                        networkStream = null;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"网络日志, 网络关闭异常{ex.Message}");
                    }
                }

                //停止状态
                NetState = NetConnectState.None;
                networkStream = null;
            }
        }

        //异步连接
        private async void startConnect()
        {
            //连接状态
            NetState = NetConnectState.Connecting;
            try
            {
                Socket socket = null;
                IPEndPoint endPoint = null;
                string ip = ip_;
                int port = port_;

                lock (sessionLock_)
                {
                    ip = ip_;
                    port = port_;
                }

                endPoint = new IPEndPoint(IPAddress.Parse(ip), port);

                // //网络属性
                // uint dummy = 0;
                // byte[] inOptionValues = new byte[System.Runtime.InteropServices.Marshal.SizeOf(dummy) * 3];
                // BitConverter.GetBytes((uint)1).CopyTo(inOptionValues, 0);
                // BitConverter.GetBytes((uint)5000).CopyTo(inOptionValues, System.Runtime.InteropServices.Marshal.SizeOf(dummy));//keep-alive间隔
                // BitConverter.GetBytes((uint)1000).CopyTo(inOptionValues, System.Runtime.InteropServices.Marshal.SizeOf(dummy) * 2);//尝试间隔


                AddressFamily family = _isIpv4(ip_) ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
                // socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
                // socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
                // socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                int timeoutMilliseconds = 3000;
                Task connectTask = socket.ConnectAsync(endPoint);
                Task timeoutTask = Task.Delay(timeoutMilliseconds);
                // 等待连接任务或超时任务完成
                Task completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask == connectTask)
                {
                    // 连接任务已完成（成功或失败）
                    if (socket.Connected)
                    {
                        lock (sessionLock_)
                        {
                            networkStream = new NetworkStream(socket, true);
                        }
                        onConnected();
                    }
                    else
                    {
                        _onNetworkError(true, "连接失败");
                    }
                }
                else
                {
                    _onNetworkError(true, "连接超时");
                }

            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"网络日志, 异步连接异常{ex.Message}");
            }

        }

        void onConnected()
        {
            //更新数据
            NetState = NetConnectState.Established;

            //调试信息
            if (NetHelper.AllowNetLoger)
            {
                Debug.LogWarning($"网络日志, 连接成功, 地址:" + ip_);

            }
            //连接回调
            NetConnectEvent?.Invoke(this, index_, true);
            //准备接受
            _beginReceive();
        }

        //网络类型
        private bool _isIpv4(string address)
        {
            IPAddress ipAddress;
            if (IPAddress.TryParse(address, out ipAddress))
            {
                return ipAddress.AddressFamily != AddressFamily.InterNetworkV6;
            }
            return true;
        }

        //网络错误
        private void _onNetworkError(bool bInvoke, string errorMessage)
        {
            //调试日志
            if (NetHelper.AllowNetLoger)
            {
                Debug.LogWarning($"网络日志, 网络关闭, 地址:{ip_}, 状态:{NetState}, 标记:{isQuickConnect_}, 原因:{errorMessage}");
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
