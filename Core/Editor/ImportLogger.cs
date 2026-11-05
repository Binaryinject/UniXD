using System;
using System.Collections.Generic;
using System.Linq;

namespace BIEngine.UniXD.Editor
{
    public class ImportLogger
    {
        private readonly string _name;
        private readonly ImportLogType _logType;
        private readonly List<string> _categories = new List<string>();

        private string Header => $"{_name}{string.Join("", _categories.Select(x => $"/{x}"))} |";

        public ImportLogger(string name, ImportLogType logType)
        {
            _name = name;
            _logType = logType;
        }

        public IDisposable SetCategory(string category)
        {
            _categories.Add(category);
            return new DisposeCategory(this, category);
        }

        public void Log(string message)
        {
            UnityEngine.Debug.Log($"{Header} {message}");
        }

        public void Log(string message, params (string, object)[] values)
        {
            UnityEngine.Debug.Log($"{Header} {message} {string.Join(", ", values.Select(x => $"{x.Item1} = {x.Item2}"))}");
        }

        public void Warning(string message)
        {
            var s = $"{Header} {message}";
            if (_logType == ImportLogType.WarningAsException) throw new ImportException(s);
            if (_logType == ImportLogType.WarningAsLogError) UnityEngine.Debug.LogError(s);
            else UnityEngine.Debug.LogWarning(s);
        }

        public void Warning(string message, params (string, object)[] values)
        {
            var s = $"{Header} {message} {string.Join(", ", values.Select(x => $"{x.Item1} = {x.Item2}"))}";
            if (_logType == ImportLogType.WarningAsException) throw new ImportException(s);
            if (_logType == ImportLogType.WarningAsLogError) UnityEngine.Debug.LogError(s);
            else UnityEngine.Debug.LogWarning(s);
        }

        public void Error(string message)
        {
            var s = $"{Header} {message}";
            if (_logType == ImportLogType.WarningAsException) throw new ImportException(s);
            UnityEngine.Debug.LogError(s);
        }

        public void Error(string message, params (string, object)[] values)
        {
            var s = $"{Header} {message} {string.Join(", ", values.Select(x => $"{x.Item1} = {x.Item2}"))}";
            if (_logType == ImportLogType.WarningAsException) throw new ImportException(s);
            UnityEngine.Debug.LogError(s);
        }

        private class DisposeCategory : IDisposable
        {
            private readonly ImportLogger _parent;
            private readonly string _category;

            public DisposeCategory(ImportLogger parent, string category)
            {
                _parent = parent;
                _category = category;
            }

            public void Dispose()
            {
                _parent._categories.Remove(_category);
            }
        }
    }

    public enum ImportLogType
    {
        Default,
        WarningAsLogError,
        WarningAsException,
    }

    public class ImportException : Exception
    {
        public ImportException(string message) : base(message)
        {
        }
    }
}
