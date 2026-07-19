# 生产可观测性与 SLO 处置手册

本文定义 AI Mentor 业务指标的生产解释、告警阈值和处置顺序。值班人员必须同时查看 Prometheus、OTLP Collector、应用健康端点和依赖服务；没有数据、规则未评估或采集目标消失均为“未知/不健康”，不得解释为零失败。

## 数据链路与低基数约束

应用通过 OTLP 导出追踪与指标。Collector 转换为 Prometheus 命名时应保留标准单位与计数器后缀，例如 `aimentor.workflow.executions` 对应 `aimentor_workflow_executions_total`，单位为秒的 `aimentor.workflow.duration` 对应 `aimentor_workflow_duration_seconds_*`。发布前必须在预生产环境核对 Collector 的转换策略，不能只验证目标 `up=1`。

业务指标只允许下列受控标签：

| 指标 | 允许的标签 | 用途 |
| --- | --- | --- |
| `aimentor_telemetry_heartbeat` | 无应用标签；Exporter 可增加 `job`、`instance` 与 scope 标签 | 应用当前 Unix 秒，不依赖请求流量并可识别 Collector 缓存重放和单副本丢失 |
| `aimentor_workflow_executions_total` | `aimentor_workflow_kind`、`aimentor_outcome_class` | 真实业务完成量、失败率；通用审计事件不进入分母 |
| `aimentor_workflow_duration_seconds_*` | `aimentor_workflow_kind`、`aimentor_outcome_class` | 真实业务端到端持续时间 |
| `aimentor_memory_retention_runs_total` | `aimentor_result` | 清理成功、失败和锁竞争 |
| `aimentor_memory_retention_deleted_total` | `aimentor_record_type` | 被清理记录数 |
| `aimentor_memory_retention_duration_seconds_*` | `aimentor_result` | 清理持续时间 |
| `aimentor_memory_retention_last_completed_at` / `aimentor_memory_retention_monitoring_started_at` | 无应用标签 | 数据库共享的最近成功清理/开始监控 Unix 秒；不使用 Pod 重启时间延长首次成功期限 |

租户、主体、运行 ID、提示词、正文、工具参数、异常消息和任意用户输入不得成为指标标签。未知阶段或结果必须折叠为 `other`；遇到标签基数持续增长时，先阻断新增标签，再检查 Collector 处理器与应用版本。

## SLI、SLO 与默认阈值

| SLI | PromQL | 生产目标 | 默认告警 |
| --- | --- | --- | --- |
| 工作流成功率 | `failure / (success + failure)`，限定 `aimentor_workflow_kind="trusted_question"` | 5 分钟窗口成功率至少 95%；业务拒绝和客户端取消不冒充成功，也不计作服务故障 | 至少 20 次可用性样本且失败率连续 5 分钟高于 5% |
| 工作流 P95 持续时间 | `histogram_quantile(0.95, ...{aimentor_workflow_kind="trusted_question",aimentor_outcome_class=~"success|failure"})` | P95 不超过 15 秒 | 样本量至少 20 且连续 10 分钟超过 15 秒 |
| 记忆清理成功 | `time() - max(aimentor_memory_retention_last_completed_at)` | 每 30 分钟至少成功一次 | 共享完成时间陈旧、启动 30 分钟仍为零，或 5 分钟窗口出现 failed |
| 遥测完整性 | 心跳实例数不少于 Helm 副本数，且 `time() - heartbeat <= 120` | 每个副本的自定义 Meter 心跳持续推进，Collector `up=1`，规则评估间隔不超过 5 分钟 | 两分钟窗口少副本、心跳超过 120 秒没有推进、Collector `up=0/缺失` 或规则最后评估时间过期 |

上述阈值是初始生产基线。调整必须依据至少 14 天的有效样本并经过服务负责人审批；不得通过提高失败率或延迟阈值掩盖事故。`lock_not_acquired` 代表本轮未完成清理，不等价于成功。

## Helm 启用与发布验证

Prometheus Operator CRD 不是 API 运行依赖，因此 `monitoring.prometheusRule.enabled` 默认关闭。已部署 Prometheus Operator 的生产集群应显式启用，并设置实际的责任团队、可访问 runbook 和目标 job 正则：

```yaml
monitoring:
  prometheusRule:
    enabled: true
    team: ai-platform
    runbook: https://runbooks.example.com/aimentor/observability
    targetJobRegex: "production/AiMentor.Api"
    collectorJobRegex: "production/otel-collector"
```

发布前依次完成：

1. 用生产覆盖值执行 `helm lint --strict` 和 `helm template`，确认 `PrometheusRule` 只在启用时生成。
2. 在 Prometheus 的 Targets 页面确认目标存在且 `up=1`，再检查每个 Pod UID 都映射为不同的 `service.instance.id`/`instance`，避免多副本累计指标串流。
3. 逐条执行上表 PromQL，确认 `aimentor_telemetry_heartbeat` 在零业务流量时仍接近当前 Unix 秒，且最近两分钟不同 `instance` 数不少于 Helm `replicaCount`；只看到 Prometheus 样本时间更新但心跳值不推进仍属于链路故障。
4. 检查 `prometheus_rule_group_last_evaluation_timestamp_seconds{rule_group=~".*aimentor-slo.*"}` 持续更新。
5. 在非生产环境短暂制造失败和停止采集，验证业务心跳、目标缺失告警及通知路由；不得在生产通过真实用户请求演练。

## 告警处置

### 工作流失败率过高

先按 `aimentor_workflow_kind` 和 `aimentor_outcome_class` 分组确认影响范围，再用 Trace 的阶段事件检查模型、OpenSearch、SQL 与输出门禁。策略拒绝和客户端取消被单独分类，不进入可用性成功分母；异常逃逸仍必须记录为 failure。若存在重复副作用风险，停止相关工具入口并保留协调账本，不得盲目重试。恢复后确认至少两个完整 5 分钟窗口回到目标内。

### 工作流 P95 过高

对照追踪拆分模型、检索、工具与数据库阶段，检查依赖配额、连接池、超时和最近发布。延迟升高同时伴随失败时按失败事故升级；禁止只增加超时。恢复后确认 P95 与样本量共同正常。

### 记忆清理失败或 30 分钟无成功

检查 `/health/ready`、SQL 连接、数据库应用锁、后台服务日志和清理批次耗时。`failed` 必须立即处置；持续 `lock_not_acquired` 说明没有实例完成清理，也必须处置。确认密文数据仍可解密后再手工触发；不得删除未知密钥版本的数据。

### 遥测目标或规则评估缺失

按应用 Pod、OTLP Collector、Prometheus ServiceMonitor/PodMonitor、Prometheus 规则加载状态的顺序检查。`collectorJobRegex` 只匹配 Prometheus 抓取 Collector exporter 的 `up`，`targetJobRegex` 则匹配 OTel 业务指标映射后的 `job`，两者不得用同一占位值蒙混。Collector 目标 `up=1` 但业务心跳缺失或值停止推进时，核对应用 exporter 错误、Collector 的队列与缓存、过滤、重命名和单位后缀。心跳是 ObservableGauge，在没有用户请求时也必须持续更新为应用当前 Unix 秒；不能用“当前零流量”或“Collector 仍在返回缓存样本”解释其陈旧。PrometheusRule 自身无法在完全未加载时可靠地自报故障，因此生产告警平台还必须从独立监控域检查规则组评估时间或配置 dead-man 信号。

在数据链路恢复并覆盖至少一个完整查询窗口之前，仪表盘和 SLO 一律标记为“数据不完整”，不得关闭事故或宣告恢复。
