using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Domain.Common;

namespace Orbit.Application.Common;

public sealed class ResultJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(Result)
        || (typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Result<>));

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(Result))
            return new ResultConverter();

        var valueType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(
            typeof(GenericResultConverter<>).MakeGenericType(valueType))!;
    }

    private static (bool IsSuccess, string Error, string? ErrorCode) ReadEnvelope(JsonElement root)
    {
        var isSuccess = root.GetProperty("isSuccess").GetBoolean();
        var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() ?? "" : "";
        var errorCode = root.TryGetProperty("errorCode", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
            ? codeElement.GetString()
            : null;
        return (isSuccess, error, errorCode);
    }

    private static void WriteEnvelope(Utf8JsonWriter writer, Result value)
    {
        writer.WriteBoolean("isSuccess", value.IsSuccess);
        writer.WriteString("error", value.Error);
        if (value.ErrorCode is null)
            writer.WriteNull("errorCode");
        else
            writer.WriteString("errorCode", value.ErrorCode);
    }

    private sealed class ResultConverter : JsonConverter<Result>
    {
        public override Result Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var (isSuccess, error, errorCode) = ReadEnvelope(document.RootElement);
            if (isSuccess)
                return Result.Success();
            return errorCode is null ? Result.Failure(error) : Result.Failure(error, errorCode);
        }

        public override void Write(Utf8JsonWriter writer, Result value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            WriteEnvelope(writer, value);
            writer.WriteEndObject();
        }
    }

    private sealed class GenericResultConverter<T> : JsonConverter<Result<T>>
    {
        public override Result<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var (isSuccess, error, errorCode) = ReadEnvelope(root);

            if (isSuccess)
            {
                var value = root.TryGetProperty("value", out var valueElement)
                    ? valueElement.Deserialize<T>(options)
                    : default;
                return Result.Success(value!);
            }

            return errorCode is null ? Result.Failure<T>(error) : Result.Failure<T>(error, errorCode);
        }

        public override void Write(Utf8JsonWriter writer, Result<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            WriteEnvelope(writer, value);
            if (value.IsSuccess)
            {
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, value.Value, options);
            }
            writer.WriteEndObject();
        }
    }
}
