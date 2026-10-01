# Specs — RoomForge（核心：双低音空间均匀性）+ 后加功能轨

本目录采用 **Kiro / Claude Code spec mode** 三阶段规格。

> **产品身份（锁定）：** [ModerRAS/RoomForge](https://github.com/ModerRAS/RoomForge)（solution：`AudioOptimizer.sln`）的**核心产品**是已交付的 **双低音听音区空间均匀性优化器**。  
> **ART / 相位校准 / CamillaDSP 导出** 是用户稍后提出的 **可选后加功能轨**，不得改写产品身份，不得把双低音降为脚注，不得要求替换 `SubwooferOptimizer`。

> **v2.1（2026-10-01）framing fix：** 废除 v2.0 中「Product A / Product B 对等」叙事；以核心产品契约开篇，后加轨降权为 Extension。  
> **v2.0：** 废弃 v1.1 Rust greenfield；基线见 [`../research/roomforge-baseline.md`](../research/roomforge-baseline.md)。

旧总览 [`../PLAN.md`](../PLAN.md) 仍标为 **OBSOLETE（v1.x Rust greenfield）**；以实现代理为准的权威文档是本目录三文件。

## 三阶段

| 阶段 | 文件 | 用途 |
|------|------|------|
| 1. Requirements | [`requirements.md`](requirements.md) | **先**核心产品契约（BASELINE）；**后**后加轨 EARS（Extension） |
| 2. Design | [`design.md`](design.md) | 核心层保留；后加以新项目/命名空间加性扩展；开放 ADR |
| 3. Tasks | [`tasks.md`](tasks.md) | M0 = 文档化/保全核心；后加任务仅在「双低音产品契约非回归」门后启动 |

## 推荐工作流

1. 先读 `../research/roomforge-baseline.md`：**原始设计目标**与已交付管线。
2. 通读 `requirements.md`：§0–§4 核心产品 → §5+ 后加轨；勿从 ART 章节倒推产品定义。
3. 按 `design.md` 扩展时 **不得**破坏双低音 UI、契约测试、Simulation regression。
4. 按 `tasks.md`：先 M0 保全；再做后加切片。

## 核心产品（已交付）— 摘要

- **使命：** 双低音（Sub A 固定 / Sub B 可调）在**已测听音网格**上最小化空间不均匀度。
- **管线：** 网格三模式 A / B / AB（`SubMode`）→ ESS + Farina + FFT → WASAPI `PlayAndRecord` → 正向模型复数求和 → 仅搜 B → 硬 boost 拒绝 + ProductSafety ≤ **3.0 dB**。
- **契约：** 已测位置保证、AB 线性叠加校验（§21）、三角色 boost 报告、固定 0.5 dB 不确定度裕量、level-invariant 分数 + 单独 `LevelChangeDb`。
- **栈：** C# `net10.0` / `net10.0-windows`、WPF、GPL-3.0。

## 后加功能轨（规划）— 摘要

- 多扬声器 ART-like：**primary + N supports**（与双低音 A/B **并存**，非替换）
- Excess phase → near-min-phase 校准（`REQ-PHASE-*`）
- CamillaDSP Mixer + Conv 导出
- **前置条件：** 不破坏双低音 UI / 产品契约 / 既有测试；不强制替换 `SubwooferOptimizer`

## 相关研究

- [`../research/roomforge-baseline.md`](../research/roomforge-baseline.md) — **原始设计目标** + 仓库清单
- [`../research/dirac-phase-calibration.md`](../research/dirac-phase-calibration.md) — excess→near-min-phase 研究笔记（后加轨）

*Spec 版本：**v2.1** · 2026-10-01 (CST) · framing fix：核心优先*
