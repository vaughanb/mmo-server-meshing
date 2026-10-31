using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Waits on kit UniTasks that finish synchronously, as the SQLite database and most data code
    /// do. One that is still pending fails the test instead of blocking the editor; write that
    /// test as a [UnityTest] around UniTask.ToCoroutine instead.
    /// </summary>
    public static class TestAsync
    {
        private const string Pending = "The task did not finish synchronously; make this a [UnityTest] and await it with UniTask.ToCoroutine.";

        public static void Wait(UniTask task)
        {
            UniTask.Awaiter awaiter = task.GetAwaiter();
            Assert.That(awaiter.IsCompleted, Pending);
            awaiter.GetResult();
        }

        public static T Wait<T>(UniTask<T> task)
        {
            UniTask<T>.Awaiter awaiter = task.GetAwaiter();
            Assert.That(awaiter.IsCompleted, Pending);
            return awaiter.GetResult();
        }
    }
}
