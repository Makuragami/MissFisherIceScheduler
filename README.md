# MissFisher ICE Scheduler

在 MissFisher 的钓鱼等待间隙运行 ICE，并在下一目标窗口前停止 ICE、切回捕鱼人及恢复所选 MissFisher 目标。

## 依赖

- Dalamud API 15
- MissFisher 2.3.1.x 或 2.4.0.0
- ICE 1.0.0.2000
- ICE 正常运行所需的 vnavmesh、Artisan、AutoHook 等插件

ICE 只能在它支持的宇宙探索区域运行。调度器不会修改 ICE 的任务配置；请先在 ICE 中配置并验证其任务模式，并在调度器中选择 ICE 启动时使用的生产/采集套装。

当前版本支持在“跨区步骤命令（用 || 分隔）”中依次调用已有的传送、寻路和任务插件，例如“传送到入口 || 前往入口 || 与入口交互”。调度器会逐步发送命令，并严格确认已经进入所选 ICE 区域后才启动 ICE。从任意地图全自动进入宇宙探索区域仍需要外部插件提供这些命令；ICE 1.0.0.2000 本身没有公开进入区域的 IPC，四个宇宙探索地图也不是普通传送目的地。未配置有效命令时，调度器会等待手动进入并在超时后恢复捕鱼。

默认开启“只观察”。输入 `/mfice` 打开配置。

## 命令

```text
/mfice enable
/mfice disable
/mfice abort
/mfice reset
/mfice status
```
