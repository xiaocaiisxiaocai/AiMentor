---
id: BK-POL-006
tenant_id: demo-beichen
title: PostgreSQL 数据库规范
document_type: policy
version: "3.1"
authority_level: formal_technical_standard
security_level: internal
acl_allow_groups: [all-rnd]
restricted_detail_groups: [data-platform]
owner: data-platform
status: published
valid_from: 2026-01-20
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# PostgreSQL 数据库规范

## 访问

- 应用通过连接池访问数据库，并正确释放连接。
- 禁止将任意 SQL 工具暴露给 Agent。
- 禁止未经审批直接操作 Production。

## 备份与恢复目标

- Production 每日一次全量备份，并持续归档 WAL。
- 目标 RPO 为 15 分钟。
- 目标 RTO 为 60 分钟。

## Schema

- 变更必须兼容尚未完成升级的旧应用实例。
- 采用 Expand-Contract 完成迁移。
- 数据库是订单等核心业务事实的最终来源，缓存只用于加速。
