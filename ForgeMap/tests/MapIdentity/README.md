# MapIdentity：实际托管实现验收

本测试引用编译后的 `ForgeMap.dll` 和唯一 `ForgeRuntime.Framework.dll`。
生产身份类通过 friend assembly 可见；不在测试内复制身份表或链接生产源码。
`IdentityFixture` 只模拟 native 存活、精确创建生命见证和回调。
Callback 只投递观察，不改变物理生命；Bind 才建立测试中的新原生生命。
这一区分验证迟到回调不能把旧生命写回当前生命表。

覆盖地址/来源锁、int64 pathId、重复观察、冲突隔离、取消/销毁、world/generation/life、
指针与 Unity ID 同时复用、probe 异常/重入、跨线程、停机/注销、历史上限和不完整采集。
这些是托管实现测试，不能替代原生创建 API、权限/阶段、主客机、导航或恢复测试。
`../fixtures/native-identity-scenarios.json` 仍明确未执行原生场景。

从仓库根运行，输出目录必须位于 ForgeMap 内且尚不存在：

```powershell
python ForgeMap/tools/run_identity_checks.py --out ForgeMap/evidence/identity-recheck-01
```

runner 同时运行本套测试、原有 MapContracts 与 Architecture；构建产物隔离在 Map/bin。
构建失败不会运行旧 DLL；保存命令、退出码、stdout/stderr、测试 JSON、DLL 哈希和源码摘要。
检查期间相关源码发生变化，最终结果不会标为通过；不隐去并发编辑的中间态。
报告中的 `nativeGameExecuted: false` 与模块空运行清单必须保持真实。

交付边界见 [模块说明](../../README.md) 和 [验证记录](../../VALIDATION.md)。
