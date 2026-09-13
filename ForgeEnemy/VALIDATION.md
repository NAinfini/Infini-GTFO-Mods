# ForgeEnemy 验证记录

**上次更新：2026-09-13**（本文件为新建，内容合并自原 E1-BOUNDARY-HANDOFF、E1-NATIVE-CUTOVER、E1-PLUGIN-LIFETIME-SAFETY、E1-R3-INTEGRATION、E2-DELIVERY、E2-OBSERVATION-HANDOFF、E23-CONTINUATION、E3-LIFECYCLE-FACTS、E3-SAFETY-HANDOFF 九份交接记录）。

## 当前结论

E1 的源码与程序集切换完成，R3 实体观察在共享 SDK 中实际接通，E3 的死亡流程与肢体破坏两个事件已接入真实 provider 和计划调度。完整宿主与 Native 插件构建都通过，0 警告 0 错误。

**全部结果都是 implementation-only。** 没有启动 GTFO、没有安装、没有主客机或迟加入测试、没有执行任何原生游戏方法。E2 的空间要求、E3 剩余的 damage 与 status、E4–E7 全部未完成。

## 最后一次记录的通过数

不同套件覆盖不同边界并共享用例，**任何两行都不得相加成独立机制数**。

| 套件 | 结果 | 口径 |
| --- | --- | --- |
| 生命周期事实（LifecycleFacts） | 52/52 | 死亡流程与肢体破坏；含原生 Hook 适配器与真实计划→Heal 联调 |
| 接收器（ReceiverProbe） | 40/40 | 唯一现行接收器 |
| 实体观察（EntityObservation） | 66/66 | 通过实际共享 SDK |
| 插件生命周期（NativePlugin） | 24/24 | 跨线程、卸载、失败清理 |
| 提交路径审计（CommitAudit） | 现行路径 32/32 | 辅助组件重复的 20 个治疗用例另记，不算独立机制 |
| cutover 布局（NativeLayout） | 38/38 | Enemy 五 Hook、Runtime 四 Hook |
| 原生静态审计 | 112/112 | 99 个精确签名；读元数据与 PE 指令，不加载也不调用游戏方法 |
| 架构 / Framework | 36 / 253 | 253 已含 timing 116、result 49 |
| cutover CLI 防错 / 迁移检查脚本 | 7/7 / 16 项 | 正常基线加六种故意回退 |

`ForgeEnemy.Native` 与完整宿主的零警告结果单独记录。Trigger 的测试工程构建有 3 条 NETSDK1138 目标框架提示，0 错误；本仓库没有切换目标框架。

## E1 / R3 切换

唯一 `EnemyModule` 从 `ForgeRuntime/GameBindings/` 移入 `ForgeEnemy/Native/EnemyModule.cs` 并切换命名空间。Runtime 不再创建或清理 Enemy provider，只保留四个世界、会话与检查点 Hook；三个敌人 Hook（后来扩为五个）由独立的 Native 插件拥有，空的 `ModuleDefinition.Create` 已删除。所有实际的 receiver、bridge、插件、实体观察测试的源码引用同步更新，不保留兼容执行副本。Native 仍单向引用宿主与唯一 SDK；SDK 无 Unity/BepInEx 依赖，Runtime 无 Enemy 生产依赖。

R3 在既有 Registry 中登记了带所有权和容量校验的观察函数，注销时清理，并接上只读回调约束。跨模块测试进一步检出生命周期订阅注销的缺口：`RemoveLifecycleObserver` 现在只在实体观察期间拒绝修改，仍允许普通清理、停止后清理与生命周期回调自注销。

集成修改经过 25 个目标路径的哈希前置检查，没有恢复或覆盖并发的 Trigger 工作。首次全快照检查发现四个未拥有的 Trigger 文件发生变化，保留了它们的工作树并刷新测试副本。

E23 早期留下的七个未使用草稿与空测试工程已移出源码目录，原字节备份保留在该批的 `obj/retired-drafts`。清理前核对了源码、工程与解决方案引用；同名的 `EnemyHealthSnapshot` 的现有使用明确属于 `Receivers` 命名空间，不是被退休的根目录草稿。

## E3 事件的验证细节

