---
id: BK-INC-004
tenant_id: demo-beichen
title: 缓存同刻失效导致 OrionOrder 回源峰值
document_type: incident_case
version: "1.0"
authority_level: expert_verified_case
security_level: internal
acl_allow_groups: [all-rnd]
owner: trading-platform
status: published
occurred_at: 2026-02-11T02:00:00Z
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# 缓存同刻失效导致 OrionOrder 回源峰值

## 现象

大量同类缓存 Key 在整点同时过期，数据库请求瞬时增加并引起延迟。

## 根因

所有 Key 使用相同固定 TTL，热点请求没有 Singleflight，形成集中回源。

## 已验证处理

为 TTL 增加随机抖动，对热点 Key 使用 Singleflight，并分批预热。

## 适用边界

缓存仍不是事实来源。单个低频 Key 的普通 Miss 不能判定为缓存雪崩。
