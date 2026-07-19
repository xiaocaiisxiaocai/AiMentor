# 灾难恢复运行手册

## 目标与适用范围

本手册覆盖 AiMentor 生产环境的 SQL Server 耐久状态、OpenSearch 检索状态、应用信封加密密钥环和运营台 Data Protection 密钥环。目标为：

- 恢复点目标（RPO）：不超过 15 分钟。
- 恢复时间目标（RTO）：不超过 60 分钟。
- SQL、索引 Alias 或任一密钥依赖未完成验证时，服务必须保持未就绪，不得通过降级配置带流量启动。

RPO 是备份链和复制策略的承诺，不由验收脚本单独保证。生产环境至少应每日完整备份、按风险设置差异备份，并以不超过 15 分钟的间隔备份事务日志；备份应启用校验和、加密、异地不可变保留和独立恢复凭据。每次密钥或索引发布后还必须立即产生对应的受保护恢复点。

## 必须成组恢复的依赖

| 依赖 | 必需恢复材料 | 失败关闭条件 |
|---|---|---|
| SQL Server | 完整/差异/日志备份链、备份校验和、数据库加密或备份加密凭据 | 备份链断裂、`RESTORE VERIFYONLY` 或 `DBCC CHECKDB` 失败 |
| OpenSearch | Snapshot Repository 中的物理索引、模板/管线、`current`/`previous` Alias；或可验证的原始语料与发布清单 | Alias 指向不存在或未验证的物理索引、向量维度或模型版本漂移 |
| 记忆/工作流信封加密 | `Memory:Encryption:Keys`、`Workflow:Encryption:Keys` 的全部在用版本与活动版本，兼容期内的 `AIMENTOR_MEMORY_ENCRYPTION_KEY` | SQL 中存在未知 `KeyVersion`，或历史密文抽样认证解密失败 |
| 运营台 Data Protection | 共享 `key-*.xml`、`.aimentor-cluster-id`、活动与历史解密证书、证书密码、预期指纹 | marker、证书或任一未撤销 key 缺失，计算指纹与部署值不一致 |
| 发布元数据 | 已批准的镜像摘要、配置版本、SQL 迁移版本、Atlas Runbook 版本 | 恢复镜像无法解释已持久化状态，或迁移清单/校验和不匹配 |

秘密材料只允许从受审计的密钥保管系统注入。工单、聊天记录、命令历史和演练证据中不得记录真实密钥、密码、证书私钥或原始 Token。

## 备份策略与持续验证

1. SQL 完整、差异和日志备份均使用 `CHECKSUM`，备份任务失败立即告警；每条备份链至少在隔离环境执行一次 `RESTORE VERIFYONLY WITH CHECKSUM`。
2. OpenSearch 定期创建 Snapshot，并记录物理索引、Alias、嵌入模型、向量维度和语料发布清单。Snapshot 成功但 Alias/清单缺失不能算可恢复。
3. 应用密钥环和 Data Protection 共享卷与业务数据采用不同故障域的受控备份。轮换遵循“先加、再切、后删”，删除旧密钥前必须证明所有历史记录已迁移且已越过最长有效期。
4. 每月至少执行一次 SQL 自动恢复验收；每季度执行包含 OpenSearch、密钥环、证书和流量切换的完整隔离演练。连续两次未在 60 分钟内完成时，应视为 RTO 失守并阻止高风险发布。

## 恢复步骤

### 1. 宣告事件并冻结写入

记录事件编号、事件负责人、UTC 起点和目标恢复时间。停止入口流量和后台写入者，保留原环境只读证据；不得在故障库上直接试跑未知迁移。根据最后一条可用事务日志选择不超过 15 分钟 RPO 的恢复点。

### 2. 准备隔离恢复环境

按已批准的镜像摘要创建隔离环境，但暂不接入生产流量。先恢复应用信封密钥环、Data Protection 共享卷、cluster marker、活动/历史证书和密钥指纹配置。限制卷和秘密的访问主体，确认审计日志已启用。

### 3. 恢复并校验 SQL

按完整、差异、事务日志顺序恢复到选定时间点；跨主机恢复时用 `RESTORE FILELISTONLY` 确认逻辑文件，并用 `MOVE` 指向新环境的受控数据目录。随后必须执行：

```sql
RESTORE VERIFYONLY FROM DISK = N'<受控备份路径>' WITH CHECKSUM;
DBCC CHECKDB(N'<恢复数据库>') WITH NO_INFOMSGS, ALL_ERRORMSGS;
```

