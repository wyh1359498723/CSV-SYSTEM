# CSV-SYSTEM

这是一个用于处理 CSV 文件的系统。它提供了一个 API 来上传、处理和管理 CSV 数据。

## 设置

### 先决条件

- .NET SDK 8.0
- Oracle 客户端（如果需要连接 Oracle 数据库）

### 构建和运行

1.  克隆仓库：

    ```bash
    git clone <仓库URL>
    cd CSV-SYSTEM
    ```

2.  导航到 API 项目：

    ```bash
    cd CSV_SYSTEM_API
    ```

3.  还原 NuGet 包：

    ```bash
    dotnet restore
    ```

4.  构建项目：

    ```bash
    dotnet build
    ```

5.  运行项目：

    ```bash
    dotnet run
    ```

    API 将在配置的地址上运行（通常是 `https://localhost:7001` 或 `http://localhost:5001`）。

## API 参考

### `CsvProcessController`

此控制器处理所有与 CSV 文件处理相关的操作。

#### 端点

-   `POST /api/CsvProcess/UploadAndProcessCsv`
    -   上传 CSV 文件并启动处理过程。
    -   **请求体:** `multipart/form-data`，包含一个名为 `file` 的文件。
    -   **响应:** 包含处理状态和结果。

-   `GET /api/CsvProcess/GetProcessedData/{id}`
    -   根据处理 ID 检索已处理的数据。
    -   **参数:** `id` (string) - 处理任务的唯一标识符。
    -   **响应:** 已处理的数据或错误消息。

-   `GET /api/CsvProcess/GetProcessingStatus/{id}`
    -   检查特定处理任务的状态。
    -   **参数:** `id` (string) - 处理任务的唯一标识符。
    -   **响应:** 处理任务的当前状态（例如，`Processing`，`Completed`，`Failed`）。

## 配置

配置文件（`appsettings.json` 和 `appsettings.Development.json`）包含数据库连接字符串和其他设置。

以下是 `appsettings.json` 的预期结构示例：

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "您的数据库连接字符串"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Serilog": {
    "Using": ["Serilog.Sinks.File"],
    "MinimumLevel": "Information",
    "WriteTo": [
      {
        "Name": "File",
        "Args": {
          "path": "./logs/log-.txt",
          "rollingInterval": "Day",
          "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
        }
      }
    ],
    "Enrich": ["FromLogContext", "WithMachineName", "WithProcessId", "WithThreadId"],
    "Destructure": [
      {
        "Name": "With"
      }
    ]
  },
  "AllowedHosts": "*"
}
```

## 日志

日志文件位于 `CSV_SYSTEM_API/logs/` 目录下。
