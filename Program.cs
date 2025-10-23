using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Oracle.ManagedDataAccess.Client; // Added for Oracle database operations
using Dapper; // Added for Dapper
using Microsoft.Extensions.Configuration; // Added for configuration
// using System.Runtime.InteropServices; // Removed for INI file reading

namespace CSV_SYSTEM
{
    internal class Program
    {
        // Removed DllImport for GetPrivateProfileString
        // [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        // private static extern int GetPrivateProfileString(
        //     string lpAppName,
        //     string lpKeyName,
        //     string lpDefault,
        //     System.Text.StringBuilder lpReturnedString,
        //     int nSize,
        //     string lpFileName
        // );

        static void Main(string[] args)
        {
            // 构建配置
            IConfiguration configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddIniFile("devices.ini", optional: false, reloadOnChange: true) // Added for INI file
                .Build();

            string connectionString = configuration.GetConnectionString("DefaultConnection");

            Console.WriteLine("请输入要进行合并的CSV对应工卡号：");
            string lotid = Console.ReadLine()?.Trim();
            string custid = lotid.Substring(0, 3);
            string device = "";
            string wflot = "";

            long expectedGrossDie = 0; // 初始化预期 GrossDie

            try
            {
                (device, wflot) = GetDeviceAndWFLot(lotid, connectionString);
                Console.WriteLine($"查询结果：Device = {device}, WF_LOT = {wflot}");

                // 从配置中读取 devices.ini 获取预期 GrossDie
                string device_ini = device.Substring(0, 6);
                string grossDieString = configuration[$"{device_ini}:GrossDie"];
                if (long.TryParse(grossDieString, out expectedGrossDie))
                {
                    Console.WriteLine($"从 devices.ini 读取到 {device_ini} 的预期 GrossDie: {expectedGrossDie}");
                }
                else
                {
                    Console.WriteLine($"警告：无法从 devices.ini 读取或解析 {device_ini} 的 GrossDie 值。使用默认值 0。");
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"数据库查询或配置读取发生错误: {ex.Message}");
                return; // 发生错误时退出程序
            }

            string folderPath = $@"\\10.20.6.14\testdata\Data\{custid}\{device}\{wflot}"; // CSV文件所在的文件夹路径
            string outputBaseDirectory = $@"\\10.20.6.14\testdata\Data\{custid}\{device}\{wflot}\Final"; // 输出文件基础目录

            // 检查文件夹是否存在
            if (!Directory.Exists(folderPath))
            {

                Console.WriteLine($"错误：指定的文件夹路径 '{folderPath}' 不存在。");
                return;
            }

            

            CsvDataProcessor fileGrouper = new CsvDataProcessor();
            Dictionary<string, List<FileInfo>> groupedFilesToMerge = fileGrouper.GetSortedCsvFiles(folderPath);

            if (groupedFilesToMerge.Count == 0)
            {
                Console.WriteLine($"指定文件夹 '{folderPath}' 中没有找到需要合并的CSV文件组。");
                return;
            }

            Console.WriteLine($"找到 {groupedFilesToMerge.Count} 个需要合并的CSV文件组。");

            foreach (var groupEntry in groupedFilesToMerge)
            {
                string mergeKey = groupEntry.Key;
                List<FileInfo> csvFilesForGroup = groupEntry.Value;

                Console.WriteLine($"\n--- 正在处理合并组: {mergeKey} ---");
                Console.WriteLine("按生成时间排序的CSV文件：");
                foreach (var file in csvFilesForGroup)
                {
                    Console.WriteLine($"- {file.Name} (创建时间: {file.CreationTime})");
                }

                // 为每个合并组创建一个新的处理器实例，传入 expectedGrossDie
                CsvDataProcessor processor = new CsvDataProcessor(expectedGrossDie);

                // 首先处理CSV文件获取X和Y的最大最小值
                if (!processor.ProcessFilesForMinMaxCoords(csvFilesForGroup))
                {
                    Console.WriteLine($"合并组 '{mergeKey}' 没有找到有效的坐标数据，无法继续处理。");
                    continue; // 跳过当前组，处理下一个组
                }

                Console.WriteLine("\n使用获取的坐标边界进行初始化...");
                Console.WriteLine($"坐标边界: X [{processor.GlobalMinX}, {processor.GlobalMaxX}], Y [{processor.GlobalMinY}, {processor.GlobalMaxY}]");

                // 再次处理CSV文件，这次存储和更新数据
                Console.WriteLine("\n开始处理CSV文件并存储数据...");
                processor.ProcessFilesToConsolidateData(csvFilesForGroup);

                // 重新计算并聚合汇总数据
                Console.WriteLine("\n重新计算并聚合汇总数据...");
                processor.CalculateAndAggregateSummaryData();

                // 生成合并后的CSV文件
                // 使用文件组中第一个（最旧的）文件的名称作为输出文件名
                string firstFileName = csvFilesForGroup.First().Name;
                string outputFileName = firstFileName; // 直接使用最旧的文件名
                if (!string.IsNullOrEmpty(outputBaseDirectory) && !Directory.Exists(outputBaseDirectory))
                {
                    Directory.CreateDirectory(outputBaseDirectory); // 递归创建所有需要的目录
                }
                string outputFilePath = Path.Combine(outputBaseDirectory, outputFileName);
                Console.WriteLine($"\n正在生成合并后的CSV文件: {outputFilePath}");
                processor.GenerateConsolidatedCsvFile(outputFilePath);

                Console.WriteLine($"\n处理完成！合并组 '{mergeKey}' 已生成包含{processor.ConsolidatedCoordinateDataCount}条唯一坐标数据的CSV文件。");
            }
            Console.WriteLine("\n所有合并组处理完毕！");
        }

        // 新增数据库查询方法
        static (string device, string wflot) GetDeviceAndWFLot(string lotId, string connectionString)
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

        // Removed ReadGrossDieFromIni method
        // private static long ReadGrossDieFromIni(string deviceName, string iniFilePath)
        // {
        //     System.Text.StringBuilder temp = new System.Text.StringBuilder(255);
        //     long grossDie = 0;

        //     Console.WriteLine($"调试信息：尝试从INI文件读取。deviceName: '{deviceName}', iniFilePath: '{iniFilePath}'");

        //     // 尝试读取特定设备配置
        //     int bytesRead = GetPrivateProfileString(deviceName, "GrossDie", "0", temp, 255, iniFilePath);
            
        //     Console.WriteLine($"调试信息：GetPrivateProfileString 返回值 bytesRead: {bytesRead}");
        //     Console.WriteLine($"调试信息：GetPrivateProfileString 读取到的原始值: '{temp.ToString()}'");

        //     // 如果读取成功且值为有效数字，则返回
        //     if (bytesRead > 0 && long.TryParse(temp.ToString(), out grossDie))
        //     {
        //         Console.WriteLine($"调试信息：成功解析 GrossDie: {grossDie}");
        //         return grossDie;
        //     }
        //     Console.WriteLine("调试信息：未成功读取或解析 GrossDie。");
        //     return 0; // 如果特定设备没有配置或者配置无效，则返回0
        // }
    }
}