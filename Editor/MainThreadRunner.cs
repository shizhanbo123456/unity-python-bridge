using System;
using System.Collections.Generic;

namespace UnityPythonBridge
{
    /// <summary>
    /// 主线程执行队列。
    /// TCP 后台线程只负责收/发数据，实际执行 Unity 命令的 Action 会被
    /// 投递到该队列，再由 EditorApplication.update（Editor 侧驱动）在主线程
    /// 逐条 Flush，从而保证所有 Unity API 访问都发生在主线程。
    /// </summary>
    public static class MainThreadRunner
    {
        private static readonly Queue<Action> Queue = new Queue<Action>();
        private static readonly object Gate = new object();

        public static void Enqueue(Action action)
        {
            if (action == null) return;
            lock (Gate) Queue.Enqueue(action);
        }

        /// <summary>在主线程调用：一次性执行完队列中所有任务。</summary>
        public static void Flush()
        {
            while (true)
            {
                Action action;
                lock (Gate)
                {
                    if (Queue.Count == 0) return;
                    action = Queue.Dequeue();
                }
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }
        }

        public static int PendingCount
        {
            get { lock (Gate) return Queue.Count; }
        }
    }
}
