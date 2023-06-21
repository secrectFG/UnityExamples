using System;

namespace DefensiveNet
{
    public interface INetSession
    {
        void Connect();

        void Disconnect(bool bActive, string errorMessage);

        void SetQuickConnect();

        bool IsFreeSession();

        bool IsActiveSession();

        void SetSessionInfo(string ip, int port, int index);

        bool Send(byte[] buffer, int index, int length);

        event Action<INetSession, int, bool> NetConnectEvent;

        event Action<INetSession, byte[], int, int> NetMessageEvent;

        event Action<INetSession, string> NetErrorEvent;
    }
}
