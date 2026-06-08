using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;

namespace AlCellControl.Services;

public record AlarmMessage(int CellId, int AlarmLevel, string AlarmType, string Message, DateTime Timestamp);

public enum MqttMessagePriority
{
    Critical = 0,
    High = 1,
    Normal = 2,
    Low = 3
}

public class PrioritizedMqttMessage : IComparable<PrioritizedMqttMessage>
{
    public MqttMessagePriority Priority { get; }
    public string Topic { get; }
    public string Payload { get; }
    public DateTime EnqueuedAt { get; }
    public int CellId { get; }

    public PrioritizedMqttMessage(MqttMessagePriority priority, string topic, string payload, int cellId)
    {
        Priority = priority;
        Topic = topic;
        Payload = payload;
        EnqueuedAt = DateTime.UtcNow;
        CellId = cellId;
    }

    public int CompareTo(PrioritizedMqttMessage? other)
    {
        if (other == null) return -1;
        int cmp = Priority.CompareTo(other.Priority);
        return cmp != 0 ? cmp : EnqueuedAt.CompareTo(other.EnqueuedAt);
    }
}

public class MqttPublisherService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MqttPublisherService> _logger;
    private readonly IMqttClient _mqttClient;
    private readonly string _host;
    private readonly int _port;
    private readonly string _topicPrefix;
    private bool _disposed;

    private readonly PriorityQueue<PrioritizedMqttMessage, PrioritizedMqttMessage> _messageQueue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly object _queueLock = new();
    private int _queueCount = 0;

    private readonly TimeSpan _minSendInterval = TimeSpan.FromMilliseconds(100);
    private DateTime _lastSendTime = DateTime.MinValue;
    private int _messagesPerCellPerMinute = 10;
    private readonly ConcurrentDictionary<int, CellRateTracker> _cellRateTrackers = new();
    private readonly ConcurrentDictionary<string, DateTime> _deduplicationCache = new();
    private readonly TimeSpan _dedupWindow = TimeSpan.FromSeconds(5);

    private Task? _dispatchTask;
    private CancellationTokenSource _dispatchCts = new();

    public MqttPublisherService(IConfiguration configuration, ILogger<MqttPublisherService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _host = _configuration["Mqtt:Host"] ?? "localhost";
        _port = int.TryParse(_configuration["Mqtt:Port"], out var port) ? port : 1883;
        _topicPrefix = _configuration["Mqtt:TopicPrefix"] ?? "alcell";
        _messagesPerCellPerMinute = int.TryParse(_configuration["Mqtt:RateLimitPerCellPerMinute"], out var limit) ? limit : 10;

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

        _dispatchTask = Task.Run(() => DispatchLoopAsync(_dispatchCts.Token));
    }

    public Task PublishAlarmAsync(AlarmMessage msg)
    {
        var priority = msg.AlarmLevel == 2 ? MqttMessagePriority.Critical : MqttMessagePriority.High;
        var topic = $"{_topicPrefix}/alarms/{msg.AlarmLevel}";
        var payload = JsonSerializer.Serialize(msg);

        var dedupKey = $"alarm:{msg.CellId}:{msg.AlarmType}";
        if (IsDuplicate(dedupKey)) return Task.CompletedTask;

        Enqueue(new PrioritizedMqttMessage(priority, topic, payload, msg.CellId));
        return Task.CompletedTask;
    }

    public Task PublishCellStatusAsync(int cellId, string status)
    {
        var topic = $"{_topicPrefix}/status/{cellId}";
        var payload = JsonSerializer.Serialize(new { CellId = cellId, Status = status, Timestamp = DateTime.UtcNow });

        var dedupKey = $"status:{cellId}:{status}";
        if (IsDuplicate(dedupKey)) return Task.CompletedTask;

        Enqueue(new PrioritizedMqttMessage(MqttMessagePriority.Normal, topic, payload, cellId));
        return Task.CompletedTask;
    }

    private bool IsDuplicate(string key)
    {
        if (_deduplicationCache.TryGetValue(key, out var lastSent))
        {
            if (DateTime.UtcNow - lastSent < _dedupWindow) return true;
        }
        _deduplicationCache[key] = DateTime.UtcNow;
        CleanupDedupCache();
        return false;
    }

    private void CleanupDedupCache()
    {
        if (_deduplicationCache.Count <= 1000) return;
        var cutoff = DateTime.UtcNow - _dedupWindow;
        var keysToRemove = _deduplicationCache.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
        foreach (var key in keysToRemove)
        {
            _deduplicationCache.TryRemove(key, out _);
        }
    }

    private bool IsRateLimited(int cellId)
    {
        var tracker = _cellRateTrackers.GetOrAdd(cellId, _ => new CellRateTracker());
        return tracker.IsRateLimited(_messagesPerCellPerMinute);
    }

    private void Enqueue(PrioritizedMqttMessage message)
    {
        lock (_queueLock)
        {
            _messageQueue.Enqueue(message, message);
            _queueCount++;
        }
        _queueSignal.Release();
    }

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(ct);

                PrioritizedMqttMessage? msg = null;
                lock (_queueLock)
                {
                    if (_messageQueue.Count > 0)
                    {
                        msg = _messageQueue.Dequeue();
                        _queueCount--;
                    }
                }

                if (msg == null) continue;

                if (IsRateLimited(msg.CellId) && msg.Priority >= MqttMessagePriority.Normal)
                {
                    _logger.LogDebug("Rate limited message for cell {CellId} on topic {Topic}", msg.CellId, msg.Topic);
                    continue;
                }

                var elapsed = DateTime.UtcNow - _lastSendTime;
                if (elapsed < _minSendInterval)
                {
                    await Task.Delay(_minSendInterval - elapsed, ct);
                }

                await SendAsync(msg);
                _lastSendTime = DateTime.UtcNow;

                var tracker = _cellRateTrackers.GetOrAdd(msg.CellId, _ => new CellRateTracker());
                tracker.RecordSend();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in MQTT dispatch loop");
            }
        }
    }

    private async Task SendAsync(PrioritizedMqttMessage msg)
    {
        try
        {
            if (!_mqttClient.IsConnected)
            {
                await InitializeAsync();
            }

            var applicationMessage = new MqttApplicationMessageBuilder()
                .WithTopic(msg.Topic)
                .WithPayload(Encoding.UTF8.GetBytes(msg.Payload))
                .WithQualityOfServiceLevel(msg.Priority <= MqttMessagePriority.High
                    ? MqttQualityOfServiceLevel.AtLeastOnce
                    : MqttQualityOfServiceLevel.AtMostOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.PublishAsync(applicationMessage);
            _logger.LogDebug("Published to {Topic} (priority={Priority}): {Payload}", msg.Topic, msg.Priority, msg.Payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish to {Topic} for cell {CellId}", msg.Topic, msg.CellId);
        }
    }

    public int GetPendingQueueCount()
    {
        lock (_queueLock) { return _queueCount; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _dispatchCts.Cancel();
        _dispatchTask?.Wait(TimeSpan.FromSeconds(5));
        _dispatchCts.Dispose();

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

public class CellRateTracker
{
    private readonly Queue<DateTime> _sendTimes = new();
    private readonly object _lock = new();

    public bool IsRateLimited(int maxPerMinute)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(1);
            while (_sendTimes.Count > 0 && _sendTimes.Peek() < cutoff)
                _sendTimes.Dequeue();
            return _sendTimes.Count >= maxPerMinute;
        }
    }

    public void RecordSend()
    {
        lock (_lock)
        {
            _sendTimes.Enqueue(DateTime.UtcNow);
        }
    }
}
