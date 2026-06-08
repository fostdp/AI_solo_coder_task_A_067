using AluminumCellControl.Data;
using AluminumCellControl.Hubs;
using AluminumCellControl.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Server=localhost;Database=AluminumCellControl;Trusted_Connection=True;TrustServerCertificate=True;"));

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = null;
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

builder.Services.AddSingleton<SvrConcentrationModel>();
builder.Services.AddSingleton<RandomForestEffectModel>();
builder.Services.AddSingleton<DataBufferService>();
builder.Services.AddSingleton<MqttService>();
builder.Services.AddScoped<ConcentrationService>();
builder.Services.AddScoped<PredictionService>();
builder.Services.AddScoped<AlarmService>();
builder.Services.AddScoped<FeedingService>();
builder.Services.AddScoped<DataProcessingService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttService>());

var app = builder.Build();

app.UseCors();

app.UseStaticFiles();

app.UseRouting();

app.MapControllers();
app.MapHub<CellHub>("/hubs/cell");

app.MapFallbackToFile("index.html");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        db.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Database migration skipped - ensure SQL Server is available");
    }
}

app.Run();
