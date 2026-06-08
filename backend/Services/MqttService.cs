using System.Collections.Concurrent;
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

    private enum MqttPriority
    {
        Critical = 0,
        High = 1,
        Normal = 2,
        Low = 3
    }

    private record MqttMessage(string Topic, string Payload, MqttPriority Priority, DateTime EnqueueTime);

    private readonly PriorityQueue<MqttMessage, (int Priority, DateTime EnqueueTime)> _messageQueue = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private CancellationTokenSource? _queueCts;
    private Task? _queueProcessorTask;

    private readonly ConcurrentDictionary<string, DateTime> _topicLastSendTime = new();
    private static readonly TimeSpan CriticalMinInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HighMinInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan NormalMinInterval = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan LowMinInterval = TimeSpan.FromMilliseconds(5000);

    private const int MaxQueueSize = 500;
    private const int MaxDedupWindowMs = 3000;

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

            _queueCts = new CancellationTokenSource();
            _queueProcessorTask = Task.Run(() => ProcessQueueAsync(_queueCts.Token));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize MQTT client");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queueCts?.Cancel();

        if (_queueProcessorTask != null)
        {
            try { await _queueProcessorTask; } catch { }
        }

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

        var json = JsonConvert.SerializeObject(payload);

        Enqueue($"alarm/cell/{cellId}", json, MqttPriority.Critical);
        Enqueue("alarm/concentration", json, MqttPriority.High);
        Enqueue("screen/workshop", json, MqttPriority.High);
        Enqueue("dispatch/system", json, MqttPriority.Normal);

        await Task.CompletedTask;
    }

    public async Task PublishCellStatusAsync(int cellId, object status)
    {
        var json = JsonConvert.SerializeObject(status);
        Enqueue($"cell/{cellId}/status", json, MqttPriority.Low);
        await Task.CompletedTask;
    }

    public async Task PublishEffectQuenchAsync(int cellId, string action)
    {
        var payload = new
        {
            CellId = cellId,
            Action = action,
            Timestamp = DateTime.UtcNow.ToString("O")
        };
        var json = JsonConvert.SerializeObject(payload);
        Enqueue($"effect/quench/{cellId}", json, MqttPriority.Critical);
        Enqueue("screen/workshop", json, MqttPriority.Critical);
        await Task.CompletedTask;
    }

    private void Enqueue(string topic, string payload, MqttPriority priority)
    {
        lock (_queueLock)
        {
            if (_messageQueue.Count >= MaxQueueSize)
            {
                while (_messageQueue.Count > MaxQueueSize * 3 / 4)
                {
                    _messageQueue.TryDequeue(out _, out _);
                }
                _logger.LogWarning("MQTT queue overflow, dropped low-priority messages");
            }

            var dedupKey = $"{topic}:{payload.GetHashCode()}";
            var now = DateTime.UtcNow;
            if (_topicLastSendTime.TryGetValue(dedupKey, out var lastSent))
            {
                var minInterval = priority switch
                {
                    MqttPriority.Critical => CriticalMinInterval,
                    MqttPriority.High => HighMinInterval,
                    _ => NormalMinInterval
                };

                if (now - lastSent < minInterval) return;
            }

            _topicLastSendTime[dedupKey] = now;
            _messageQueue.Enqueue(new MqttMessage(topic, payload, priority, now), ((int)priority, now));
        }

        try { _queueSignal.Release(); } catch { }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(ct);
            }
            catch (OperationCanceledException) { break; }

            MqttMessage? msg = null;
            lock (_queueLock)
            {
                if (_messageQueue.Count > 0)
                    _messageQueue.TryDequeue(out msg, out _);
            }

            if (msg == null) continue;

            var minInterval = msg.Priority switch
            {
                MqttPriority.Critical => CriticalMinInterval,
                MqttPriority.High => HighMinInterval,
                MqttPriority.Normal => NormalMinInterval,
                MqttPriority.Low => LowMinInterval,
                _ => NormalMinInterval
            };

            if (_topicLastSendTime.TryGetValue(msg.Topic, out var lastSent))
            {
                var elapsed = DateTime.UtcNow - lastSent;
                if (elapsed < minInterval)
                {
                    await Task.Delay(minInterval - elapsed, ct);
                }
            }

            await SendAsync(msg.Topic, msg.Payload);
            _topicLastSendTime[msg.Topic] = DateTime.UtcNow;
        }
    }

    private async Task SendAsync(string topic, string payload)
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

        var qosLevel = topic.Contains("alarm") || topic.Contains("quench")
            ? MqttQualityOfServiceLevel.AtLeastOnce
            : MqttQualityOfServiceLevel.AtMostOnce;

        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"aluminum/{topic}")
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(qosLevel)
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
