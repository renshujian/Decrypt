using CsvHelper;
using CsvHelper.Configuration;
using LiteDB;
using System.Globalization;
using System.IO;
using System.Text.Json;

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
            if (reader[0] == "EV_CURVE")
            {
                reader.Read();
                reader.ReadHeader();
                reader.Read();
                string? YMax_Y = reader["YMax_Y"];
                if (YMax_Y != null)
                {
                    using var json = JsonDocument.Parse(YMax_Y.Replace('$', ','));
                    double.TryParse(json.RootElement.GetProperty("value").GetRawText(), out entry1);
                }
                string? YMin_Y = reader["YMin_Y"];
                if (YMin_Y != null)
                {
                    using var json = JsonDocument.Parse(YMin_Y.Replace('$', ','));
                    double.TryParse(json.RootElement.GetProperty("value").GetRawText(), out entry2);
                }
            }
        }

        return (entry1, entry2);
    }
}
