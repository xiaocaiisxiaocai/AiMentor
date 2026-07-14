---
id: BK-POL-005
tenant_id: demo-beichen
title: 日志、隐私与审计规范
document_type: policy
version: "2.0"
authority_level: formal_security_policy
security_level: internal
acl_allow_groups: [all-rnd]
restricted_detail_groups: [security]
owner: security-group
status: published
valid_from: 2026-02-15
review_cycle_days: 90
synthetic: true
language: zh-CN
---

# 日志、隐私与审计规范

## 保留期

- 应用日志默认保留 14 天。
- 分布式 Trace 默认保留 30 天。
- 安全审计事件保留 180 天。

## 禁止内容

- 禁止记录密码、Token、密钥、完整身份证号和支付数据。
- 邮箱和手机号默认掩码。
- 排障需要明文时必须获得授权并缩短保留期。

## 审计字段

安全日志至少记录主体、动作、资源范围、时间、结果、原因码和 TraceId。LLM Trace 遵循相同的数据分类和最小化原则。
