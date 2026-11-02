using System.Text.Json;
using System.Text.Json.Serialization;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Orchestrator.Services.Persistence
{
    /// <summary>
    /// The storage format of <c>StoredObjects/</c>: <see cref="RIoT2JsonOptions"/>, but with a
    /// <see cref="ValueModel"/> written as <c>{"type":n}</c>, as Newtonsoft wrote it before Orchestrator
    /// 1.11.0 (no type names, no ValueModel converter). Node configurations keep their nulls; the other
    /// types leave them out. The golden files in RIoT2.Core's <c>contracts/golden/stored-saved/</c> are
    /// the definition.
    /// </summary>
    public static class StoredObjectJson
    {
        private static readonly JsonSerializerOptions _withNulls = Create(ignoreNulls: false);
        private static readonly JsonSerializerOptions _withoutNulls = Create(ignoreNulls: true);

        public static string Serialize<T>(T obj) => JsonSerializer.Serialize(obj, Options(typeof(T)));

        /// <exception cref="JsonException">The JSON is invalid or doesn't fit <typeparamref name="T"/>.</exception>
        public static T? Deserialize<T>(string json) =>
            string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options(typeof(T)));

        private static JsonSerializerOptions Options(Type type) =>
            type == typeof(NodeDeviceConfiguration) ? _withNulls : _withoutNulls;

        private static JsonSerializerOptions Create(bool ignoreNulls)
        {
            var options = RIoT2JsonOptions.Create(ignoreNulls: ignoreNulls);
            // Before the wire converter, which would write the value itself.
            options.Converters.Insert(0, new StoredValueModelConverter());
            options.MakeReadOnly();
            return options;
        }

        // Writes what Newtonsoft wrote for a ValueModel without a converter: its one public property.
        // Reads like the wire converter, so a real value is kept.
        private sealed class StoredValueModelConverter : JsonConverter<ValueModel>
        {
            private static readonly ValueModelJsonConverter _wire = new();

            public override ValueModel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                _wire.Read(ref reader, typeToConvert, options);

            public override void Write(Utf8JsonWriter writer, ValueModel value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                writer.WriteNumber(options.PropertyNamingPolicy?.ConvertName("Type") ?? "Type", (int)value.Type);
                writer.WriteEndObject();
            }
        }
    }
}
