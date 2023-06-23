using System.Collections.Generic;
using System.Threading;

namespace DefensiveNet
{
    class BlockingQueue<T>
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
}