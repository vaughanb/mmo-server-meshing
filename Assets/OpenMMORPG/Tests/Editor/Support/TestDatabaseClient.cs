// The login, central and map servers reach the database through MMOServerInstance, which the kit
// compiles for server builds only; match its condition.
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// A stand-in for the database the login and central servers talk to (IDatabaseClient). The
    /// real one is a network client, so a handler that asks it anything would wait for a reply
    /// forever. This answers at once: a call set up with <see cref="On{TRequest,TResponse}"/> or
    /// <see cref="Succeed"/> returns what it gives, any other call returns an error result (the
    /// servers treat that as an internal error). Every call is recorded, so a test can check that
    /// a request was refused before it reached the database.
    ///
    /// It is a DispatchProxy, so a method added to IDatabaseClient upstream does not break it.
    /// <see cref="Install"/> makes it MMOServerInstance.Singleton's database client for the test.
    /// </summary>
    public class TestDatabaseClient : DispatchProxy
    {
        private static readonly MethodInfo s_fromResult = typeof(UniTask).GetMethods()
            .First(m => m.Name == nameof(UniTask.FromResult) && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);

        private readonly Dictionary<string, Func<object, object>> _handlers = new Dictionary<string, Func<object, object>>();
        private readonly List<KeyValuePair<string, object>> _calls = new List<KeyValuePair<string, object>>();

        /// <summary>The proxy to hand to the code under test.</summary>
        public IDatabaseClient Client { get; private set; }

        /// <summary>The names of the methods called, in order.</summary>
        public IEnumerable<string> Calls => _calls.Select(c => c.Key);

        /// <summary>Creates a client that is not installed anywhere.</summary>
        public static TestDatabaseClient Create()
        {
            IDatabaseClient client = Create<IDatabaseClient, TestDatabaseClient>();
            TestDatabaseClient fake = (TestDatabaseClient)(object)client;
            fake.Client = client;
            return fake;
        }

        /// <summary>
        /// Creates a client and makes it the database client of a new MMOServerInstance.Singleton,
        /// which the login and central servers read it from. Both are put back after the test.
        /// </summary>
        public static TestDatabaseClient Install(TestObjects objects)
        {
            TestDatabaseClient fake = Create();
            objects.Own(new Installation(objects, fake.Client));
            return fake;
        }

        /// <summary>Answers calls to <paramref name="method"/> (use nameof) with a success holding the response.</summary>
        public void On<TRequest, TResponse>(string method, Func<TRequest, TResponse> respond)
        {
            _handlers[method] = request => new DatabaseApiResult<TResponse>() { Response = respond((TRequest)request) };
        }

        /// <summary>Answers calls to a method that returns no data with a success.</summary>
        public void Succeed(string method)
        {
            _handlers[method] = request => new DatabaseApiResult();
        }

        public bool WasCalled(string method)
        {
            return _calls.Any(c => c.Key == method);
        }

        /// <summary>The request of the last call to <paramref name="method"/>; fails the test if it was never called.</summary>
        public TRequest LastRequest<TRequest>(string method)
        {
            for (int i = _calls.Count - 1; i >= 0; --i)
            {
                if (_calls[i].Key == method)
                    return (TRequest)_calls[i].Value;
            }
            Assert.Fail($"{method} was never called.");
            return default;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            object request = args != null && args.Length > 0 ? args[0] : null;
            _calls.Add(new KeyValuePair<string, object>(targetMethod.Name, request));

            Type returnType = targetMethod.ReturnType;
            if (returnType == typeof(UniTask))
                return UniTask.CompletedTask;
            Type resultType = returnType.GetGenericArguments()[0];

            object result;
            if (_handlers.TryGetValue(targetMethod.Name, out Func<object, object> handler))
            {
                result = handler(request);
            }
            else
            {
                result = Activator.CreateInstance(resultType);
                resultType.GetProperty(nameof(DatabaseApiResult.IsError))?.SetValue(result, true);
            }
            return s_fromResult.MakeGenericMethod(resultType).Invoke(null, new[] { result });
        }

        private sealed class Installation : IDisposable
        {
            private readonly MMOServerInstance _previous;

            public Installation(TestObjects objects, IDatabaseClient client)
            {
                _previous = MMOServerInstance.Singleton;
                // Awake does not run for AddComponent in edit mode, so nothing starts.
                MMOServerInstance instance = objects.NewGameObject(nameof(TestDatabaseClient)).AddComponent<MMOServerInstance>();
                TestReflection.SetField(instance, "useCustomDatabaseClient", true);
                TestReflection.SetField(instance, "_customDatabaseClient", client);
                TestReflection.SetStaticProperty(typeof(MMOServerInstance), nameof(MMOServerInstance.Singleton), instance);
            }

            public void Dispose()
            {
                TestReflection.SetStaticProperty(typeof(MMOServerInstance), nameof(MMOServerInstance.Singleton), _previous);
            }
        }
    }
}
#endif
