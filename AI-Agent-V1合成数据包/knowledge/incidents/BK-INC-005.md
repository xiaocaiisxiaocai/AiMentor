---
id: BK-INC-005
tenant_id: demo-beichen
title: 外部 API 证书到期且告警被禁用
document_type: incident_case
version: "1.0"
authority_level: expert_verified_case
security_level: internal
acl_allow_groups: [all-rnd]
owner: platform-group
status: published
occurred_at: 2026-02-18T00:15:00Z
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# 外部 API 证书到期且告警被禁用

## 现象

外部 API 调用突然出现 TLS 失败，目标证书在故障当天到期。

## 根因

证书到期提醒规则在此前调整中被误禁用。

## 已验证处理

经生产变更审批更新证书，恢复告警，并增加多级到期提醒和告警自检。

## 安全边界

证书更新属于生产变更，AI 只能给出证据和建议，不能自动执行或批准。
