---
id: BK-INC-006
tenant_id: demo-beichen
title: OCR 任务阻塞 HarborDocs 索引 Worker
document_type: incident_case
version: "1.0"
authority_level: expert_verified_case
security_level: internal
acl_allow_groups: [all-rnd]
owner: rnd-efficiency-group
status: published
occurred_at: 2026-02-23T05:40:00Z
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# OCR 任务阻塞 HarborDocs 索引 Worker

## 现象

文件上传和解析状态成功，但新文档长时间无法搜索；同期大量扫描 PDF 进入 OCR。

## 根因

耗时 OCR 与索引任务共用 Worker 队列，索引任务被长期阻塞。

## 已验证处理

分离解析和索引队列，独立限制 OCR 并发，恢复积压任务并核对索引结果。

## 适用边界

不应在确认原因前直接重建完整索引；先检查任务状态、队列和失败记录。
