using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using CSV_SYSTEM_API.Model;
using System.Text.RegularExpressions; 
using Microsoft.Extensions.Logging;

namespace CSV_SYSTEM_API
{
    public class CsvDataProcessor
    {
        private readonly ILogger<CsvDataProcessor> _logger;

        private Dictionary<Tuple<double, double>, Dictionary<string, string>> consolidatedCoordinateData;
        private Dictionary<string, string> standardMetadata;
        private HashSet<string> allHeaders;
        private Dictionary<string, int> headerIndices;
        private List<string> orderedNonCoordinateLineIdentifiers;
        private List<string> orderedCoordinateHeaders;
        private List<string> orderedSecondaryCoordinateHeaders;
        private List<string> secondaryCoordinateHeaderLineContent;

        public double GlobalMinX { get; private set; }
        public double GlobalMaxX { get; private set; }
        public double GlobalMinY { get; private set; }
        public double GlobalMaxY { get; private set; }

        private Dictionary<string, long> aggregatedCounts;
        private Dictionary<int, SBinData> aggregatedSBins;
        private List<double> allAverageTestTimes;
        private List<TimeSpan> allIdleTimes;
        private DateTime? earliestBeginningTime;
        private DateTime? latestEndingTime;
        private List<TimeSpan> allTotalTestingTimes;
        private long _expectedGrossDie;

        public int ConsolidatedCoordinateDataCount => consolidatedCoordinateData.Count;

        public CsvDataProcessor(ILogger<CsvDataProcessor> logger, long expectedGrossDie = 0)
        {
            _logger = logger;
            consolidatedCoordinateData = new Dictionary<Tuple<double, double>, Dictionary<string, string>>();
            standardMetadata = new Dictionary<string, string>();
            allHeaders = new HashSet<string>();
            headerIndices = new Dictionary<string, int>();

            aggregatedCounts = new Dictionary<string, long>();
            aggregatedSBins = new Dictionary<int, SBinData>();
            allAverageTestTimes = new List<double>();
            allIdleTimes = new List<TimeSpan>();
            earliestBeginningTime = null;
            latestEndingTime = null;
            allTotalTestingTimes = new List<TimeSpan>();

            orderedNonCoordinateLineIdentifiers = new List<string>();
            orderedCoordinateHeaders = new List<string>();
            orderedSecondaryCoordinateHeaders = new List<string>();
            secondaryCoordinateHeaderLineContent = new List<string>();

            GlobalMinX = double.MaxValue;
            GlobalMaxX = double.MinValue;
            GlobalMinY = double.MaxValue;
            GlobalMaxY = double.MinValue;
            _expectedGrossDie = expectedGrossDie;
        }

        /// <summary>
        /// 从元数据行中提取一个标识符，用于在合并时作为键。
        /// 简单地使用冒号前的部分作为标识符。
        /// </summary>
        /// <param name="line">元数据行。</param>
        /// <returns>元数据行的标识符。</returns>
        private string GetMetadataIdentifier(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return string.Empty;

            string trimmedLine = line.Trim();

            // 检查特定汇总数据模式
            if (trimmedLine.StartsWith("Total:", StringComparison.OrdinalIgnoreCase)) return "Total";
            if (trimmedLine.StartsWith("Pass:", StringComparison.OrdinalIgnoreCase)) return "Pass";
            if (trimmedLine.StartsWith("Fail:", StringComparison.OrdinalIgnoreCase)) return "Fail";
            if (trimmedLine.StartsWith("Average Test Time (ms):", StringComparison.OrdinalIgnoreCase)) return "Average Test Time (ms)";
            if (trimmedLine.StartsWith("Idle Time:", StringComparison.OrdinalIgnoreCase)) return "Idle Time";
            if (trimmedLine.StartsWith("Beginning Time:", StringComparison.OrdinalIgnoreCase)) return "Beginning Time";
            if (trimmedLine.StartsWith("Ending Time:", StringComparison.OrdinalIgnoreCase)) return "Ending Time";
            if (trimmedLine.StartsWith("Total Testing Time:", StringComparison.OrdinalIgnoreCase)) return "Total Testing Time";

            // 检查 SBin 模式: SBin [X]
            if (trimmedLine.StartsWith("SBin [", StringComparison.OrdinalIgnoreCase))
            {
                int bracketStartIndex = trimmedLine.IndexOf('[');
                int bracketEndIndex = trimmedLine.IndexOf(']');
                if (bracketStartIndex != -1 && bracketEndIndex != -1 && bracketEndIndex > bracketStartIndex)
                {
                    string sbinIdStr = trimmedLine.Substring(bracketStartIndex + 1, bracketEndIndex - bracketStartIndex - 1).Trim();
                    if (int.TryParse(sbinIdStr, out int sbinId))
                    {
                        return $"SBin [{sbinId}]"; // 返回特定的 SBin 标识符
                    }
                }
            }

            // 回退到基于冒号的标准元数据识别
            int colonIndex = trimmedLine.IndexOf(':');
            if (colonIndex != -1)
            {
                return trimmedLine.Substring(0, colonIndex).Trim();
            }

            return trimmedLine; // 如果没有特定的模式或冒号，则使用整行作为标识符
        }

