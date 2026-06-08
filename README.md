# 电解铝氧化铝浓度在线检测与槽控优化系统

## 架构总览

```
┌─────────────────────────────────────────────────────────────────┐
│                      Docker Compose Network                     │
│                                                                 │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────────────┐│
│  │  ZigBee      │   │  C# API      │   │  SQL Server 2022     ││
│  │  Simulator   ├──▶│  .NET 8      ├──▶│  AlCellMonitor DB    ││
│  │  (Python)    │   │              │   │                      ││
│  │  200槽/15s   │   │  ┌────────┐  │   │  7张表 + 索引维护    ││
│  │  注入功能    │   │  │MediatR │  │   │  定期备份            ││
│  └──────────────┘   │  └───┬────┘  │   └──────────────────────┘│
│                     │      │       │                           │
│                     │  ┌───▼────────────────────────────┐     │
│                     │  │ ZigBeeReceiver                 │     │
│                     │  │  数据采集 + 槽位缓冲管理       │     │
│                     │  └───┬────────────────────────────┘     │
│                     │      │ CellDataReceivedEvent             │
│                     │  ┌───▼────────────────┐                 │
│                     │  │ ConcentrationEst.  │                 │
│                     │  │  SVR推理+补料控制  │                 │
│                     │  └───┬────────────────┘                 │
│                     │      │ ConcentrationEstimatedEvent       │
│                     │  ┌───▼──────────────────┐               │
│                     │  │ AnodeEffectPredictor │               │
│                     │  │  RF预测+漂移检测     │               │
│                     │  └───┬──────────────────┘               │
│                     │      │ AnodeEffectPredictedEvent         │
│                     │  ┌───▼──────────────────┐               │
│                     │  │ AlarmOrchestrator    │               │
│                     │  │  告警评估+效应熄灭   │               │
│                     │  └─────────────────────┘               │
│                     │      │                                   │
│                     │  ┌───▼──────────────┐                   │
│                     │  │ MqttPublisher     │                   │
│                     │  │  优先级队列+限流  │                   │
│                     │  └─────────────────┘                    │
│                     └──────┬──────────────┘                   │
│                            │                                  │
│                     ┌──────▼──────────────┐                   │
│                     │  Mosquitto MQTT     │                   │
│                     │  Broker (QoS 1)     │                   │
│                     │  持久化 + 限流      │                   │
│                     └─────────────────────┘                   │
│                                                              │
│                     ┌─────────────────────┐                   │
│                     │  Browser            │                   │
│                     │  Canvas车间布局     │                   │
│                     │  Chart.js趋势图     │                   │
│                     │  Gzip压缩传输       │                   │
│                     └─────────────────────┘                   │
└─────────────────────────────────────────────────────────────────┘

外部可观测:
  Serilog → Console + File (日志轮转)
  Application Insights → Azure Monitor (可选)
```

## 目录结构

```
.
├── docker-compose.yml              # 容器编排
├── .dockerignore                   # Docker构建忽略
├── backend/
│   └── AlCellControl/
│       ├── Dockerfile              # 多阶段构建
│       ├── AlCellControl.csproj    # 项目依赖
│       ├── Program.cs              # Serilog + AppInsights + Gzip
│       ├── appsettings.json        # 配置(容器内服务名)
│       ├── Configuration/
│       │   ├── svr_model.json      # SVR模型参数
│       │   └── rf_model.json       # RF模型参数
│       ├── Controllers/            # API端点
│       ├── Services/               # 核心业务(MediatR Handler)
│       ├── Events/                 # MediatR事件定义
│       ├── Commands/               # MediatR命令定义
│       ├── Models/                 # EF Core实体
│       ├── Data/                   # DbContext
│       └── wwwroot/                # 前端静态文件
│           ├── index.html
│           └── js/
│               ├── potline_view.js # Canvas车间布局组件
│               └── pot_detail.js   # 槽详情弹窗组件
├── simulator/
│   ├── Dockerfile                  # Python模拟器镜像
│   ├── requirements.txt            # Python依赖
│   ├── zigbee_simulator.py         # 模拟器主程序
│   └── injection_example.json      # 注入配置示例
├── mqtt-broker/
│   └── mosquitto.conf              # Mosquitto配置(QoS 1)
├── database/
│   ├── init.sql                    # 数据库初始化
│   ├── index_maintenance.sql       # 索引维护脚本
│   └── backup_job.sql              # 备份+清理脚本
└── README.md
```

## 快速部署

### 前置要求

- Docker 24+
- Docker Compose v2+
- 4GB+ 可用内存

### 一键启动

