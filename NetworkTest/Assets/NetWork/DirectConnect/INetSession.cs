using System;

namespace DirectNet
{
    internal interface INetSession
    {
        void Disconnect();

        void Update();

        void Connect(string ip, int port, int timeoutInMillionSeconds, Action<bool> notifyCallback);

        event Action<string> NetErrorEvent;

        bool Send(byte[] buffer, int index, int length);

        event Action<byte[], int, int> NetRecvDataEvent;
    }
}
