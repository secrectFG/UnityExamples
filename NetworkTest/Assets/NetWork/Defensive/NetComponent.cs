using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace DefensiveNet
{
    //1开始重连 2取消重连 3重连失败 4重连成功
    public enum NetReconnectAction
    {
        None = 0,                                       //没有状态
        Start,                                          //开始重连
        Cancel,                                         //取消重连
        Failed,                                         //重连失败
        Succeed,                                        //重连成功
    }

    public enum NetConnectState
    {
        None = 0,                                       //没有状态
        Domain,                                         //解析状态
        Connecting,                                     //正在连接
        Established,                                    //建立连接
    }

    public enum NetMessageType
    {
        MSG_TYPE_NORMAL_DATA = 0,                       // 正常数据
        MSG_TYPE_REPLY_SITE = 1,                        // 答复地址
        MSG_TYPE_QUICK_CONNECT = 2,                     // 快速连接
        MSG_TYPE_CLIENT_QUIT = 3,                       // 客户退出
        MSG_TYPE_SERVER_QUIT = 4,                       // 服务退出
        MSG_TYPE_SYNC_MESSAGE = 5,                      // 同步数据
        MSG_TYPE_CONF_MESSAGE = 6,                      // 确认数据
        MSG_TYPE_SERVER_HEART = 7,                      // 检测心跳
        MSG_TYPE_REUSER_EXCEPT = 8,                     // 复用异常
        MSG_TYPE_FEEDBACK_MESSAGE = 9,                  // 回馈消息
        MSG_TYPE_SERVER_CLOSE = 10,                     // 服务关闭
        MSG_TYPE_SERVER_DEFEND = 11,                    // 服务维护
        MSG_TYPE_SERVER_FINISH = 12,                    // 服务完成
        MSG_TYPE_SERVER_DETECT = 13,                    // 服务检测
        MSG_TYPE_NOT_INITIALIZE = 14,                   // 未初始化
    }

    public class NetSendData
    {
        public NetMessageType wMsgType_;
        public string protocolName_;
        public byte[] data_ = null;
        public int length_ = 0;
        public ulong sendIndex_ = 0;
        public INetSession session_ = null;

        public NetSendData(INetSession session, NetMessageType wMsgType, string protocolName, byte[] data, ulong sendIndex, int length)
        {
            wMsgType_ = wMsgType;
            protocolName_ = protocolName;
            data_ = new byte[length + 1];
            Array.Copy(data, data_, length);
            sendIndex_ = sendIndex;
            length_ = length;
            session_ = session;
        }
    }

    public class NetRecvData
    {
        public byte[] data_;
        public int length_;
        public int recvIndex_ = 0;
        public INetSession session_ = null;

        public NetRecvData(INetSession session, byte[] data, int index, int length)
        {
            data_ = new byte[length + 1];
            Array.Copy(data, index, data_, 0, length);
            recvIndex_ = 0;
            length_ = length;
            session_ = session;
        }
    }

    public class BlockingQueue<T>
    {
        //队列名称
        private string m_name;
        //FIFO队列
        private Queue<T> m_queue;
        //是否运行中
        private bool m_isRunning;
        //出队手动复位事件
        private ManualResetEvent m_dequeueWait;
        /// <summary>
        /// 队列长度
        /// </summary>
        public int Count => m_queue.Count;

        public BlockingQueue(string name = "BlockingQueue")
        {
            m_name = name;
            m_isRunning = true;
            m_queue = new Queue<T>();
            m_dequeueWait = new ManualResetEvent(false); // 无信号, 出队waitOne阻塞

        }

        /// <summary>
        /// 关闭阻塞队列
        /// </summary>
        public void Close()
        {
            // 停止队列
            m_isRunning = false;
            // 发送信号，通知出队阻塞waitOne可继续执行，可进行出队操作
            m_dequeueWait.Set();
        }

        public void Clear()
        {
            //清空队列
            lock (m_queue)
            {
                while (m_queue.Count > 0)
                {
                    m_queue.Dequeue();
                }
            }
        }

        /// <summary>
        /// 入队
        /// </summary>
        /// <param name="item"></param>
        public void Enqueue(T item)
        {
            if (!m_isRunning)
            {
                return;
            }

            lock (m_queue)
            {
                m_queue.Enqueue(item);
                // 发送信号，通知出队阻塞waitOne可继续执行，可进行出队操作
                m_dequeueWait.Set();
            }
        }

        /// <summary>
        /// 出队
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool Dequeue(out T item)
        {
            while (true)
            {
                if (!m_isRunning)
                {
                    lock (m_queue)
                    {
                        item = default(T);
                        return false;
                    }
                }
                lock (m_queue)
                {
                    // 如果队列有数据，则执行出队
                    if (m_queue.Count > 0)
                    {
                        item = m_queue.Dequeue();
                        // 置为无信号
                        m_dequeueWait.Reset();
                        return true;
                    }
                }
                // 如果队列无数据，则阻塞队列，停止出队，等待信号
                m_dequeueWait.WaitOne();
            }
        }
    }

    public class NetComponent : IDisposable,INetComponent
    {
        private string sessionGuid_ = string.Empty;
        private byte[] encryptKey_ = null;
        private byte[] lastEncryptKey_;
        private bool isInitialize_ = false;
        private bool isConnected_ = false;
        private bool isReconnected_ = false;

        private List<string> ipList_ = new List<string>();
        private List<INetSession> allSessionList_ = new List<INetSession>();
        private List<INetSession> useSessionList_ = new List<INetSession>();

        private int maxStart_ = 1;
        private int maxindex_ = 0;

        private int timeoutInMillionSeconds_ = 12000;

        private Action<bool> connectCallback_;

        private static readonly object sessionLock_ = new object();                                 //会话锁定
        private static readonly object recvdataLock_ = new object();                                //接收锁定
        private static readonly object senddataLock_ = new object();                                //发送锁定
        private static readonly object sendlistLock_ = new object();                                //缓存锁定

        private string networkGuid_ = "";                                                           //网络标识

        private System.Timers.Timer reConnectTimer_ = null;                                         //重连定时

        private double dbStartConnectTickCount_ = 0;                                                //开始时间
        private double dbSyncDataTickCount_ = 0;                                                    //重发时间
        private double dbDelayQuitTime_ = 0;                                                        //延退时间

        private ulong lClientRecvIndex_ = 0;                                                        //接收序号
        private ulong lClientSendIndex_ = 0;                                                        //发送序号
        private ulong lTotalHeartIndex_ = 0;                                                        //心跳序号

        private string localAddres_ = "";                                                           //本机地址
        private string encrpty_string_ = "badangel44..";                                            //加密子串

        private MemoryStream sharedMemoryStream_ = new MemoryStream();                              //内存对象

        private Queue<NetSendData> sendDataList_ = null;                                             //发送数组

        private BlockingQueue<NetSendData> sendDataQueue_ = null;                                   //发送集合
        private BlockingQueue<NetRecvData> recvDataQueue_ = null;                                   //接收集合

        private Task tSendMessageTask_ = null;                                                    //发送线程
        private Task tRecvMessageTask_ = null;                                                    //接收线程

        /// <summary>
        /// 获取当前网络是否处于连接状态
        /// </summary>
        public bool IsConnected => isConnected_;

        /// <summary>
        /// 获取或设置是否启用框架层的断线重连
        /// </summary>
        public bool UseFrameworkReconnect { get; set; } = true;

        //构造函数
        public NetComponent()
        {
            sessionGuid_ = string.Empty;
            encryptKey_ = null;
        }

        //重载函数
        public void Dispose()
        {
            Disconnect();
        }

        //获取地址
        public string GetIpAddress()
        {
            return localAddres_;
        }

        //开始连接
        public void Connect(List<string> ipList, int port, int timeoutInMillionSeconds, int maxStart, Action<bool> connectCallback)
        {
            try
            {
                //自旋加锁
                lock (sessionLock_)
                {
                    //初始标志
                    isInitialize_ = true;

                    //唯一标识
                    if (networkGuid_ == "")
                    {
                        //唯一标识
                        networkGuid_ = Guid.NewGuid().ToString("N");

                        //构建对象
                        sendDataQueue_ = new BlockingQueue<NetSendData>();
                        recvDataQueue_ = new BlockingQueue<NetRecvData>();
                        sendDataList_ = new Queue<NetSendData>();

                        //连接条数
                        maxStart_ = maxStart > 0 ? maxStart : maxStart_;

                        //连接回调
                        connectCallback_ = b =>
                        {
                            MessageCenter.Instance.RunInMainThread(o => connectCallback(b));
                        };

                        //超时控制
                        timeoutInMillionSeconds_ = timeoutInMillionSeconds;

                        //开始时间
                        dbStartConnectTickCount_ = NetHelper.GetTickCount();

                        //发送线程
                        if (tSendMessageTask_ == null)
                        {
                            tSendMessageTask_ = new Task(OnSendMessageTask);
                            tSendMessageTask_.Start();
                        }

                        //接收线程
                        if (tRecvMessageTask_ == null)
                        {
                            tRecvMessageTask_ = new Task(OnRecvMessageTask);
                            tRecvMessageTask_.Start();
                        }

                        //调试信息
                        Debug.LogWarning($"网络日志, 首次连接, 标识:" + networkGuid_);
                    }


                    //重连定时
                    if (reConnectTimer_ == null)
                    {
                        reConnectTimer_ = new System.Timers.Timer(1000);//实例化Timer类，设置时间间隔
                        reConnectTimer_.Elapsed += new System.Timers.ElapsedEventHandler(onNetReConnectEvent);//到达时间的时候执行事件
                        reConnectTimer_.AutoReset = true;//设置是执行一次（false）还是一直执行(true)
                        reConnectTimer_.Enabled = true;//是否执行System.Timers.Timer.Elapsed事件
                    }

                    //保存信息
                    foreach (string ip in ipList)
                    {
                        //判断重复
                        if (ipList_.Exists(temp => temp == ip)) continue;

                        //保存地址
                        ipList_.Add(ip);

                        //创建对象
                        INetSession session = new NetSessionImpl();

                        //设置属性
                        session.SetSessionInfo(ip, port, maxindex_++);

                        //绑定事件
                        session.NetConnectEvent += onConnectEvent;
                        session.NetMessageEvent += onMessageEvent;
                        session.NetErrorEvent += onErrorEvent;

                        //保存连接
                        allSessionList_.Add(session);
                    }

                    //连接空闲
                    ConnectFreeServer(false);
                }
            }
            catch (Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 连接接口出现异常{ex.Message}");
            }
        }

        //断开连接
        public void Disconnect()
        {
            try
            {
                //自旋加锁
                lock (sessionLock_)
                {
                    //清理消息
                    MessageCenter.Instance.CleanMessage();

                    //清理资源
                    _cleanup(true);
                }
            }
            catch (Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 断开接口出现异常{ex.Message}");
            }
        }

        //重连定时
        protected void onNetReConnectEvent(object source, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                //自旋加锁
                lock (sessionLock_)
                {
                    //初始标志
                    if (!isInitialize_) return;

                    /*
                    //首包超时
                    if (dbStartConnectTickCount_ != 0 && lClientRecvIndex_ == 0 && NetHelper.GetTickCount() - dbStartConnectTickCount_ >= timeoutInMillionSeconds_)
                    {
                        //调试信息
                        if (NetHelper.AllowNetLoger)
                        {
                            Debug.LogWarning($"网络日志, 长时间没有收到首个数据包, 关闭连接");
                        }
                        //失败回调
                        connectCallback_?.Invoke(false);
                        //通知断开
                        Task.Run(() => { NotifyUserLogout(false); });
                        return;
                    }
                    */

                    //当前连接
                    if (GetConnectCount() == 0)
                    {
                        //判断延退
                        if(dbDelayQuitTime_ != 0 && NetHelper.GetTickCount() > dbDelayQuitTime_)
                        {
                            //调试信息
                            if (NetHelper.AllowNetLoger)
                            {
                                Debug.LogWarning($"网络日志, 连接条数为空时间过程过长, 关闭连接");
                            }
                            //失败回调
                            connectCallback_?.Invoke(false);
                            //通知断开
                            Task.Run(() => { NotifyUserLogout(false); });
                        }
                        else
                        {
                            //连接空闲
                            ConnectFreeServer(true);
                        }
                    }
                    else
                    {
                        //延退时间
                        if(dbDelayQuitTime_ != 0)
                        {
                            //调试信息
                            if (NetHelper.AllowNetLoger)
                            {
                                Debug.LogWarning($"网络日志, 停止延退定时");
                            }
                            //重置时间
                            dbDelayQuitTime_ = 0;
                        }
                        //连接不足
                        if (GetConnectCount() < maxStart_)
                        {
                            //连接空闲
                            ConnectFreeServer(true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 定时重连回调出现异常{ex.Message}");
            }
        }

        //连接消息
        protected void onConnectEvent(INetSession session, int index, bool success)
        {
            try
            {
                //自旋加锁
                lock (sessionLock_)
                {

                    //初始标志
                    if (!isInitialize_) return;

                    //成功判断
                    if (success)
                    {
                        //快速连接
                        Send(NetMessageType.MSG_TYPE_QUICK_CONNECT, session);
                    }
                }
            }
            catch (Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 连接回调出现异常{ex.Message}");
            }
        }

        //接收消息
        /*
        * ******************************************************
        * 
        * public int      iPacketSize;                                 //包头标识
        * 
        * public ushort   wMsgType;                                    //消息类型
        * public ulong    llCmdNo;                                     //发包序号
        * 
        * public byte     controlFlag;                                 //控制字节
        * 
        * 备注:
        * 控制字节高2位代表控制标记: 
        *   00普通消息
        *   01接收确认消息
        *   02重连请求/应答
        *      req:guid[32]recvid[4]reconnectId[4]
        *      ack:errcode[1]recvid[4]
        *      errcode:1通信错误 2当前状态允许 3session不存在 4接收id错误
        *   03预留
        * 控制字节低6位代表加密方式:
        *   0代表不加密
        *   1代表RC4加密
        * ********************************************************
        */
        protected void onMessageEvent(INetSession session, byte[] buffer, int index, int length)
        {
            try
            {
                //自旋加锁
                lock (recvdataLock_)
                {
                    //初始标志
                    if (!isInitialize_) return;

                    //消息入队
                    NetRecvData recvData = new NetRecvData(session, buffer, index, length);
                    recvDataQueue_.Enqueue(recvData);
                }
            }
            catch (Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 消息回调出现异常{ex.Message}");
            }
        }

        //错误消息
        protected void onErrorEvent(INetSession session, string disconnectMessage)
        {
            try
            {
                //自旋加锁
                lock (sessionLock_)
                {

                    //初始标志
                    if (!isInitialize_) return;

                    //使用对象
                    useSessionList_.Remove(session);

                    //判断延退
                    if(useSessionList_.Count == 0 && dbDelayQuitTime_ == 0)
                    {
                        //调试信息
                        if (NetHelper.AllowNetLoger)
                        {
                            Debug.LogWarning($"网络日志, 激活对象为空, 开启延退定时器");
                        }
                        //延退时间
                        dbDelayQuitTime_ = NetHelper.GetTickCount() + timeoutInMillionSeconds_;
                    }
                }
            }
            catch (Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 错误回调出现异常{ex.Message}");
            }
        }

        //发送线程
        protected void OnSendMessageTask()
        {
            //线程循环
            while (isInitialize_)
            {
                try
                {
                    //读取数据
                    if (!sendDataQueue_.Dequeue(out NetSendData sendData))
                    {
                        continue;
                    }

                    //发送数据
                    if (sendData.session_ == null)
                    {
                        //调试信息
                        if (NetHelper.AllowNetLoger && sendData.wMsgType_ == NetMessageType.MSG_TYPE_NORMAL_DATA)
                        {
                            Debug.LogWarning($"网络日志, 群发正常数据, 序号:" + sendData.sendIndex_ + " 协议:" + sendData.protocolName_);
                        }

                        //群发数据
                        foreach (INetSession sessionTemp in useSessionList_)
                        {
                            sessionTemp?.Send(sendData.data_, 0, sendData.length_);
                        }
                    }
                    else
                    {
                        //调试信息
                        if (NetHelper.AllowNetLoger && sendData.wMsgType_ == NetMessageType.MSG_TYPE_NORMAL_DATA)
                        {
                            Debug.LogWarning($"网络日志, 单发正常数据, 序号:" + sendData.sendIndex_ + " 协议:" + sendData.protocolName_);
                        }

                        //单发数据
                        sendData.session_?.Send(sendData.data_, 0, sendData.length_);
                    }
                }
                catch (System.Exception ex)
                {
                    //调试信息
                    if (NetHelper.AllowNetLoger)
                    {
                        Debug.LogWarning($"网络日志, 发送线程出现异常{ex.Message}");
                    }
                }
            }

            //调试信息
            if (NetHelper.AllowNetLoger)
            {
                Debug.LogWarning($"网络日志, 发送线程正常退出");
            }
        }

        //消息处理
        protected void OnRecvMessageTask()
        {
            //线程循环
            while (isInitialize_)
            {
                //读取数据
                if (!recvDataQueue_.Dequeue(out NetRecvData recvData))
                {
                    continue;
                }

                //获取参数
                INetSession session = recvData.session_;
                byte[] buffer = recvData.data_;
                int length = recvData.length_;
                int index = recvData.recvIndex_;

                //读取数据
                var wMsgType = BitConverter.ToUInt16(buffer, 4);
                var llCmdNo = BitConverter.ToUInt64(buffer, 4 + 2);

                try
                {
                    //加密位置
                    var encryptStart = 4 + 2 + 8;

                    //有效序号
                    if (llCmdNo == 0 || (llCmdNo != 0 && lClientRecvIndex_ < llCmdNo))
                    {
                        //消息类型
                        switch (wMsgType)
                        {
                            case (ushort)NetMessageType.MSG_TYPE_QUICK_CONNECT:
                                {
                                    //调试信息
                                    if (NetHelper.AllowNetLoger)
                                    {
                                        Debug.LogWarning($"网络日志, 接收快速连接包, 通知结果");
                                    }

                                    //连接判断
                                    if (!isConnected_)
                                    {
                                        //连接成功
                                        isConnected_ = true;

                                        //通知成功
                                        connectCallback_?.Invoke(true);
                                    }

                                    //连接管理
                                    if (GetConnectCount() > maxStart_)
                                    {
                                        //关闭连接
                                        session?.Disconnect(false, $"");
                                    }
                                    else
                                    {
                                        //设置状态
                                        session.SetQuickConnect();

                                        //重连成功
                                        if (isReconnected_)
                                        {
                                            isReconnected_ = false;
                                            _triggerNetReconnectEvent(NetReconnectAction.Succeed);
                                        }

                                        //使用对象
                                        if (!useSessionList_.Contains(session))
                                        {
                                            useSessionList_.Add(session);
                                        }

                                        //重发缓存
                                        resendDataList(session);
                                    }

                                    break;
                                }
                            case (ushort)NetMessageType.MSG_TYPE_NORMAL_DATA:
                                {
                                    /*
                                    //调试信息
                                    if (NetHelper.AllowNetLoger)
                                    {
                                        Debug.LogWarning($"网络日志, 接收服务器消息类型:" + wMsgType + " 序号:" + llCmdNo + " 本地序号:" + lClientRecvIndex_ + " 数据大小:" + length);
                                    }
                                    */

                                    //跳包处理
                                    if (llCmdNo > (lClientRecvIndex_ + 1))
                                    {
                                        if (NetHelper.GetTickCount() - dbSyncDataTickCount_ >= 2000)
                                        {
                                            //同步时间
                                            dbSyncDataTickCount_ = NetHelper.GetTickCount();

                                            //同步数据
                                            Send(NetMessageType.MSG_TYPE_SYNC_MESSAGE, null);
                                        }
                                    }
                                    else
                                    {
                                        //接收序号
                                        lClientRecvIndex_ = llCmdNo;

                                        /*
                                        //调试信息
                                        if (NetHelper.AllowNetLoger)
                                        {
                                            Debug.LogWarning($"网络日志, 更新接收序号:" + lClientRecvIndex_);
                                        }
                                        */

                                        //正常数据
                                        var controlByte = buffer[encryptStart];
                                        var controlFlag = (byte)(controlByte >> 6);

                                        //数据偏移
                                        index += (encryptStart + 1);
                                        length -= (encryptStart + 1);

                                        //正常数据
                                        switch (controlFlag)
                                        {
                                            case 0: //普通消息
                                                int cryptFlag = controlByte & 0x3f;
                                                onReceiveNormalPBMessage(buffer, index, length, cryptFlag);
                                                break;
                                            default:
                                                //调试信息
                                                if (NetHelper.AllowNetLoger)
                                                {
                                                    Debug.LogWarning($"网络日志, 不支持的控制标记：{controlFlag}");
                                                }
                                                break;
                                        }
                                    }

                                    break;
                                }
                                
                            case (ushort)NetMessageType.MSG_TYPE_SERVER_DEFEND:
                                {
                                    //结束接收
                                    isInitialize_ = false;

                                    //维护消息
                                    var controlByte = buffer[encryptStart];
                                    var controlFlag = (byte)(controlByte >> 6);

                                    //数据偏移
                                    index += (encryptStart + 1);
                                    length -= (encryptStart + 1);

                                    //正常数据
                                    switch (controlFlag)
                                    {
                                        case 0: //普通消息
                                            int cryptFlag = controlByte & 0x3f;
                                            onReceiveNormalPBMessage(buffer, index, length, cryptFlag);
                                            break;
                                        default:
                                            //调试信息
                                            if (NetHelper.AllowNetLoger)
                                            {
                                                Debug.LogWarning($"网络日志, 不支持的控制标记：{controlFlag}");
                                            }
                                            break;
                                    }

                                    break;
                                }
                            case (ushort)NetMessageType.MSG_TYPE_REPLY_SITE:
                                {
                                    //答复地址
                                    if (localAddres_ == "")
                                    {
                                        localAddres_ = Encoding.ASCII.GetString(buffer, encryptStart, 46);
                                    }

                                    break;
                                }
                            case (ushort)NetMessageType.MSG_TYPE_SERVER_HEART:
                                {
                                    //心跳回复
                                    var HeartIndex = BitConverter.ToUInt64(buffer, encryptStart); //心跳序号
                                    if (HeartIndex > lTotalHeartIndex_)
                                    {
                                        //心跳序号
                                        lTotalHeartIndex_ = HeartIndex;

                                        //服务序号
                                        var ServerSendIndex = BitConverter.ToUInt64(buffer, encryptStart + 8);
                                        var ServerRecvIndex = BitConverter.ToUInt64(buffer, encryptStart + 8 + 8);
                                        
                                        /*
                                        //调试信息
                                        if (NetHelper.AllowNetLoger)
                                        {
                                            Debug.LogWarning($"网络日志, 接收到服务器心跳包, 服务器发送:{ServerSendIndex}, 客户端接收:{lClientRecvIndex_}, 客户端发送:{lClientSendIndex_}, 服务器接收:{ServerRecvIndex}");
                                        }
                                        */

                                        //删除缓存
                                        if (ServerRecvIndex > 0)
                                        {
                                            deleteDataList(ServerRecvIndex);
                                        }

                                        //重发数据
                                        if (ServerRecvIndex < lClientSendIndex_)
                                        {
                                            resendDataList(null);
                                        }

                                        //同步数据
                                        if (ServerSendIndex > lClientRecvIndex_)
                                        {
                                            //同步时间
                                            dbSyncDataTickCount_ = NetHelper.GetTickCount();

                                            //同步数据
                                            Send(NetMessageType.MSG_TYPE_SYNC_MESSAGE, null);
                                        }

                                        //确认数据
                                        Send(NetMessageType.MSG_TYPE_CONF_MESSAGE, null);
                                    }

                                    break;
                                }
                            case (ushort)NetMessageType.MSG_TYPE_SERVER_CLOSE:
                                {
                                    //结束接收
                                    isInitialize_ = false;

                                    //调试信息
                                    if (NetHelper.AllowNetLoger)
                                    {
                                        Debug.LogWarning($"网络日志, 接收到服务器断开包：{wMsgType}");
                                    }
                                    //通知失败
                                    if (!isConnected_)
                                    {
                                        //失败回调
                                        connectCallback_?.Invoke(false);
                                    }
                                    //通知断开
                                    Task.Run(() => { NotifyUserLogout(true); });
                                    break;
                                }
                            case (ushort)NetMessageType.MSG_TYPE_REUSER_EXCEPT:
                                {
                                    //结束接收
                                    isInitialize_ = false;

                                    //调试信息
                                    if (NetHelper.AllowNetLoger)
                                    {
                                        Debug.LogWarning($"网络日志, 接收到服务器异常包：{wMsgType}");
                                    }
                                    //通知失败
                                    if (!isConnected_)
                                    {
                                        //失败回调
                                        connectCallback_?.Invoke(false);
                                    }
                                    //通知断开
                                    Task.Run(() => { NotifyUserLogout(true); });
                                    break;
                                }
                            case (ushort)NetMessageType.MSG_TYPE_NOT_INITIALIZE:
                                {
                                    //调试信息
                                    if (NetHelper.AllowNetLoger)
                                    {
                                        Debug.LogWarning($"网络日志, 服务没有初始化：{wMsgType}");
                                    }
                                    //关闭连接
                                    session.Disconnect(true, $"服务没有初始化");
                                    break;
                                }
                            default:
                                {
                                    //调试信息
                                    if (NetHelper.AllowNetLoger)
                                    {
                                        Debug.LogWarning($"网络日志, 不支持的消息类型：{wMsgType}");

                                    }
                                    break;
                                }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"网络日志, 接收线程出现异常:{ex.Message}, 消息类型：{wMsgType}");
                }
            }

            //调试信息
            if (NetHelper.AllowNetLoger)
            {
                Debug.LogWarning($"网络日志, 消息处理线正常退出");
            }
        }

        public void SetEncryptProtocolKey(string sessionGuid, byte[] encryptKey)
        {
            sessionGuid_ = sessionGuid;
            encryptKey_ = encryptKey;
            lastEncryptKey_ = encryptKey;
        }

        //发送数据
        public bool Send(string protocolName, byte[] pbContent)
        {
            return Send(protocolName, pbContent, NetMessageType.MSG_TYPE_NORMAL_DATA, null);
        }

        //发送数据
        public bool Send(NetMessageType wMsgType, INetSession session)
        {
            return Send("", null, wMsgType, session);
        }

        //发送数据
        /*
        * ******************************************************
        * 
        * public int      iPacketSize;                                 //包头标识
        * 
        * public ushort   wMsgType;                                    //消息类型
        * public ulong    llCmdNo;                                     //发包序号
        * 
        * public byte     controlFlag;                                 //控制字节
        * 
        * 备注:
        * 控制字节高2位代表控制标记: 
        *   00普通消息
        *   01接收确认消息
        *   02重连请求/应答
        *      req:guid[32]recvid[4]reconnectId[4]
        *      ack:errcode[1]recvid[4]
        *      errcode:1通信错误 2当前状态允许 3session不存在 4接收id错误
        *   03预留
        * 控制字节低6位代表加密方式:
        *   0代表不加密
        *   1代表RC4加密
        * ********************************************************
        */
        public bool Send(string protocolName, byte[] pbContent, NetMessageType wMsgType, INetSession session)
        {
            try
            {
                //自旋加锁
                lock (senddataLock_)
                {
                    //初始标志
                    if (!isInitialize_) return false;

                    //条件判断
                    if (isConnected_ || wMsgType == NetMessageType.MSG_TYPE_NORMAL_DATA || wMsgType == NetMessageType.MSG_TYPE_QUICK_CONNECT || wMsgType == NetMessageType.MSG_TYPE_CLIENT_QUIT)
                    {
                        //数据准备
                        sharedMemoryStream_.Position = 0;
                        sharedMemoryStream_.SetLength(0);

                        //是否加密
                        bool useEncrypt = encryptKey_ != null;
                        byte encryptControlFlag = useEncrypt ? (byte)1 : (byte)0;   //当前加密版本号是1 0代表未加密
                        
                        //发送序号
                        ulong lClientSendIndex = 0;
                        if(wMsgType == NetMessageType.MSG_TYPE_NORMAL_DATA)
                        {
                            lClientSendIndex = ++lClientSendIndex_;
                        }

                        //构造数据
                        var bw = new BinaryWriter(sharedMemoryStream_);

                        //消息类型
                        switch (wMsgType)
                        {
                            case NetMessageType.MSG_TYPE_QUICK_CONNECT: //快速连接
                                {
                                    //包头数据
                                    bw.Write((int)0);
                                    bw.Write((ushort)wMsgType);
                                    bw.Write(lClientSendIndex);

                                    //唯一标识
                                    bw.Write(Encoding.UTF8.GetBytes(networkGuid_), 0, 32);

                                    //包体数据
                                    string randString = GetRandString();
                                    string calcMd5String = Md5String(randString + encrpty_string_);
                                    bw.Write(Encoding.UTF8.GetBytes(randString), 0, 32);
                                    bw.Write(Encoding.UTF8.GetBytes(calcMd5String), 0, 32);
                                    bw.Write(lClientSendIndex);
                                }
                                break;
                            case NetMessageType.MSG_TYPE_NORMAL_DATA: //正常数据
                                {
                                    //包头数据
                                    bw.Write((int)0);
                                    bw.Write((ushort)wMsgType);
                                    bw.Write(lClientSendIndex);

                                    //游戏数据
                                    bw.Write(useEncrypt ? (byte)1 : (byte)0);
                                    bw.Write(Encoding.UTF8.GetBytes(protocolName));
                                    bw.Write((byte)0);
                                    bw.Write(pbContent);
                                }
                                break;
                            case NetMessageType.MSG_TYPE_CONF_MESSAGE: //确认数据
                                {
                                    //包头数据
                                    bw.Write((int)0);
                                    bw.Write((ushort)wMsgType);
                                    bw.Write(lClientSendIndex);

                                    //包体数据
                                    bw.Write(lClientRecvIndex_);
                                }
                                break;
                            case NetMessageType.MSG_TYPE_SYNC_MESSAGE: //同步数据
                                {
                                    //包头数据
                                    bw.Write((int)0);
                                    bw.Write((ushort)wMsgType);
                                    bw.Write(lClientSendIndex);

                                    //包体数据
                                    bw.Write(lClientRecvIndex_ + 1);
                                }
                                break;
                            default: //其他消息
                                {
                                    //包头数据
                                    bw.Write((int)0);
                                    bw.Write((ushort)wMsgType);
                                    bw.Write(lClientSendIndex);
                                }
                                break;
                        }

                        //加密数据
                        if (wMsgType == NetMessageType.MSG_TYPE_NORMAL_DATA && useEncrypt)
                        {
                            //加密开始
                            int encryptStart = 4 + 2 + 8 + 1;
                            NetHelper.Rc4Algorithm(encryptKey_, sharedMemoryStream_.GetBuffer(), encryptStart, (int)sharedMemoryStream_.Length - encryptStart);
                        }

                        //重置位置
                        sharedMemoryStream_.Position = 0;

                        //有效数据
                        int dataSize = (int)sharedMemoryStream_.Length - 4;

                        //包头大小
                        int networkLen = IPAddress.HostToNetworkOrder(dataSize);
                        bw.Write(networkLen);

                        try
                        {
                            //缓存加锁
                            lock (sendlistLock_)
                            {
                                //byte[] sendbuf = new byte[(int)sharedMemoryStream_.Length];
                                //sharedMemoryStream_.Position = 0;
                                //sharedMemoryStream_.Read(sendbuf, 0, sendbuf.Length);
                                //缓存队列
                                NetSendData sendData = new NetSendData(session, wMsgType, protocolName,sharedMemoryStream_.GetBuffer(), lClientSendIndex_, (int)sharedMemoryStream_.Length);
                                sendDataList_.Enqueue(sendData);
                                //Debug.Log("sendDataList_:"+sendDataList_.Count);

                                //通知发送
                                sendDataQueue_.Enqueue(sendData);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"网络日志, 保存发送队列异常:" + ex.Message);
                            return false;
                        }

                        /*
                        //调试信息
                        if (NetHelper.AllowNetLoger)
                        {
                            Debug.LogWarning($"网络日志, 发送数据, 类型:" + wMsgType);
                        }
                        */
                        return true;
                    }
                    else
                    {
                        //调试信息
                        if (NetHelper.AllowNetLoger)
                        {
                            Debug.LogWarning($"网络日志, 没有连接或不是退出包, 无法发送游戏数据, 类型：{wMsgType}");
                        }
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"网络日志, 发送接口异常:" + ex.Message);
                return false;
            }
        }

        //线程循环
        public void Update()
        {
            return;
        }

        //重发缓存数据
        protected void resendDataList(INetSession session)
        {
            try
            {
                //缓存加锁
                lock (sendlistLock_)
                {

                    //重发方式
                    if (session == null)
                    {
                        //整体重发
                        foreach (NetSendData tempSendData in sendDataList_)
                        {
                            /*
                            //调试信息
                            if (NetHelper.AllowNetLoger)
                            {
                                Debug.LogWarning($"网络日志, 重发发送序号:" + tempSendData.sendIndex_ + "类型:" + tempSendData.wMsgType_);
                            }
                            */

                            //通知发送
                            sendDataQueue_.Enqueue(tempSendData);
                        }
                    }
                    else
                    {
                        //指向重发
                        foreach (NetSendData tempSendData in sendDataList_)
                        {
                            //指向发送
                            if (tempSendData.session_ == null || tempSendData.session_ == session)
                            {
                                sendDataQueue_.Enqueue(tempSendData);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //调试信息
                Debug.LogWarning($"网络日志, 重发缓存出现异常{ex.Message}");
            }
        }

        //删除过期数据
        protected void deleteDataList(ulong ServerRecvIndex)
        {
            //判断删除
            if (ServerRecvIndex != 0)
            {
                try
                {
                    //缓存加锁
                    lock (sendlistLock_)
                    {
                        while (sendDataList_.Count > 0)
                        {
                            // 首个数据
                            NetSendData sendData = sendDataList_.Peek();

                            // 判断索引
                            if (sendData.sendIndex_ <= ServerRecvIndex)
                            {
                                //删除数据
                                sendDataList_.Dequeue();
                                Debug.Log("RemoveAt sendDataList_:" + sendDataList_.Count);

                                /*
                                //调试信息
                                if (NetHelper.AllowNetLoger)
                                {
                                    //调试信息
                                    Debug.LogWarning($"网络日志, 删除发送序号:" + sendData.sendIndex_ + ", 剩余数据数量:" + sendDataList_.Count);
                                }
                                */
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    //调试信息
                    Debug.LogWarning($"网络日志, 删除缓存出现异常{ex.Message}");
                }
            }
        }


        #region 内部接口,无需加锁

        //开始连接
        private void ConnectFreeServer(bool bReConnect)
        {
            //初始标志
            if (!isInitialize_)
            {
                //调试信息
                if (NetHelper.AllowNetLoger)
                {
                    Debug.LogWarning($"网络日志, 开始连接，状态没有初始化");
                }
                return;
            }

            //参数检测
            if (allSessionList_.Count == 0)
            {
                //调试信息
                if (NetHelper.AllowNetLoger)
                {
                    Debug.LogWarning($"网络日志, 连接空闲服务器,原始服务器列表为空, 肯定有错误发生");
                }
                return;
            }

            //连接上限
            int nCount = GetConnectCount();
            if (nCount >= maxStart_)
            {
                return;
            }

            //重连通知
            if (nCount == 0 && !isReconnected_ && bReConnect)
            {
                isReconnected_ = true;
                _triggerNetReconnectEvent(NetReconnectAction.Start);
            }

            //空闲会话
            List<INetSession> FreeSessionList = new List<INetSession>();
            foreach (INetSession sessionTemp in allSessionList_)
            {
                if (sessionTemp.IsFreeSession())
                {
                    FreeSessionList.Add(sessionTemp);
                }
            }

            //随机排序
            List<INetSession> RandomFreeSessionList = RandomSortList(FreeSessionList);
            
            /*
            //调试日志
            if (NetHelper.AllowNetLoger)
            {
                Debug.LogWarning($"网络日志, 重连服务器, 空闲条数:{RandomFreeSessionList.Count}");
            }
            */

            //空闲连接
            if (RandomFreeSessionList.Count > 0)
            {
                //发起连接
                foreach (INetSession sessionTemp in RandomFreeSessionList)
                {
                    //开始连接
                    sessionTemp.Connect();
                }
            }
        }

        //随机排序
        private List<T> RandomSortList<T>(List<T> ListT)
        {
            System.Random random = new System.Random();
            List<T> newList = new List<T>();
            foreach (T item in ListT)
            {
                newList.Insert(random.Next(newList.Count + 1), item);
            }
            return newList;
        }

        //连接数量
        private int GetConnectCount()
        {
            int count = 0;
            foreach (INetSession sessionTemp in useSessionList_)
            {
                if (sessionTemp.IsActiveSession())
                {
                    count++;
                }
            }
            return count;
        }

        //MD5加密
        private string Md5String(string str)
        {
            MD5 md5 = MD5.Create();
            // 将字符串转换成字节数组
            byte[] byteOld = Encoding.UTF8.GetBytes(str);
            // 调用加密方法
            byte[] byteNew = md5.ComputeHash(byteOld);
            // 将加密结果转换为字符串
            StringBuilder sb = new StringBuilder();
            foreach (byte b in byteNew)
            {
                // 将字节转换成16进制表示的字符串，
                sb.Append(b.ToString("x2"));
            }
            // 返回加密的字符串
            return sb.ToString();
        }

        //随机子串
        private string GetRandString()
        {
            string srcChaString = "0123456789abcdefghijklmnopqrstuvwxyz";
            char[] charArray = srcChaString.ToCharArray();
            string str = "";

            System.Random rd = new System.Random((int)DateTime.Now.Ticks);
            for (int i = 0; i < 32; i++)
            {
                int randIndex = rd.Next(1, charArray.Length);
                string findStr = charArray[randIndex % charArray.Length].ToString();
                str += findStr;
            }

            return str;
        }

        //计算验证
        private string calcReplyString(string secondKey, int agentReplyLen)
        {
            string srcChaString = "0123456789abcdefghijklmnopqrstuvwxyz";

            char[] charArray = srcChaString.ToCharArray();
            string repeatString = "";

            for (int i = 0; i < agentReplyLen; i++)
            {
                repeatString += "0";
            }

            while (true)
            {
                int length = charArray.Length;

                string str = "";
                System.Random rd = new System.Random((int)DateTime.Now.Ticks);
                for (int i = 0; i < 32; i++)
                {
                    int randIndex = rd.Next(1, length);
                    string findStr = charArray[randIndex % length].ToString();
                    str += findStr;
                }

                string strString = str.ToString();
                string calcString = secondKey + strString;
                string calcMd5String = Md5String(calcString);
                if (calcMd5String.Substring(0, agentReplyLen) == repeatString)
                {
                    return str;
                }
            }
        }

        // 玩家退出
        private void NotifyUserLogout(bool bClose)
        {
            //通知失败
            if (!bClose) _triggerNetReconnectEvent(NetReconnectAction.Failed);

            //内部清理
            _cleanup(false);

            //异步关闭
            _triggerNetLostConnection("与服务器连接断开");
        }

        //游戏数据
        private void onReceiveNormalPBMessage(byte[] buffer, int index, int length, int cryptFlag)
        {
            switch (cryptFlag)
            {
                case 0: //do nothing.
                    break;
                case 1: //rc4
                    NetHelper.Rc4Algorithm(encryptKey_, buffer, index, length);
                    break;
                default:
                    throw new Exception($"不支持的协议加密方式：{cryptFlag}");
            }

            // 读取到结构体名字
            var beginIndex = index;
            string fullName = null;
            int tmpLength = 0;
            try
            {
                while (true)
                {
                    byte d = buffer[beginIndex++];
                    if (d == 0)
                        break;
                    tmpLength++;
                }
                fullName = Encoding.UTF8.GetString(buffer, index, tmpLength);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"解析协议名称出错：\n{ex.ToString()}");
            }

            /*
            //调试日志
            if(NetHelper.AllowNetLoger)
            {
                Debug.LogWarning($"网络日志, 解析协议名称:" + fullName);
            }
            */

            var tmpBuffer = new byte[length - tmpLength - 1];
            Array.Copy(buffer, beginIndex, tmpBuffer, 0, tmpBuffer.Length);

            var data = new NetHelper.NetDataPack()
            {
                packName = fullName,
                pbdata = tmpBuffer,
            };

            //投递消息
            MessageCenter.Instance.PostMessage(MsgType.NET_RECEIVE_DATA, this, data);
        }

        //执行清理,调用处需要加锁
        private void _cleanup(bool bActive)
        {
            //调试信息
            if (NetHelper.AllowNetLoger)
            {
                Debug.LogWarning($"网络日志, 执行清理开始");
            }

            //关闭重连
            if (reConnectTimer_ != null)
            {
                reConnectTimer_.Stop();
                reConnectTimer_ = null;
            }
            
            //主动退出
            if (bActive && IsConnected)
            {
                //通知退出
                Send(NetMessageType.MSG_TYPE_CLIENT_QUIT, null);
            }

            //关键变量
            isInitialize_ = false;
            networkGuid_ = "";
            isConnected_ = false;
            isReconnected_ = false;
            sessionGuid_ = string.Empty;
            encryptKey_ = null;
            dbStartConnectTickCount_ = 0;
            dbSyncDataTickCount_ = 0;
            dbDelayQuitTime_ = 0;
            lClientRecvIndex_ = 0;
            lClientSendIndex_ = 0;
            lTotalHeartIndex_ = 0;

            //缓存变量
            if (useSessionList_ != null)
            {
                foreach (INetSession sessionTemp in useSessionList_)
                {
                    sessionTemp.Disconnect(false, $"");
                }
                useSessionList_.Clear();
            }

            //所有连接
            if (allSessionList_ != null)
            {
                foreach (INetSession sessionTemp in allSessionList_)
                {
                    sessionTemp.Disconnect(false, $"");
                }
            }

            //发送列表
            if (sendDataList_ != null)
            {
                sendDataList_.Clear();
            }

            //发送队列
            if (sendDataQueue_ != null)
            {
                sendDataQueue_.Close();
                sendDataQueue_.Clear();
            }

            if (tSendMessageTask_ != null)
            {
                tSendMessageTask_.Wait();
                tSendMessageTask_ = null;
            }

            //接收队列
            if (recvDataQueue_ != null)
            {
                recvDataQueue_.Close();
                recvDataQueue_.Clear();
            }
            
            if (tRecvMessageTask_ != null)
            {
                tRecvMessageTask_.Wait();
                tRecvMessageTask_ = null;
            }

            //调试信息
            if (NetHelper.AllowNetLoger)
            {
                Debug.LogWarning($"网络日志, 执行清理完成");
            }
        }

        //通知断开
        private void _triggerNetLostConnection(string disconnectMessage)
        {
            /*
            //调试日志
            if (NetHelper.AllowNetLoger)
            {
                Debug.LogWarning("LOST_CONNECTION: " + (disconnectMessage ?? string.Empty));
            }
            */

            MessageCenter.Instance.PostMessage(new Message("LOST_CONNECTION", disconnectMessage, lastEncryptKey_));
        }

        //触发断线重连事件通知 1开始重连 2取消重连 3重连失败 4重连成功
        private void _triggerNetReconnectEvent(NetReconnectAction action)
        {
            MessageCenter.Instance.PostMessage(new Message("RECONNECT_EVENT", this, (int)action));
        }

        #endregion
    }
}
