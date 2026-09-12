# MissFisher ICE Scheduler

在 MissFisher 的钓鱼等待间隙运行 ICE，并在下一目标窗口前停止 ICE、切回捕鱼人及恢复所选 MissFisher 目标。

## 依赖

- Dalamud API 15
- MissFisher 2.3.1.x
- ICE 1.0.0.2000
- ICE 正常运行所需的 vnavmesh、Artisan、AutoHook 等插件

ICE 只能在它支持的宇宙探索区域运行。调度器不会修改 ICE 的任务配置；请先在 ICE 中配置并验证其任务模式，并在调度器中选择 ICE 启动时使用的生产/采集套装。若 MissFisher 等待时角色不在月面，需要在配置中填写能进入目标区域的命令，否则调度器会保持等待并在三分钟后恢复捕鱼。

默认开启“只观察”。输入 `/mfice` 打开配置。

## 命令

```text
/mfice enable
/mfice disable
/mfice abort
/mfice reset
/mfice status
```
