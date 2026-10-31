using System;
using LiteNetLibManager;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// LiteNetLib's Logging throws a NullReferenceException outside play mode, because nothing
    /// has initialized LogManager (a network manager does it when it starts). Server code logs
    /// as it registers peers and handles requests, so a test that reaches such code calls
    /// <see cref="Use"/>: it initializes the logger if needed and removes it again after the test.
    /// </summary>
    public sealed class TestLogger : IDisposable
    {
        private readonly bool _owns;

        public static void Use(TestObjects objects)
        {
            objects.Own(new TestLogger());
        }

        private TestLogger()
        {
            if (LogManager.LoggerManager == null)
            {
                LogManager.Initialize();
                _owns = true;
            }
        }

        public void Dispose()
        {
            if (_owns)
                LogManager.LoggerManager = null;
        }
    }
}
