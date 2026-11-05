// Newline-delimited JSON over the child process's standard streams.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace DshLitePet
{
    internal sealed class Ipc : IDisposable
    {
        private readonly Queue<string> _inbox = new Queue<string>();
        private readonly object _lock = new object();
        private Thread _thread;
        private StreamWriter _out;
        private volatile bool _closed;

        public void Start()
        {
            _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            _out.AutoFlush = true;
            _thread = new Thread(ReadLoop);
            _thread.IsBackground = true;
            _thread.Start();
        }

        private void ReadLoop()
        {
            try
            {
                var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                while (true)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    lock (_lock) _inbox.Enqueue(line);
                }
            }
            catch (Exception)
            {
                // Host went away; the UI thread notices through the pipe instead.
            }
            _closed = true;
        }

        public bool TryRead(out string line)
        {
            lock (_lock)
            {
                if (_inbox.Count > 0) { line = _inbox.Dequeue(); return true; }
            }
            line = null;
            return false;
        }

        public bool Closed { get { return _closed; } }

        public void Send(string json)
        {
            try
            {
                if (_out != null) _out.WriteLine(json);
            }
            catch (Exception)
            {
                // Nothing useful to do; the process is exiting anyway.
            }
        }

        public void Dispose()
        {
            try { if (_out != null) _out.Dispose(); } catch (Exception) { }
        }
    }
}
