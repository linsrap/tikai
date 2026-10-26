using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tikai.Data;

/// <summary>Stores enums as snake_case text ("https_token"), matching the API's JSON, with a check constraint on the values.</summary>
internal static class SnakeCaseEnum
{
    public static ValueConverter<TEnum, string> Converter<TEnum>()
        where TEnum : struct, Enum =>
        new(value => ToName(value), name => Parse<TEnum>(name));

    public static string CheckIn<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetValues<TEnum>().Select(v => $"'{ToName(v)}'"))})";

    public static string ToName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static TEnum Parse<TEnum>(string name)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Single(v => ToName(v) == name);
}
