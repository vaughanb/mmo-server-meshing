using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Reaches the private fields and methods a test has to set up. Core mirrors upstream, so it
    /// is not changed to make it testable; serialized fields are private and set by reflection
    /// instead. A name that no longer exists fails the test with the type and member named, which
    /// is usually an upstream rename.
    /// </summary>
    public static class TestReflection
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Sets a field and returns the target, so data can be built in one expression.</summary>
        public static T With<T>(this T target, string fieldName, object value) where T : class
        {
            SetField(target, fieldName, value);
            return target;
        }

        public static void SetField(object target, string fieldName, object value)
        {
            FindField(target.GetType(), fieldName, InstanceFlags).SetValue(target, value);
        }

        public static TValue GetField<TValue>(object target, string fieldName)
        {
            return (TValue)FindField(target.GetType(), fieldName, InstanceFlags).GetValue(target);
        }

        public static TValue GetStaticField<TValue>(Type type, string fieldName)
        {
            return (TValue)FindField(type, fieldName, StaticFlags).GetValue(null);
        }

        /// <summary>Sets a property, including one with a private setter declared on a base type.</summary>
        public static void SetProperty(object target, string propertyName, object value)
        {
            FindSetter(target.GetType(), propertyName, InstanceFlags).Invoke(target, new[] { value });
        }

        public static void SetStaticProperty(Type type, string propertyName, object value)
        {
            FindSetter(type, propertyName, StaticFlags).Invoke(null, new[] { value });
        }

        /// <summary>Calls a method, including a private one declared on a base type.</summary>
        public static object Invoke(object target, string methodName, params object[] args)
        {
            return Call(FindMethod(target.GetType(), methodName, InstanceFlags, args.Length), target, args);
        }

        public static object InvokeStatic(Type type, string methodName, params object[] args)
        {
            return Call(FindMethod(type, methodName, StaticFlags, args.Length), null, args);
        }

        private static object Call(MethodInfo method, object target, object[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                // Rethrow what the method threw, not the reflection wrapper.
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        private static FieldInfo FindField(Type type, string name, BindingFlags flags)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo field = t.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            Assert.Fail($"{type.Name} has no field `{name}`.");
            return null;
        }

        private static MethodInfo FindSetter(Type type, string name, BindingFlags flags)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo property = t.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                MethodInfo setter = property?.GetSetMethod(true);
                if (setter != null)
                    return setter;
            }
            Assert.Fail($"{type.Name} has no settable property `{name}`.");
            return null;
        }

        private static MethodInfo FindMethod(Type type, string name, BindingFlags flags, int argCount)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                foreach (MethodInfo method in t.GetMethods(flags | BindingFlags.DeclaredOnly))
                {
                    if (method.Name == name && method.GetParameters().Length == argCount)
                        return method;
                }
            }
            Assert.Fail($"{type.Name} has no method `{name}` taking {argCount} argument(s).");
            return null;
        }
    }
}
