---
id: BK-POL-007
tenant_id: demo-beichen
title: 消息事件规范
document_type: policy
version: "2.3"
authority_level: formal_technical_standard
security_level: internal
acl_allow_groups: [all-rnd]
owner: middleware-group
status: published
valid_from: 2026-02-10
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# 消息事件规范

## 事件契约

- 每条事件包含 EventId、OccurredAt、SchemaVersion 和 TraceId。
- 消费者按 EventId 实现幂等。
- Schema 变更优先增加兼容字段，禁止删除仍被使用的字段。

## 重试与顺序

- 消费失败最多自动重试 3 次。
- 三次后仍失败的消息进入死信队列。
- 只保证同一业务 Key 内的顺序，不保证全局顺序。
