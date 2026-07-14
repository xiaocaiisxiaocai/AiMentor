---
id: BK-POL-004
tenant_id: demo-beichen
title: 生产变更规范
document_type: policy
version: "4.2"
authority_level: formal_policy
security_level: internal
acl_allow_groups: [all-rnd]
owner: change-governance-group
status: published
valid_from: 2026-03-01
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# 生产变更规范

## 常规变更

- 必须关联变更工单。
- 必须完成同伴复核。
- 必须提供验证方案和回滚方案。
- 数据库变更采用 Expand-Contract，保证滚动发布期间向后兼容。

## 紧急变更

- 由 Incident Commander 批准。
- 先控制影响，再补全记录。
- 结束后两个工作日内完成复盘。

## AI 边界

- AI 不得代表审批人批准变更。
- AI 不得把未执行的建议描述成已经完成的动作。
