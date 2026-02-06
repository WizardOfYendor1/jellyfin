using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Extensions.Json.Converters
{
    /// <summary>
    /// Legacy DateTime converter.
    /// Milliseconds aren't output if zero by default.
    /// </summary>
    public class JsonDateTimeConverter : JsonConverter<DateTime>
    {
        /// <inheritdoc />
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString();
                if (string.IsNullOrEmpty(text))
                {
                    return default;
                }

                if (text.StartsWith("0000", StringComparison.Ordinal))
                {
                    return DateTime.MinValue;
                }

                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    return parsed;
                }

                throw new JsonException($"Invalid DateTime value: '{text}'.");
            }

            return reader.GetDateTime();
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            if (value.Millisecond == 0)
            {
                // Remaining ticks value will be 0, manually format.
                writer.WriteStringValue(value.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffffZ", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteStringValue(value);
            }
        }
    }
}
