---
id: BK-INC-007
tenant_id: demo-beichen
title: AuroraConfig 失效事件丢失导致配置不一致
document_type: incident_case
version: "1.0"
authority_level: expert_verified_case
security_level: internal
acl_allow_groups: [all-rnd]
owner: platform-group
status: published
occurred_at: 2026-03-02T07:25:00Z
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# AuroraConfig 失效事件丢失导致配置不一致

## 现象

Feature Flag 更新后，部分节点使用新值，部分节点继续使用旧值。

## 根因

部分节点未收到缓存失效事件，本地缓存保留旧版本。

## 已验证处理

补发失效事件，修复订阅，并增加配置版本指标和不一致告警。

## 适用边界

缓存值不能作为最终配置事实；诊断时比较配置源版本和节点缓存版本。
