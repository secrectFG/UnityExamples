using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using System.Threading.Tasks;
using System.Net;
using System.Linq;

namespace DirectNet
{
    //1开始重连 2取消重连 3重连失败 4重连成功
    enum NetReconnectAction
    {
        None = 0,
        Start,
        Cancel,
        Failed,
        Succeed,
    }
    public class NetComponent : IDisposable, INetComponent
    {
        private INetSession session_;
        private string sessionGuid_;
        private byte[] encryptKey_;
        private byte[] lastEncryptKey_;
        private bool isConnected_ = false;
        private int reconnectId_ = 0;

        private string cacheIp_ = string.Empty;
        private int cachePort_ = 0;
        private bool isReconnecting_ = false;
        private int reconnectRetryCount_ = 0;
        private NetMessageCache cacheData_ = new NetMessageCache();

        /// <summary>
        /// 获取当前网络是否处于连接状态
        /// </summary>
        public bool IsConnected => isConnected_ || isReconnecting_;

        /// <summary>
        /// 获取或设置是否启用框架层的断线重连
        /// </summary>
        public bool UseFrameworkReconnect { get; set; } = true;

        public NetComponent()
        {
            session_ = new NetSessionImpl();
            session_.NetRecvDataEvent += onReceiveData;
            session_.NetErrorEvent += disconnectMessage =>
            {
                if (!UseFrameworkReconnect)
                {
                    Debug.LogError($"网络发生错误(不使用框架层重连)：{disconnectMessage}");
                    cleanup();
                    _triggerNetLostConnection(disconnectMessage);
                    return;
                }

                if (!isReconnecting_)
                {
                    Debug.LogWarning($"网络错误：{disconnectMessage} 尝试重连...");
                    _performReconnect();
                }
                else
                {
                    Debug.LogWarning($"重连过程中发生网络错误，尝试重试...");
                    _performReconnectImpl();
                }
            };
            sessionGuid_ = string.Empty;
            encryptKey_ = null;
        }

        public void Dispose()
        {
            Disconnect();
            session_ = null;
        }

        private void _triggerNetLostConnection(string disconnectMessage)
        {
            MessageCenter.Instance.SendMessage("LOST_CONNECTION", disconnectMessage, lastEncryptKey_);
            Debug.LogWarning("LOST_CONNECTION: " + (disconnectMessage ?? string.Empty));
        }

        //触发断线重连事件通知 1开始重连 2取消重连 3重连失败 4重连成功
        private void _triggerNetReconnectEvent(NetReconnectAction action)
        {
            MessageCenter.Instance.SendMessage("RECONNECT_EVENT", this, (int)action);
        }

        private async Task<string> GetIpFromIpOrDomain(string input)
        {
            // 尝试将输入解析为IP地址
            if (IPAddress.TryParse(input, out IPAddress ipAddress))
            {
                // 输入可以解析为IP地址，因此直接返回
                return ipAddress.ToString();
            }
            else
            {
                // 输入无法解析为IP地址，可能是一个域名
                // 尝试对域名进行DNS解析
                try
                {
                    IPHostEntry hostEntry = await Dns.GetHostEntryAsync(input);

                    // 优先选择IPv4地址
                    ipAddress = hostEntry.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                    // 如果没有IPv4地址，就选择第一个IPv6地址
                    if (ipAddress == null)
                    {
                        ipAddress = hostEntry.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
                    }

                    if (ipAddress != null)
                    {
                        return ipAddress.ToString();
                    }
                    else
                    {
                        throw new Exception("DNS解析没有返回任何IP地址");
                    }
                }
                catch (Exception ex)
                {
                    // DNS解析失败，打印错误信息并返回null
                    Debug.LogError("DNS解析失败: " + ex.Message);
                    return null;
                }
            }
        }

        public void Connect(List<string> ipList, int port, int timeout, int maxStart, Action<bool> callback)
        {
            _ConnectAsync(ipList[0], port, timeout, callback);
        }

        private async void _ConnectAsync(string ip, int port, int timeoutInMillionSeconds, Action<bool> callback)
        {
            Debug.Log($"IP: {ip} Port: {port}");
            ip = await GetIpFromIpOrDomain(ip);
            Debug.Log($"IP: {ip} Port: {port}");
            Connect(ip, port, timeoutInMillionSeconds, callback);
        }

        public void Connect(string ip, int port, int timeoutInMillionSeconds, Action<bool> callback)
        {
            if (isReconnecting_)
            {
                _triggerNetReconnectEvent(NetReconnectAction.Cancel);
                cleanup();
            }

            cacheIp_ = ip;
            cachePort_ = port;
            sessionGuid_ = string.Empty;
            encryptKey_ = null;
            reconnectId_++;
            session_.Connect(ip, port, timeoutInMillionSeconds, success =>
            {
                isConnected_ = success;
                callback?.Invoke(success);
            });
        }

        public void SetEncryptProtocolKey(string sessionGuid, byte[] encryptKey)
        {
            sessionGuid_ = sessionGuid;
            encryptKey_ = encryptKey;
            lastEncryptKey_ = encryptKey;
        }

