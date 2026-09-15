# DamageWindow

E10 敌人伤害窗口的离线证据提取器，产出 `ForgeEnemy/evidence/e10-damage-window.json`。

它只读两种输入：GTFO 的 `GameAssembly.dll`（PE 头、`.text` 与 `il2cpp` 节字节）与 Il2CppDumper 生成的 `dump.cs`（方法 RVA、virtualSlot、字段偏移、枚举值）。它不加载游戏程序集、不调用游戏方法、不启动游戏，也不写任何 profile。反汇编用 BepInEx 自带的 Iced，只在有界窗口内解码：每个评审入口一段，每个调用点从 call 指令本身开始一段。

```powershell
$env:GTFO_BEPINEX_PATH = "$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx"
dotnet build ForgeEnemy/tests/DamageWindow/DamageWindow.csproj -c Release --artifacts-path $out
dotnet $out/bin/DamageWindow/release/DamageWindow.dll `
  E:\SteamLibrary\steamapps\common\GTFO <dump.cs> ForgeEnemy/evidence/e10-damage-window.json
```

退出码 0 表示提取完成；2 表示用法错误、输入缺失、dump 映射锚点不符（`SendSetHealth` 0x161F790、`ReceiveSetHealth` 0x1380D50、`ProcessReceivedDamage` 0x137E570 必须按该 RVA 解析到），或某个冻结条目在 dump 里找不到、参数个数与 dump 声明不同。生成物应与仓库内文件字节一致。

**调用边是定位，不是证明。** 扫描只按 `E8 rel32` 找目标地址，再要求目标地址是本次冻结类型的方法；调用者归属用"起始地址最近且在方法体内"的规则，且排除自我调用。真正的证明在 `tests/NativeEvidence`：它重新反汇编每个冻结窗口，并要求窗口内出现指向该目标的 call 指令。RVA→方法的绑定来自该构建的 dump，审计只证明地址上的指令形状。

**边界。** 这里没有任何运行期证据：伤害数值、复制时序、主客机行为、炮塔来源归属都未验证。未解析的虚表槽、未被本窗口覆盖的发射点（例如工具/炮塔调用 `BulletDamage` 的位置）不在这份证据的结论范围内。