新事件套件 52/52，包含原生 Hook 适配器（作为托管测试代码调用 prefix/postfix，不注入）、真实计划 → 现行 Heal 的 +5 HP 正例，以及"死亡目标不隐式复活"的负例。六种事件错误变体全部被指定断言检出，原 receiver 的六种错误变体同样检出；**编译失败不算通过**。

两处初始失败保留：首次 50 项用例检出"owner ID 变化而指针相同"的缺口，补上 owner ID 校验后通过；新增的 Heal 联调最初因夹具的权限与绑定列表未按 canonical ordinal 排序被严格加载器拒绝，修正夹具排序后通过——**没有降低加载器校验**。

原生签名扩至 99 个、静态审计 112/112；新增的 `DestroyLimb` 与四个部位读取入口都核对了实际 interop。

## 接收器安全修复的复现记录

原始 13 项探针先复现 8/13。**E2-002 一项没有被修成通过**：它只给出同指针的新 wrapper，没有可证明的新生命边界，失败证据保留。v2 显式加入"捕获旧生命 → 销毁 → 同指针重生 → 重放旧令牌"，同时验证 wrapper 本身不代表新生命；扩展后 34/34 通过。裸指针的迟到网络回调如何取得原生代次，仍属 E2/E7 的未验证范围。

插件生命周期的 18 项用例最初 12/18 通过、六个新用例全部失败；修复并扩展后 24/24。移除一层冗余外层检查后仍被下一层保护的一个探针单独记录，**不冒充检错成功**。

## 输入锁

本机 Steam app 493520 / build 20403457；游戏与 BepInEx 只作只读输入。没有复制游戏 DLL、模型或第三方代码到可分发目录。

| 输入 | SHA-256 |
| --- | --- |
| GameAssembly.dll | `C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55` |
| interop/Modules-ASM.dll | `A31AF38FAC3F96F1130CF7CDFCD10AE436E0D553EDA7163B9A9FF82392907943` |
| interop/GameData-ASM.dll | `3A74E6656CDA3A563290BBE71B70578E1E8D0745A5ECDDD05C894934A7A8CC7B` |
| interop/SNet_ASM.dll | `99175A1EF40454C1C8DD45D86D9EB9D3DF4D24891FB26ED649835BA25E7360B2` |

冻结的元数据 JSON 保存三个 interop 的 MVID。**哈希和元数据证明这份输入的签名，不证明原生参数语义、合法提交阶段、复制完成或性能。**

审计工具本身也验过：正常基线通过，10 个故意错误的 build / hash / MVID / 签名 / 标志 / 类型 / 缺失声明 / schema 输入全部被精确检出，共 11/11 项工具检错检查通过。

## 复跑

先按 [README 的构建入口](README.md#复跑) 得到 `$hostDll` 与 `$sdkDll`，然后从仓库根运行，输出目录必须隔离：

```powershell
dotnet build ForgeEnemy/tests/LifecycleFacts/LifecycleFacts.csproj -c Release `
  "-p:ForgeFrameworkAssembly=$sdkDll" --artifacts-path $out
dotnet "$out/bin/LifecycleFacts/release/LifecycleFacts.dll" `
  ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime "$out/lifecycle-result.json"
python ForgeEnemy/tests/LifecycleFacts/verify_mutations.py --sdk $sdkDll `
  --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime --output "$out/mutations"
python ForgeEnemy/tests/ReceiverProbe/verify_mutations.py
python ForgeEnemy/tests/NativeEvidence/verify_negative_cases.py
python ForgeEnemy/tests/CutoverGuard/test_guard.py
```

各测试目录的边界说明见 [提交路径审计](tests/CommitAudit/README.md)、[实体观察](tests/EntityObservation/README.md)、[生命周期事实](tests/LifecycleFacts/README.md)。每批的实际命令与退出码记录在对应的 `evidence/` 目录的 `commands.json` 里。

构建失败时不要运行旧的可执行文件当作新构建的结果。在活动工作区里构建时要比较前后的源码哈希——源码变化意味着报告只对那个版本有效，不是最新文件的证明。

## 边界

原生读取、替身、编译和元数据是不同的证据层，任何一层通过都不代表游戏或多人已通过。GameBindings 的 fixture 60、bridge 57、host native metadata 58 三种模式共享基础断言（58 是 E1 切换后的数字，切换前记录为 62，需重跑确认）。

没有安装、发布、启动游戏、修改用户 profile、Git 提交或推送。
