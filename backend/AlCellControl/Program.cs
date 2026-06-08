using Microsoft.EntityFrameworkCore;
using AlCellControl.Data;
using AlCellControl.Services;
using AlCellControl.Events;
using AlCellControl.Commands;
using MediatR;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/alcell-.log", rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Application", "AlCellControl")
    .CreateLogger();

try
{
    Log.Information("Starting AlCellControl service");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    builder.Services.AddApplicationInsightsTelemetry(options =>
    {
        options.ConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
    });

    var connectionStr = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

    builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionStr));

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowAll", policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
        options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(
            new[] { "application/json", "text/html", "text/css", "application/javascript" });
    });

    builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(
        options => options.Level = System.IO.Compression.CompressionLevel.Optimal);

    builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

    builder.Services.AddSingleton<CellBufferService>();
    builder.Services.AddSingleton<ZigBeeReceiver>();
    builder.Services.AddSingleton<ConcentrationEstimator>();
    builder.Services.AddSingleton<AnodeEffectPredictorService>();
    builder.Services.AddSingleton<AlarmOrchestrator>();
    builder.Services.AddSingleton<MqttPublisherService>();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseResponseCompression();
    app.UseCors("AllowAll");
    app.UseStaticFiles();
    app.UseRouting();
    app.MapControllers();

    var mqttPublisher = app.Services.GetRequiredService<MqttPublisherService>();
    await mqttPublisher.InitializeAsync();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
