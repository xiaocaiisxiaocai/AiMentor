---
id: BK-INC-002
tenant_id: demo-beichen
title: OrionOrder 异常路径未释放数据库连接
document_type: incident_case
version: "1.0"
authority_level: expert_verified_case
security_level: internal
acl_allow_groups: [all-rnd]
owner: trading-platform
status: published
occurred_at: 2026-01-28T08:10:00Z
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# OrionOrder 异常路径未释放数据库连接

## 现象

高峰期数据库连接等待持续上升，请求出现连接超时；请求量增长不足以解释全部变化。

## 证据

- 最近修改了一个异常处理路径。
- 活跃连接长期不回落，等待连接持续增长。
- 慢查询和数据库 CPU 未同步显著增长。

## 根因

异常路径未正确释放数据库连接。

## 已验证处理

修复资源释放逻辑，增加活跃、等待和持有时长指标。未通过简单扩大连接池掩盖问题。

## 适用边界

若连接池等待为零，应继续检查慢查询、下游依赖和请求量，不应套用本案例。
