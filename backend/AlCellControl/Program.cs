using Microsoft.EntityFrameworkCore;
using AlCellControl.Data;
using AlCellControl.Services;
using AlCellControl.Events;
using AlCellControl.Commands;
using MediatR;

var builder = WebApplication.CreateBuilder(args);

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

app.UseCors("AllowAll");
app.UseStaticFiles();
app.UseRouting();
app.MapControllers();

var mqttPublisher = app.Services.GetRequiredService<MqttPublisherService>();
await mqttPublisher.InitializeAsync();

app.Run();
