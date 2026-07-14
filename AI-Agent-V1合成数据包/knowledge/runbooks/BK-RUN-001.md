---
id: BK-RUN-001
tenant_id: demo-beichen
title: AtlasID 登录异常排查手册
document_type: runbook
version: "2.2"
authority_level: approved_runbook
security_level: internal
acl_allow_groups: [all-rnd]
restricted_detail_groups: [identity-platform]
owner: identity-platform
status: published
valid_from: 2026-02-05
review_cycle_days: 90
synthetic: true
language: zh-CN
---

# AtlasID 登录异常排查手册

## 适用范围

适用于 OIDC 登录失败、Token 过期、Token Not Yet Valid、Issuer/Audience 不匹配和签名验证异常。

## 排查顺序

1. 确认用户、区域、节点和时间范围。
2. 检查 Token 的过期时间、Issuer 和 Audience。
3. 比较服务节点 UTC 时间和 NTP 状态。
4. 检查签名证书和 JWKS 缓存。
5. 检查最近身份配置和证书变更。

## 限制

- 不能仅凭“签名失败”建议轮换全部密钥。
- 不得把用户 Token 粘贴到普通日志或模型上下文。
- 任何生产配置或证书变更均需审批。
