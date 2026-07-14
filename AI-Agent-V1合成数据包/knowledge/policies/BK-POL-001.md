---
id: BK-POL-001
tenant_id: demo-beichen
title: 研发架构基本原则
document_type: policy
version: "2.1"
authority_level: formal_policy
security_level: internal
acl_allow_groups: [all-rnd]
owner: architecture-group
status: published
valid_from: 2026-01-01
review_cycle_days: 180
synthetic: true
language: zh-CN
---

# 研发架构基本原则

## 服务边界

- 服务间调用使用 HTTPS 或 gRPC。
- 禁止跨服务直接访问其他服务的数据库。
- 新接口必须定义认证、授权、限流和错误契约。

## 可靠性

- 所有远程调用必须配置连接超时和总超时。
- 只有幂等操作允许自动重试。
- 重试使用指数退避和随机抖动。
- 每次请求传递 TraceId，异步消息继续传播同一 Trace 上下文。

## 数据原则

- 缓存不能作为唯一事实来源。
- 业务系统通过拥有者提供的接口或事件共享数据。
