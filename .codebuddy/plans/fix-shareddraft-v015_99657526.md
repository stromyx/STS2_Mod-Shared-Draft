---
name: fix-shareddraft-v015
overview: 修复SharedDraft三个Bug：跳过按钮改为不选卡(opt-out)、NCard卡面显示偏移被裁剪、右键查看卡片详情被选卡UI遮挡。
todos:
  - id: fix-skip-optout
    content: 修改 OnSkipPressed 为 opt-out 逻辑，在 SharedDraftManager/Synchronizer/NetDraftAction 中新增 opt-out 信号支持（encodedValue=-2）
    status: completed
  - id: fix-ncard-display
    content: 修复 PopulateCardInCell 中 NCard 卡面显示：设置 PivotOffset 为中心点、调整 Position 居中、关闭 ClipContents
    status: completed
  - id: fix-inspect-overlay
    content: 修改 OpenCardInspector 在打开详情前隐藏 CanvasLayer，通过 VisibilityChanged 信号在关闭后恢复显示
    status: completed
---

## 产品概述

修复 SharedDraft（共享选卡）mod 的三个 UI/功能问题，提升用户体验。

## 核心功能

### 1. 跳过按钮改为"不选卡"（Opt Out）

- 当前"跳过"按钮会自动选择一张卡牌加入牌组，用户期望跳过意味着"不选择任何卡牌"
- 点击跳过后，玩家状态标记为已跳过，不会获得任何卡牌
- 跳过状态需要通过网络同步给其他玩家
- 侧边栏玩家状态显示"已跳过"标识

### 2. 修复 NCard 卡面显示偏移/被裁剪

- 当前每张卡只显示了右下角区域，卡牌名称、费用、图片被裁剪
- 原因是 NCard 内部内容不是从左上角 (0,0) 开始绘制，而容器使用了 ClipContents=true 从左上角裁剪
- 修复后卡面应完整居中显示在网格单元格内

### 3. 右键查看卡片详情时不被选卡界面遮挡

- 当前右键打开的 NInspectCardScreen 渲染层级低于 SharedDraft 的 CanvasLayer(Layer=95)，导致详情被遮挡
- 修复后打开详情时临时隐藏选卡界面，关闭详情后恢复显示

## 技术栈

- 语言：C# (.NET)
- 引擎：Godot 4 (GodotSharp)
- 框架：Harmony 补丁系统（用于游戏 mod）
- 目标游戏：Slay the Spire 2

## 实现方案

### 问题1：跳过按钮改为 Opt Out

**策略**：新增 opt-out 专用编码值 `-2`，复用现有 `DraftGameAction` 网络同步机制。

**关键改动**：

1. `DraftGameAction` / `NetDraftAction`：新增 `OptOutSignalValue = -2` 常量，在 `ExecuteAction` 中增加对 `-2` 的分支处理
2. `SharedDraftSynchronizer`：新增 `BroadcastOptOut()` 方法发送 opt-out 信号，新增 `HandleNetworkOptOut(Player)` 处理接收
3. `SharedDraftManager`：新增 `OnLocalPlayerOptOut()` 方法，标记本地玩家 `IsOptedOut = true`，并触发网络广播
4. `SharedDraftScreen.OnSkipPressed()`：改为调用 `manager.OnLocalPlayerOptOut()` 而非自动选卡，更新状态提示为"已跳过选卡"

**关键决策**：使用 `-2` 作为 opt-out 编码值（`-1` 已用于 ready 信号），利用 32 位 int 序列化不会有截断问题。`AllPlayersSelected()` 已有 `.Where(ps => !ps.IsOptedOut)` 过滤，opt-out 玩家会自动被跳过等待。

### 问题2：NCard 卡面偏移/被裁剪修复

**根因分析**：

- NCard 的 `card.tscn` 场景中，内容绘制可能不是从 Control 的 (0,0) 左上角开始
- 当前代码设置 `nCard.Scale = 0.65`，Godot 的 Control Scale 以 `PivotOffset` 为中心缩放，默认 PivotOffset = (0,0) 即左上角
- `cardContainer.ClipContents = true` 裁剪到 195x274，而 NCard 的 Size 仍然是 300x422（不受 Scale 影响），因此只有 NCard 内部坐标 (0,0) 到 (195,274) 的区域可见——如果内容的绘制中心不在左上角，就会显示错误区域

