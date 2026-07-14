---
id: BK-POL-003
tenant_id: demo-beichen
title: 故障分级与响应规范
document_type: policy
version: "2.4"
authority_level: formal_policy
security_level: internal
acl_allow_groups: [all-rnd]
owner: reliability-group
status: published
valid_from: 2026-01-15
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# 故障分级与响应规范

## 分级

- P1：核心服务整体不可用、确认的数据泄漏或数据不可逆损坏；5 分钟内确认响应。
- P2：主要功能部分不可用或显著影响一类用户；15 分钟内确认响应。
- P3：局部降级、有替代路径且无数据风险；4 小时内确认响应。

## 时间与记录

- 故障时间线统一使用 UTC。
- 展示界面可以同时显示本地时区。
- 结案记录包含影响、时间线、根因、处理、验证和后续措施。

## AI 边界

- AI 可以辅助整理证据、匹配案例和建议排查。
- AI 不得自动执行生产变更。
