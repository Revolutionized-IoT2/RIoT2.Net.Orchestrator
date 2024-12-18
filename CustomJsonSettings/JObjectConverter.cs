using Newtonsoft.Json.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RIoT2.Net.Orchestrator.CustomJsonSettings
{
    public class JObjectConverter : JsonConverter<JObject>
    {
        public override JObject Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            return JObject.Parse(reader.GetString());
        }

        public override void Write(
            Utf8JsonWriter writer,
            JObject value,
            JsonSerializerOptions options)
        {
            writer.WriteRawValue(value.ToString(Newtonsoft.Json.Formatting.None));
        }
    }
}
