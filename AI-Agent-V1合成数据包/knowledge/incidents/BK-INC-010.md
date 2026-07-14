---
id: BK-INC-010
tenant_id: demo-beichen
title: 数据库变更未兼容旧实例导致滚动发布失败
document_type: incident_case
version: "1.0"
authority_level: expert_verified_case
security_level: internal
acl_allow_groups: [all-rnd]
owner: data-platform
status: published
occurred_at: 2026-03-12T01:35:00Z
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# 数据库变更未兼容旧实例导致滚动发布失败

## 现象

滚动发布期间，新实例正常，尚未升级的旧实例访问新 Schema 时失败。

## 根因

Schema 变更直接收缩旧结构，没有遵循 Expand-Contract。

## 已验证处理

恢复兼容 Schema，完成应用分阶段升级和数据迁移后，再移除旧结构。

## 适用边界

适用于新旧应用实例并存期间的 Schema 兼容问题。生产数据库操作必须审批。
