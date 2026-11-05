using System;
using System.Threading;
using UnityEditor;

namespace BIEngine.UniXD.Editor
{
    public interface IImportProgress : IDisposable
    {
        void SetTotal(int total);
        void IncrementCurrent();

        IImportProgress TaskStart(string message);
        void Update(string message, float progress);
    }

    public class ImportProgressBar : IImportProgress
    {
        private readonly string _title;
        private int _current;
        private int _total;
        private readonly CancellationToken _cancellationToken;
        private bool _cancelRequested;
        private bool _disposed;

        public ImportProgressBar(string title, CancellationToken cancellationToken = default)
        {
            _title = title;
            _cancellationToken = cancellationToken;
            _cancelRequested = EditorUtility.DisplayCancelableProgressBar(_title, "准备导入…", 0f);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorUtility.ClearProgressBar();
        }

        public void SetTotal(int total)
        {
            ThrowIfCanceled();
            _current = 0;
            _total = total;
        }

        public void IncrementCurrent()
        {
            _current++;
        }

        public IImportProgress TaskStart(string message)
        {
            return new ImportProgressBarTask(this, message);
        }

        public void Update(string message, float progress)
        {
            ThrowIfCanceled();
            if (EditorUtility.DisplayCancelableProgressBar(_title, $"[{Math.Min(_current + 1, Math.Max(1, _total))}/{Math.Max(1, _total)}] {message}",
                UnityEngine.Mathf.Clamp01((_current + progress) / Math.Max(1, _total))))
            {
                _cancelRequested = true;
                ThrowIfCanceled();
            }
        }

        private void ThrowIfCanceled()
        {
            if (_cancelRequested || _cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("用户已取消导入。", _cancellationToken);
        }

        public class ImportProgressBarTask : IImportProgress
        {
            private readonly IImportProgress _parent;
            private readonly string _message;
            private bool _disposed;

            private int _current;
            private int _total;

            public ImportProgressBarTask(IImportProgress parent, string message)
            {
                _parent = parent;
                _message = message;

                _parent.Update(_message, 0f);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                // Never poll or throw during cleanup: that would mask a real import failure.
                _parent.IncrementCurrent();
            }

            public void SetTotal(int total)
            {
                _current = 0;
                _total = total;
            }

            public void IncrementCurrent()
            {
                _current++;
            }

            public IImportProgress TaskStart(string message)
            {
                return new ImportProgressBarTask(this, message);
            }

            public void Update(string message, float progress)
            {
                _parent.Update($"{_message} | [{Math.Min(_current + 1, Math.Max(1, _total))}/{Math.Max(1, _total)}] {message}", (_current + progress) / Math.Max(1, _total));
            }
        }
    }
}
