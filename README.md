# MissFisher ICE Scheduler

在 MissFisher 的钓鱼等待间隙运行 ICE，并在下一目标窗口前停止 ICE、切回捕鱼人及恢复所选 MissFisher 目标。

## 依赖

- Dalamud API 15
- MissFisher 2.3.1.x 或 2.4.0.0
- ICE 1.0.0.2000
- ICE 正常运行所需的 vnavmesh、Artisan、AutoHook 等插件

ICE 只能在它支持的宇宙探索区域运行。调度器不会修改 ICE 的任务配置；请先在 ICE 中配置并验证其任务模式，并在调度器中选择 ICE 启动时使用的生产/采集套装。

默认使用内置月球入口流程，不依赖快捷传送面板或 `/tp` 命令：调度器通过稳定的以太之光 ID `175` 调用游戏传送服务前往“最佳兔威洞”，通过 vnavmesh 移动到已确认的入口世界坐标，查找并与“驾行威”交互，自动推进对话和确认。传送请求未生效时会自动重试，只有严格确认已经进入所选 ICE 区域后才会切换职业并启动 ICE。

内置流程可在配置中关闭。关闭后仍可使用“跨区步骤命令（用 || 分隔）”调用其他插件提供的传送、寻路和交互命令。

默认开启“只观察”。输入 `/mfice` 打开配置。

## 命令

```text
/mfice enable
/mfice disable
/mfice abort
/mfice reset
/mfice status
```

## 月球入口测试

1. 等待当前 MissFisher 钓鱼任务结束，停止 MissFisher，并关闭 ICE。
2. 在调度器中勾选“使用内置最佳兔威洞入口流程”，确认 ICE 目标区域和生产/采集套装正确。
3. 确认 vnavmesh 已启用并完成当前地图构建。
4. 启动恢复目标，将等待阈值临时调到大于下一目标等待时间，关闭“只观察”，再启用调度。
5. 观察状态依次经过“暂停 MissFisher / 传送 / 前往驾行威 / 处理对话 / 切换职业 / 启动 ICE”。

入口菜单根据“ICE 目标区域”自动选择：Sinus Ardorum、Phaenna、Oizys、Auxesia 分别对应第 1～4 项。选择后会等待换区并严格核对实际区域，避免重复与 NPC 交互。
