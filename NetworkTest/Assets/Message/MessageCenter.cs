
/******************************************************************************
 * 
 *  Title:  捕鱼项目
 *
 *  Version:  1.0版
 *
 *  Description:
 *
 *  Author:  WangXingXing
 *       
 *  Date:  2018
 * 
 ******************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

public class MessageCenter : MonoBehaviour
{

    ConcurrentQueue<Message> msgQueue = new ConcurrentQueue<Message>();

    public static MessageCenter Instance { get; private set; }
    private void Awake()
    {
        Instance = this;
    }

    class MessageEventData
    {
        public System.Action<Message> messageEvent;
        public bool autoRemove;
    }

    private Dictionary<string, List<MessageEventData>> dicMsgEvents = new Dictionary<string, List<MessageEventData>>();



    public void AddListener(string messageName, System.Action<Message> messageEvent, bool autoRemove = false)
    {
        MessageEventData messageEventData = new MessageEventData();
        messageEventData.messageEvent = messageEvent;
        messageEventData.autoRemove = autoRemove;

        List<MessageEventData> list;
        if (!dicMsgEvents.TryGetValue(messageName, out list))
        {
            list = new List<MessageEventData>();
            dicMsgEvents.Add(messageName, list);
        }
        list.Add(messageEventData);
    }

    public void RemoveListener(string messageName, System.Action<Message> messageEvent)
    {
        List<MessageEventData> list;
        if (dicMsgEvents.TryGetValue(messageName, out list))
        {
            var index = list.FindIndex(0, data => data.messageEvent == messageEvent);
            if (index >= 0)
            {
                list.RemoveAt(index);
                if (list.Count <= 0)
                {
                    dicMsgEvents.Remove(messageName);
                }
            }
        }
    }
    public void RemoveOneTypeListener(string messageName)
    {
        dicMsgEvents.Remove(messageName);
    }

    public void RemoveAllListener()
    {
        dicMsgEvents.Clear();
    }

    public void SendMessage(Message message)
    {
        DoMessageDispatcher(message);
    }

    public void SendMessage(string name, object sender, object content = null, params object[] dicParams)
    {
        SendMessage(new Message(name, sender, content, dicParams));
    }

    private void DoMessageDispatcher(Message message)
    {
        if (dicMsgEvents.TryGetValue(message.Name, out List<MessageEventData> list))
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                list[i].messageEvent?.Invoke(message);
                if (list[i].autoRemove)
                {
                    list.RemoveAt(i);
                    if (list.Count == 0)
                    {
                        dicMsgEvents.Remove(message.Name);
                        break;
                    }
                }
            }
        }
    }

    public void CleanMessage()
    {
        while (msgQueue.TryDequeue(out Message msg))
        {

        }
    }

    public void PostMessage(Message message)
    {
        msgQueue.Enqueue(message);
    }

    public void PostMessage(string name, object sender, object content = null, params object[] dicParams)
    {
        PostMessage(new Message(name, sender, content, dicParams));
    }

    public void RunInMainThread(Action<object> action, object content = null)
    {
        PostMessage(new Message(null, null, content) { Action = action });
    }

    public void Update()
    {
        while (msgQueue.TryDequeue(out Message msg))
        {
            if (msg.Action != null)
            {
                msg.CallInMainThread();
            }
            else
            {
                SendMessage(msg);
            }
        }
    }

    private void OnDestroy()
    {
        dicMsgEvents.Clear();
        Instance = null;
    }
}