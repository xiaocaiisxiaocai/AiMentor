---
id: BK-POL-002
tenant_id: demo-beichen
title: 身份、凭证与权限规范
document_type: policy
version: "3.0"
authority_level: formal_security_policy
security_level: internal
acl_allow_groups: [all-rnd]
restricted_detail_groups: [identity-platform, security]
owner: security-group
status: published
valid_from: 2026-02-01
review_cycle_days: 90
synthetic: true
language: zh-CN
---

# 身份、凭证与权限规范

## 用户与服务身份

- 用户登录使用 OIDC。
- Access Token 默认有效期为 30 分钟。
- Refresh Token 最长有效期为 8 小时。
- 服务账号凭证最长 90 天轮换一次。
- 禁止共享账号。

## 凭证保护

- 禁止在代码、配置文件和日志中保存密码、Token 和密钥。
- Production 密钥只存放在 Secret Manager。
- 工作负载通过受管身份读取密钥。

## 授权

- 权限遵循最小必要原则。
- 管理员身份不自动获得业务数据读取权限。
- 权限判断使用当前授权状态，不依赖过期缓存。
