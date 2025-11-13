using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Ini;

namespace CSV_SYSTEM_API
{
    public class CsvToMapConverter
    {
        private readonly ILogger _logger;
        private readonly string _iniFilePath;
        private readonly IConfiguration _configuration;

        public CsvToMapConverter(ILogger logger, string iniFilePath, IConfiguration configuration)
        {
            _logger = logger;
            _iniFilePath = iniFilePath;
            _configuration = configuration;
        }

        public (byte[] fileContent, string fileName) ConvertCsvToMap(string csvFilePath, string originalFileName)
        {
            try
            {
                // 读取 INI 文件
                Dictionary<string, string[]> code1Map = ReadIniSection("CODE_1");
                Dictionary<string, string[]> code2Map = ReadIniSection("CODE_2");

                TStringList str_CSV = new TStringList();
                str_CSV.Add(csvFilePath); // 假设只处理一个 CSV 文件

                int pos1 = 0, pos2 = 0, posx = 0, posy = 0;
                bool siteNumFound = false; // 标志，用于检查是否找到 SITE_NUM
                bool limitUFound = false; // 标志，用于检查是否找到 LimitU

                // 第一遍扫描：检查 SITE_NUM, CODE1, CODE2, X_COORD, Y_COORD 和 LimitU
                using (StreamReader reader = new StreamReader(csvFilePath))
                {
                    string ls_line;
                    while ((ls_line = reader.ReadLine()) != null)
                    {
                        if (ls_line.Contains("SITE_NUM"))
                        {
                            siteNumFound = true;

                            // 检查 CODE1 和 CODE2 的位置
                            if (ls_line.Contains("CODE1") && ls_line.Contains("CODE2"))
                            {
                                string temp2 = ls_line.Substring(0, ls_line.IndexOf("CODE1"));
                                pos1 = temp2.CountOccurrences(",");
                                temp2 = ls_line.Substring(0, ls_line.IndexOf("CODE2"));
                                pos2 = temp2.CountOccurrences(",");
                            }
                            else
                            {
                                _logger.LogError($@"CSV文件格式不正确：SITE_NUM行没有CODE1或CODE2。 文件: {originalFileName}");
                                throw new InvalidDataException("CSV文件格式不正确：SITE_NUM行没有CODE1或CODE2。");
                            }

                            // 检查 X_COORD 和 Y_COORD 的位置
                            if (ls_line.Contains("X_COORD") && ls_line.Contains("Y_COORD"))
                            {
                                string temp2 = ls_line.Substring(0, ls_line.IndexOf("X_COORD"));
                                posx = temp2.CountOccurrences(",");
                                temp2 = ls_line.Substring(0, ls_line.IndexOf("Y_COORD"));
                                posy = temp2.CountOccurrences(",");
                            }
                            else
                            {
                                _logger.LogError($@"CSV文件格式不正确：SITE_NUM行没有X_COORD或Y_COORD。 文件: {originalFileName}");
                                throw new InvalidDataException("CSV文件格式不正确：SITE_NUM行没有X_COORD或Y_COORD。");
                            }
                        }
                        if (ls_line.Contains("LimitU"))
                        {
                            limitUFound = true;
                        }
                    }
                }

                if (!siteNumFound)
                {
                    _logger.LogError($@"CSV文件格式不正确：没有SITE_NUM。 文件: {originalFileName}");
                    throw new InvalidDataException("CSV文件格式不正确：没有SITE_NUM。");
                }

                if (!limitUFound)
                {
                    _logger.LogError($@"CSV文件格式不正确：没有LimitU。 文件: {originalFileName}");
                    throw new InvalidDataException("CSV文件格式不正确：没有LimitU。");
                }

                // 第二遍扫描：读取 LimitU 之后的数据
                List<CsvRecord> records = new List<CsvRecord>();
                using (StreamReader reader = new StreamReader(csvFilePath))
                {
                    string ls_line;
                    bool afterLimitU = false;
                    while ((ls_line = reader.ReadLine()) != null)
                    {
                        if (ls_line.Contains("LimitU"))
                        {
                            afterLimitU = true;
                            reader.ReadLine(); // 跳过 LimitU 之后的下一行（标题行）
                            continue;
                        }

                        if (afterLimitU)
                        {
                            string[] parts = ls_line.Split(',');
                            if (parts.Length > Math.Max(Math.Max(posx, posy), Math.Max(pos1, pos2)))
                            {
                                records.Add(new CsvRecord
                                {
                                    X_COORD = parts[posx].Trim(),
                                    Y_COORD = parts[posy].Trim(),
                                    CODE1 = parts[pos1].Trim(),
                                    CODE2 = parts[pos2].Trim()
                                });
                            }
                        }
                    }
                }

                // 根据 X_COORD 和 Y_COORD 去除重复项，并更新 CODE1/CODE2
                List<CsvRecord> distinctRecords = new List<CsvRecord>();
                foreach (var record in records)
                {
                    var existingRecord = distinctRecords.FirstOrDefault(d => d.X_COORD == record.X_COORD && d.Y_COORD == record.Y_COORD);
                    if (existingRecord == null)
                    {
                        distinctRecords.Add(record);
                    }
                    else
                    {
                        // 如果找到重复项，更新 CODE1 和 CODE2（Delphi 行为）
                        existingRecord.CODE1 = record.CODE1;
                        existingRecord.CODE2 = record.CODE2;
                    }
                }

                // 构建 MAP 文件内容
                StringBuilder mapFileBuilder = new StringBuilder();
                mapFileBuilder.AppendLine("wafer offset");

                foreach (var record in distinctRecords)
                {
                    if (string.IsNullOrEmpty(record.X_COORD) || string.IsNullOrEmpty(record.Y_COORD) ||
                        string.IsNullOrEmpty(record.CODE1) || string.IsNullOrEmpty(record.CODE2))
                    {
                        continue; // 跳过不完整的记录
                    }

                    mapFileBuilder.AppendLine($"die {record.X_COORD} {record.Y_COORD}");

                    int code1Value;
                    if (int.TryParse(record.CODE1.Split('.').First(), out code1Value))
                    {
                        // 将值限制在 -3 到 3 之间（包括）
                        code1Value = Math.Max(-3, Math.Min(3, code1Value));
                        // int index = code1Value + 3; // 调整到 0-6 索引
                        if (code1Map.ContainsKey(code1Value.ToString()) && code1Map[code1Value.ToString()].Length > 0)
                        {
                            foreach (var val in code1Map[code1Value.ToString()])
                            {
                                if (!string.IsNullOrEmpty(val))
                                {
                                    mapFileBuilder.AppendLine(val);
                                }
                            }
                        }
                    }

                    int code2Value;
                    if (int.TryParse(record.CODE2.Split('.').First(), out code2Value))
                    {
                        // 将值限制在 -3 到 3 之间（包括）
                        code2Value = Math.Max(-3, Math.Min(3, code2Value));
                        // int index = code2Value + 3; // 调整到 0-6 索引
                        if (code2Map.ContainsKey(code2Value.ToString()) && code2Map[code2Value.ToString()].Length > 0)
                        {
                            foreach (var val in code2Map[code2Value.ToString()])
                            {
                                if (!string.IsNullOrEmpty(val))
                                {
                                    mapFileBuilder.AppendLine(val);
                                }
                            }
                        }
                    }
                }

                byte[] fileBytes = System.Text.Encoding.UTF8.GetBytes(mapFileBuilder.ToString());
                
                // 从输出文件名中提取所需部分。
                // 示例: VCE100_V1P0_4SV01_CP1_GMF531.1_GMF531.1-03_.csv -> GMF531.1-03.IN
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(originalFileName);
                string[] nameParts = fileNameWithoutExtension.Split('_');

                string baseMapFileName = fileNameWithoutExtension; // 如果提取失败，默认为完整文件名
                if (nameParts.Length >= 2) 
                {
                    // 所需部分是倒数第二个片段
                    baseMapFileName = nameParts[nameParts.Length - 2]; 
                }
                string mapFileName = baseMapFileName + ".IN";

                return (fileBytes, mapFileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"CSV 到 MAP 转换失败: {ex.Message}");
                return (null, null);
            }
        }

