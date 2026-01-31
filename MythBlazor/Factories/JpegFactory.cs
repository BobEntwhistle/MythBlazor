using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace MythBlazor.Factories
{
    public class JpegParseNode : IParseNode, IDisposable
    {
        private MemoryStream? _content;
        private bool _disposed;

        public Action<IParsable>? OnBeforeAssignFieldValues { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public Action<IParsable>? OnAfterAssignFieldValues { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public JpegParseNode(Stream content)
        {
            _content = new MemoryStream();
            content.CopyTo(_content);
            _content.Position = 0;
        }

        public string? GetStringValue()
        {
            throw new NotSupportedException("GetStringValue is not supported for JpegParseNode.");
        }
        public IParseNode? GetChildNode(string identifier)
        {
            throw new NotSupportedException("GetChildNode is not supported for JpegParseNode.");
        }
        public bool? GetBoolValue()
        {
            throw new NotSupportedException("GetBoolValue is not supported for JpegParseNode.");
        }
        public byte? GetByteValue()
        {
            throw new NotSupportedException("GetByteValue is not supported for JpegParseNode.");
        }
        public sbyte? GetSbyteValue()
        {
            throw new NotSupportedException("GetSbyteValue is not supported for JpegParseNode.");
        }
        public int? GetIntValue()
        {
            throw new NotSupportedException("GetIntValue is not supported for JpegParseNode.");
        }
        public float? GetFloatValue()
        {
            throw new NotSupportedException("GetFloatValue is not supported for JpegParseNode.");
        }
        public long? GetLongValue()
        {
            throw new NotSupportedException("GetLongValue is not supported for JpegParseNode.");
        }

        public double? GetDoubleValue()
        {
            throw new NotImplementedException();
        }

        public decimal? GetDecimalValue()
        {
            throw new NotImplementedException();
        }

        public Guid? GetGuidValue()
        {
            throw new NotImplementedException();
        }

        public DateTimeOffset? GetDateTimeOffsetValue()
        {
            throw new NotImplementedException();
        }

        public TimeSpan? GetTimeSpanValue()
        {
            throw new NotImplementedException();
        }

        public Date? GetDateValue()
        {
            throw new NotImplementedException();
        }

        public Time? GetTimeValue()
        {
            throw new NotImplementedException();
        }

        public IEnumerable<T> GetCollectionOfPrimitiveValues<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>()
        {
            throw new NotImplementedException();
        }

        public IEnumerable<T?> GetCollectionOfEnumValues<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>() where T : struct, Enum
        {
            throw new NotImplementedException();
        }

        public IEnumerable<T> GetCollectionOfObjectValues<T>(ParsableFactory<T> factory) where T : IParsable
        {
            throw new NotImplementedException();
        }

        public T? GetEnumValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>() where T : struct, Enum
        {
            throw new NotImplementedException();
        }

        public T GetObjectValue<T>(ParsableFactory<T> factory) where T : IParsable
        {
            var def = factory(this);
            var additional = def as IAdditionalDataHolder;
            additional.AdditionalData = new Dictionary<string, object>
            {
                { "content", GetByteArrayValue() ?? Array.Empty<byte>() }
            };
            return def;
        }

        public byte[]? GetByteArrayValue()
        {
            ThrowIfDisposed();
            return _content?.ToArray();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(JpegParseNode));
        }

        // IDisposable implementation to ensure underlying stream is released
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                _content?.Dispose();
                _content = null;
            }
            _disposed = true;
        }

        ~JpegParseNode()
        {
            Dispose(false);
        }

        // Implement other methods as needed, throwing NotSupportedException
    }
    public class JpegFactory : IParseNodeFactory
    {
        public string ValidContentType => "image/jpeg";

        public IParseNode GetRootParseNode(string contentType, Stream content)
        {
            return new JpegParseNode(content);
        }
    }
}