        private MemoryStream sharedMemoryStream_ = new MemoryStream();
        public bool Send(string protocolName, byte[] pbContent)
        {
            sharedMemoryStream_.Position = 0;
            sharedMemoryStream_.SetLength(0);

            bool useEncrypt = encryptKey_ != null;

            var bw = new BinaryWriter(sharedMemoryStream_);
            bw.Write((int)0);
            bw.Write(useEncrypt ? (byte)1 : (byte)0);
            bw.Write(Encoding.UTF8.GetBytes(protocolName));
            bw.Write((byte)0);
            bw.Write(pbContent);
            sharedMemoryStream_.Position = 0;
            bw.Write((int)sharedMemoryStream_.Length);

            if (useEncrypt)
            {
                Rc4Algorithm(encryptKey_, sharedMemoryStream_.GetBuffer(), 5, (int)sharedMemoryStream_.Length - 5);
            }

            //UnityEngine.Debug.Log($"发送消息：{message.GetType().FullName}");

            //发送消息缓存到队列中
            var data = sharedMemoryStream_.ToArray();
            cacheData_.AddMessageToQueue(data);
            // 意外断网触发重连会重置网络导致 session_ 被清空，如果不加判断游戏发消息的时候这里会报空指针错误
            if (!isReconnecting_)
                return session_?.Send(sharedMemoryStream_.GetBuffer(), 0, (int)sharedMemoryStream_.Length) ?? false;
            return false;
        }

        public void Update()
        {
            session_.Update();
        }

        public void Disconnect()
        {
            if (isReconnecting_)
            {
                _triggerNetReconnectEvent(NetReconnectAction.Cancel);
            }

            cleanup();
        }


        public void TryReconnect()
        {
            if (!UseFrameworkReconnect)
            {
                Debug.LogError($"心跳超时，网络已断开(不使用框架层重连)");
                cleanup();
                _triggerNetLostConnection("与服务器连接断开");
                return;
            }

            if (!isReconnecting_)
            {
                Debug.LogWarning("心跳超时，尝试重连...");
                _performReconnect();
            }
        }
        // 玩家退出
        public void NotifyUserLogout()
        {
            //如果正在重连中，需要触发网络错误事件，错误内容为空，辅助客户端继续退出流程。
            if (isReconnecting_)
            {
                _triggerNetReconnectEvent(NetReconnectAction.Cancel);
                cleanup();
                _triggerNetLostConnection(string.Empty);
            }
        }

        private void onReceiveData(byte[] buffer, int index, int length)
        {
            if (!isConnected_)
                return;

            /*******************************************************
            * 第一个字节代表控制字节
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
            *******************************************************/

            byte controlByte = buffer[index];
            byte controlFlag = (byte)(controlByte >> 6);
            index++;
            length--;

            switch (controlFlag)
            {
                case 0: //普通消息
                    {
                        int cryptFlag = controlByte & 0x3f;
                        onReceiveNormalPBMessage(buffer, index, length, cryptFlag);
                    }
                    break;

                case 1: //接收确认消息
                    cacheData_.PopMessageQueue();
                    break;

                case 2: //重连应答
                    {
                        int errcode = (sbyte)buffer[index];
                        int remoteRecvId = 0;
                        if (length == 5)
                            remoteRecvId = BitConverter.ToInt32(buffer, index + 1);
                        else if (length != 1)
                            Debug.LogError($"接收重连应答消息的包体长度错误:{length}");
                        onReceiveReconnectAck(errcode, remoteRecvId);
                    }
                    break;

                default:
                    Debug.LogError($"不支持的控制标记：{controlFlag}");
                    break;
            }
        }

        private void onReceiveNormalPBMessage(byte[] buffer, int index, int length, int cryptFlag)
        {
            switch (cryptFlag)
            {
                case 0: //do nothing.
                    break;
                case 1: //rc4
                    Rc4Algorithm(encryptKey_, buffer, index, length);
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
                Debug.LogError($"解析协议名称出错：\n{ex.ToString()}");
            }

            //通知服务端接收到该消息
            /*{
                sharedMemoryStream_.Position = 0;
                sharedMemoryStream_.SetLength(0);
                var bw = new BinaryWriter(sharedMemoryStream_);
                bw.Write(5);
                bw.Write((byte)(1 << 6));
                session_.Send(sharedMemoryStream_.GetBuffer(), 0, 5);
            }*/

            //自增Id
            cacheData_.AddReceiveCount();

            var tmpBuffer = new byte[length - tmpLength - 1];
            Array.Copy(buffer, beginIndex, tmpBuffer, 0, tmpBuffer.Length);

            var data = new NetHelper.NetDataPack()
            {
                packName = fullName,
                pbdata = tmpBuffer,
            };
            MessageCenter.Instance.SendMessage(MsgType.NET_RECEIVE_DATA, this, data);
        }

