---
id: BK-RUN-002
tenant_id: demo-beichen
title: 数据库连接池耗尽排查手册
document_type: runbook
version: "1.9"
authority_level: approved_runbook
security_level: internal
acl_allow_groups: [all-rnd, data-platform]
owner: data-platform
status: published
valid_from: 2026-01-25
review_cycle_days: 90
synthetic: true
language: zh-CN
---

# 数据库连接池耗尽排查手册

## 排查顺序

1. 确认连接超时和等待连接指标。
2. 查看活跃、空闲和等待连接数量。
3. 对比请求量和近期流量变化。
4. 检查慢查询和长事务。
5. 检查异常路径是否正确释放连接。
6. 评估连接池大小与数据库容量。

## 判断原则

- 等待增加但请求量平稳时，优先检查连接泄漏和长事务。
- 扩大连接池必须有数据库容量依据。
- 单纯扩大连接池不能代替修复连接泄漏。
- 生产 SQL 和配置变更需要正式审批。
