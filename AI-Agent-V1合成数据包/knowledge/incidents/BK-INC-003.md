---
id: BK-INC-003
tenant_id: demo-beichen
title: VegaBus 消费者不兼容新 Schema 导致积压
document_type: incident_case
version: "1.0"
authority_level: expert_verified_case
security_level: internal
acl_allow_groups: [all-rnd]
owner: middleware-group
status: published
occurred_at: 2026-02-04T06:30:00Z
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# VegaBus 消费者不兼容新 Schema 导致积压

## 现象

发布新事件版本后，主队列和死信队列同时增长，部分消费者持续反序列化失败。

## 根因

生产者发布了消费者尚不兼容的 SchemaVersion。

## 已验证处理

恢复兼容消费者，确认 EventId 幂等后分批受控重放死信消息。

## 适用边界

如果没有死信且消费者处理时间变长，应优先检查下游延迟和容量，不应直接判断为 Schema 问题。