```bash
# 克隆后进入项目根目录
cd AI_solo_coder_task_A_067

# 构建并启动所有服务
docker-compose up --build -d

# 查看服务状态
docker-compose ps

# 查看API日志
docker-compose logs -f alcell-api
```

### 数据库初始化

SQL Server容器启动后，手动执行初始化脚本：

```bash
docker exec -it alcell-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "AlCell2024!Strong" -C \
  -i /docker-entrypoint-initdb.d/init.sql
```

### 访问服务

| 服务 | 地址 |
|------|------|
| C# API + 前端 | http://localhost:5000 |
| Swagger UI | http://localhost:5000/swagger |
| MQTT Broker | tcp://localhost:1883 |
| SQL Server | localhost:1433 (sa / AlCell2024!Strong) |

### 停止服务

```bash
docker-compose down

# 停止并删除数据卷
docker-compose down -v
```

## 模拟器用法

### 基础运行

模拟器随 `docker-compose up` 自动启动，默认参数：200台电解槽、15秒间隔、带注入。

### 命令行参数

```bash
python zigbee_simulator.py \
  --url http://alcell-api:5000 \   # API地址
  --cells 200 \                     # 电解槽数量
  --interval 15 \                   # 上报间隔(秒)
  --injection injection_example.json # 注入配置(可选)
```

### 注入配置格式

```json
{
  "concentration_drops": [
    {
      "cell_ids": [15, 42, 78],
      "magnitude": 0.8,
      "at_cycle": 20,
      "description": "指定槽在第20轮注入浓度快速下降"
    },
    {
      "random_count": 10,
      "magnitude": 0.5,
      "at_cycle": 50,
      "description": "随机10台槽在第50轮注入中等浓度下降"
    }
  ],
  "anode_effect_precursors": [
    {
      "cell_ids": [7, 88, 155],
      "duration_cycles": 12,
      "at_cycle": 30,
      "description": "指定槽在第30轮注入阳极效应前兆(电压毛刺)"
    },
    {
      "random_count": 5,
      "duration_cycles": 8,
      "at_cycle": 60,
      "description": "随机5台槽在第60轮注入阳极效应前兆"
    }
  ]
}
```

| 字段 | 说明 |
|------|------|
| `cell_ids` | 指定注入的槽号列表 |
| `random_count` | 随机选择N台槽（与cell_ids二选一） |
| `magnitude` | 浓度下降幅度（0.1~2.0） |
| `duration_cycles` | 阳极效应前兆持续轮数（5~20） |
| `at_cycle` | 在第N轮触发（null=持续注入） |

### 单独运行模拟器

```bash
# 无注入
python simulator/zigbee_simulator.py --url http://localhost:5000 --cells 200

# 带注入
python simulator/zigbee_simulator.py --url http://localhost:5000 --cells 200 \
  --injection simulator/injection_example.json
```

## 可观测性

### Serilog日志

- **Console输出**：实时查看请求和错误日志
- **File输出**：`/app/logs/alcell-YYYYMMDD.log`（按日轮转）
- **结构化字段**：Application=AlCellControl, MachineName, SourceContext

### Application Insights

在 `docker-compose.yml` 或 `.env` 中设置连接字符串：

```bash
APPINSIGHTS_CONNECTION_STRING=InstrumentationKey=xxx;IngestionEndpoint=https://xxx.applicationinsights.azure.com/
```

启用后可观测：请求追踪、依赖调用、异常捕获、自定义事件。

### SQL Server维护

**索引维护**（建议每周执行）：
```bash
docker exec -it alcell-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "AlCell2024!Strong" -C \
  -i /docker-entrypoint-initdb.d/index_maintenance.sql
```

**数据库备份**（建议每日执行）：
```bash
docker exec -it alcell-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "AlCell2024!Strong" -C \
  -i /docker-entrypoint-initdb.d/backup_job.sql
```

备份文件存储在 `/var/opt/mssql/backup/` 卷中，自动清理7天前的备份。

## 技术栈

| 层 | 技术 |
|------|------|
| 后端 | C# .NET 8, EF Core 8, MediatR 12 |
| 数据库 | SQL Server 2022 |
| 消息 | MQTTnet 4 + Mosquitto 2.0 (QoS 1) |
| ML | SVR (RBF核) + 随机森林 (5棵树加权投票) |
| 前端 | HTML5 Canvas + Chart.js |
| 日志 | Serilog (Console + File) + Application Insights |
| 压缩 | ASP.NET Response Compression (Gzip Optimal) |
| 容器 | Docker多阶段构建 + Docker Compose |
