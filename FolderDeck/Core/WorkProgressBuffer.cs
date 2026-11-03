using System;
using System.Collections.Generic;

namespace FolderDeck.Core
{
    // Worker threads report here; the UI drains at most once a second.
    public sealed class WorkProgressBuffer : IProgress<WorkProgress>
    {
        private readonly object _gate = new object();
        private readonly Queue<string> _messages = new Queue<string>();
        private readonly bool _captureMessages;
        private WorkProgress _latest;
        public WorkProgressBuffer(bool captureMessages) { _captureMessages = captureMessages; }
        public void Report(WorkProgress value)
        {
            if (value == null) return;
            lock (_gate)
            {
                _latest = value;
                if (_captureMessages && !value.IsDetail && !string.IsNullOrEmpty(value.Message)) _messages.Enqueue(value.Message);
            }
        }
        public void Drain(out WorkProgress latest, out string[] messages)
        {
            lock (_gate)
            { latest = _latest; _latest = null; messages = _messages.ToArray(); _messages.Clear(); }
        }
    }
}
