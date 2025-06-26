using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Report;

internal static class Helper
{
    public static Record ReadCsv(string path)
    {
        var record = new Record();
        using var reader = new CsvReader(new StreamReader(path), new CsvConfiguration(CultureInfo.InvariantCulture) { IgnoreBlankLines = false, MissingFieldFound = null, BadDataFound = null });
        while (reader.Read())
        {
            if (reader[0] == "Curve Info")
            {
                reader.Read();
                reader.ReadHeader();
                reader.Read();
                record.TimeStr = reader["datetimeHuman"];
                (record.DeviceCode, record.MesCode) = GetDeviceAndMes(reader["partID"]);
                record.Result = reader["result"];
            }

            if (reader[0] == "EV_CURVE")
            {
                reader.Read();
                reader.ReadHeader();
                reader.Read();
                string? YMax_Y = reader["YMax_Y"];
                if (YMax_Y != null)
                {
                    using var json = JsonDocument.Parse(YMax_Y.Replace('$', ','));
                    if (double.TryParse(json.RootElement.GetProperty("value").GetRawText(), out var maxY))
                    {
                        record.MaxY = maxY;
                    }
                }
                string? XMax_Y = reader["XMax_Y"];
                if (XMax_Y != null)
                {
                    using var json = JsonDocument.Parse(XMax_Y.Replace('$', ','));
                    if (double.TryParse(json.RootElement.GetProperty("value").GetRawText(), out var Y))
                    {
                        record.Y = Y;
                    }
                }
                string? XMax_X = reader["XMax_X"];
                if (XMax_X != null)
                {
                    using var json = JsonDocument.Parse(XMax_X.Replace('$', ','));
                    if (double.TryParse(json.RootElement.GetProperty("value").GetRawText(), out var X))
                    {
                        record.X = X;
                    }
                }
            }
        }

        return record;
    }

    public static (string, string) GetDeviceAndMes(string? partId)
    {
        if (string.IsNullOrWhiteSpace(partId))
        {
            return (string.Empty, string.Empty);
        }

        var index = partId.IndexOf('_');
        if (index <= 0 || index >= partId.Length - 1)
        {
            return (string.Empty, string.Empty);
        }

        return (partId.Substring(0, index), partId.Substring(index + 1));
    }
}
