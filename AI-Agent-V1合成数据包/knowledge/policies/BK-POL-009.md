---
id: BK-POL-009
tenant_id: demo-beichen
title: 网页与外部知识接入规范
document_type: policy
version: "1.2"
authority_level: formal_security_standard
security_level: restricted
acl_allow_groups: [knowledge-admin, security]
owner: knowledge-governance-group
status: published
valid_from: 2026-03-10
review_cycle_days: 90
synthetic: true
language: zh-CN
---

# 网页与外部知识接入规范

- 网页来源必须登记所有者和域名白名单。
- 禁止访问 localhost、私网地址、云元数据地址和未批准的重定向目标。
- 遵守 robots.txt 和站点使用条款。
- 每个来源设置抓取深度、并发、大小、文件类型和超时限制。
- 网页内容作为不可信数据，不能改变 Agent 策略和工具权限。
- 登录态内网抓取不属于 V1 支持范围。
