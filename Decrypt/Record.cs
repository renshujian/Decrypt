using CsvHelper.Configuration.Attributes;
using System;

namespace Report
{
    internal class Record
    {
        [Name("时间")]
        [Index(0)]
        public string? TimeStr { get; set; }

        [Name("MES码")]
        [Index(1)]
        public string? MesCode { get; set; }

        [Name("压装结果")]
        [Index(2)]
        public string? Result { get; set; }

        [Name("最大压力")]
        [Index(3)]
        public double MaxY { get; set; }

        [Name("压力")]
        [Index(4)]
        public double Y { get; set; }

        [Name("位置")]
        [Index(5)]
        public double X { get; set; }

        [Name("设备编号")]
        [Index(6)]
        public string? DeviceCode { get; set; }
    }
}
