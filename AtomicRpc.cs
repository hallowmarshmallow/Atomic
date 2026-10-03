using System;
using System.Collections.Generic;
using System.Reflection;
using Hazel;

namespace Atomic
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class AtomicRpcAttribute : Attribute
    {
        public byte? CallId { get; }
        public string Key { get; }

        // marks an rpc function with a fixed id.
        public AtomicRpcAttribute(byte callId)
        {
            CallId = callId;
        }

        // marks an rpc function with a key and reserves its id.
        public AtomicRpcAttribute(string key)
        {
            Key = key;
            RpcIdAllocator.Reserve(key);
        }
    }

    public static class AtomicRpc
    {
        private static readonly List<Action> _pendingRegistrations = new();
        private static bool _flushed;

        // finds rpc functions on this object and registers them.
        public static void RegisterMethods(object target)
        {
            if (target == null)
            {
                return;
            }

            RegisterMethods(target.GetType(), target);
        }

        // finds rpc functions on this type and registers them.
        public static void RegisterMethods(Type type)
        {
            RegisterMethods(type, null);
        }

        // connects each marked function to its rpc message id.
        private static void RegisterMethods(Type type, object target)
        {
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (MethodInfo method in methods)
            {
                AtomicRpcAttribute attribute = method.GetCustomAttribute<AtomicRpcAttribute>();
                if (attribute == null)
                {
                    continue;
                }

                if (!method.IsStatic && target == null)
                {
                    AtomicPlugin.Log.LogError($"[AtomicRpc] {type.Name}.{method.Name} is an instance method but no instance was provided, skipping.");
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(byte))
                {
                    AtomicPlugin.Log.LogError($"[AtomicRpc] {type.Name}.{method.Name} needs a leading byte senderId parameter, skipping.");
                    continue;
                }

                var instance = method.IsStatic ? null : target;
                MethodInfo methodToCall = method;
                ParameterInfo[] methodParameters = parameters;
                AtomicRpcAttribute methodAttribute = attribute;

                // adds this function as a handler when registration is ready.
                void Register()
                {
                    byte callId;
                    if (methodAttribute.CallId.HasValue)
                    {
                        callId = methodAttribute.CallId.Value;
                    }
                    else
                    {
                        callId = RpcIdAllocator.GetId(methodAttribute.Key);
                    }

                    NetworkManager.RegisterHandler(callId, (senderId, reader) =>
                    {
                        object[] arguments = new object[methodParameters.Length];
                        arguments[0] = senderId;

                        for (int i = 1; i < methodParameters.Length; i++)
                        {
                            Type parameterType = methodParameters[i].ParameterType;
                            arguments[i] = ReadValue(reader, parameterType);
                        }

                        methodToCall.Invoke(instance, arguments);
                    });
                }

                if (methodAttribute.CallId.HasValue)
                {
                    Register();
                }
                else
                {
                    _pendingRegistrations.Add(Register);
                }
            }
        }

        // registers rpc handlers that were waiting for their ids.
        public static void EnsureFlushed()
        {
            if (_flushed)
            {
                return;
            }

            _flushed = true;

            foreach (Action register in _pendingRegistrations)
            {
                register();
            }

            _pendingRegistrations.Clear();
        }

        // sends an rpc message using its id.
        public static void Send(byte callId, params object[] args)
        {
            NetworkManager.SendRpc(callId, w =>
            {
                foreach (object argument in args)
                {
                    WriteValue(w, argument);
                }
            });
        }

        // finds the id for this key, then sends the rpc message.
        public static void Send(string key, params object[] args)
        {
            EnsureFlushed();
            Send(RpcIdAllocator.GetId(key), args);
        }

        // reads one supported value from an rpc message.
        private static object ReadValue(MessageReader reader, Type type)
        {
            if (type == typeof(bool))
            {
                return reader.ReadBoolean();
            }

            if (type == typeof(byte))
            {
                return reader.ReadByte();
            }

            if (type == typeof(int))
            {
                return reader.ReadInt32();
            }

            if (type == typeof(float))
            {
                return reader.ReadSingle();
            }

            if (type == typeof(string))
            {
                return reader.ReadString();
            }

            throw new NotSupportedException($"[AtomicRpc] Unsupported parameter type: {type}");
        }

        // writes one supported value into an rpc message.
        private static void WriteValue(MessageWriter writer, object value)
        {
            switch (value)
            {
                case bool boolValue:
                    writer.Write(boolValue);
                    break;
                case byte byteValue:
                    writer.Write(byteValue);
                    break;
                case int intValue:
                    writer.Write(intValue);
                    break;
                case float floatValue:
                    writer.Write(floatValue);
                    break;
                case string stringValue:
                    writer.Write(stringValue);
                    break;
                default:
                    throw new NotSupportedException($"[AtomicRpc] Unsupported argument type: {value?.GetType()}");
            }
        }
    }
}
