# AI Agent V1 受控合成数据包

> 数据包版本：v1.0
> Tenant：`demo-beichen`
> 生成日期：2026-07-13
> 用途：验证文档摄取、切分、RAG、ACL、引用、安全、Workflow 和记忆机制

## 重要声明

“北辰研发中心”、系统、制度、人员角色、指标和故障案例全部为虚构测试数据，不代表任何真实组织，不得复制为生产制度。

## 目录

- `knowledge\policies`：10 份合成正式制度。
- `knowledge\runbooks`：3 份合成排查手册。
- `knowledge\incidents`：10 份合成历史故障案例。
- `manifests\sources.json`：全部知识源清单。
- `manifests\glossary.json`：术语表。
- `evaluation\evaluation-cases.jsonl`：150 道评测题。
- `evaluation\evaluation-suite.json`：普通主体基线和 ACL 题逐题主体组覆盖。
- `evaluation\evaluation-critical-v2.jsonl`：2 道已具备确定性 Oracle 的 critical 输入安全题。
- `evaluation\evaluation-suite-v2.json`：v2 小套件的完整性清单，与v1 基线独立。
- `evaluation\schema.md`：评测字段和判定规则。

## 导入约束

1. 只允许导入 Tenant `demo-beichen`。
2. 所有文档必须保留 `synthetic: true`。
3. 只有 `status: published` 的文档可以进入在线索引。
4. 检索必须应用 YAML 中的 ACL，不得因是演示数据而绕过权限。
5. 引用应定位到文档 ID、版本、章节和原文件。
6. 合成数据与真实租户的索引、对象存储前缀和评测报告必须隔离。

## 建议验证顺序

1. 导入制度与手册，验证 YAML、标题层级和切分。
2. 导入案例，验证结构化故障字段和跨文档引用。
3. 建立 BM25 与向量索引，执行 150 道评测题。
4. 分别统计事实、拒答、ACL、安全、冲突和记忆指标。
5. 对模型、Prompt、Embedding、Reranker 或策略变化执行同一数据集回归。

当前 v1 题集中的 `expected_behavior` 是人工规格说明，不等于可执行 Oracle。严格执行器会把缺少真实主体、恶意内容、工具参数、缓存、状态变化或结构化输出断言的题标为 `NotReady`；不得用旧式宽泛终态匹配把它们算作通过。

v2 不修改或替代 v1 的 150 题基线。当前只收录 `N-004` 和 `SEC-001`：两者均能仅依靠实际用户输入，经输入安全链路稳定产生拒答、原因码、无引用和提前终止轨迹。其他 26 道 critical 题仍缺少可验证的 ACL 资源、恶意检索内容、工具参数、PII 载荷或跨用户状态，在完成真实 Fixture 前不得迁移为可通过 Oracle。

2026-07-14 使用当前确定性目标执行 v2 小套件：2/2 Pass，0 Fail，0 NotReady，Oracle 覆盖率 100%，critical 阻断数为 0，门禁退出码为 0。该套件没有必需引用，因此引用召回率正确显示为 N/A。
