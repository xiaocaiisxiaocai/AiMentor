---
id: BK-INC-001
tenant_id: demo-beichen
title: AtlasID 节点时钟失步导致 Token Not Yet Valid
document_type: incident_case
version: "1.0"
authority_level: expert_verified_case
security_level: internal
acl_allow_groups: [all-rnd]
owner: identity-platform
status: published
occurred_at: 2026-01-12T03:20:00Z
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# AtlasID 节点时钟失步导致 Token Not Yet Valid

## 现象

同一用户在部分节点登录成功、部分节点返回 Token Not Yet Valid，问题呈间歇性。

## 环境与证据

- 受影响节点 UTC 时间比标准时间慢约 7 分钟。
- Issuer、Audience、签名证书和 JWKS 均正常。
- 恢复 NTP 后相同 Token 验证成功。

## 根因

应用节点 NTP 异常，导致本地时间早于 Token 的 NotBefore。

## 已验证处理

恢复时间同步，验证节点时间和 Token；增加节点时钟偏移告警。

## 适用边界

只适用于时间相关验证失败。Invalid Signature 等错误不能直接套用本案例。
