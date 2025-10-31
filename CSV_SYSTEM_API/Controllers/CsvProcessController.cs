using CSV_SYSTEM_API; 
using CSV_SYSTEM_API.Model; // Add this using statement
using Dapper;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client; 
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions; 
using System.IO.Compression;

namespace CSV_SYSTEM_API.Controllers
{
    [ApiController]
    // 我将暂时注释掉这一行，以排除控制器级别 CORS 策略的干扰。
    // [EnableCors("AllowSpecificOrigin")]
    [Route("[controller]")]
    public class CsvProcessController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<CsvProcessController> _logger;
        private readonly ILogger<CsvDataProcessor> _csvDataProcessorLogger;

        public CsvProcessController(IConfiguration configuration, ILogger<CsvProcessController> logger, ILogger<CsvDataProcessor> csvDataProcessorLogger)
        {
            _configuration = configuration;
            _logger = logger;
            _csvDataProcessorLogger = csvDataProcessorLogger;
        }
        
        [HttpPost("process")]
        public async Task<ActionResult<List<string>>> ProcessCsv([FromBody] ProcessRequest request)
        {
            List<string> generatedFilePaths = new List<string>(); // 新增：存储生成的文件路径列表
            try // 扩展 try 块以覆盖整个方法逻辑
            {
                if (string.IsNullOrEmpty(request.LotId))
                {
                    _logger.LogWarning("LotId 不能为空。");
                    return BadRequest("LotId 不能为空。");
                }

                _logger.LogInformation($"收到请求，LotId: {request.LotId}");

                string lotid = request.LotId.Trim();
                string custid = string.Empty;
                string device = string.Empty;
                string wflot = string.Empty;
                long expectedGrossDie = 0;

                if (lotid.Length < 3)
                {
                    _logger.LogError($"LotId \'{lotid}\' 长度不足3位，无法提取 custid。");
                    return BadRequest($"LotId \'{lotid}\' 长度不足3位，无法提取 custid。");
                }
                custid = lotid.Substring(0, 3);

                string connectionString = _configuration.GetConnectionString("DefaultConnection");
                (device, wflot) = GetDeviceAndWFLot(lotid, connectionString);

                if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(wflot))
                {
                    _logger.LogError($"LotId \'{lotid}\' 未能从数据库中匹配到设备或WF_LOT信息。");
                    return NotFound($"LotId \'{lotid}\' 未能从数据库中匹配到设备或WF_LOT信息。");
                }

                _logger.LogInformation($"查询结果：Device = {device}, WF_LOT = {wflot}");

                if (device.Length < 6)
                {
                    _logger.LogError($"Device \'{device}\' 长度不足6位，无法提取 device_ini。");
                    return BadRequest($"Device \'{device}\' 长度不足6位，无法提取 device_ini。");
                }
                string device_ini = device.Substring(0, 6);
                string grossDieString = _configuration[$"{device_ini}:GrossDie"];
                if (long.TryParse(grossDieString, out expectedGrossDie))
                {
                    _logger.LogInformation($"从 devices.ini 读取到 {device_ini} 的预期 GrossDie: {expectedGrossDie}");
                }
                else
                {
                    _logger.LogWarning($"警告：无法从 devices.ini 读取或解析 {device_ini} 的 GrossDie 值。使用默认值 0。");
                }

                string folderPath = $@"\\10.20.6.14\testdata\Data\{custid}\{device}\{wflot}";
                string outputBaseDirectory = $@"\\10.20.6.14\testdata\Data\{custid}\{device}\{wflot}\Final";

                // 连接共享文件夹
                var folderPassword = _configuration.GetConnectionString("sharedFolderConnectPassword");
                var sharedFolderConnectResult = SharedFolderHelper.Connect(@"\\10.20.6.14\testdata", @"htsh\daizun", $"{folderPassword}");
                try
                {
                    // 检查目录是否存在，如果不存在则创建（包括所有必要的父目录）
                    if (!Directory.Exists(outputBaseDirectory))
                    {
                        Directory.CreateDirectory(outputBaseDirectory);

                    }

                }
                catch (Exception ex)
                {
                    // 捕获可能的异常，比如网络路径不可访问、权限不足等
                    _logger.LogError($"创建目录时出错: {ex.Message}");
                    return StatusCode(500, $"无法连接共享文件夹。请检查网络和凭据。错误信息: {ex.Message}");
                }
                if (!sharedFolderConnectResult.Item1)
                {
                    _logger.LogError($"错误：无法连接共享文件夹 \\\\10.20.6.14\\testdata。错误信息: {sharedFolderConnectResult.Item2}");
                    return StatusCode(500, $"无法连接共享文件夹。请检查网络和凭据。错误信息: {sharedFolderConnectResult.Item2}");
                }

                if (!Directory.Exists(folderPath))
                {
                    _logger.LogError($"错误：指定的文件夹路径 \'{folderPath}\' 不存在。");
                    return NotFound($"指定的文件夹路径 \'{folderPath}\' 不存在。");
                }

                CsvDataProcessor fileGrouper = new CsvDataProcessor(_csvDataProcessorLogger, expectedGrossDie);
                Dictionary<string, List<FileInfo>> groupedFilesToMerge = fileGrouper.GetSortedCsvFiles(folderPath);

                if (groupedFilesToMerge.Count == 0)
                {
                    _logger.LogInformation($"指定文件夹 \'{folderPath}\' 中没有找到需要合并的CSV文件组。");
                    return Ok($"指定文件夹 \'{folderPath}\' 中没有找到需要合并的CSV文件组。");
                }

                _logger.LogInformation($"找到 {groupedFilesToMerge.Count} 个需要合并的CSV文件组。");

                foreach (var groupEntry in groupedFilesToMerge)
                {
                    string mergeKey = groupEntry.Key;
                    List<FileInfo> csvFilesForGroup = groupEntry.Value;

                    _logger.LogInformation($"\n--- 正在处理合并组: {mergeKey} ---");
                    _logger.LogInformation("按生成时间排序的CSV文件：");
                    foreach (var file in csvFilesForGroup)
                    {
                        _logger.LogInformation($"- {file.Name} (创建时间: {file.CreationTime})");
                    }

                    CsvDataProcessor processor = new CsvDataProcessor(_csvDataProcessorLogger, expectedGrossDie);

                    if (!processor.ProcessFilesForMinMaxCoords(csvFilesForGroup))
                    {
                        _logger.LogWarning($"合并组 \'{mergeKey}\' 没有找到有效的坐标数据，无法继续处理。");
                        continue;
                    }

                    _logger.LogInformation("\n开始处理CSV文件并存储数据...");
                    processor.ProcessFilesToConsolidateData(csvFilesForGroup);

                    _logger.LogInformation("\n重新计算并聚合汇总数据...");
                    processor.CalculateAndAggregateSummaryData();

                    string firstFileName = csvFilesForGroup.First().Name;
                    string outputFileName = firstFileName;

                    // 提取文件名详细信息
                    var fileNameDetails = ExtractFileNameDetails(firstFileName);

                    // 根据 custid 决定文件名格式
                    if (custid == "SWL")
                    {
                        string newFileName = $"IO#{fileNameDetails["tp_name"]}#{fileNameDetails["tester_id"]}#{fileNameDetails["probecard_id"]}#CP-0000#OI{fileNameDetails["customer_wafer_id"]}_ALL_{fileNameDetails["utc_enddate_code_date"]}{fileNameDetails["utc_enddate_code_time"]}.csv";
                        outputFileName = newFileName;
                        _logger.LogInformation($"Custid 匹配 SWL，使用新文件名: {outputFileName}");
                    }
                    else
                    {
                         _logger.LogInformation($"Custid 不匹配 SWL，使用原始文件名: {firstFileName}");
                        outputFileName = firstFileName;
                    }

                    if (!string.IsNullOrEmpty(outputBaseDirectory) && !Directory.Exists(outputBaseDirectory))
                    {
                        Directory.CreateDirectory(outputBaseDirectory);
                    }
                    string outputFilePath = Path.Combine(outputBaseDirectory, outputFileName);
                    _logger.LogInformation($"\n正在生成合并后的CSV文件: {outputFilePath}");
                    processor.GenerateConsolidatedCsvFile(outputFilePath);
                    generatedFilePaths.Add(outputFilePath); // 新增：将生成的文件路径添加到列表中

                    _logger.LogInformation($"\n处理完成！合并组 \'{mergeKey}\' 已生成包含{processor.ConsolidatedCoordinateDataCount}条唯一坐标数据的CSV文件。");
                }

                _logger.LogInformation("\n所有合并组处理完毕！");
                string formattedPaths = string.Join("\n", generatedFilePaths); // 使用换行符连接列表中的所有路径
                return Ok($"CSV文件生成完毕，以下是文件路径，请检查：\n{formattedPaths}"); // 修改：返回生成的文件路径列表
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"处理 POST 请求时发生未捕获的错误: {ex.Message}, {ex.StackTrace}");
                return StatusCode(500, $"处理 POST 请求时发生内部服务器错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 压缩指定目录下的所有CSV文件为一个ZIP文件。
        /// ZIP文件命名规则：IO#<TP Name>I<TESTER ID>H<PROBECARD ID>#OIcuStomer LOT ID>-<WAFER ID> ALL.CSV <UTC ENDDATECODE>.Zip
        /// </summary>
        /// <param name="outputBaseDirectory">包含CSV文件的目录路径。</param>
        /// <returns>操作结果。</returns>
        [HttpPost("ZipCsvFiles")]
        public async Task<IActionResult> ZipCsvFiles([FromQuery] string outputBaseDirectory)
        {
            if (string.IsNullOrEmpty(outputBaseDirectory) || !Directory.Exists(outputBaseDirectory))
            {
                return BadRequest("无效的输出目录。");
            }

            try
            {
                var csvFiles = new DirectoryInfo(outputBaseDirectory).GetFiles("*.csv").ToList();
                if (!csvFiles.Any())
                {
                    return Ok($"目录 \'{outputBaseDirectory}\' 中没有找到CSV文件，无需压缩。");
                }

                List<string> zippedFilePaths = new List<string>();

                foreach (var csvFile in csvFiles)
                {
                    string originalFileName = csvFile.Name;
                    string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(originalFileName);
                    string basePart = "";
                    string timePart = "";

                    int allIndex = fileNameWithoutExtension.LastIndexOf("_ALL");
                    if (allIndex != -1)
                    {
                        basePart = fileNameWithoutExtension.Substring(0, allIndex + "_ALL".Length); // 例如: "IO#..._ALL"
                        string remaining = fileNameWithoutExtension.Substring(allIndex + "_ALL".Length); // 例如: "_20250310_4091434"
                        if (remaining.StartsWith("_"))
                        {
                            timePart = remaining.Substring(1); // 移除开头的下划线: "20250310_4091434"
                        } else {
                            timePart = remaining;
                        }
                    }
                    else
                    {
                        // 如果文件名中没有找到 "_ALL"，则作为回退处理
                        basePart = fileNameWithoutExtension;
                        timePart = "";
                    }

                    // 构造新的 ZIP 文件名: "basePart.csv_timePart.zip"
                    string zipFileName = $"{basePart}.csv_{timePart}.zip";

                    string zipFilePath = Path.Combine(outputBaseDirectory, zipFileName);

                    // 检查目标ZIP文件是否已存在，如果存在则删除
                    if (System.IO.File.Exists(zipFilePath))
                    {
                        System.IO.File.Delete(zipFilePath);
                    }

                    using (var zipArchive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create))
                    {
                        zipArchive.CreateEntryFromFile(csvFile.FullName, csvFile.Name, CompressionLevel.Fastest);
                    }
                    zippedFilePaths.Add(zipFilePath);
                    _logger.LogInformation($"成功将文件 '{csvFile.Name}' 压缩到: '{zipFilePath}'");
                }

                string formattedPaths = string.Join("\n", zippedFilePaths);
                _logger.LogInformation($"成功将 {csvFiles.Count} 个CSV文件压缩到单独的ZIP文件: \n{formattedPaths}");
                return Ok($"成功压缩CSV文件到: \n{formattedPaths}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"压缩CSV文件时发生错误: {ex.Message}, {ex.StackTrace}");
                return StatusCode(500, $"压缩CSV文件时发生内部服务器错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 压缩指定目录下的所有 .STD 或 .stdf 文件为一个 .gz 压缩文件。
        /// 压缩文件命名规则：TA31_CP1_KWIC414PR3_ONXQ000_CP_HT_PR2_20250310_5029C54_5029C54_5029C54-14_R0_20250724_091434.STD.gz
        /// 其中 R0 代表的是 RP0。变量命名：TA31->testerId，CP1->modeCode，KWIC414PR3->deviceName
        /// </summary>
        /// <param name="folderPath">包含 .STD 或 .stdf 文件的目录路径。</param>
        /// <returns>操作结果。</returns>
        [HttpPost("GZipStdFiles")]
        public async Task<IActionResult> GZipStdFiles([FromQuery] string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                return BadRequest("无效的输出目录。");
            }

            try
            {
                var stdFiles = new DirectoryInfo(folderPath).GetFiles("*.STD", SearchOption.AllDirectories)
                                        .Concat(new DirectoryInfo(folderPath).GetFiles("*.stdf", SearchOption.AllDirectories))
                                        .ToList();
                if (!stdFiles.Any())
                {
                    return Ok($"目录 '{folderPath}' 中没有找到 .STD 或 .stdf 文件，无需压缩。");
                }

                List<string> zippedFilePaths = new List<string>();

                string finalOutputDirectory = Path.Combine(folderPath, "Final");
                if (!Directory.Exists(finalOutputDirectory))
                {
                    Directory.CreateDirectory(finalOutputDirectory);
                }

                foreach (var file in stdFiles)
                {
                    // 提取文件名详细信息
                    var fileNameDetails = ExtractStdFileNameDetails(file.Name);

                    // 格式化 gz 文件名
                    string gzFileName = FormatStdGzFileName(fileNameDetails);
                    string gzFilePath = Path.Combine(finalOutputDirectory, gzFileName);

                    // 检查目标 .gz 文件是否已存在，如果存在则删除
                    if (System.IO.File.Exists(gzFilePath))
                    {
                        System.IO.File.Delete(gzFilePath);
                    }

                    // 执行 GZip 压缩
                    using (FileStream originalFileStream = file.OpenRead())
                    using (FileStream compressedFileStream = System.IO.File.Create(gzFilePath))
                    using (GZipStream gzipStream = new GZipStream(compressedFileStream, CompressionMode.Compress))
                    {
                        await originalFileStream.CopyToAsync(gzipStream);
                    }
                    zippedFilePaths.Add(gzFilePath);
                    _logger.LogInformation($"成功将文件 '{file.Name}' 压缩到: '{gzFilePath}'");
                }
                
                string formattedPaths = string.Join("\n", zippedFilePaths);
                return Ok($"成功压缩 .STD/.stdf 文件到: \n{formattedPaths}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"压缩 .STD/.stdf 文件时发生错误: {ex.Message}, {ex.StackTrace}");
                return StatusCode(500, $"压缩 .STD/.stdf 文件时发生内部服务器错误: {ex.Message}");
            }
        }

        // 在 CsvProcessController 或类似的控制器中
        [HttpGet("test")]
        public IActionResult Test()
        {
            return Ok("这是一个来自后端的 GET 测试响应！");
        }

        private (string device, string wflot) GetDeviceAndWFLot(string lotId, string connectionString)
        {
            string device = string.Empty;
            string wflot = string.Empty;

            string query = $"SELECT Distinct DEVICE, WF_LOT FROM rtm_admin.rtm_p_data_head WHERE lot_id = :LotId";

            using (OracleConnection connection = new OracleConnection(connectionString))
            {
                connection.Open();
                var result = connection.QueryFirstOrDefault<dynamic>(query, new { LotId = lotId });

                if (result != null)
                {
                    device = result.DEVICE;
                    wflot = result.WF_LOT;
                }
            }
            return (device, wflot);
        }

        /// <summary>
        /// 从原始CSV文件名中提取详细信息，根据下划线拆分并依照次序选择。
        /// 例如：KWIC414PR1__4292730__4292730-02_CP1_RP0_TA30_PU144_ONXQ000_CP_HT_PR1_20250207_20250225145307.csv
        /// </summary>
        /// <param name="fileName">原始文件名。</param>
        /// <returns>包含提取信息的字典。</returns>
        private Dictionary<string, string> ExtractFileNameDetails(string fileName)
        {
            var details = new Dictionary<string, string>();
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            string[] parts = fileNameWithoutExtension.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);

            // 根据示例文件名 `KWIC414PR1__4292730__4292730-02_CP1_RP0_TA30_PU144_ONXQ000_CP_HT_PR1_20250207_20250225145307.csv`
            // 拆分后的索引（假设 StringSplitOptions.RemoveEmptyEntries 移除空字符串）：
            // [0] = KWIC414PR1
            // [1] = 4292730
            // [2] = 4292730-02
            // [3] = CP1
            // [4] = RP0
            // [5] = TA30
            // [6] = PU144
            // [7] = ONXQ000
            // [8] = CP
            // [9] = HT
            // [10] = PR1
            // [11] = 20250207 (日期部分)
            // [12] = 20250225145307 (完整时间戳)

            // 确保数组有足够的长度以避免索引越界
            if (parts.Length > 12)
            {
                // <TP Name> (ONXQ000)
                details["tp_name"] = parts[7];

                // <TESTER ID> (CP) - 原始文件名中可能是 CP1, CP2 等，但目标是 CP
                details["tester_id"] = parts[8].StartsWith("CP", StringComparison.OrdinalIgnoreCase) ? "CP" : "UNKNOWN";

                // <PROBECARD ID> (TA30)
                details["probecard_id"] = parts[5];

                // <cuStomer LOT ID>-<WAFER ID> (4292730-02)
                details["customer_wafer_id"] = parts[2];

                // <UTC ENDDATECODE> (日期和时间)
                details["utc_enddate_code_date"] = parts[11];
                // 从最后一个部分 (parts[12]) 提取时间部分 (后6位)
                string fullTimestamp = parts[12];
                if (fullTimestamp.Length >= 8)
                {
                    details["utc_enddate_code_time"] = fullTimestamp.Substring(7); // 从第8个字符开始（索引7）
                } else {
                    details["utc_enddate_code_time"] = "UNKNOWN";
                }
            } else {
                // 如果文件名的部分不足，则所有字段都设置为 UNKNOWN
                details["tp_name"] = "UNKNOWN";
                details["tester_id"] = "UNKNOWN";
                details["probecard_id"] = "UNKNOWN";
                details["customer_wafer_id"] = "UNKNOWN";
                details["utc_enddate_code_date"] = "UNKNOWN";
                details["utc_enddate_code_time"] = "UNKNOWN";
            }

            return details;
        }

        /// <summary>
        /// 从 .STD 或 .stdf 文件名中提取详细信息。
        /// 示例文件名: KWIC414PR3_5029C54_5029C54-14_CP1_RP0_TA31_PU88_ONXQ000_CP_HT_PR2_20250310_20250724091434.STD
        /// </summary>
        private Dictionary<string, string> ExtractStdFileNameDetails(string fileName)
        {
            var details = new Dictionary<string, string>();
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            string[] parts = fileNameWithoutExtension.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            string originalExtension = Path.GetExtension(fileName); // 重新获取原始扩展名

            details["originalExtension"] = originalExtension; // 存储原始扩展名，不转换为小写

            // 示例文件名解析:
            // [0] = KWIC414PR3 (deviceName)
            // [1] = 5029C54
            // [2] = 5029C54-14
            // [3] = CP1 (modeCode)
            // [4] = RP0 (R0)
            // [5] = TA31 (testerId)
            // [6] = PU88
            // [7] = ONXQ000
            // [8] = CP
            // [9] = HT
            // [10] = PR2
            // [11] = 20250310 (日期部分)
            // [12] = 20250724091434 (时间戳)

            if (parts.Length > 12)
            {
                details["deviceName"] = parts[0]; // KWIC414PR3
                details["LotNo"] = parts[1]; // 5029C54
                details["waferId"] = parts[2]; // 5029C54-14
                details["modeCode"] = parts[3];   // CP1
                details["rpValue"] = parts[4];    // RP0
                details["testerId"] = parts[5];   // TA31
                // details["programPart"] = parts[7]; // ONXQ000 -- 旧的，将被 programName 替换
                // details["datePart"] = parts[11]; // 20250310 -- 旧的，将被 programName 包含

                // programName: ONXQ000_CP_HT_PR2_20250310
                details["programName"] = string.Join("_", parts.Skip(7).Take(5));
                
                // 从完整时间戳中提取日期和时间
                string fullTimestamp = parts[12]; // 20250724091434
                if (fullTimestamp.Length >= 8) // 至少包含 YYYYMMDD
                {
                    details["endDateTime"] = fullTimestamp; // 完整时间戳
                    details["endDate"] = fullTimestamp.Substring(0, 8); // 20250724
                    details["endTime"] = fullTimestamp.Substring(8, 6); // 091434
                } else {
                    details["endDateTime"] = "UNKNOWN";
                    details["endDate"] = "UNKNOWN";
                    details["endTime"] = "UNKNOWN";
                }
            }
            else
            {
                // 如果文件名的部分不足，则所有字段都设置为 UNKNOWN
                details["deviceName"] = "UNKNOWN";
                details["LotNo"] = "UNKNOWN";
                details["waferId"] = "UNKNOWN";
                details["modeCode"] = "UNKNOWN";
                details["rpValue"] = "UNKNOWN";
                details["testerId"] = "UNKNOWN";
                details["programName"] = "UNKNOWN";
                details["endDateTime"] = "UNKNOWN";
                details["endDate"] = "UNKNOWN";
                details["endTime"] = "UNKNOWN";
                details["originalExtension"] = originalExtension; // 确保在错误情况下也设置
            }
            return details;
        }

        /// <summary>
        /// 根据提取的详细信息格式化 .gz 文件名。
        /// 目标格式：TA31_CP1_KWIC414PR3_ONXQ000_CP_HT_PR2_20250310_5029C54_5029C54_5029C54-14_R0_20250724_091434.STD.gz
        /// </summary>
        private string FormatStdGzFileName(Dictionary<string, string> details)
        {
            string testerId = details.GetValueOrDefault("testerId", "UNKNOWN"); // TA31
            string modeCode = details.GetValueOrDefault("modeCode", "UNKNOWN"); // CP1
            string deviceName = details.GetValueOrDefault("deviceName", "UNKNOWN"); // KWIC414PR3
            string programName = details.GetValueOrDefault("programName", "UNKNOWN"); // ONXQ000_CP_HT_PR2_20250310
            string LotNo = details.GetValueOrDefault("LotNo", "UNKNOWN"); // 5029C54
            string waferId = details.GetValueOrDefault("waferId", "UNKNOWN"); // 5029C54-14
            string endDate = details.GetValueOrDefault("endDate", "UNKNOWN"); // 20250724
            string endTime = details.GetValueOrDefault("endTime", "UNKNOWN"); // 091434
            string originalExtension = details.GetValueOrDefault("originalExtension", ".STD"); // 获取原始扩展名

            // RP0 -> R0
            string rpValueFormatted = details.GetValueOrDefault("rpValue", "UNKNOWN").Replace("RP", "R");

            // 构造新的文件名
            // 示例: TA31_CP1_KWIC414PR3_ONXQ000_CP_HT_PR2_20250310_5029C54_5029C54_5029C54-14_R0_20250724_091434.STD.gz
            string formattedFileName = $"{testerId}_{modeCode}_{deviceName}_{programName}_{LotNo}_{LotNo}_{waferId}_{rpValueFormatted}_{endDate}_{endTime}{originalExtension}.gz";

            return formattedFileName;
        }
    }

    public class ProcessRequest
    {
        public string LotId { get; set; }
    }
}
