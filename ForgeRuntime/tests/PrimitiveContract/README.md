# Primitive 合同检查

在模组仓库根目录运行：

```powershell
dotnet run --project ForgeRuntime/tests/PrimitiveContract -- ../Infini-GTFO-Model-Site/Docs/forge-contract/primitive/primitives.json
```

输入来自网站仓库唯一的 Primitive 文件，不复制一份测试标准。
测试直接编译并调用 Framework 的合同检查代码，不写第二套类型判定器。

检查范围：

- 文件中所有 Primitive 的技术合同格式，包括提案；格式通过不等于提案已实现。
- 共享案例中对象种类的宽窄赋值关系，包括炮台作为治疗来源、任意对象不得冒充玩家。
- `CombatContracts.Module()` 实际提供的治疗合同必须等于 Primitive 投影，并包含实际恢复量。

其他连接维度由网站的共享连接案例检查；这里的对象种类测试不代表 C# 执行计划加载器已覆盖全部连接维度。
本项目不启动 GTFO，不证明原生行为、多人同步或已安装发行包通过验证。

治疗的 C# 声明由网站仓库的 `pnpm build:primitive-runtime` 生成；用 `pnpm check:primitive-runtime` 检查漂移。
游戏运行时只消费编译好的程序集，不读取网站仓库或本测试的输入文件。
