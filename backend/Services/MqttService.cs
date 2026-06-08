using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using Newtonsoft.Json;

namespace AluminumCellControl.Services;

public class MqttService : IHostedService
{
    private IMqttClient? _mqttClient;
    private MqttClientOptions? _mqttOptions;
    private readonly ILogger<MqttService> _logger;
    private readonly IConfiguration _config;
    private bool _isConnected;

    public MqttService(ILogger<MqttService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();

            var broker = _config["Mqtt:Broker"] ?? "localhost";
            var port = int.Parse(_config["Mqtt:Port"] ?? "1883");

            _mqttOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(broker, port)
                .WithClientId($"AluminumCellControl-{Environment.MachineName}")
                .WithCleanSession(true)
                .Build();

            _mqttClient.DisconnectedAsync += async e =>
            {
                _logger.LogWarning("MQTT disconnected: {Reason}", e.Reason);
                _isConnected = false;
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                try
                {
                    await _mqttClient.ConnectAsync(_mqttOptions, cancellationToken);
                    _isConnected = true;
                }
                catch { }
            };

            try
            {
                await _mqttClient.ConnectAsync(_mqttOptions, cancellationToken);
                _isConnected = true;
                _logger.LogInformation("MQTT connected to {Broker}:{Port}", broker, port);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MQTT connection failed: {Message}. Will retry on publish.", ex.Message);
                _isConnected = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize MQTT client");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_mqttClient?.IsConnected == true)
        {
            await _mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
        }
        _mqttClient?.Dispose();
    }

    public async Task PublishAlarmAsync(int cellId, string alarmType, string message)
    {
        var payload = new
        {
            CellId = cellId,
            AlarmType = alarmType,
            Message = message,
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        await PublishAsync("alarm/concentration", payload);
        await PublishAsync($"alarm/cell/{cellId}", payload);
        await PublishAsync("screen/workshop", payload);
        await PublishAsync("dispatch/system", payload);
    }

    public async Task PublishCellStatusAsync(int cellId, object status)
    {
        await PublishAsync($"cell/{cellId}/status", status);
    }

    private async Task PublishAsync(string topic, object payload)
    {
        if (_mqttClient == null) return;

        if (!_mqttClient.IsConnected)
        {
            try
            {
                await _mqttClient.ConnectAsync(_mqttOptions);
                _isConnected = true;
            }
            catch
            {
                _logger.LogWarning("MQTT publish skipped - not connected");
                return;
            }
        }

        var json = JsonConvert.SerializeObject(payload);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"aluminum/{topic}")
            .WithPayload(Encoding.UTF8.GetBytes(json))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        try
        {
            await _mqttClient.PublishAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MQTT publish failed: {Message}", ex.Message);
        }
    }
}
