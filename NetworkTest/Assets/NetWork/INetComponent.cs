

using System.Collections.Generic;

public interface INetComponent
{
    //根据NetComponent public信息提取接口
    void Connect(List<string> ipList, int port, int timeout, int maxStart, System.Action<bool> callback);
    void Disconnect();
    void Update();
    void Dispose();
    bool IsConnected { get; }
    void SetEncryptProtocolKey(string sessionGuid, byte[] encryptKey);
    bool UseFrameworkReconnect { get; set; }

    bool Send(string protocolName, byte[] pbContent);
}