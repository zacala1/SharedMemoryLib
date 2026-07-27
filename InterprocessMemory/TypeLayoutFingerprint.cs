using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace InterprocessMemory
{
    internal readonly record struct TypeLayoutFingerprint(ulong Low, ulong High)
    {
        public static TypeLayoutFingerprint Create<T>() where T : unmanaged
        {
            var descriptor = new StringBuilder(256);
            AppendType(descriptor, typeof(T), new HashSet<Type>());
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(descriptor.ToString()));
            return new TypeLayoutFingerprint(
                BinaryPrimitives.ReadUInt64LittleEndian(hash),
                BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(8)));
        }

        private static void AppendType(StringBuilder target, Type type, HashSet<Type> active)
        {
            target.Append(type.Assembly.GetName().Name)
                .Append('|').Append(type.FullName)
                .Append('|').Append(Marshal.SizeOf(type));

            var layout = type.StructLayoutAttribute;
            target.Append('|').Append((int)(layout?.Value ?? LayoutKind.Auto))
                .Append('|').Append(layout?.Pack ?? 0)
                .Append('|').Append(layout?.Size ?? 0);

            if (type.IsPrimitive || type.IsEnum || !active.Add(type))
                return;

            var fields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => new
                {
                    Field = field,
                    Offset = checked((int)Marshal.OffsetOf(type, field.Name))
                })
                .OrderBy(item => item.Offset)
                .ThenBy(item => item.Field.Name, StringComparer.Ordinal);

            foreach (var item in fields)
            {
                target.Append(";f:").Append(item.Offset)
                    .Append(':').Append(item.Field.Name)
                    .Append(':').Append(item.Field.FieldType.FullName);
                AppendType(target, item.Field.FieldType, active);
            }

            active.Remove(type);
        }
    }
}
