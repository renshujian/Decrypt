using CsvHelper.Configuration;
using CsvHelper;
using LiteDB;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;

namespace Report;

internal static class Helper
{
    public static DirectoryInfo DataDir { get; } = Directory.CreateDirectory("data");
    public static LiteDatabase Database { get; } = new LiteDatabase("data/app.db");
    public static ILiteCollection<Measurement> Measurements { get; } = Database.GetCollection<Measurement>("measurements");

    public static (double, double) ReadCsv(string path)
    {
        double entry1 = 0;
        double entry2 = 0;
        using var reader = new CsvReader(new StreamReader(path), new CsvConfiguration(CultureInfo.InvariantCulture) { IgnoreBlankLines = false, MissingFieldFound = null, BadDataFound = null });
        while (reader.Read())
        {
            if (reader[0] == "EV_1")
            {
                reader.Read();
                reader.ReadHeader();
                reader.Read();
                string? entry = reader["entry"];
                if (entry != null)
                {
                    using var json = JsonDocument.Parse(entry.Replace('$', ','));
                    json.RootElement.GetProperty("value").TryGetDouble(out entry1);
                }
            }
            else if (reader[0] == "EV_2")
            {
                reader.Read();
                reader.ReadHeader();
                reader.Read();
                string? entry = reader["entry"];
                if (entry != null)
                {
                    using var json = JsonDocument.Parse(entry.Replace('$', ','));
                    json.RootElement.GetProperty("value").TryGetDouble(out entry2);
                }
            }
        }

        return (entry1, entry2);
    }
}
