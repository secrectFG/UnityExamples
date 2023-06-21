
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using DefensiveNet;
using Google.Protobuf;
using UnityEngine;
using static DefensiveNet.NetHelper;

namespace Test
{

    public interface INetworkHandler
    {
        string Filter { get; }
        void OnMsg(string name,byte[] data);
    }

    public class Network
    {
        INetComponent netComponent;
        Dictionary<string, List<Action<byte[]>>> callbackDic = new Dictionary<string, List<Action<byte[]>>>();
        List<INetworkHandler> networkHandlers = new List<INetworkHandler>();


        //string ip = "47.101.62.170";
        public int port = 16000;
        //int port = 9000;
        bool connecting = false;
        public bool Logined => logined;
        //public string Ip { get; set; } = "192.168.101.221";
        public List<string> IpList { get; set; } = new List<string> {  "47.101.147.244", "8.134.37.170", "8.210.106.28"  };

        public Network()
        {
            
        }

        bool logined = false;

        public void Login(string token, Action loginOk,Action<string> loginStatus,bool useDirect=false)
        {
            if (connecting) return;
            netComponent = new DefensiveNet.NetComponent();
            DefensiveNet.NetHelper.AllowNetLoger = false;
            
            System.Action<Message> recvData = msg=>{
                var netDataPack = (NetHelper.NetDataPack)msg.Content;
                // Debug.Log($"netDataPack.packName:{netDataPack.packName}");
                if(logined){
                    OnRecvData(netDataPack);
                    return;
                }
                switch (netDataPack.packName)
                {
                    case "CLGT.HandAck":
                        {
                            var ack = PaserData<CLGT.HandAck>(netDataPack.pbdata);
                            netComponent.SetEncryptProtocolKey(ack.SessionGuid, ack.RandomKey.ToByteArray());
                            Send(new CLGT.LoginReq()
                            {
                                LoginType = 1,
                                Token = "46phgkSAQfvNb9Vpxo3QmDE7m",
                                //LoginType = CLGT.LoginReq.Types.LoginType.Phone,
                                //Token = token
                            });
                        }
                        break;
                    case "CLGT.LoginAck":
                        {
                            var ack = PaserData<CLGT.LoginAck>(netDataPack.pbdata);
                            if (ack.Errcode != 0)
                            {
                                var reasons = new List<string>() { "成功", "平台服务器不可用", "账号被封禁", "系统繁忙", "系统错误", "系统暂未开放", "认证失败", "机器码未绑定", };
                                var reason = ack.Errcode >= reasons.Count? $"错误代码:{ack.Errcode}": reasons[ack.Errcode];
                                Debug.Log($"登录失败，{reason}");
                                netComponent.Dispose();
                                netComponent = null;
                            }
                            else
                            {
                                logined = true;
                                loginStatus?.Invoke("账号登录成功");
                                //print($"登录成功:{ack}");
                                loginOk?.Invoke();
                                //netComponent.KeepAliveActionData = new CLGT.KeepAliveReq().ToByteArray();
                                //netComponent.KeepAliveActionDataProtoName = typeof(CLGT.KeepAliveReq).FullName;
                            }
                        }
                        break;
                }
            };

            MessageCenter.Instance.AddListener(MsgType.NET_RECEIVE_DATA, recvData);
             MessageCenter.Instance.AddListener("LOST_CONNECTION", msg=>{
                OnLostConnectionCallBack((string)msg.Sender, (string)msg.Content);
             });
            // MessageCenter.Instance.RemoveListener()

            
            loginStatus?.Invoke($"开始连接...{string.Join(",", IpList)} port:{port}");
            connecting = true;
            netComponent.Connect(IpList, port, 10000, 
                maxStart:3,
              callback:  b =>
            {
                connecting = false;
                if (!b)
                {
                    loginStatus?.Invoke($"连接失败");
                    
                }
                else
                {
                    loginStatus?.Invoke("连接成功");
                    Send(new CLGT.HandReq()
                    {
                        Platform = 3,
                        Product = 1,
                        Version = 1,
                        Device = "test",
                        Channel = 1,
                        Country = "MM",
                    });
                }
            });
        }

        public void Close()
        {
            netComponent?.Disconnect();
            netComponent?.Dispose();
            netComponent = null;
        }

        public void Step()
        {
            netComponent?.Update();
        }

        //public void SendAccessServiceReq(string ServerName,string NameCN,bool enter, string appId="", Action<bool> doneCallback=null)
        //{
        //    SendReq(new CLGT.AccessServiceReq() { 
        //        ServerName = ServerName,
        //        Action = enter?1:2,
        //        AppId = appId,
        //    },pbdata=> {
        //       var ack = PaserData<CLGT.AccessServiceAck>(pbdata);
        //        if (ack.Errcode != 0)
        //        {
        //            var err = new string[] { "", "服务不存在", "拒绝访问" };
        //            var enters = enter ? "登录" : "登出";
        //            var s = $"{NameCN}{enters}失败 {err[ack.Errcode]}";

        //        }
        //        doneCallback?.Invoke(ack.Errcode == 0);
        //    });
        //}

        void OnRecvData(NetDataPack netDataPack)
        {
            //Logging.logger.Information($"{netDataPack.packName}");
            //print($"packName:{netDataPack.packName}");
            if (callbackDic.TryGetValue(netDataPack.packName, out List<Action<byte[]>> list))
            {
                var item = list[0];
                list.RemoveAt(0);
                if (list.Count ==0)
                {
                    callbackDic.Remove(netDataPack.packName);
                }
                item(netDataPack.pbdata);
            }
            for (int i = networkHandlers.Count - 1; i >= 0; i--)
            {
                var networkHandler = networkHandlers[i];
                if (string.IsNullOrEmpty(networkHandler.Filter))
                {
                    networkHandler.OnMsg(netDataPack.packName, netDataPack.pbdata);
                }
                else if (netDataPack.packName.Contains(networkHandler.Filter))
                    networkHandler.OnMsg(netDataPack.packName, netDataPack.pbdata);
            }
        }

        void OnLostConnectionCallBack(string rescon, string s)
        {
            Debug.Log($"网络连接断开:{rescon}");
        }

        public static void print(string s)
        {
            Debug.Log("网络:"+s);
        }

        public void Send<T>(T msg) where T:IMessage
        {
            var FullName = typeof(T).FullName;
            netComponent?.Send(FullName,msg.ToByteArray());
        }

        public void SendReq<T>(T msg, Action<byte[]> callback) where T : IMessage
        {
            var reqName = typeof(T).FullName;
            var ackName = reqName.Replace("Req","Ack");
            if (!callbackDic.TryGetValue(ackName, out List<Action<byte[]>> list))
            {
                list = new List<Action<byte[]>>();
                callbackDic[ackName] = list;
            }
            list.Add(callback);
            netComponent?.Send(reqName, msg.ToByteArray());
        }

        public void AddHanlder(INetworkHandler networkHandler)
        {
            networkHandlers.Add(networkHandler);
        }

        public void RemoveHanlder(INetworkHandler networkHandler)
        {
            networkHandlers.Remove(networkHandler);
        }

        public static T PaserData<T>(byte[] data) where T : IMessage<T>, new()
        {
            MessageParser<T> messageParser = new MessageParser<T>(() => new T());
            return messageParser.ParseFrom(data);
        }

        public static bool IsIPAddress(string ipAddress)
        {
            bool retVal = false;

            try
            {
                IPAddress address;
                retVal = IPAddress.TryParse(ipAddress, out address);
            }
            catch (Exception)
            {
            }
            return retVal;
        }
    }
}
