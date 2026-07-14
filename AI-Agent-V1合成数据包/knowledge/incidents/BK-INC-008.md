---
id: BK-INC-008
tenant_id: demo-beichen
title: 服务迁移后 DNS 负缓存指向旧地址
document_type: incident_case
version: "1.0"
authority_level: expert_verified_case
security_level: internal
acl_allow_groups: [all-rnd]
owner: platform-group
status: published
occurred_at: 2026-03-05T09:00:00Z
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# 服务迁移后 DNS 负缓存指向旧地址

## 现象

服务迁移后大多数节点正常，少量节点继续访问旧地址。

## 根因

受影响节点的 Resolver 负缓存未按预期过期。

## 已验证处理

核对 DNS 和 Resolver TTL，经审批受控刷新受影响节点，并补充 DNS 缓存指标。

## 安全边界

重启或刷新生产节点属于生产动作，需要审批；AI 不得自动执行。