        private void onReceiveReconnectAck(int errcode, int remoteRecvId)
        {
            if (!isReconnecting_)
            {
                Debug.LogError($"非重连状态下收到重连回应包！");
                return;
            }

            if (errcode != 0 || remoteRecvId == 0)
            {
                Debug.LogError($"重连被拒绝 errcode:{errcode} remoteRecvId:{remoteRecvId}");
                _triggerNetReconnectEvent(NetReconnectAction.Failed);
                cleanup();
                _triggerNetLostConnection($"网络恢复失败");
                return;
            }

            var messageList = new List<byte[]>();
            if (!cacheData_.AdaptRemote(remoteRecvId, messageList))
            {
                Debug.LogError($"重连失败，由于本地消息缓存队列无法匹配服务端 remoteRecvId:{remoteRecvId} sendId:{cacheData_.SendId} queue:{cacheData_.MessageQueueCount}");
                _triggerNetReconnectEvent(NetReconnectAction.Failed);
                cleanup();
                _triggerNetLostConnection($"网络恢复失败");
                return;
            }

            Debug.LogWarning($"重连成功");
            isReconnecting_ = false;
            _triggerNetReconnectEvent(NetReconnectAction.Succeed);

            foreach (var data in messageList)
            {
                session_.Send(data, 0, data.Length);
            }
        }

        private void cleanup()
        {
            session_.Disconnect();

            sessionGuid_ = string.Empty;
            encryptKey_ = null;
            isConnected_ = false;
            isReconnecting_ = false;
            reconnectRetryCount_ = 0;
            cacheData_.Clear();
        }

        private void _performReconnect()
        {
            if (isReconnecting_)
                return;

            if (string.IsNullOrEmpty(sessionGuid_))
            {
                Debug.LogError($"会话Guid为空，当前阶段不支持重连，尝试断开...");
                cleanup();
                _triggerNetLostConnection("与服务器断开连接");
                return;
            }

            _triggerNetReconnectEvent(NetReconnectAction.Start);
            isReconnecting_ = true;
            reconnectRetryCount_ = 0;
            _performReconnectImpl();
        }

        private void _performReconnectImpl()
        {
            if (reconnectRetryCount_ >= 10)
            {
                //重试连接次数已达上限
                Debug.LogError($"建立连接失败，重连次数已达上限，尝试断开...");
                _triggerNetReconnectEvent(NetReconnectAction.Failed);
                cleanup();
                _triggerNetLostConnection($"与服务器断开连接，请检查本地网络设置。");
                return;
            }

            ++reconnectRetryCount_;
            Debug.LogWarning($"正在尝试第{reconnectRetryCount_}次重连... {cacheIp_}:{cachePort_}");
            ++reconnectId_;
            var tmpReconnectId = reconnectId_;
            session_.Connect(cacheIp_, cachePort_, 2000, async success =>
            {
                if (!success)
                {
                    Debug.LogWarning($"建立连接失败");
                    await Task.Delay(2000);
                    if (isReconnecting_ && tmpReconnectId == reconnectId_)
                        _performReconnectImpl();
                }
                else
                {
                    Debug.LogWarning($"连接建立成功，发送重连请求...");
                    //建立连接成功，发送重连请求
                    sharedMemoryStream_.Position = 0;
                    sharedMemoryStream_.SetLength(0);
                    var bw = new BinaryWriter(sharedMemoryStream_);
                    bw.Write(4 + 1 + sessionGuid_.Length + 4 + 4);
                    bw.Write((byte)(2 << 6));
                    bw.Write(Encoding.UTF8.GetBytes(sessionGuid_));
                    bw.Write(cacheData_.GetReceiveId());
                    bw.Write(reconnectId_);
                    session_.Send(sharedMemoryStream_.GetBuffer(), 0, (int)sharedMemoryStream_.Length);

                    //等待3秒钟，判断是否已经连接上
                    await Task.Delay(2000);
                    if (isReconnecting_ && tmpReconnectId == reconnectId_)
                    {
                        Debug.LogWarning($"检测到未收到重连回应，这里进行重试...");
                        _performReconnectImpl();
                    }
                }
            });
        }

        /// <summary>
        /// Rc4算法，用于协议层加解密
        /// </summary>
        /// <param name="key">秘钥</param>
        /// <param name="data">数据</param>
        private void Rc4Algorithm(byte[] key, byte[] buffer, int index, int length)
        {
            int key_len = key.Length;
            int data_len = length;

            int[] s = new int[256];
            int[] k = new int[256];

            int i = 0, j = 0, temp;

            for (i = 0; i < 256; i++)
            {
                s[i] = i;
                k[i] = key[i % key_len];
            }
            for (i = 0; i < 256; i++)
            {
                j = (j + s[i] + k[i]) & 0xff;
                temp = s[i];
                s[i] = s[j];
                s[j] = temp;
            }

            int x = 0, y = 0, t = 0;
            for (i = 0; i < data_len; i++)
            {
                x = (x + 1) & 0xff;
                y = (y + s[x]) & 0xff;
                temp = s[x];
                s[x] = s[y];
                s[y] = temp;
                t = (s[x] + s[y]) & 0xff;
                buffer[index + i] ^= (byte)s[t];
            }
        }


    }
}
