using System.Globalization;
using System.Text;

namespace ClassBox.Plugins.UnitConverter.Converting;

/// <summary>单个单位定义：通过 ToBase / FromBase 与同类基准单位互换。</summary>
public sealed class UnitDef
{
    public string Name { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public Func<double, double> ToBase { get; init; } = v => v;

    public Func<double, double> FromBase { get; init; } = v => v;

    public string Display => string.IsNullOrEmpty(Symbol) ? Name : $"{Name} ({Symbol})";
}

/// <summary>单位类别。</summary>
public sealed class UnitCategory
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<UnitDef> Units { get; init; } = Array.Empty<UnitDef>();

    public override string ToString() => Name;
}

/// <summary>八大类单位的换算引擎。</summary>
public static class UnitEngine
{
    private static UnitDef Factor(string name, string symbol, double factor) => new()
    {
        Name = name,
        Symbol = symbol,
        ToBase = v => v * factor,
        FromBase = v => v / factor
    };

    public static IReadOnlyList<UnitCategory> Categories { get; } = BuildCategories();

    private static List<UnitCategory> BuildCategories()
    {
        var length = new UnitCategory
        {
            Name = "长度",
            Units = new[]
            {
                Factor("毫米", "mm", 0.001),
                Factor("厘米", "cm", 0.01),
                Factor("米", "m", 1),
                Factor("千米", "km", 1000),
                Factor("英寸", "in", 0.0254),
                Factor("英尺", "ft", 0.3048),
                Factor("码", "yd", 0.9144),
                Factor("英里", "mile", 1609.344),
                Factor("海里", "nmi", 1852)
            }
        };

        var weight = new UnitCategory
        {
            Name = "重量",
            Units = new[]
            {
                Factor("毫克", "mg", 0.000001),
                Factor("克", "g", 0.001),
                Factor("千克", "kg", 1),
                Factor("吨", "t", 1000),
                Factor("斤", "市斤", 0.5),
                Factor("两", "市两", 0.05),
                Factor("盎司", "oz", 0.028349523125),
                Factor("磅", "lb", 0.45359237)
            }
        };

        // 温度使用仿射换算，基准为摄氏度
        var temperature = new UnitCategory
        {
            Name = "温度",
            Units = new[]
            {
                new UnitDef
                {
                    Name = "摄氏度",
                    Symbol = "°C",
                    ToBase = v => v,
                    FromBase = v => v
                },
                new UnitDef
                {
                    Name = "华氏度",
                    Symbol = "°F",
                    ToBase = v => (v - 32) * 5 / 9,
                    FromBase = v => v * 9 / 5 + 32
                },
                new UnitDef
                {
                    Name = "开尔文",
                    Symbol = "K",
                    ToBase = v => v - 273.15,
                    FromBase = v => v + 273.15
                },
                new UnitDef
                {
                    Name = "兰氏度",
                    Symbol = "°R",
                    ToBase = v => (v - 491.67) * 5 / 9,
                    FromBase = v => v * 9 / 5 + 491.67
                }
            }
        };

        var area = new UnitCategory
        {
            Name = "面积",
            Units = new[]
            {
                Factor("平方毫米", "mm²", 0.000001),
                Factor("平方厘米", "cm²", 0.0001),
                Factor("平方米", "m²", 1),
                Factor("平方千米", "km²", 1000000),
                Factor("公顷", "ha", 10000),
                Factor("亩", "市亩", 666.6666666667),
                Factor("平方英尺", "ft²", 0.09290304),
                Factor("英亩", "acre", 4046.8564224)
            }
        };

        var volume = new UnitCategory
        {
            Name = "体积",
            Units = new[]
            {
                Factor("毫升", "mL", 0.001),
                Factor("立方厘米", "cm³", 0.001),
                Factor("升", "L", 1),
                Factor("立方米", "m³", 1000),
                Factor("美制加仑", "gal", 3.785411784),
                Factor("美制品脱", "pt", 0.473176473),
                Factor("立方英尺", "ft³", 28.316846592)
            }
        };

        var speed = new UnitCategory
        {
            Name = "速度",
            Units = new[]
            {
                Factor("米/秒", "m/s", 1),
                Factor("千米/小时", "km/h", 1 / 3.6),
                Factor("英里/小时", "mph", 0.44704),
                Factor("英尺/秒", "ft/s", 0.3048),
                Factor("节", "kn", 0.5144444444),
                Factor("马赫（海平面）", "Mach", 340.3)
            }
        };

        var data = new UnitCategory
        {
            Name = "数据存储",
            Units = new[]
            {
                Factor("比特", "bit", 0.125),
                Factor("字节", "B", 1),
                Factor("千字节", "KB", 1024),
                Factor("兆字节", "MB", 1024d * 1024),
                Factor("吉字节", "GB", 1024d * 1024 * 1024),
                Factor("太字节", "TB", 1024d * 1024 * 1024 * 1024),
                Factor("拍字节", "PB", 1024d * 1024 * 1024 * 1024 * 1024)
            }
        };

        var time = new UnitCategory
        {
            Name = "时间",
            Units = new[]
            {
                Factor("毫秒", "ms", 0.001),
                Factor("秒", "s", 1),
                Factor("分钟", "min", 60),
                Factor("小时", "h", 3600),
                Factor("天", "d", 86400),
                Factor("周", "wk", 604800),
                Factor("月（平均）", "mo", 2629746),
                Factor("年（平均）", "yr", 31556952)
            }
        };

        return new List<UnitCategory>
        {
            length, weight, temperature, area, volume, speed, data, time
        };
    }

    /// <summary>同类单位之间换算。</summary>
    public static double Convert(UnitDef from, UnitDef to, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return double.NaN;
        return to.FromBase(from.ToBase(value));
    }

    /// <summary>生成常用换算对照表：把 value 个基准单位换算到同类全部单位。</summary>
    public static string BuildTable(UnitCategory category, UnitDef unit, double value)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{FormatNumber(value)} {unit.Name} = ");

        foreach (var target in category.Units)
        {
            if (ReferenceEquals(target, unit)) continue;
            var converted = Convert(unit, target, value);
            sb.AppendLine($"    {FormatNumber(converted)} {target.Name}" + (string.IsNullOrEmpty(target.Symbol) ? string.Empty : $"（{target.Symbol}）"));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>数值格式化：控制有效数字，极值与极小值使用科学计数法。</summary>
    public static string FormatNumber(double value)
    {
        if (double.IsNaN(value)) return "无效数值";
        if (double.IsInfinity(value)) return value > 0 ? "∞" : "-∞";
        if (value == 0) return "0";

        var abs = Math.Abs(value);
        if (abs >= 1e15 || abs < 1e-6)
        {
            return value.ToString("0.######E+0", CultureInfo.InvariantCulture);
        }

        if (abs >= 1e9) return value.ToString("#,0.####", CultureInfo.InvariantCulture);
        if (abs >= 1000) return value.ToString("#,0.####", CultureInfo.InvariantCulture);
        if (abs >= 1) return value.ToString("0.######", CultureInfo.InvariantCulture);
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }
}
