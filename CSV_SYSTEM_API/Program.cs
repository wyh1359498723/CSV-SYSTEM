using Microsoft.Extensions.Configuration;
using System.IO;
using Serilog; // Add this line

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()); // You can add other sinks here, like .WriteTo.File()

// Add services to the container.
builder.Configuration.SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddIniFile("devices.ini", optional: false, reloadOnChange: true)
    .Build();

builder.Services.AddControllers();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSpecificOrigin",
        builder =>
        {
            builder.WithOrigins("http://localhost:8848", "http://10.20.80.54:8848", "http://10.20.5.21:8081")
                   .AllowAnyHeader()
                   .AllowAnyMethod()
                   .WithExposedHeaders("Content-Disposition"); // 暴露 Content-Disposition 头部
        });
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register CsvDataProcessor for dependency injection
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
           // _logger.LogDebug($"应用程序基目录: {baseDirectory}");
            string biasFilePath = Path.Combine(baseDirectory, "Bias.csv");
           // _logger.LogDebug($"构建的 Bias 文件路径: {biasFilePath}");

            // 将 biasFilePath 添加到配置中，以便 CsvDataProcessor 可以通过 IConfiguration 获取
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
            {
                {"BiasFilePath", biasFilePath}
            });

            builder.Services.AddTransient<CSV_SYSTEM_API.CsvDataProcessor>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<CSV_SYSTEM_API.CsvDataProcessor>>();
                var configuration = provider.GetRequiredService<IConfiguration>(); // 获取 IConfiguration
                // 现在 CsvDataProcessor 构造函数接收 IConfiguration 作为参数
                return new CSV_SYSTEM_API.CsvDataProcessor(logger, configuration);
            });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

//app.UseHttpsRedirection();

app.UseRouting(); // 添加 UseRouting

app.UseCors("AllowSpecificOrigin");

app.UseAuthorization();

app.MapControllers();

app.Run();
