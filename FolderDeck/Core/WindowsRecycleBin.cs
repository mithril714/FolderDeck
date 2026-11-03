using System;
using System.IO;
using System.Threading;
namespace FolderDeck.Core
{
    public interface IRecycleBin
    {
        void Run(Action work);
        void Recycle(string path, CancellationToken token, Action validate);
    }
    public sealed class RecycleUnavailableException : IOException
    { public RecycleUnavailableException(string message) : base(message) { } }
    // Compatibility policy for core callers. UI submits copies only.
    public sealed class WindowsRecycleBin : IRecycleBin
    {
        public void Run(Action work) { work(); }
        public void Recycle(string path, CancellationToken token, Action validate)
        {
            token.ThrowIfCancellationRequested();
            throw new RecycleUnavailableException("この版では自動削除しません。エクスプローラーで確認して操作してください。");
        }
    }
}