        private Dictionary<string, string[]> ReadIniSection(string sectionName)
        {
            var sectionData = new Dictionary<string, string[]>();
            // 使用 Microsoft.Extensions.Configuration.Ini 库来读取 INI 文件
            var configBuilder = new ConfigurationBuilder()
                .AddIniFile(_iniFilePath, optional: false, reloadOnChange: true);
            var config = configBuilder.Build();

            var section = config.GetSection(sectionName);
            foreach (var child in section.GetChildren())
            {
                if (!string.IsNullOrEmpty(child.Value))
                {
                    sectionData[child.Key] = child.Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                }
            }
            return sectionData;
        }
    }

    // 模拟 Delphi 的 TStringList，简化起见
    public class TStringList : List<string>
    {
        public void Sort()
        {
            base.Sort();
        }
    }

    public static class StringExtensions
    {
        // 相当于 Delphi 的 stringn 函数
        public static int CountOccurrences(this string text, string pattern)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
            {
                return 0;
            }
            int count = 0;
            int i = 0;
            while ((i = text.IndexOf(pattern, i)) != -1)
            {
                i += pattern.Length;
                count++;
            }
            return count;
        }
    }

    public class CsvRecord
    {
        public string X_COORD { get; set; }
        public string Y_COORD { get; set; }
        public string CODE1 { get; set; }
        public string CODE2 { get; set; }
    }
}
