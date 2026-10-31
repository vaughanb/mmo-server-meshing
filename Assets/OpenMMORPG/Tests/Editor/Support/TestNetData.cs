using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using LiteNetLib.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Builds network messages full of data, sends them through LiteNetLib's writer and reader
    /// and lists what came back different. Used by the round-trip tests: a field written but not
    /// read, read in a different order or behind a different #if or flag corrupts everything
    /// after it in the packet, so every field gets a distinct non-default value that would show
    /// up somewhere else if it slipped.
    ///
    /// <see cref="Fill"/> sets every public field and public settable property: strings, all
    /// numeric types (ints above 65535 and longs above 2^32, so a narrowing write shows),
    /// bools, enums (the first value after the default; every flag for [Flags] enums), Unity
    /// vectors, lists, arrays and dictionaries of two filled elements, nullables and nested
    /// types. It also adds two elements to get-only lists the type creates itself, and to types
    /// that are themselves lists. Unity objects, delegates and interfaces stay null.
    /// </summary>
    public static class TestNetData
    {
        /// <summary>How a member of a type is compared: left out, or floats within a tolerance.</summary>
        public delegate MemberRule MemberRules(Type owner, string member);

        public struct MemberRule
        {
            public bool Skip;
            public float Tolerance;

            public static readonly MemberRule Exact = default;
            public static MemberRule Within(float tolerance) => new MemberRule { Tolerance = tolerance };
            public static readonly MemberRule Ignore = new MemberRule { Skip = true };
        }

        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public;
        private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int MaxDepth = 6;

        /// <summary>
        /// A new instance of <paramref name="type"/> with every member filled.
        /// <paramref name="afterFill"/> sees every object built, at any depth, once its members
        /// are set (a boxed struct for value types), so a test can steer a message onto the
        /// branch it wants, e.g. a discriminator that decides which fields are written.
        /// </summary>
        public static object Fill(Type type, Action<object> afterFill = null)
        {
            return new Filler(afterFill).Make(type, 0);
        }

        public static T Fill<T>()
        {
            return (T)Fill(typeof(T));
        }

        /// <summary>An empty instance to deserialize into (a boxed struct for value types).</summary>
        public static object New(Type type)
        {
            return Activator.CreateInstance(type, true);
        }

        public static byte[] Write(INetSerializable value)
        {
            NetDataWriter writer = new NetDataWriter();
            value.Serialize(writer);
            return writer.CopyData();
        }

        /// <summary>
        /// Reads <paramref name="bytes"/> into a new <typeparamref name="T"/> and fails if the
        /// reader did not use up exactly the bytes written.
        /// </summary>
        public static T Read<T>(byte[] bytes) where T : INetSerializable
        {
            object received = New(typeof(T));
            NetDataReader reader = new NetDataReader(bytes);
            ((INetSerializable)received).Deserialize(reader);
            NUnit.Framework.Assert.That(reader.AvailableBytes, NUnit.Framework.Is.EqualTo(0),
                $"{typeof(T).Name} left bytes unread out of {bytes.Length}");
            return (T)received;
        }

        /// <summary>Serializes and deserializes <paramref name="value"/>, checking every byte is read.</summary>
        public static T RoundTrip<T>(T value) where T : INetSerializable
        {
            return Read<T>(Write(value));
        }

        /// <summary>
        /// The members of <paramref name="actual"/> that differ from <paramref name="expected"/>,
        /// as "path: expected X, got Y" lines. Compares what <see cref="Fill"/> sets, recursing
        /// into nested types, lists and dictionaries; a type with no public members is compared
        /// by all its fields. Null and empty count as the same, for collections and strings:
        /// the kit writes a null list as zero elements and LiteNetLib a null string as "".
        /// </summary>
        public static List<string> Differences(object expected, object actual, MemberRules rules = null)
        {
            List<string> differences = new List<string>();
            Compare(expected, actual, expected?.GetType().Name ?? "value", 0f, rules, differences, 0);
            return differences;
        }

        /// <summary>The members Fill sets and Differences compares, in declaration order.</summary>
        public static IEnumerable<MemberInfo> Members(Type type)
        {
            // Members a kit type inherits from List<T> and the like (Capacity) are not its data.
            foreach (FieldInfo field in type.GetFields(Instance))
            {
                if (!field.IsInitOnly && !field.IsLiteral && !IsUnfillable(field.FieldType) && !IsSystemType(field.DeclaringType))
                    yield return field;
            }
            foreach (PropertyInfo property in type.GetProperties(Instance))
            {
                if (property.GetIndexParameters().Length > 0 || IsUnfillable(property.PropertyType) || IsSystemType(property.DeclaringType))
                    continue;
                if (property.GetSetMethod() != null && property.GetGetMethod() != null)
                    yield return property;
                else if (property.GetGetMethod() != null && IsGrowableList(property.PropertyType))
                    yield return property;
            }
        }

        private static bool IsUnfillable(Type type)
        {
            return typeof(Object).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type)
                || type == typeof(object) || type == typeof(Type) || type.IsPointer;
        }

        private static bool IsGrowableList(Type type)
        {
            return !type.IsArray && ListElementType(type) != null;
        }

        private static Type ListElementType(Type type)
        {
            foreach (Type i in type.GetInterfaces().Concat(type.IsInterface ? new[] { type } : Type.EmptyTypes))
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
                    return i.GetGenericArguments()[0];
            }
            return null;
        }

        private static object GetValue(MemberInfo member, object owner)
        {
            return member is FieldInfo field ? field.GetValue(owner) : ((PropertyInfo)member).GetValue(owner);
        }

        private static Type MemberType(MemberInfo member)
        {
            return member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
        }

        private sealed class Filler
        {
            private readonly Action<object> _afterFill;
            private int _counter;

            public Filler(Action<object> afterFill)
            {
                _afterFill = afterFill;
            }

            public object Make(Type type, int depth)
            {
                int n = ++_counter;
                if (type == typeof(string)) return "text" + n.ToString(CultureInfo.InvariantCulture);
                if (type == typeof(bool)) return true;
                if (type == typeof(byte)) return (byte)(1 + n % 250);
                if (type == typeof(sbyte)) return (sbyte)(1 + n % 120);
                if (type == typeof(short)) return (short)(300 + n);
                if (type == typeof(ushort)) return (ushort)(300 + n);
                if (type == typeof(int)) return 70000 + n;
                if (type == typeof(uint)) return (uint)(70000 + n);
                if (type == typeof(long)) return (1L << 33) + n;
                if (type == typeof(ulong)) return (1UL << 33) + (ulong)n;
                if (type == typeof(float)) return 1000.5f + n;
                if (type == typeof(double)) return 100000.25 + n;
                if (type == typeof(char)) return (char)('a' + n % 26);
                if (type == typeof(decimal)) return 1000.5m + n;
                if (type.IsEnum) return EnumValue(type);
                if (type == typeof(Vector2)) return new Vector2(n + 0.25f, n + 0.5f);
                if (type == typeof(Vector3)) return new Vector3(n + 0.25f, n + 0.5f, n + 0.75f);
                if (type == typeof(Vector4)) return new Vector4(n + 0.125f, n + 0.25f, n + 0.5f, n + 0.75f);
                if (type == typeof(Vector2Int)) return new Vector2Int(n, n + 1);
                if (type == typeof(Vector3Int)) return new Vector3Int(n, n + 1, n + 2);
                if (type == typeof(Quaternion)) return Quaternion.Euler(10f + n, 20f + n, 30f + n);
                if (type == typeof(Color)) return new Color(0.2f, 0.4f, 0.6f, 0.8f);
                if (type == typeof(Color32)) return new Color32(20, 40, 60, 80);
                if (IsUnfillable(type)) return null;

                Type nullable = Nullable.GetUnderlyingType(type);
                if (nullable != null) return Make(nullable, depth);

                if (depth >= MaxDepth) return type.IsValueType ? Activator.CreateInstance(type) : null;

                if (type.IsArray)
                {
                    Type element = type.GetElementType();
                    Array array = Array.CreateInstance(element, 2);
                    for (int i = 0; i < 2; ++i)
                        array.SetValue(Make(element, depth + 1), i);
                    return array;
                }

                if (type.IsGenericType && type.IsInterface)
                {
                    Type definition = type.GetGenericTypeDefinition();
                    Type[] args = type.GetGenericArguments();
                    if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                        type = typeof(Dictionary<,>).MakeGenericType(args);
                    else if (args.Length == 1 && typeof(IEnumerable).IsAssignableFrom(type))
                        type = typeof(List<>).MakeGenericType(args);
                    else
                        return null;
                }

                if (type.IsInterface || type.IsAbstract) return null;

                object instance;
                try
                {
                    instance = Activator.CreateInstance(type, true);
                }
                catch (MissingMethodException)
                {
                    return null;
                }

                if (instance is IDictionary dictionary && type.IsGenericType)
                {
                    Type[] args = type.GetGenericArguments();
                    for (int i = 0; i < 2; ++i)
                        dictionary[Make(args[0], depth + 1)] = Make(args[1], depth + 1);
                    return instance;
                }

                if (!type.IsArray)
                    AddElements(instance, depth);

                if (type.Namespace != null && type.Namespace.StartsWith("System"))
                    return instance;

                FillMembers(instance, depth);
                _afterFill?.Invoke(instance);
                return instance;
            }

            private void FillMembers(object instance, int depth)
            {
                foreach (MemberInfo member in Members(instance.GetType()))
                {
                    if (member is FieldInfo field)
                    {
                        field.SetValue(instance, Make(field.FieldType, depth + 1));
                        continue;
                    }
                    PropertyInfo property = (PropertyInfo)member;
                    if (property.GetSetMethod() != null)
                    {
                        property.SetValue(instance, Make(property.PropertyType, depth + 1));
                    }
                    else
                    {
                        object list = property.GetValue(instance);
                        if (list != null)
                            AddElements(list, depth);
                    }
                }
            }

            /// <summary>Adds two filled elements to a writable IList&lt;T&gt; (the kit's NotifiableList is not an IList).</summary>
            private void AddElements(object list, int depth)
            {
                Type element = ListElementType(list.GetType());
                if (element == null)
                    return;
                Type collection = typeof(ICollection<>).MakeGenericType(element);
                if ((bool)collection.GetProperty("IsReadOnly").GetValue(list) || (list is IList fixedList && fixedList.IsFixedSize))
                    return;
                MethodInfo add = collection.GetMethod("Add");
                for (int i = 0; i < 2; ++i)
                    add.Invoke(list, new[] { Make(element, depth + 1) });
            }

            private static object EnumValue(Type type)
            {
                Array values = Enum.GetValues(type);
                object zero = Enum.ToObject(type, 0);
                if (type.IsDefined(typeof(FlagsAttribute), false))
                {
                    ulong all = 0;
                    foreach (object value in values)
                        all |= Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                    return Enum.ToObject(type, all);
                }
                foreach (object value in values)
                {
                    if (!value.Equals(zero))
                        return value;
                }
                return zero;
            }
        }

        private static void Compare(object expected, object actual, string path, float tolerance, MemberRules rules, List<string> differences, int depth)
        {
            if (IsEmptyOrNull(expected) && IsEmptyOrNull(actual))
                return;
            if (expected == null || actual == null)
            {
                differences.Add($"{path}: expected {Describe(expected)}, got {Describe(actual)}");
                return;
            }

            Type type = expected.GetType();
            if (type != actual.GetType())
            {
                differences.Add($"{path}: expected a {type.Name}, got a {actual.GetType().Name}");
                return;
            }

            if (type == typeof(float) || type == typeof(double))
            {
                double e = Convert.ToDouble(expected, CultureInfo.InvariantCulture);
                double a = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
                bool same = tolerance > 0f ? Math.Abs(e - a) <= tolerance : expected.Equals(actual);
                if (!same)
                    differences.Add($"{path}: expected {Describe(expected)}, got {Describe(actual)}" + (tolerance > 0f ? $" (tolerance {tolerance})" : string.Empty));
                return;
            }

            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
            {
                if (!expected.Equals(actual))
                    differences.Add($"{path}: expected {Describe(expected)}, got {Describe(actual)}");
                return;
            }

            if (depth > 12)
                return;

            if (expected is IDictionary expectedDictionary)
            {
                IDictionary actualDictionary = (IDictionary)actual;
                if (expectedDictionary.Count != actualDictionary.Count)
                {
                    differences.Add($"{path}: expected {expectedDictionary.Count} entries, got {actualDictionary.Count}");
                    return;
                }
                foreach (object key in expectedDictionary.Keys)
                {
                    if (!actualDictionary.Contains(key))
                        differences.Add($"{path}[{Describe(key)}]: missing");
                    else
                        Compare(expectedDictionary[key], actualDictionary[key], $"{path}[{Describe(key)}]", tolerance, rules, differences, depth + 1);
                }
                return;
            }

            bool isSystemType = IsSystemType(type);
            bool isList = ListElementType(type) != null || expected is IList;
            if (isList || (isSystemType && expected is IEnumerable))
            {
                List<object> e = ((IEnumerable)expected).Cast<object>().ToList();
                List<object> a = ((IEnumerable)actual).Cast<object>().ToList();
                if (e.Count != a.Count)
                {
                    differences.Add($"{path}: expected {e.Count} elements, got {a.Count}");
                    return;
                }
                for (int i = 0; i < e.Count; ++i)
                    Compare(e[i], a[i], $"{path}[{i}]", tolerance, rules, differences, depth + 1);
                // A kit type that is a list may add fields of its own; compare those too.
                if (isSystemType)
                    return;
            }

            // Unity's structs: their public fields only (Quaternion.eulerAngles and the like are derived).
            List<MemberInfo> members = (type.Namespace ?? string.Empty).StartsWith("UnityEngine")
                ? type.GetFields(Instance).Cast<MemberInfo>().ToList()
                : Members(type).ToList();
            if (members.Count == 0 && !isList)
                members = type.GetFields(AllInstance).Where(f => !IsSystemType(f.DeclaringType)).Cast<MemberInfo>().ToList();

            foreach (MemberInfo member in members)
            {
                MemberRule rule = rules != null ? rules(type, member.Name) : MemberRule.Exact;
                if (rule.Skip)
                    continue;
                float memberTolerance = rule.Tolerance > 0f ? rule.Tolerance : tolerance;
                Compare(GetValue(member, expected), GetValue(member, actual), $"{path}.{member.Name}", memberTolerance, rules, differences, depth + 1);
            }
        }

        private static bool IsSystemType(Type type)
        {
            return (type.Namespace ?? string.Empty).StartsWith("System");
        }

        private static bool IsEmptyOrNull(object value)
        {
            return value == null || (value is ICollection collection && collection.Count == 0) || (value is string text && text.Length == 0);
        }

        private static string Describe(object value)
        {
            switch (value)
            {
                case null: return "null";
                case string text: return "\"" + text + "\"";
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case ICollection collection: return $"{value.GetType().Name} of {collection.Count}";
                default: return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }
    }
}
