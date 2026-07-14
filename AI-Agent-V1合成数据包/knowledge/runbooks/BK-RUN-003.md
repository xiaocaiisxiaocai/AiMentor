---
id: BK-RUN-003
tenant_id: demo-beichen
title: VegaBus 消息积压排查手册
document_type: runbook
version: "2.0"
authority_level: approved_runbook
security_level: internal
acl_allow_groups: [all-rnd, middleware-group]
owner: middleware-group
status: published
valid_from: 2026-02-20
review_cycle_days: 90
synthetic: true
language: zh-CN
---

# VegaBus 消息积压排查手册

## 排查顺序

1. 确认队列长度和增长率。
2. 检查消费者在线状态和处理耗时。
3. 检查失败、自动重试和死信队列。
4. 核对生产者与消费者的 SchemaVersion。
5. 检查下游依赖和数据库延迟。
6. 评估是否需要受控扩容。

## 限制

- 未确认死信原因前不得盲目重放全部消息。
- 重放前必须验证消费者幂等性和数据范围。
- 扩容、重启或批量重放均属于生产动作，需要审批。
