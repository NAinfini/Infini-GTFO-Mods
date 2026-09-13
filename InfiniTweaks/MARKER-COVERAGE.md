# 标记覆盖清单 — Infini Tweaks 2.5.4

对照 Hikaria ItemMarker 1.1.0 的 66 条拾取物定义及 7 个设备 marker 类型：64 条拾取物有对应新图，2 条未确认／不制作专用外形的条目使用通用问号新图；其中 6 类设备使用新图；安全门标记按用户要求取消。共 58 张 PNG（部分数据条目共享图案）。未知模组物品也走新图标，不返回原生分类图案。

源码覆盖与实机可见性分开：物品按实际 ResourcePack／inventorySlot 分类进入同一登记、发现、拾放和距离流程，不靠这张 ID 表过滤物品。下表确认图标路由；未进行本轮 Unity、多人或完整关卡逐项实测。

参考： [ItemMarker](https://github.com/Mamizu1028/GTFO_ItemMarker) 的 Settings/ItemInLevelMarkerDefinitions.json、Handlers/Markers/*.cs；[HUDInfoPlus](https://github.com/Mamizu1028/GTFO_CGHUDInfo) 主要处理玩家资源 HUD，不是场景设备定义的来源。锁定版本和 commit 见 UPSTREAM-ACCEPTANCE.md。

## 设备

| 上游类型 | 本项目路由 | 图案及显示条件 |
| --- | --- | --- |
| ComputerTerminal | RegisterComputer | terminal.png；发现后按范围显示 |
| PowerGenerator | RegisterGenerator | generator.png；可插电池且槽位可见。图案为 Generator_Slot_a，不是环境电机道具 |
| DisinfectionStation | RegisterDisinfection | disinfection-station.png；消毒交互有效 |
| HSU | RegisterHsu | hsu.png；取样交互有效 |
| HSUActivator | RegisterActivator / RegisterCustomActivator | hsu-activator.png；插入交互有效，与固定 HSU 分开分类 |
| BulkheadDoorController | RegisterController | bulkhead-control.png；控制器仍可操作 |
| SecurityDoor_Locks | 不登记 | 按用户要求移除，包含锁住的安全门；door.png 仅为图案库资产 |

## 拾取物

| ID | 上游名称 | 本版图案 |
| --- | --- | --- |
| 27 | Red keycard | keycard-red.png |
| 85 | Blue keycard | keycard-blue.png |
| 86 | Green keycard | keycard-green.png |
| 87 | Yellow keycard | keycard-yellow.png |
| 88 | White keycard | keycard-white.png |
| 89 | Black keycard | keycard-black.png |
| 90 | Grey keycard | keycard-grey.png |
| 91 | Orange keycard | keycard-orange.png |
| 92 | Purple keycard | keycard-purple.png |
| 93 | Gold keycard | keycard-gold.png |
| 94 | Brown keycard | keycard-brown.png |
| 146 | Bulkhead Key | bulkhead-key.png |
| 113 | Flare | flare.png |
| 30 | Long Range Flashlight | flashlight.png |
| 114 | Glow Stick | glow.png |
| 130 | Red Glow Stick | glow-red.png |
| 167 | Glow Stick Orange | glow-orange.png |
| 174 | Glow Stick Yellow | glow-yellow.png |
| 140 | I2-LP Syringe | syringe-health.png |
| 142 | IIx Syringe | syringe-speed.png |
| 136 | Glow Stick | glow.png；当前数据块引用普通荧光棒模型 |
| 115 | C-Foam Grenade | foam.png；按用户要求绘制的闪光弹式圆柱手雷 |
| 116 | Lock Melter | lock.png |
| 117 | Fog Repeller | fog.png |
| 139 | Explosive Trip Mine | mine.png |
| 159 | Shaped Charge Explosive Mine | unknown-item.png；未确认成品外形，不捏造模型 |
| 144 | C-Foam Tripmine | foam-mine.png |
| 157 | Portable Barrier | 不制作专用图（用户排除）；若自定义作品使用此条目，则使用 unknown-item.png |
| 101 | Ammo Pack | ammo.png |
| 102 | MediPack | health.png |
| 127 | Tool Refill Pack | tool.png |
| 132 | Disinfection Pack | disinfection.png |
| 128 | Personnel ID | personnel-id.png |
| 129 | Partial Decoder | decoder.png |
| 147 | Hard drive | hard-drive.png |
| 183 | Micro Drive | hard-drive.png；与硬盘共享图案，未验证独立微型硬盘造型 |
| 149 | GLP Hormone | glp.png |
| 150 | OSIP Hormone | osip.png |
| 169 | GLP Hormone mk 2 | glp-mk2.png |
| 168 | TAMPERED_DATA_CUBE | data-cube.png |
| 165 | DATA_CUBE | data-cube.png |
| 179 | DATA_CUBES | data-cubes.png |
| 178 | BACKUP_DATA_CUBE | data-cube.png |
| 153 | Plant Sample | plant-sample.png |
| 171 | Memory_Stick | memory-stick.png |
| 172 | Memory_Stick | memory-stick.png |
| 180 | Hard_drive | hard-drive.png |
| 131 | Power Cell | cell.png |
| 133 | Fog Repeller Turbine | turbine.png |
| 137 | NEONATE HSU | neonate.png |
| 141 | NEONATE HSU | neonate.png |
| 143 | NEONATE HSU | neonate-open.png |
| 170 | NEONATE HSU | neonate-open.png |
| 164 | MATTER_WAVE_PROJECTOR | projector.png |
| 145 | IMPRINTED NEONATE HSU | imprinted-hsu.png |
| 175 | IMPRINTED NEONATE HSU | neonate-open.png |
| 177 | IMPRINTED NEONATE HSU | neonate-open.png |
| 151 | DATA SPHERE | data-sphere.png |
| 181 | DATA SPHERE | data-sphere.png |
| 166 | MATTER_WAVE_PROJECTOR | projector.png |
| 138 | Cargo Crate | cargo.png |
| 176 | Cargo Crate | cargo-case.png |
| 148 | Cryo Hardcase | cryo.png |
| 154 | Cargo Crate High Security | cargo.png |
| 155 | Cargo Crate High Security Open | cargo.png |
| 173 | Collection Case | collection-case.png |

## 导出和运行时

每张 PNG 是 256×256 真透明 RGBA；颜色在图片中，SpriteRenderer 固定不透明白色，不继承原生 alpha；PNG 透明背景和抗锯齿边缘保留。Sprite保持一单位纹理尺寸；可见图案统一最长边224px，自定义标记显示倍率保持0.72；以alpha≥128的主体边界统一最长边约224px并居中，保留宽高比。所有图标内嵌 DLL，按图案共享 Sprite，无外部图标加载插件。

Windows ICO 导出含 16、32、48、64、128、256 像素版本。图标包和不同尺寸预览与模组包独立提供。