检查迁移清单的名称、顺序和 SHA-256 校验和，再使用受控迁移器补齐明确缺失的版本。不得手工把迁移账本标成成功。统计 SQL 中全部记忆、工作流、补偿和 Atlas 记录的 `KeyVersion`，任何未知版本都必须停止恢复。

### 4. 恢复 OpenSearch

从 Snapshot 恢复不可变物理索引、模板和搜索管线，或者从已签名语料发布清单完整重建。对租户/ACL 过滤、BM25 与向量分支、向量维度和固定查询集执行验收后，才可原子切换 `current` Alias；保留可回滚的 `previous` Alias。检索恢复失败不能以空索引伪装成功。

### 5. 单实例无流量验证

只启动一个 API 副本且不接入负载均衡。以下检查必须全部通过：

- `/health/ready` 返回成功，SQL schema、DML 权限、密钥覆盖、Data Protection 往返和 OIDC discovery 均通过。
- 读取事件前已存在的一条加密记忆，正文和版本正确，证明恢复的是原密钥环而不是空库。
- 读取一个 Atlas 检查点，状态、Runbook 版本和安全输入保持一致。
- 使用原 `Idempotency-Key` 回放已完成或已对账工具，必须返回 `IdempotentReplay=true` 且执行账本行数不增加。
- 结果不确定的工具和补偿仍保持 `OutcomeUnknown`，不得在恢复时自动重放副作用。

### 6. 恢复流量并观察

逐步扩容后先放入内部探测流量，再按批准比例恢复生产流量。持续观察 readiness、SQL 错误、历史密文解密失败、幂等冲突、Atlas 版本冲突和 OpenSearch 查询错误。确认稳定后解除写入冻结，并记录实际 RPO/RTO。

## 自动 SQL 恢复验收

下列命令会创建临时 SQL Server 数据库，运行真实多实例故障/对账场景，然后停止 API，执行 `BACKUP ... WITH COPY_ONLY, INIT, CHECKSUM`、`RESTORE VERIFYONLY WITH CHECKSUM`、删除并恢复同一数据库、`DBCC CHECKDB`，最后以原密钥重新启动 API：

```powershell
./scripts/Test-DistributedReconciliation.ps1 -UseEphemeralSqlServer -Configuration Release
```

成功输出必须稳定包含：`BackupVerified=true`、`DatabaseCheckPassed=true`、`RestoredMemoryValue=after`、`RestoredReplay=Reconciled`、`RestoredAtlasStatus=DiagnosisReady`、`RestoredExecutionRows=1`、`RestoredMigrationRows=14` 和 `Passed=true`。数据库由与 Helm Hook 相同的账本迁移器创建；恢复后还会用其只读模式逐项核对迁移名称、顺序、SHA-256 与关键表、列、索引和外键物理契约，不能只按账本行数宣告成功。这项验收证明 SQL 备份可恢复、历史密文可解密以及工具不会重复执行；它不替代 OpenSearch Snapshot、Data Protection 共享卷和外部密钥保管系统的完整演练。

## 立即停止恢复的条件

- 恢复校验和、`DBCC CHECKDB`、迁移清单或迁移校验和任一失败。
- SQL 中出现当前部署不认识的密钥版本，或历史记忆/工作流/补偿抽样解密失败。
- Data Protection 指纹、cluster marker、证书链或 Protect/Unprotect 往返失败。
- OpenSearch Alias、模型/维度、租户 ACL 或固定查询集验证失败。
- 幂等回放产生新账本行、再次执行工具，或 `OutcomeUnknown` 被自动接管。
- 无法证明目标恢复点在 15 分钟 RPO 内，或预计无法在 60 分钟 RTO 内安全恢复。

发生上述任一情况时保持流量冻结，保留日志和恢复环境，由事件负责人选择更早的已验证恢复点或升级到人工处置；不得删除原证据、伪造成功字段或绕过 readiness。

## 演练证据

每次演练保存事件编号、镜像摘要、备份标识及时间范围、目标/实际 RPO 与 RTO、迁移最高版本、OpenSearch Snapshot/物理索引/Alias、密钥版本名称与指纹（不含密钥材料）、自动验收 JSON、`DBCC CHECKDB` 结果摘要、审批人和遗留风险。证据应进入受访问控制的审计系统，并按组织保留策略归档。