        /// <summary>
        /// 尝试解析 "0 day 1:59:4" 格式的时间字符串为 TimeSpan。
        /// </summary>
        private static bool TryParseTimeSpan(string timeString, out TimeSpan timeSpan)
        {
            timeSpan = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(timeString))
                return false;

            // 1️⃣ 去掉逗号后面的无用内容
            int commaIndex = timeString.IndexOf(',');
            if (commaIndex >= 0)
                timeString = timeString.Substring(0, commaIndex);

            timeString = timeString.Trim();

            // 2️⃣ 用正则提取格式 "X day H:M:S"
            var match = System.Text.RegularExpressions.Regex.Match(
                timeString,
                @"^\s*(\d+)\s+day\s+(\d+):(\d+):(\d+)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );

            if (!match.Success)
                return false;

            if (long.TryParse(match.Groups[1].Value, out long days) &&
                int.TryParse(match.Groups[2].Value, out int hours) &&
                int.TryParse(match.Groups[3].Value, out int minutes) &&
                int.TryParse(match.Groups[4].Value, out int seconds))
            {
                try
                {
                    timeSpan = new TimeSpan((int)days, hours, minutes, seconds);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }


        /// <summary>
        /// 尝试解析 "Pass: 4863 91.69%" 或 "Fail: 441 8.31%" 格式的字符串。
        /// </summary>
        private static bool TryParsePassFail(string passFailString, out long count, out double percentage)
        {
            count = 0;
            percentage = 0.0;
            if (string.IsNullOrWhiteSpace(passFailString)) return false;

            // 移除 "Pass: " 或 "Fail: " 前缀
            string valuePart = passFailString.Split(':', 2).LastOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(valuePart)) return false;

            string[] parts = valuePart.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false; // 预期格式: "COUNT PERCENTAGE%"

            if (!long.TryParse(parts[0], out count)) return false;
            string percentageStr = parts[1].TrimEnd('%');
            if (!double.TryParse(percentageStr, out percentage)) return false;

            return true;
        }

        /// <summary>
        /// 尝试解析 "SBin [1] Pass Default 4863 91.69% 1" 格式的字符串。
        /// </summary>
        private static bool TryParseSBin(string sbinString, out int sbinId, out string description, out long count, out double percentage, out int siteId)
        {
            sbinId = 0;
            description = string.Empty;
            count = 0;
            percentage = 0.0;
            siteId = 0;

            if (string.IsNullOrWhiteSpace(sbinString)) return false;

            // 使用更强大的正则表达式或基于已知模式进行拆分
            // 示例: "SBin[1] Pass Default 4863 91.69% 1"
            var match = System.Text.RegularExpressions.Regex.Match(
                sbinString.Trim(),
                @"^SBin\[\s*(\d+)\s*\]\s+(.*?)\s*(\d+)\s+([\d\.]+)%\s+(\d+)\s*,*$"
            );

            if (match.Success)
            {

                if (!int.TryParse(match.Groups[1].Value, out sbinId)) return false;
                description = match.Groups[2].Value.Trim();
                if (!long.TryParse(match.Groups[3].Value, out count)) return false;
                if (!double.TryParse(match.Groups[4].Value, out percentage)) return false;
                if (!int.TryParse(match.Groups[5].Value, out siteId)) return false;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 处理非坐标数据行（元数据、汇总数据等）。
        /// </summary>
        /// <param name="identifier">数据行的标识符。</param>
        /// <param name="line">数据行内容。</param>
        private void ProcessNonCoordinateLine(string identifier, string line)
        {
            if (string.IsNullOrEmpty(identifier)) return;

            // 根据标识符更新汇总数据
            if (identifier == "Average Test Time (ms)")
            {
                // 从行中提取数字部分，例如 "Average Test Time (ms): 9664"
                string valuePart = line.Split(':', 2).LastOrDefault()?.Trim();
                if (double.TryParse(valuePart, out double averageTime))
                {
                    allAverageTestTimes.Add(averageTime);
                }
            }
            else if (identifier == "Idle Time")
            {
                // 从行中提取时间部分，例如 "Idle Time: 0 day 1:59:4"
                string valuePart = line.Split(':', 2).LastOrDefault()?.Trim();
                if (TryParseTimeSpan(valuePart, out TimeSpan idleTime))
                {
                    allIdleTimes.Add(idleTime);
                }
            }
            else if (identifier == "Beginning Time")
            {
                string valuePart = line.Split(':', 2).LastOrDefault()?.Trim();
                if (DateTime.TryParse(valuePart, out DateTime beginningTime))
                {
                    earliestBeginningTime = earliestBeginningTime.HasValue ? (earliestBeginningTime.Value < beginningTime ? earliestBeginningTime.Value : beginningTime) : beginningTime;
                }
            }
            else if (identifier == "Ending Time")
            {
                string valuePart = line.Split(':', 2).LastOrDefault()?.Trim();
                if (DateTime.TryParse(valuePart, out DateTime endingTime))
                {
                    latestEndingTime = latestEndingTime.HasValue ? (latestEndingTime.Value > endingTime ? latestEndingTime.Value : endingTime) : endingTime;
                }
            }
            else if (identifier == "Total Testing Time")
            {
                string valuePart = line.Split(':', 2).LastOrDefault()?.Trim();
                if (TryParseTimeSpan(valuePart, out TimeSpan totalTestingTime))
                {
                    allTotalTestingTimes.Add(totalTestingTime);
                }
            }
            else if (identifier.StartsWith("SBin["))
            {
                // SBin数据：只存储描述和SiteId，计数将在后面从坐标数据中重新统计
                if (TryParseSBin(line, out int sbinId, out string description, out long _, out double _, out int siteId))
                {
                    // 如果已经存在，更新描述和SiteId（取最新的），计数会在CalculateAndAggregateSummaryData中处理
                    if (aggregatedSBins.ContainsKey(sbinId))
                    {
                        aggregatedSBins[sbinId] = new SBinData(0, description, siteId);
                    }
                    else
                    {
                        aggregatedSBins[sbinId] = new SBinData(0, description, siteId);
                    }
                }
            }
            else
            {
                // 对于标准元数据，直接覆盖
                standardMetadata[identifier] = line;
            }
        }

        /// <summary>
        /// 根据标识符格式化聚合后的汇总数据行。
        /// </summary>
        /// <param name="identifier">汇总数据行的标识符。</param>
        /// <returns>格式化后的汇总数据行字符串。</returns>
        private string FormatAggregatedSummaryLine(string identifier)
        {
            switch (identifier)
            {
                case "Total":
                    {
                        if (aggregatedCounts.TryGetValue(identifier, out long count))
                        {
                            return $"{identifier}: {count}"; // Total只有计数，没有百分比
                        }
                    }
                    break;
                case "Pass":
                case "Fail":
                    {
                        if (aggregatedCounts.TryGetValue(identifier, out long count))
                        {
                            long totalCount = aggregatedCounts.ContainsKey("Total") ? aggregatedCounts["Total"] : 0;
                            double percentage = (totalCount > 0) ? ((double)count / totalCount * 100) : 0.0;
                            return $"{identifier}: {count} {percentage:F2}%";
                        }
                    }
                    break;
                case "Average Test Time (ms)":
                    if (allAverageTestTimes.Any())
                    {
                        return $"{identifier}: {allAverageTestTimes.Average():F0}"; // 四舍五入到最近的整数，与原始保持一致
                    }
                    break;
                case "Idle Time":
                    if (allIdleTimes.Any())
                    {
                        TimeSpan totalIdleTime = new TimeSpan(allIdleTimes.Sum(ts => ts.Ticks));
                        return $"{identifier}: {totalIdleTime.Days} day {totalIdleTime.Hours}:{totalIdleTime.Minutes}:{totalIdleTime.Seconds}";
                    }
                    break;
                case "Beginning Time":
                    if (earliestBeginningTime.HasValue)
                    {
                        return $"{identifier}: {earliestBeginningTime.Value:yyyy-MM-dd HH:mm:ss}";
                    }
                    break;
                case "Ending Time":
                    if (latestEndingTime.HasValue)
                    {
                        return $"{identifier}: {latestEndingTime.Value:yyyy-MM-dd HH:mm:ss}";
                    }
                    break;
                case "Total Testing Time":
                    if (allTotalTestingTimes.Any())
                    {
                        TimeSpan totalTestingTime = new TimeSpan(allTotalTestingTimes.Sum(ts => ts.Ticks));
                        return $"{identifier}: {totalTestingTime.Days} day {totalTestingTime.Hours}:{totalTestingTime.Minutes}:{totalTestingTime.Seconds}";
                    }
                    break;
                default:
                    if (identifier.StartsWith("SBin["))
                    {
                        int bracketStartIndex = identifier.IndexOf('[');
                        int bracketEndIndex = identifier.IndexOf(']');
                        if (bracketStartIndex != -1 && bracketEndIndex != -1 && bracketEndIndex > bracketStartIndex)
                        {
                            string sbinIdStr = identifier.Substring(bracketStartIndex + 1, bracketEndIndex - bracketStartIndex - 1).Trim();
                            if (int.TryParse(sbinIdStr, out int sbinId))
                            {

                                if (aggregatedSBins.TryGetValue(sbinId, out SBinData sbinData))
                                {
                                    long totalCount = aggregatedCounts.ContainsKey("Total") ? aggregatedCounts["Total"] : 0;
                                    double percentage = (totalCount > 0) ? ((double)sbinData.Count / totalCount * 100) : 0.0;

                                    return string.Format(
                                            "SBin[{0,-2}]  {1,-28}  {2,8}  {3,7:F2}%  {4,3}",
                                            sbinId,
                                            sbinData.Description,
                                            sbinData.Count,
                                            percentage,
                                            sbinData.SiteId
                                        );
                                }
                            }
                        }
                    }
                    break;
            }
            return string.Empty; // 如果未找到或无法格式化，则返回空字符串
        }

        /// <summary>
        /// 解析CSV文件名，提取片号、RP值、文件时间戳和合并键。
        /// 例如：KWIC414PR3_5029C54_5029C54-14_CP1_RP0_TA31_PU88_ONXQ000_CP_HT_PR2_20250310_20250724091434.csv
        /// </summary>
        /// <param name="file">原始 FileInfo 对象。</param>
        /// <returns>包含解析信息的 ParsedCsvFileInfo 结构。</returns>
        private ParsedCsvFileInfo ParseFilename(FileInfo file)
        {
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file.Name);
            string[] parts = fileNameWithoutExtension.Split('_');

            string waferId = string.Empty;
            string rpValue = string.Empty;
            DateTime fileTimestamp = file.CreationTime; // 默认使用文件创建时间
            string mergeKey = string.Empty;

            if (parts.Length >= 3)
            {

                waferId = parts[1];


                if (parts[4].StartsWith("RP", StringComparison.OrdinalIgnoreCase))
                {
                    rpValue = parts[4];
                }

                // 时间戳是最后一个部分
                if (parts.Length >= 1)
                {
                    string timestampStr = parts[parts.Length - 1];
                    if (DateTime.TryParseExact(timestampStr, "yyyyMMddHHmmss",
                                               System.Globalization.CultureInfo.InvariantCulture,
                                               System.Globalization.DateTimeStyles.None, out DateTime parsedTimestamp))
                    {
                        fileTimestamp = parsedTimestamp;
                    }
                }

                // 构建 MergeKey: 除RP值和时间戳之外的所有部分
                List<string> mergeKeyParts = new List<string>();
                for (int i = 0; i < parts.Length; i++)
                {
                    // 跳过RP值和时间戳部分
                    if (i == 4 && parts[i].StartsWith("RP", StringComparison.OrdinalIgnoreCase)) continue;
                    if (i == parts.Length - 1) continue;

                    mergeKeyParts.Add(parts[i]);
                }
                mergeKey = string.Join("_", mergeKeyParts);
            }

            return new ParsedCsvFileInfo(file, waferId, rpValue, fileTimestamp, mergeKey);
        }

        /// <summary>
        /// 获取指定文件夹中所有CSV文件，并按创建时间排序。
        /// </summary>
        /// <param name="folderPath">文件夹路径。</param>
        /// <returns>按合并键分组的，且每组内按创建时间排序的 FileInfo 列表的字典。</returns>
        public Dictionary<string, List<FileInfo>> GetSortedCsvFiles(string folderPath)
        {
            DirectoryInfo directoryInfo = new DirectoryInfo(folderPath);
            List<FileInfo> allCsvFiles = directoryInfo.GetFiles("*.csv").ToList();

            var parsedFiles = allCsvFiles.Select(file => ParseFilename(file))
                                         .Where(p => !string.IsNullOrEmpty(p.WaferId) && !string.IsNullOrEmpty(p.MergeKey))
                                         .ToList();

            var groupedFiles = parsedFiles.GroupBy(p => new { p.WaferId, p.MergeKey })
                                          .ToDictionary(g => $"{g.Key.WaferId}_{g.Key.MergeKey}",
                                                        g => g.OrderBy(p => p.FileTimestamp) // 按文件名中的时间戳排序
                                                              .Select(p => p.OriginalFile) // 直接选择原始 FileInfo
                                                              .ToList());

            return groupedFiles;
        }

        /// <summary>
        /// 处理CSV文件，查找 X_COORD 和 Y_COORD 的最大最小值。
        /// </summary>
        /// <param name="csvFiles">按创建时间排序的CSV文件列表。</param>
        /// <returns>如果找到有效的坐标数据，则返回 true；否则返回 false。</returns>
        public bool ProcessFilesForMinMaxCoords(List<FileInfo> csvFiles)
        {
            foreach (var file in csvFiles)
            {

                try
                {
                    using (StreamReader reader = new StreamReader(file.FullName))
                    {
                        string headerLine = null;
                        int xCoordIndex = -1;
                        int yCoordIndex = -1;
                        int linesScanned = 0;
                        const int maxHeaderScanLines = 200; // 最多扫描的行数来查找标题

                        // 寻找标题行
                        string currentLine;
                        while ((currentLine = reader.ReadLine()) != null && linesScanned < maxHeaderScanLines)
                        {
                            linesScanned++;
                            string[] headers = currentLine.Split(',');
                            xCoordIndex = Array.IndexOf(headers, "X_COORD");
                            yCoordIndex = Array.IndexOf(headers, "Y_COORD");

                            if (xCoordIndex != -1 && yCoordIndex != -1)
                            {
                                headerLine = currentLine;
                                break; // 找到标题行，退出循环
                            }
                        }

                        if (string.IsNullOrEmpty(headerLine))
                        {
                            _logger.LogWarning($"警告：文件 {file.Name} 在前 {maxHeaderScanLines} 行中未找到 X_COORD 或 Y_COORD 列的标题，跳过。");
                            continue;
                        }

                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            string[] values = line.Split(',');
                            if (values.Length > xCoordIndex && values.Length > yCoordIndex)
                            {
                                if (double.TryParse(values[xCoordIndex], out double xCoord) &&
                                    double.TryParse(values[yCoordIndex], out double yCoord))
                                {
                                    GlobalMinX = Math.Min(GlobalMinX, xCoord);
                                    GlobalMaxX = Math.Max(GlobalMaxX, xCoord);
                                    GlobalMinY = Math.Min(GlobalMinY, yCoord);
                                    GlobalMaxY = Math.Max(GlobalMaxY, yCoord);
                                }
                                
                            }
                            
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"处理文件 {file.Name} 时发生错误: {ex.Message}");
                }
            }

            if (GlobalMinX == double.MaxValue)
            {
                _logger.LogInformation("\n没有找到有效的坐标数据。");
                return false;
            }
            else
            {
                
                return true;
            }
        }

        /// <summary>
        /// 再次处理CSV文件，这次存储和更新数据。
        /// 此方法现在只负责数据读取和分类，不进行复杂的判断和跳过。
        /// </summary>
        /// <param name="csvFiles">要处理的CSV文件列表。</param>
        public void ProcessFilesToConsolidateData(List<FileInfo> csvFiles)
        {
            // 在开始处理前，清除上一次运行可能留下的数据
            orderedNonCoordinateLineIdentifiers.Clear();
            orderedCoordinateHeaders.Clear();
            allHeaders.Clear();
            headerIndices.Clear();
            secondaryCoordinateHeaderLineContent.Clear();
            orderedSecondaryCoordinateHeaders.Clear();

            standardMetadata.Clear();
            consolidatedCoordinateData.Clear();

            // 这些标志在整个合并文件组的处理过程中保持状态
            bool coordinateHeaderFound = false; // 组级别的标志
            bool secondaryCoordinateHeaderFound = false; // 组级别的标志

            foreach (FileInfo file in csvFiles.OrderBy(f => f.CreationTime))
            {
                using (StreamReader reader = new StreamReader(file.FullName))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue; // 跳过所有空白行

                        // 1. 尝试识别主坐标标题行
                        if (!coordinateHeaderFound && line.Contains("X_COORD") && line.Contains("Y_COORD"))
                        {
                            string[] currentFileHeaders = line.Split(',');
                            // 第一次找到坐标头时，记录其顺序并设置 headerIndices
                            orderedCoordinateHeaders.AddRange(currentFileHeaders.Select(h => h.Trim()));
                            coordinateHeaderFound = true; // 设置组级别的标志

                            headerIndices = Enumerable.Range(0, currentFileHeaders.Length)
                                                      .ToDictionary(i => currentFileHeaders[i].Trim(), i => i);
                            // 将所有标题添加到allHeaders以确保最终输出包含所有列
                            foreach (string header in currentFileHeaders)
                            {
                                allHeaders.Add(header.Trim());
                            }
                            continue; // 跳过此行
                        }
                        // 如果主坐标标题行已经找到（来自当前文件或之前的文件），但再次遇到另一个标题行
                        else if (coordinateHeaderFound && line.Contains("X_COORD") && line.Contains("Y_COORD"))
                        {
                            string[] currentFileHeaders = line.Split(',');
                            // 仅更新 allHeaders，不改变 orderedCoordinateHeaders 和 headerIndices
                            foreach (string header in currentFileHeaders)
                            {
                                allHeaders.Add(header.Trim());
                            }
                            continue; // 跳过此行
                        }

                        // 2. 尝试识别辅助坐标标题行 (Unit, LimitL, LimitU)
                        // 只有在主坐标标题行已找到且辅助标题行未找到时才检查
                        if (coordinateHeaderFound && !secondaryCoordinateHeaderFound)
                        {
                            // 检查当前行是否匹配任何辅助标题模式
                            bool isSecondaryHeader = (line.Contains("Unit") || line.Contains("LimitL") || line.Contains("LimitU") || System.Text.RegularExpressions.Regex.IsMatch(line, @"Bias \d+")) && !line.StartsWith("SBin[");

                            if (isSecondaryHeader)
                            {
                                secondaryCoordinateHeaderLineContent.Add(line); // 存储完整的辅助标题行
                                orderedSecondaryCoordinateHeaders.AddRange(line.Split(',').Select(h => h.Trim()));
                                continue; // 跳过此行，因为它是一个辅助标题行
                            }
                            else
                            {
                                // 如果我们正在寻找辅助标题行，但当前行不是辅助标题行，
                                // 那么所有辅助标题行都应该已经读取完毕。
                                secondaryCoordinateHeaderFound = true;
                                // 注意：这里不会 `continue`。当前行（它不是辅助标题）将被视为第一行数据，
                                // 并会继续由下面的坐标数据处理或非坐标行处理逻辑来处理。
                            }
                        }

                        // 3. 处理坐标数据行
                        // 只有在主坐标标题行和辅助标题行都已找到且 headerIndices 已设置时才处理
                        if (coordinateHeaderFound && secondaryCoordinateHeaderFound && headerIndices.Any())
                        {
                            string[] values = line.Split(',');
                            if (!headerIndices.ContainsKey("X_COORD") || !headerIndices.ContainsKey("Y_COORD"))
                            {
                                _logger.LogWarning($"警告：文件 '{file.Name}' 中未找到X_COORD或Y_COORD列。跳过该文件。");
                                break; // 跳过该文件
                            }

                            // 在尝试访问值之前检查数组边界
                            if (values.Length <= headerIndices["X_COORD"] || values.Length <= headerIndices["Y_COORD"])
                            {
                                continue; // 跳过此行
                            }

                            if (double.TryParse(values[headerIndices["X_COORD"]], out double x) &&
                                double.TryParse(values[headerIndices["Y_COORD"]], out double y))
                            {
                                Tuple<double, double> coord = Tuple.Create(x, y);
                                Dictionary<string, string> rowData = new Dictionary<string, string>();
                                
                                // 使用 headerIndices 来填充 rowData，确保按正确的标题顺序和索引填充
                                foreach (var headerEntry in headerIndices)
                                {
                                    if (headerEntry.Value < values.Length)
                                    {
                                        rowData[headerEntry.Key] = values[headerEntry.Value];
                                    }
                                }
                                consolidatedCoordinateData[coord] = rowData;
                            }
                            
                        }
                        // 4. 处理非坐标行 (在主标题行未找到之前，或在坐标数据区域中遇到非数据行)
                        else 
                        {
                            string identifier = GetMetadataIdentifier(line);
                            if (!string.IsNullOrEmpty(identifier))
                            {
                                // 确保标识符只添加一次
                                if (!orderedNonCoordinateLineIdentifiers.Contains(identifier))
                                {
                                    orderedNonCoordinateLineIdentifiers.Add(identifier);
                                }
                                ProcessNonCoordinateLine(identifier, line);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 重新计算并聚合汇总数据。
        /// </summary>
        public void CalculateAndAggregateSummaryData()
        {
            // 清除之前的聚合数据以便重新计算
            aggregatedCounts.Clear();
            // SBinData 描述和SiteId 不清除，只清除Count
            foreach (var key in aggregatedSBins.Keys.ToList())
            {
                var sbinData = aggregatedSBins[key];
                aggregatedSBins[key] = new SBinData(0, sbinData.Description, sbinData.SiteId);
            }

            // 初始化计数
            aggregatedCounts["Total"] = 0;
            aggregatedCounts["Pass"] = 0;
            aggregatedCounts["Fail"] = 0;

            // 遍历合并后的坐标数据，重新统计
            foreach (var entry in consolidatedCoordinateData.Values)
            {
                aggregatedCounts["Total"]++; // 每行坐标数据都计入Total

                if (entry.TryGetValue("SOFT_BIN", out string softBinValue) && int.TryParse(softBinValue, out int softBinId))
                {
                    if (softBinId == 1) // 假设 SBin 1 是 Pass Default
                    {
                        aggregatedCounts["Pass"]++;
                    }
                    else
                    {
                        aggregatedCounts["Fail"]++;
                    }

                    // 统计 SBin [X] 的数量
                    if (aggregatedSBins.ContainsKey(softBinId))
                    {
                        var sbinData = aggregatedSBins[softBinId];
                        aggregatedSBins[softBinId] = new SBinData(sbinData.Count + 1, sbinData.Description, sbinData.SiteId);
                    }
                    else
                    {
                        // 如果 SBin ID 是新的，添加到 aggregatedSBins 中，描述和 SiteId 暂时为空或默认值
                        // 实际的描述和 SiteId 会在 ProcessNonCoordinateLine 中设置
                        aggregatedSBins[softBinId] = new SBinData(1, string.Empty, 0);
                    }
                }
            }

            // 验证 Total 值与 expectedGrossDie 是否相符
            if (_expectedGrossDie > 0 && aggregatedCounts.TryGetValue("Total", out long totalCount))
            {
                if (totalCount != _expectedGrossDie)
                {
                    _logger.LogWarning($"警告：汇总数据的 Total 值 ({totalCount}) 与 devices.ini 中的预期 GrossDie ({_expectedGrossDie}) 不符！");
                    // Environment.Exit(1); // Remove this, as we don't want to exit the entire web application
                    // You might want to throw an exception or return an error status here in a real API
                }
                else
                {
                    _logger.LogInformation($"验证成功：汇总数据的 Total 值 ({totalCount}) 与 devices.ini 中的预期 GrossDie ({_expectedGrossDie}) 相符。");
                }
            }
        }

        /// <summary>
        /// 生成合并后的CSV文件。
        /// </summary>
        /// <param name="outputFilePath">输出文件的完整路径。</param>
        public void GenerateConsolidatedCsvFile(string outputFilePath)
        {
            using (StreamWriter writer = new StreamWriter(outputFilePath))
            {
                //  写入非坐标行数据（包括标准元数据和汇总数据）
                foreach (string identifier in orderedNonCoordinateLineIdentifiers)
                {
                    string formattedLine = FormatAggregatedSummaryLine(identifier);
                    if (!string.IsNullOrEmpty(formattedLine))
                    {
                        writer.WriteLine(formattedLine);
                    }
                    else if (standardMetadata.TryGetValue(identifier, out string metadataLine))
                    {
                        // 如果是标准元数据，直接写入最新的值
                        writer.WriteLine(metadataLine);
                    }
                    else
                    {
                        // 如果是非坐标行的标识符，但没有对应的聚合或标准元数据，则写入原始空行，
                        // 这种情况通常发生在 ProcessNonCoordinateLine 无法识别并填充的行
                        writer.WriteLine(identifier); // 写入原始标识符，因为它在 orderedNonCoordinateLineIdentifiers 中
                    }
                }

                writer.WriteLine();
                writer.WriteLine();
                writer.WriteLine();

                //  写入坐标列标题
                List<string> finalCoordinateHeaders = new List<string>(orderedCoordinateHeaders);
                foreach (string header in allHeaders)
                {
                    if (!finalCoordinateHeaders.Contains(header))
                    {
                        finalCoordinateHeaders.Add(header);
                    }
                }
                writer.WriteLine(string.Join(",", finalCoordinateHeaders));

                // 写入辅助坐标标题行 (Unit, LimitL, LimitU)
                if (secondaryCoordinateHeaderLineContent.Count > 0)
                {
                    foreach (var sline in secondaryCoordinateHeaderLineContent)
                    {
                        writer.WriteLine(sline);
                    }
                    
                }

                writer.WriteLine(); // 添加一个空行分隔标题和数据

                // 写入合并后的坐标数据
                // 按照X_COORD, Y_COORD排序输出
                foreach (var entry in consolidatedCoordinateData.OrderBy(e => e.Key.Item1).ThenBy(e => e.Key.Item2))
                {
                    // 构建一行数据，确保所有列都存在，不存在的填充为空字符串
                    List<string> rowValues = new List<string>();
                    foreach (string header in finalCoordinateHeaders)
                    {
                        if (entry.Value.TryGetValue(header, out string value))
                        {
                            rowValues.Add(value);
                        }
                        else
                        {
                            rowValues.Add(string.Empty);
                        }
                    }
                    writer.WriteLine(string.Join(",", rowValues));
                }
            }
        }
    }
}

