using System;
using System.Collections.Generic;
using System.Linq;

namespace DirectNet
{
    class NetMessageCache
    {
        private Queue<byte[]> messageQueue_ = new Queue<byte[]>();
        private int receiveId_ = 0;
        private int sendId_ = 0;

        public int SendId => sendId_;
        public int MessageQueueCount => messageQueue_.Count;

        public void Clear()
        {
            messageQueue_.Clear();
            receiveId_ = 0;
            sendId_ = 0;
        }

        public void AddMessageToQueue(byte[] message)
        {
            ++sendId_;
            messageQueue_.Enqueue(message);
        }

        public bool PopMessageQueue()
        {
            if (messageQueue_.Count > 0)
            {
                messageQueue_.Dequeue();
                return true;
            }
            return false;
        }

        public void AddReceiveCount()
        {
            ++receiveId_;
        }

        public int GetReceiveId()
        {
            return receiveId_;
        }

        public bool AdaptRemote(int remoteRecvId, List<byte[]> messageList)
        {
            if (remoteRecvId > sendId_)
                return false;

            int takeCount = sendId_ - remoteRecvId;
            if (messageQueue_.Count < takeCount)
                return false;

            while (messageQueue_.Count > takeCount)
            {
                messageQueue_.Dequeue();
            }

            messageList.AddRange(messageQueue_);
            return true;
        }
    }
}
