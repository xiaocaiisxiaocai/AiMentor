---
id: BK-INC-009
tenant_id: demo-beichen
title: 失败解析任务遗留对象导致 MinervaStore 容量告警
document_type: incident_case
version: "1.0"
authority_level: expert_verified_case
security_level: internal
acl_allow_groups: [all-rnd]
owner: storage-platform
status: published
occurred_at: 2026-03-09T04:50:00Z
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# 失败解析任务遗留对象导致 MinervaStore 容量告警

## 现象

对象存储容量持续增长，存在大量失败解析任务生成的中间文件。

## 根因

失败补偿未清理临时对象，且缺少生命周期规则。

## 已验证处理

先审计对象引用关系，再清理孤儿对象，并增加临时前缀生命周期和失败补偿。

## 适用边界

禁止只按文件年龄删除对象；被文档、评测或审计引用的对象必须保留。
