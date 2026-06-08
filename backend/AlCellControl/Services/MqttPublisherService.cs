using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;

namespace AlCellControl.Services;

public record AlarmMessage(int CellId, int AlarmLevel, string AlarmType, string Message, DateTime Timestamp);

public class MqttPublisherService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MqttPublisherService> _logger;
    private readonly IMqttClient _mqttClient;
    private readonly string _host;
    private readonly int _port;
    private readonly string _topicPrefix;
    private bool _disposed;

    public MqttPublisherService(IConfiguration configuration, ILogger<MqttPublisherService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _host = _configuration["Mqtt:Host"] ?? "localhost";
        _port = int.TryParse(_configuration["Mqtt:Port"], out var port) ? port : 1883;
        _topicPrefix = _configuration["Mqtt:TopicPrefix"] ?? "alcell";

        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();
    }

    public async Task InitializeAsync()
    {
        try
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_host, _port)
                .WithCleanSession(true)
                .Build();

            _mqttClient.DisconnectedAsync += async e =>
            {
                _logger.LogWarning("MQTT client disconnected: {Reason}", e.Reason);
                if (!e.ClientWasConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    try
                    {
                        await _mqttClient.ConnectAsync(options);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "MQTT reconnection failed");
                    }
                }
            };

            await _mqttClient.ConnectAsync(options);
            _logger.LogInformation("MQTT client connected to {Host}:{Port}", _host, _port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to MQTT broker at {Host}:{Port}", _host, _port);
        }
    }

    public async Task PublishAlarmAsync(AlarmMessage msg)
    {
        try
        {
            if (!_mqttClient.IsConnected)
            {
                await InitializeAsync();
            }

            var topic = $"{_topicPrefix}/alarms/{msg.AlarmLevel}";
            var payload = JsonSerializer.Serialize(msg);
            var applicationMessage = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.PublishAsync(applicationMessage);
            _logger.LogInformation("Published alarm to {Topic}: {Payload}", topic, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish alarm for cell {CellId}", msg.CellId);
        }
    }

    public async Task PublishCellStatusAsync(int cellId, string status)
    {
        try
        {
            if (!_mqttClient.IsConnected)
            {
                await InitializeAsync();
            }

            var topic = $"{_topicPrefix}/status/{cellId}";
            var payload = JsonSerializer.Serialize(new { CellId = cellId, Status = status, Timestamp = DateTime.UtcNow });
            var applicationMessage = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.PublishAsync(applicationMessage);
            _logger.LogInformation("Published cell status to {Topic}: {Payload}", topic, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish status for cell {CellId}", cellId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_mqttClient.IsConnected)
            {
                _mqttClient.DisconnectAsync().GetAwaiter().GetResult();
            }
            _mqttClient.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing MQTT client");
        }
    }
}
