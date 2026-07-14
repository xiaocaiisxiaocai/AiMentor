---
id: BK-POL-008
tenant_id: demo-beichen
title: 缓存使用规范
document_type: policy
version: "1.8"
authority_level: formal_technical_standard
security_level: internal
acl_allow_groups: [all-rnd]
owner: platform-group
status: published
valid_from: 2026-01-10
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# 缓存使用规范

- 所有缓存项必须设置 TTL。
- 大量同类 Key 的 TTL 应加入随机抖动。
- 热点回源使用 Singleflight 或互斥机制防止击穿。
- 缓存不是最终事实来源。
- 关键写入先落数据库，再发布缓存失效事件。
- 未经加密和数据治理批准，不得缓存 PII。