**修复策略**：

1. 设置 NCard 的 `PivotOffset = NCardDefaultSize / 2`（即 150, 211），使缩放以卡面中心为基准
2. 设置 `nCard.Position = ScaledCardSize / 2`（即 97.5, 137），将缩放后的中心点对齐到容器中心
3. 关闭 `cardContainer.ClipContents = false`，因为缩放后视觉尺寸已经约等于容器大小，不需要裁剪
4. 容器保持 `CustomMinimumSize = ScaledCardSize` 用于布局占位

### 问题3：右键详情被遮挡修复

**策略**：打开 InspectCardScreen 前隐藏 `_canvasLayer`，通过 `VisibilityChanged` 信号在 InspectCardScreen 关闭时恢复。

**关键分析**：

- `NInspectCardScreen.Close()` 中 `base.Visible = false` 是通过 Tween 回调延迟执行的（第220-224行），所以 `VisibilityChanged` 信号会在关闭动画结束后触发
- 使用 `VisibilityChanged` 信号而非轮询，零性能开销
- 需要确保信号只连接一次（一次性回调），避免重复连接

**实现细节**：

```
打开前: _canvasLayer.Visible = false
连接信号: inspectScreen.VisibilityChanged += handler
handler中: 
  if (!inspectScreen.Visible) {
    _canvasLayer.Visible = true
    inspectScreen.VisibilityChanged -= handler  // 断开一次性连接
  }
```

## 实现注意事项

- **网络兼容性**：opt-out 信号使用 `-2`，与现有 `-1`（ready）和 `0+`（draftId）不冲突。`NetDraftAction` 的 32 位序列化支持负数
- **边界情况**：如果所有玩家都 opt-out，`AllPlayersSelected()` 返回 true（所有非 opt-out 玩家都已选），流程正常进入 Resolving → Awarding（无人获卡）
- **Debug 模式**：opt-out 在 debug 模式下直接本地处理，无需网络广播
- **卡面修复验证**：修改后 NCard 缩放以中心为基准，视觉大小约 195x274 居中显示在同等大小的容器内
- **InspectCardScreen 信号安全**：使用 `IsInstanceValid` 检查避免在节点已销毁时操作信号

## 架构设计

数据流（opt-out）：

```
用户点击"跳过" → SharedDraftScreen.OnSkipPressed() 
  → SharedDraftManager.OnLocalPlayerOptOut()
    → 设置 IsOptedOut = true
    → SharedDraftSynchronizer.BroadcastOptOut() [真人多人]
      → DraftGameAction(player, -2) → 网络广播
      → 其他客户端 HandleNetworkOptOut() → SubmitRemoteOptOut()
```

## 目录结构

```
SharedDraft/
├── UI/
│   └── SharedDraftScreen.cs    # [MODIFY] 修改 OnSkipPressed() 为 opt-out 逻辑；
│                                #          修复 PopulateCardInCell() 中 NCard PivotOffset 和 Position；
│                                #          修改 OpenCardInspector() 添加隐藏/恢复逻辑
├── SharedDraftManager.cs       # [MODIFY] 新增 OnLocalPlayerOptOut() 和 SubmitRemoteOptOut() 方法
├── SharedDraftSynchronizer.cs  # [MODIFY] 新增 BroadcastOptOut()、HandleNetworkOptOut() 方法
└── NetDraftAction.cs           # [MODIFY] 新增 OptOutSignalValue = -2 常量，
                                #          ExecuteAction() 增加 opt-out 分支处理
```

## Agent Extensions

### SubAgent

- **code-explorer**
- Purpose: 在实现过程中需要验证 NCard PivotOffset 行为以及 Godot VisibilityChanged 信号的触发时机时，可用于快速搜索反编译代码中的相关实现细节
- Expected outcome: 确认 NCard 场景内部的坐标系和信号行为，确保修复方案正确