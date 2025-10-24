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

namespace CSV_SYSTEM_API.Controllers
{
    [ApiController]
    [EnableCors("AllowSpecificOrigin")]
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
        public async Task<IActionResult> ProcessCsv([FromBody] ProcessRequest request)
        {
            if (string.IsNullOrEmpty(request.LotId))
            {
                _logger.LogWarning("LotId 不能为空。");
                return BadRequest("LotId 不能为空。");
            }

            _logger.LogInformation($"收到请求，LotId: {request.LotId}");

            string lotid = request.LotId.Trim();
            string custid = lotid.Substring(0, 3);
            string device = string.Empty;
            string wflot = string.Empty;
            long expectedGrossDie = 0;

            try
            {
                string connectionString = _configuration.GetConnectionString("DefaultConnection");
                (device, wflot) = GetDeviceAndWFLot(lotid, connectionString);
                _logger.LogInformation($"查询结果：Device = {device}, WF_LOT = {wflot}");

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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"数据库查询或配置读取发生错误: {ex.Message}");
                return StatusCode(500, $"处理过程中发生错误: {ex.Message}");
            }

            string folderPath = $@"\\10.20.6.14\testdata\Data\{custid}\{device}\{wflot}";
            string outputBaseDirectory = $@"\\10.20.6.14\testdata\Data\{custid}\{device}\{wflot}\Final";

            if (!Directory.Exists(folderPath))
            {
                _logger.LogError($"错误：指定的文件夹路径 '{folderPath}' 不存在。");
                return NotFound($"指定的文件夹路径 '{folderPath}' 不存在。");
            }

            CsvDataProcessor fileGrouper = new CsvDataProcessor(_csvDataProcessorLogger, expectedGrossDie);
            Dictionary<string, List<FileInfo>> groupedFilesToMerge = fileGrouper.GetSortedCsvFiles(folderPath);

            if (groupedFilesToMerge.Count == 0)
            {
                _logger.LogInformation($"指定文件夹 '{folderPath}' 中没有找到需要合并的CSV文件组。");
                return Ok($"指定文件夹 '{folderPath}' 中没有找到需要合并的CSV文件组。");
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
                    _logger.LogWarning($"合并组 '{mergeKey}' 没有找到有效的坐标数据，无法继续处理。");
                    continue;
                }

                _logger.LogInformation("\n开始处理CSV文件并存储数据...");
                processor.ProcessFilesToConsolidateData(csvFilesForGroup);

                _logger.LogInformation("\n重新计算并聚合汇总数据...");
                processor.CalculateAndAggregateSummaryData();

                string firstFileName = csvFilesForGroup.First().Name;
                string outputFileName = firstFileName;
                if (!string.IsNullOrEmpty(outputBaseDirectory) && !Directory.Exists(outputBaseDirectory))
                {
                    Directory.CreateDirectory(outputBaseDirectory);
                }
                string outputFilePath = Path.Combine(outputBaseDirectory, outputFileName);
                _logger.LogInformation($"\n正在生成合并后的CSV文件: {outputFilePath}");
                processor.GenerateConsolidatedCsvFile(outputFilePath);

                _logger.LogInformation($"\n处理完成！合并组 '{mergeKey}' 已生成包含{processor.ConsolidatedCoordinateDataCount}条唯一坐标数据的CSV文件。");
            }

            _logger.LogInformation("\n所有合并组处理完毕！");
            return Ok("CSV文件处理完成。");
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
    }

    public class ProcessRequest
    {
        public string LotId { get; set; }
    }
}
