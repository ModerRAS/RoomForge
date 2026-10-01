# Requirements Specification — RoomForge 核心产品 + 后加功能轨

**Document ID:** `SPEC-REQ-001`  
**Version:** 2.1  
**Date:** 2026-10-01 (CST)  
**Status:** Approved for implementation（v2.1 framing：核心产品优先；后加轨降权）  
**Predecessor:** [ModerRAS/RoomForge](https://github.com/ModerRAS/RoomForge)（`AudioOptimizer.sln`，GPL-3.0）  
**Related:** [`design.md`](design.md), [`tasks.md`](tasks.md), [`../research/roomforge-baseline.md`](../research/roomforge-baseline.md)

---

## 0. Framing（相对 v2.0 / v1.1）

| 错误叙事 | v2.1 正确叙事 |
|----------|---------------|
| 「Product A / Product B 对等，共同定义项目」 | **核心产品** = 已交付双低音空间均匀性优化器；ART/相位/Camilla = **后加功能轨** |
| 「测量套件等待 ART」 | 双低音优化器 **就是** 产品；测量是其已交付管线 |
| 「ART 可替换 SubwooferOptimizer」 | **禁止**；后加轨加性引入，契约非回归 |
| v1.1：Rust / WebUI / ALSA 从零 | **废除**；扩展现有 C# / WPF / WASAPI |

用户更正（2026-10-01）：「你要先看好原本的项目设计目标是啥，后面我要的东西属于后加的功能了。」

---

## 1. Introduction

### 1.1 Purpose

本文档分两层：

1. **§3–§4：核心产品（已交付）** — 以 BASELINE 承认 RoomForge 现有行为与产品契约（不重建）。
2. **§5+：后加功能轨（规划）** — 以 EARS 表述 ART-like 多扬声器支撑、相位校准、CamillaDSP 导出；明确前置条件与禁区。

### 1.2 Product Positioning

| 是 | 不是 |
|----|------|
| **核心：** 双低音听音区 **空间均匀性** 优化器（`SubwooferOptimizer` 等） | 「为 ART 准备的测量壳」 |
| 已交付：ESS→去卷积→网格会话→正向模型→硬 boost 契约→WPF | 需要用 ART 重新定义身份的产品 |
| **后加（可选）：** primary + N supports、excess→near-min-phase、Camilla 导出 | 与核心对等的「第二产品」 |
| C# / .NET 10 + WPF；Windows-first；GPL-3.0 | P0 Rust / WebUI / Linux-ALSA 重写 |

### 1.3 EARS Keyword Convention

保留英文 EARS 关键词：`WHEN` / `GIVEN` / `WHILE` / `WHERE` / `IF` / `THEN` / `THE SYSTEM SHALL` / `THE SYSTEM SHALL NOT`。需求 ID、类型名、项目名使用英文。描述性散文用中文。

**优先级：**

| 标记 | 含义 |
|------|------|
| `BASELINE` | RoomForge **已具备**；规格承认；任务不得重建 |
| `P0` / `P1` / `P2` | **仅用于后加轨**交付优先级 |
| `P2-internal` | 仅内部 / feature-flag |

### 1.4 Actors

| Actor ID | 名称 | 说明 |
|----------|------|------|
| ACT-USER | 影院 / HiFi 用户 | **主路径：** 双低音测量与优化；可选后加 ART/导出 |
| ACT-ADV | 进阶用户 | 同设备 duplex、通道映射、ASIO（后加/进阶） |
| ACT-DEV | 开发 / 调试者 | Simulation lab、产品契约测试、debug scaffold |
| ACT-SYS | RoomForge（WPF + AudioOptimizer.*） | — |
| ACT-CDSP | CamillaDSP（外部） | **仅后加轨**加载导出配置 |

---

## 2. User Stories

### US-CORE-01 — 双低音空间均匀性（核心，BASELINE）

**As a** 影院用户  
**I want** 在听音网格上测量双低音 A/B/AB，并搜索 B 的增益/相位/极性/（可选）延迟，使空间散布最小且不违反产品 boost 上限  
**So that** 座位间低频更均匀，且推荐设置可安全落地

**Acceptance criteria**
- AC-US-CORE-01-1: 可完成网格三模式（A、B、AB）会话并保存。
- AC-US-CORE-01-2: `SubwooferOptimizer.Search` 可从 WPF 到达；ProductSafety 有效上限 ≤ 3.0 dB。
- AC-US-CORE-01-3: 结果覆盖声明遵循 `MeasurementCoverage`（无外推）；部分覆盖时中文警告原文出现。
- AC-US-CORE-01-4: 报告可区分 theoretical optimum / legal candidate / final recommendation；L10：报告的 achieved boost 描述 **final recommendation**。
- AC-US-CORE-01-5: 产品契约测试与 Simulation quick regression 保持绿。

### US-CORE-02 — AB 叠加可信度（核心，BASELINE）

**As a** 影院用户  
**I want** 在信任优化预测前，用实测 AB 校验线性叠加模型  
**So that** 硬件/测量破坏叠加假设时不会静默给出错误最优

**Acceptance criteria**
- AC-US-CORE-02-1: `AbValidation.Compare`（§21）可对预测 vs 实测 AB 给出 verdict 与可渲染检查项。

### US-00 — 后加演进不得破坏核心（门禁）

**As a** 影院用户  
**I want** 任何后加功能合并后仍能完整走完双低音工作流与契约  
**So that** 后加轨不会偷换产品定义

**Acceptance criteria**
- AC-US-00-1: `SubwooferOptimizer` 路径在后加功能合并后仍可从 UI 到达并完成优化。
- AC-US-00-2: 既有 CI（`dotnet test` + Simulation quick regression + ProductContract）保持绿（允许仅因新增测试加断言）。
- AC-US-00-3: THE SYSTEM SHALL NOT 删除、替换或以 ART 求解器重实现 `SubwooferOptimizer`。

### US-01 — 多点多通道测量（后加编排扩展）

**As a** 影院用户  
**I want** 用 UMIK-1 在多个听音点测量各扬声器 IR（复用既有测量栈）  
**So that** 后加 ART 优化有输入——**且不削弱**既有双低音网格路径

**Acceptance criteria**
- AC-US-01-1: ≥2 扬声器通道 × ≥3 听音点 ESS 测量可保存（基于既有 `MeasurementRunner` / `SessionStore`）。
- AC-US-01-2: WPF 可展示 IR/FR（复用 Visualization）。
- AC-US-01-3: UMIK 校准补偿复用 `MicrophoneCalibration` / `CalibrationFileParser`。

### US-02 — 支撑优化 ART（后加）

**As a** 影院用户  
**I want** 指定 primary 与 support、频段与电平上限，优化支撑 FIR  
**So that** 在限定频段内用相邻扬声器改善主通道低频/时域——**作为可选功能**，非替代双低音

**Acceptance criteria**
- AC-US-02-1: 1 primary + N supports（N≥1）。
- AC-US-02-2: 每条 support 路径长 FIR + band-limit / support level。
- AC-US-02-3: WPF 可预览预计 FR、支撑贡献与电平裕量。
- AC-US-02-4: 双低音入口与契约路径仍完整可用。

### US-03 — CamillaDSP 导出（后加）

**As a** 影院用户  
**I want** 导出 CamillaDSP YAML + FIR，并能旁路对比  
**So that** 在 Camilla 中加载后加校正（**不**要求双低音核心依赖 Camilla）

**Acceptance criteria**
- AC-US-03-1: Mixer、per-path Conv、band-limit、Gain、bypass。
- AC-US-03-2: 锁定版 Camilla 可加载。
- AC-US-03-3: support bypass ⊥ phase bypass。

### US-04 — 同设备全双工 / ASIO（进阶 / 后加相关）

**As an** 进阶用户  
**I want** 同设备多通道 PlayAndRecord（ASIO 为计划第二后端）  
**So that** 多通道后加测量更稳；**不**作为核心双低音已交付前提的重写

**Acceptance criteria**
- AC-US-04-1: UI/文档将同设备 shared clock 标为推荐（P1；ADR-Q2）。
- AC-US-04-2: ASIO 交付后可完成与双设备路径同等测量会话。

### US-05 — Debug scaffold（非用户里程碑）

**As a** 开发者  
**I want** 单通路 FIR debug scaffold  
**So that** DSP 可回归，但不对终端用户暴露为产品功能

**Acceptance criteria**
- AC-US-05-1: 仅 feature-flag / DEBUG。
- AC-US-05-2: 默认导航与用户文档 **不**将单通道 FIR EQ 列为功能。

### US-06 — 剩余相位 → 近最小相位（后加）

**As a** 影院用户  
**I want** 在支撑优化前对至少 primary 做空间稳健过量相位校正  
**So that** 时域拖尾被压低——**后加阶段**，非双低音核心契约

**Acceptance criteria**
- AC-US-06-1: 独立相位校准阶段（可与 support 分离开关）。
- AC-US-06-2: Primary 导出含相位 Conv；可预览前后 IR / excess 残差。
- AC-US-06-3: phase-bypass ⊥ support-bypass。
- AC-US-06-4: 纯最小相位幅值 EQ **不**视为满足本故事。

---

## 3. 核心产品：原始设计目标与已交付管线（BASELINE）

> 以下内容来自 ModerRAS/RoomForge 源码与产品契约测试；规格 **承认** 而非发明。实现代理 **不得** 以「后加 ART」为由削弱或重写这些行为。

### 3.1 设计使命（verbatim in spirit）

**核心产品：** 面向听音区的双低音 **空间均匀性** 优化器。

- 固定 Sub **A**（0 dB）；仅搜索 Sub **B** 的 gain / phase / polarity /（可选）delay。
- 目标：最小化空间散布（mean σ、P90−P10）+ boost/null 惩罚；分数 **level-invariant**；电平变化单独报告为 `LevelChangeDb`。
- **硬** max-boost 拒绝（不可被软惩罚否决）。
- **ProductSafety** 模式：有效 boost ≤ **3.0 dB**；**Capability** 仅为诊断。
- UI 可选 boost 上限集合 {0, 3, 6} dB，但 ProductSafety 将有效上限钳制到 ≤3。
- 三角色：theoretical optimum / legal candidate / final recommendation（report-grid 对齐）；L10 = 报告的 boost 描述 **final recommendation**。
- **MeasurementCoverage：** 无外推；部分覆盖时中文警告原文：`本次结果只对已测位置提供空间均匀性保证。`
- **AbValidation §21：** 信任预测前校验线性叠加 vs 实测 AB。
- **RecommendationUncertaintyPolicy：** 固定 **0.5 dB** 安全裕量（命名产品限制）。
- Simulation lab + ProductContract 测试。
- 栈：C# net10.0 / net10.0-windows、WPF、GPL-3.0。

### 3.2 已交付管线（顺序）

1. 网格测量三模式：**A**、**B**、**AB**（`SubMode`）— 会话为 3× 网格。
2. ESS 扫频 + Farina 逆滤波 + FFT/去卷积（`AudioOptimizer.Dsp`）。
3. WASAPI `IAudioBackend.PlayAndRecord`（**ASIO 计划中**，非已交付）。
4. 麦克风校准、会话存储、WPF 向导。
5. 正向模型（`SubwooferModel`）：  
   \(H_{\mathrm{total}}=H_A + G\cdot\mathrm{polarity}\cdot e^{j\phi}\cdot\mathrm{delay}(H_B)\) 复数求和（非 dB 相加）。
6. 搜索仅 **B**（`SubwooferOptimizer`）；A 固定 0 dB。
7. 目标函数（`ObjectiveFunction` / `SpatialMetrics`）：空间散布 + 惩罚；level-invariant；`LevelChangeDb` 另报。
8. **硬** boost 拒绝（`WithinBoostLimit`）。
9. `BoostPolicy`：ProductSafety ≤ 3.0 dB；Capability 诊断。
10. UI：{0,3,6} 选项 + ProductSafety 钳制（见 `OptimizerPanelProductSafetyTests`）。
11. BoostRoles 三角色 + L10 报告语义。
12. `MeasurementCoverage` + 中文警告常量。
13. `AbValidation` §21。
14. `RecommendationUncertaintyPolicy.FixedSafetyMarginDb = 0.5`。
15. Simulation + ProductContract / Adversarial 测试。
16. 许可证 GPL-3.0。

### 3.3 核心范围（In Scope — BASELINE）

| ID | 范围项 | 优先级 |
|----|--------|--------|
| IN-CORE-01 | 双低音网格测量 A/B/AB、ESS/Farina、会话、WPF | BASELINE |
| IN-CORE-02 | `SubwooferModel` / `SubwooferOptimizer` / 目标函数 / 硬 boost | BASELINE |
| IN-CORE-03 | ProductSafety 3 dB、BoostRoles、L10、Coverage 警告、§21、0.5 dB 裕量 | BASELINE |
| IN-CORE-04 | WASAPI `IAudioBackend`；UMIK 校准路径 | BASELINE |
| IN-CORE-05 | Simulation lab + ProductContract 测试 + windows CI | BASELINE |

### 3.4 Out of Scope（相对核心身份）

| ID | 排除项 | 说明 |
|----|--------|------|
| OUT-CORE-01 | 用 ART / Camilla **重新定义** RoomForge 为「多扬声器支撑工具」 | 禁止 |
| OUT-CORE-02 | 删除或替换 `SubwooferOptimizer` | 禁止 |
| OUT-CORE-03 | 放宽 ProductSafety 默认天花板、取消硬拒绝、外推未测位置 | 禁止（除非独立产品 ADR + 用户明确批准） |
| OUT-CORE-04 | P0 Rust / WebUI / ALSA 重写测量栈 | 禁止 |

### 3.5 Assumptions / Constraints（核心）

| ID | 内容 |
|----|------|
| AS-CORE-01 | 实现与契约以 ModerRAS/RoomForge `master` 源码为准；本工作区仅规格。 |
| CO-01 | 语言/运行时：C# / .NET 10；UI/Audio 为 `net10.0-windows`。 |
| CO-03 | 许可证 GPL-3.0。 |
| CO-05 | 优化逻辑 SHALL NOT 依赖 WPF；可被单元测试与 Simulation 调用（既有事实）。 |

---

## 4. 核心产品契约（EARS / BASELINE）

> 下列需求标记 `[BASELINE]`：描述 **已交付行为**。后加实现 SHALL NOT 削弱之。

**REQ-CORE-01** `[BASELINE]`  
THE SYSTEM SHALL 提供双低音空间均匀性优化：固定 A 于 0 dB，搜索 B 的 polarity / gain / phase /（可选）delay（`SubwooferOptimizer`）。

**REQ-CORE-02** `[BASELINE]`  
THE SYSTEM SHALL 使用复数求和正向模型 \(H_{\mathrm{total}}=H_A+G\cdot\mathrm{polarity}\cdot e^{j\phi}\cdot\mathrm{delay}(H_B)\)（`SubwooferModel`）；SHALL NOT 以 dB 幅值直接相加代替复数求和。

**REQ-CORE-03** `[BASELINE]`  
THE SYSTEM SHALL 以 level-invariant 目标函数最小化空间散布（含 mean σ 与 P90−P10 项）并施加 boost/null 惩罚；THE SYSTEM SHALL 将整体电平变化报告为 `LevelChangeDb`，SHALL NOT 将其混入均匀性分数。

**REQ-CORE-04** `[BASELINE]`  
WHEN 候选设置的 achieved boost 超过配置上限，THE SYSTEM SHALL **硬拒绝**该候选（`WithinBoostLimit`）；THE SYSTEM SHALL NOT 仅依赖可被否决的软惩罚来执行上限。

**REQ-CORE-05** `[BASELINE]`  
WHEN 运行于 `OptimizerOperatingMode.ProductSafety`，THE SYSTEM SHALL 使有效 boost 上限为 `min(configured, 3.0)` dB（`BoostPolicy.ProductSafetyMaxBoostDb`）；Capability 模式 SHALL 仅作诊断，SHALL NOT 单独作为面向用户的产品推荐依据。

**REQ-CORE-06** `[BASELINE]`  
WHERE UI 提供 boost 上限选项 {0, 3, 6} dB，WHEN ProductSafety 路径运行，THE SYSTEM SHALL 仍将有效搜索/校验上限钳制到 ≤ 3.0 dB。

**REQ-CORE-07** `[BASELINE]`  
THE SYSTEM SHALL 区分并保留 BoostRoles：theoretical optimum、legal candidate、final recommendation（report-grid）；WHEN 报告 achieved boost，THE SYSTEM SHALL 使该数值描述 **final recommendation**（L10）。

**REQ-CORE-08** `[BASELINE]`  
THE SYSTEM SHALL NOT 对未测位置外推空间均匀性保证；WHEN 覆盖非整片声明区域，THE SYSTEM SHALL 使用中文警告原文「本次结果只对已测位置提供空间均匀性保证。」（`MeasurementCoverage.MeasuredRegionOnlyWarning`）。

**REQ-CORE-09** `[BASELINE]`  
THE SYSTEM SHALL 提供 AbValidation（§21）：用与优化器相同的 `SubwooferModel` 预测 vs 实测 AB，输出 verdict 与检查建议；IF 叠加假设被破坏，THEN THE SYSTEM SHALL 以数据表明不一致，SHALL NOT 静默当作模型成立。

**REQ-CORE-10** `[BASELINE]`  
THE SYSTEM SHALL 提供 `RecommendationUncertaintyPolicy`：默认固定安全裕量 **0.5 dB**（`FixedSafetyMarginDb`），并文档化为命名产品限制（非「测量不确定度已修复」的表述）。

**REQ-CORE-11** `[BASELINE]`  
THE SYSTEM SHALL 支持网格测量模式 A、B、AB（`SubMode`），并使完整双低音会话覆盖三模式网格。

**REQ-CORE-12** `[BASELINE]`  
THE SYSTEM SHALL 通过 `IAudioBackend.PlayAndRecord`（已交付 WASAPI）完成扫频测量；THE SYSTEM SHALL 应用用户提供的麦克风校准文件。

**REQ-CORE-13** `[BASELINE]`  
THE SYSTEM SHALL 保持 Simulation lab、ProductContract 与相关 adversarial/UI 契约测试作为核心回归门禁。

**REQ-CORE-14** `[BASELINE]`  
THE SYSTEM SHALL 以 RoomForge / AudioOptimizer 为产品载体，并以 **GPL-3.0** 约束衍生工作。

**REQ-SYS-04** `[BASELINE]`（门禁，同 US-00）  
THE SYSTEM SHALL 继续提供双低音优化入口与结果展示；任何后加功能 SHALL NOT 移除或替换该入口与 `SubwooferOptimizer`。

---

## 5. 后加功能轨（Extension）— Scope

> **标签：** 后加 / Extension。叙述权重低于 §3–§4。  
> **前置条件（硬）：** 合并后加代码后，§4 全部 BASELINE 契约与双低音 UI 路径仍通过；不得要求替换 `SubwooferOptimizer`。

### 5.1 In Scope（后加）

| ID | 范围项 | 优先级 |
|----|--------|--------|
| IN-EXT-01 | Primary + N supports 多点联合支撑 FIR | P0（后加） |
| IN-EXT-02 | Excess → near-min-phase 校准（primary 必选于导出路径） | P0（后加） |
| IN-EXT-03 | CamillaDSP YAML + FIR 导出 | P0（后加） |
| IN-EXT-04 | 扩展 WPF：角色 / 相位 / ART / 导出面板（**并列**于双低音，非替换） | P0（后加） |
| IN-EXT-05 | ASIO 第二后端；同设备 duplex 推荐 | ASIO=P0/P1；duplex=P1（ADR-Q2） |
| IN-EXT-06 | 多扬声器测量编排 gap-fill（复用 Measurement） | P0（后加） |
| IN-EXT-07 | 双设备对齐扩展（`SweepAlignment`） | P0（后加） |
| IN-EXT-08 | 输出电平安全在多出路径上的复核 | P0（后加） |

### 5.2 Out of Scope（后加）

| ID | 排除项 | 说明 |
|----|--------|------|
| OUT-01 | REW / A1 / Dirac SDK 运行时依赖 | 禁止 |
| OUT-02 | 以「仅单通道 FIR 幅值 EQ」作为用户里程碑 | NG |
| OUT-03 | P0 WebUI 替代 WPF | 仅 P2 可选 |
| OUT-04 | P0 Rust 重写 Core/Dsp/Audio/UI | 非目标 |
| OUT-05 | P0 Linux/ALSA 一等平台 | Windows-first |
| OUT-06 | 耳机 HRTF / CTC / 环绕高频填充 | Future |
| OUT-07 | 云端、账号、默认遥测 | 本地工具 |
| OUT-08 | Vendoring Camilla 为库 | 仅导出 |
| OUT-09 | 重新分发厂商 UMIK 校准 | 用户自备 |
| OUT-10 | 删除/替换 `SubwooferOptimizer` 或削弱 §4 契约 | **禁止** |
| OUT-11 | 将后加轨文档写成与核心「对等产品线」 | **禁止**（v2.1） |

### 5.3 Assumptions / Constraints（后加）

| ID | 内容 |
|----|------|
| AS-01 | 用户已安装兼容版 Camilla（文档锁定测试版本）——**仅**使用导出功能时。 |
| AS-02 | 用户可提供 UMIK 校准文件。 |
| AS-03 | 测量时 RoomForge 独占设备；校正回放时由 Camilla 占用（后加路径）。 |
| AS-04 | 默认采样率目标 48 kHz；ADR-Q3。 |
| AS-05 | 后加代码落在 ModerRAS/RoomForge 内；GPL-3.0。 |
| CO-02 | 新能力以 **新项目或新命名空间** 加性引入；SHALL NOT 为后加而拆毁 Optimization 核心。 |
| CO-04 | SHALL NOT 在未写明刻意迁移时引入与 WASAPI/WPF/.NET10 矛盾的 P0 要求。 |

---

## 6. Functional Requirements — 后加轨（EARS）

### 6.1 System-wide（后加相关）

**REQ-SYS-01** `[P0]`  
THE SYSTEM SHALL 在测量路径上通过 `IAudioBackend` 打开、播放与录制；WPF SHALL 仅通过 ViewModel/服务编排，SHALL NOT 在 UI 项目散落硬件调用（延续既有分层）。

**REQ-SYS-02** `[P0]`  
THE SYSTEM SHALL NOT 在运行时依赖 REW、A1 Evo、Dirac Live 或 Dirac SDK。

**REQ-SYS-03** `[P0]`  
THE SYSTEM SHALL 将 CamillaDSP 视为外部引擎：仅导出配置与 FIR；SHALL NOT vendoring Camilla 二进制为库依赖。

**REQ-SYS-05** `[P0]`  
THE SYSTEM SHALL 在会话/导出元数据中写入版本字段（`session_format_version` / `project_format_version`、`export_version`、`optimizer_version`、`phase_cal_version`）。

**REQ-SYS-06** `[P2-internal]`  
IF feature-flag `debug_fir_scaffold` 未启用，THEN THE SYSTEM SHALL NOT 在默认 WPF 导航暴露单通道 FIR EQ。

**REQ-SYS-07** `[P0]`  
THE SYSTEM SHALL 以 **RoomForge / AudioOptimizer** 为产品载体；文档可将 ART/Camilla 标为 **后加功能轨**，SHALL NOT 要求独立发行名 `artcam`，SHALL NOT 将后加轨表述为与双低音核心对等的「第二产品」。

**REQ-SYS-08** `[P0]`  
THE SYSTEM SHALL 遵循 GPL-3.0。

---

### 6.2 Audio I/O — `REQ-IO-*`

**REQ-IO-01** `[BASELINE]`  
WHEN 用户枚举 Windows 音频设备，THE SYSTEM SHALL 通过已交付 `WasapiAudioBackend` 列出并允许选择。

**REQ-IO-02** `[P0]`  
THE SYSTEM SHALL 实现 `IAudioBackend` 的 **ASIO** 第二后端；WHEN ASIO 可用，THE SYSTEM SHALL 允许选择设备与通道映射。

**REQ-IO-03** `[BASELINE/P0]`  
WHEN 测量开始，THE SYSTEM SHALL 使用 `PlayAndRecord`；多扬声器后加场景 SHALL 支持按通道串行测量。

**REQ-IO-04** `[BASELINE]`  
THE SYSTEM SHALL 支持 UMIK-1 与用户校准文件。

**REQ-IO-05** `[P0]`  
WHEN 播放与录音非同一物理设备，THE SYSTEM SHALL 估计偏移/偏斜并在 IR 前对齐（复用/扩展 `SweepAlignment`）。

**REQ-IO-06** `[P1]`  
WHERE 同设备支持全双工，THE SYSTEM SHALL 提供 shared-clock 路径并在 UI/文档标为推荐。

**REQ-IO-07** `[P0]`  
THE SYSTEM SHALL 强制可配置最大输出电平；支持预检与紧急停止。

**REQ-IO-08** `[P0]`  
THE SYSTEM SHALL 以 48 kHz 为默认测量采样率目标；文档化 44.1/96 策略（ADR-Q3）。

**REQ-IO-09** `[P0]`  
IF 设备打开失败等，THEN THE SYSTEM SHALL 返回可操作错误且 SHALL NOT 崩溃（延续 `AudioDeviceOpenException` 风格）。

**REQ-IO-10** `[P2]`  
Linux/ALSA 或 sidecar SHALL NOT 作为 P0；未来须单独 ADR。

---

### 6.3 Measurement — `REQ-MEAS-*`

**REQ-MEAS-01** `[BASELINE]`  
THE SYSTEM SHALL 使用既有 ESS + Farina 去卷积得到 IR。

**REQ-MEAS-02** `[BASELINE/P0]`  
WHEN 多扬声器逐通道测量完成，THE SYSTEM SHALL 保存会话；后加角色标签（primary/support）SHALL 可附加存储，且 SHALL NOT 破坏既有双低音 A/B/AB 会话语义。

**REQ-MEAS-03** `[BASELINE]`  
THE SYSTEM SHALL 支持多听音点（`MeasurementGrid` / `MeasurementPoint`）。

**REQ-MEAS-04** `[BASELINE]`  
THE SYSTEM SHALL 通过 Visualization + WPF 提供 IR/FR 可视化。

**REQ-MEAS-05** `[BASELINE/P0]`  
WHEN 重测单通道或单点，THE SYSTEM SHALL 仅替换对应数据。

**REQ-MEAS-06** `[P1]`  
THE SYSTEM SHALL 支持导出原始测量包以便回归。

**REQ-MEAS-07** `[P2-internal]`  
WHILE `debug_fir_scaffold` 启用，THE SYSTEM SHALL 提供单通路 FIR debug scaffold。

**REQ-MEAS-08** `[BASELINE/P0]`  
WHEN 测量中断，THE SYSTEM SHALL 保留已完成数据并可恢复。

**REQ-MEAS-09** `[P0]`  
THE SYSTEM SHALL 支持将通道标注为 Primary / Support（后加）；该模型 SHALL 与核心双低音 A/B 语义 **并存**（ADR-Q4：分轨 UI 为默认倾向）。

---

### 6.4 Phase Calibration — `REQ-PHASE-*`（后加）

> **定位：** **后加**阶段；测量对齐之后、support MIMO 之前（或干净解耦）。公开结构参考 ICASSP 2012 / JAES 2015 / ISEAT 2015（见 research 笔记）；**不**声称复刻 Dirac。  
> **不**改变双低音核心契约。

**REQ-PHASE-01** `[P0]`  
THE SYSTEM SHALL 将已对齐传递函数分解为幅值、最小相位分量与 excess-phase；方法锁定 Hilbert 或实倒谱之一并文档化。

**REQ-PHASE-02** `[P0]`  
WHEN 某扬声器有多测点 IR，THE SYSTEM SHALL 估计跨测点空间共有 excess-phase；SHALL NOT 仅按单点无约束全逆作为默认。

**REQ-PHASE-03** `[P0]`  
THE SYSTEM SHALL 在预振铃约束下设计混合相位校正 FIR，导出为 Camilla `Conv`，使共有 excess 趋向近最小相位。

**REQ-PHASE-04** `[P0]`  
THE SYSTEM SHALL NOT 将「仅最小相位幅值 EQ」视为已满足相位校准。

**REQ-PHASE-05** `[P0]`  
WHEN 导出面向某 primary 的后加校正配置，THE SYSTEM SHALL 在 primary 路径包含相位校准 FIR；P0 默认禁止以「primary 纯直通无相位 FIR」为完成态（phase-bypass 除外）。

**REQ-PHASE-06** `[P0]`  
WHEN 相位校准完成，THE SYSTEM SHALL 提供校准前后 IR 与 excess/群延迟残差预览数据。

**REQ-PHASE-07** `[P0]`  
THE SYSTEM SHALL 支持独立于 support 的 phase-bypass。

**REQ-PHASE-08** `[P1]`  
THE SYSTEM SHALL 允许对 support 可选施加同算法相位 FIR；P0 默认可关闭，接口 SHALL 复用同一设计路径。

---

### 6.5 Optimize — `REQ-OPT-*`

**REQ-OPT-00** `[BASELINE]`  
THE SYSTEM SHALL 继续提供双低音优化（`SubwooferOptimizer.Search`）；该能力独立于后加 ART support FIR；ART SHALL NOT 调用或替换它作为双低音实现。

**REQ-OPT-01** `[P0]`  
WHEN 用户配置 **后加** ART 优化任务，THE SYSTEM SHALL 允许恰好一个 primary 与 N 个 support（N≥1），且 primary 不得同时列于 support。

**REQ-OPT-02** `[P0]`  
THE SYSTEM SHALL 允许配置 support 频率范围与 support level 上限（dB）。

**REQ-OPT-03** `[P0]`  
WHEN ART 优化运行，THE SYSTEM SHALL 在多测点联合优化相对 primary 目标的残差。

**REQ-OPT-04** `[P0]`  
THE SYSTEM SHALL 为每条 support 路径输出长 FIR 系数；长度与采样率组合锁定并文档化。

**REQ-OPT-05** `[P0]`  
THE SYSTEM SHALL 定义后加 primary 路径策略：默认 **相位校准 FIR + support 网络**；纯直通无相位 FIR 仅 phase-bypass/调试。

**REQ-OPT-06** `[P0]`  
WHEN 优化完成，THE SYSTEM SHALL 提供预览：预计 FR、support 贡献、电平裕量。

**REQ-OPT-07** `[P0]`  
THE SYSTEM SHALL 生成可一键旁路支撑网络的语义结构。

**REQ-OPT-08** `[P2]`  
THE SYSTEM SHALL 预留可替换 ART 求解器接口而不破坏 `export_version` 协商。

**REQ-OPT-09** `[P0]`  
IF 输入缺少必要 IR 或参数非法，THEN THE SYSTEM SHALL 拒绝运行并返回校验错误。

**REQ-OPT-10** `[P0]`  
THE SYSTEM SHALL 对 support FIR 施加正则化与/或 level 约束。

---

### 6.6 Export — `REQ-EXP-*`（后加）

**REQ-EXP-01** `[P0]`  
WHEN 用户请求 **后加** 导出，THE SYSTEM SHALL 写出 Camilla YAML：mixers、primary 相位 Conv（及可选幅值 Conv）、support Conv、band-limit、Gain。

**REQ-EXP-02** `[P0]`  
THE SYSTEM SHALL 将 FIR 写为与 Camilla Conv 兼容的默认格式（Wav 或 Raw f32，设计锁定）；相对路径可解析。

**REQ-EXP-03** `[P0]`  
THE SYSTEM SHALL 维护逻辑通道名与 Camilla 索引映射并写入元数据。

**REQ-EXP-04** `[P0]`  
THE SYSTEM SHALL 提供 support bypass，并与 phase-bypass 独立。

**REQ-EXP-05** `[P0]`  
BEFORE 最终写出，THE SYSTEM SHALL 校验完整性；IF 失败，THEN SHALL NOT 写出半残缺「成功」包。

**REQ-EXP-06** `[P1]`  
THE SYSTEM SHALL 可选生成中文 README/加载说明。

**REQ-EXP-07** `[P0]`  
THE SYSTEM SHALL 在清单中记录 `export_version`、目标 Camilla 大版本提示、FIR 清单与哈希或长度。

---

### 6.7 UI — `REQ-UI-*`

**REQ-UI-01** `[BASELINE/P0]`  
THE SYSTEM SHALL 以既有 WPF 为唯一 P0 UI；后加面板 SHALL **扩展**而非替换双低音向导/优化入口。

**REQ-UI-02** `[BASELINE]`  
THE SYSTEM SHALL 提供设备选择、校准、通道与点位测量向导（既有）。

**REQ-UI-03** `[P0]`  
WHILE 测量进行中，THE SYSTEM SHALL 展示进度与电平，并提供紧急停止。

**REQ-UI-04** `[P0]`  
THE SYSTEM SHALL 提供后加 ART/相位参数与预览入口；双低音优化面板 SHALL 仍可达。

**REQ-UI-05** `[P0]`  
WHEN 后加导出成功，THE SYSTEM SHALL 显示输出目录与文件清单。

**REQ-UI-06** `[BASELINE/P0]`  
THE SYSTEM SHALL 支持会话/项目打开与保存。

**REQ-UI-07** `[P1]`  
UI 文案中文优先。

**REQ-UI-08** `[P0]`  
IF 操作失败，THEN UI SHALL 展示可读错误并尽量保留已填状态。

**REQ-UI-09** `[P2]`  
localhost WebUI SHALL NOT 作为 P0。

---

### 6.8 Persistence — `REQ-PROJ-*`

**REQ-PROJ-01** `[BASELINE/P0]`  
THE SYSTEM SHALL 持久化通道、点位、IR、校准；后加字段（角色、ART/相位结果）以版本化方式附加，SHALL NOT 破坏旧双低音会话可读性（缺省升级策略）。

**REQ-PROJ-02** `[BASELINE/P0]`  
WHEN 保存，THE SYSTEM SHALL 使用原子写入或同等安全策略。

**REQ-PROJ-03** `[P0]`  
IF 格式版本高于当前支持，THEN THE SYSTEM SHALL 拒绝静默打开并提示。

---

## 7. Non-functional Requirements（EARS）

**NFR-01 Performance** `[P0]`  
WHEN 后加会话约 8 通道 × 5 点，THE SYSTEM SHALL 在消费级 CPU 上于数分钟内完成 P0 支撑优化（时限设计锁定）。

**NFR-02 Safety** `[P0]`  
THE SYSTEM SHALL 对输出电平硬上限、确认与急停；核心 ProductSafety boost 契约 SHALL 继续有效。

**NFR-03 Reliability** `[P0]`  
WHEN 测量中断，THE SYSTEM SHALL 允许从已完成子集恢复。

**NFR-04 Testability** `[P0]`  
THE SYSTEM SHALL 使核心 DSP 与后加相位/ART/导出可无硬件单测；SHALL 延续 Tests + Simulation；**核心 ProductContract 不得因后加而删减**。

**NFR-05 Platform** `[P0]`  
Windows 一等；跨平台非 P0 阻塞。

**NFR-06 Maintainability** `[P0]`  
后加 ART/相位/导出 SHALL 与 WPF 解耦；SHALL NOT 与双低音优化器耦合为实现依赖。

**NFR-07 Privacy** `[P0]`  
本地处理；默认无遥测。

**NFR-08 Observability** `[P1]`  
记录设备打开、测量/优化/导出关键事件。

**NFR-09 License** `[P0]`  
GPL-3.0。

**NFR-10 Core non-regression** `[P0]`  
WHEN 合并后加功能，THE SYSTEM SHALL 保持 §4 BASELINE 需求与双低音 UI 冒烟通过，作为后加里程碑的硬门禁。

---

## 8. Dependency Policy

**REQ-DEP-01** `[P0]`  
THE SYSTEM SHALL NOT 引入 REW、A1、Dirac SDK 或闭源测量套件作运行时依赖。

**REQ-DEP-02** `[P0]`  
允许与 GPL-3.0 兼容的 NuGet（既有 NAudio.Wasapi；未来 ASIO 须兼容）。

**REQ-DEP-03** `[P0]`  
解析用户 UMIK `.txt`；SHALL NOT 在发行包重分发厂商校准。

**REQ-DEP-04** `[P0]`  
算法 in-house；可参考公开文献；SHALL NOT 链接专有闭源实现。

---

## 9. Acceptance Criteria by Milestone

### 9.1 M0 — 核心保全 / 文档（先于任何后加编码）

| ID | Criteria |
|----|----------|
| AC-M0-01 | 规格与 baseline **以核心双低音使命开篇**；明确 ART 为后加；无 Product A/B 对等叙事。 |
| AC-M0-02 | 文档承认 §3.2 管线与 §4 契约已存在于 upstream。 |
| AC-M0-03 | `dotnet build` / `dotnet test` + quick regression + ProductContract 在 windows CI 语义下可通过（以 upstream 为准）。 |
| AC-M0-04 | **不**启动 Rust/WebUI P0；**不**把后加任务标为「重建 FFT/优化器」。 |
| AC-M0-05 | 门禁语句就位：后加编码前须声明「双低音产品契约非回归」。 |

### 9.2 M1 — 后加：多扬声器测量编排

| ID | Criteria |
|----|----------|
| AC-M1-01 | ≥2 通道 × ≥3 点测量保存；可标注 primary/support。 |
| AC-M1-02 | 重测单点不丢其它数据。 |
| AC-M1-03 | WPF 完成多扬声器测量；**双低音向导仍可用**。 |
| AC-M1-04 | ASIO 达实验室清单 **或** ADR-Q2 记录暂缓。 |
| AC-M1-05 | 既有 deconv/对齐回归仍绿。 |
| AC-M1-06 | **非回归：** ProductContract + 双低音 UI 冒烟绿。 |

### 9.3 M2 — 后加：相位 + Support FIR + Camilla

| ID | Criteria |
|----|----------|
| AC-M2-01 | 夹具上 ART 产出稳定 support FIR。 |
| AC-M2-02 | 锁定版 Camilla 可加载导出。 |
| AC-M2-03 | support bypass ⊥ phase bypass。 |
| AC-M2-04 | 至少 1 条多扬声器端到端演示记录。 |
| AC-M2-05 | 相位校准 excess 指标达标；纯 min-phase 幅值 EQ 对照不达标。 |
| AC-M2-06 | 非 bypass 时 primary 含非空相位 FIR。 |
| AC-M2-07 | **非回归：** 双低音路径与 §4 契约测试仍绿。 |

### 9.4 M3 — 后加：duplex 打磨 + solver seam

| ID | Criteria |
|----|----------|
| AC-M3-01 | 同设备 duplex 写入文档与 UI 推荐。 |
| AC-M3-02 | M2 在推荐路径可重复。 |
| AC-M3-03 | ART 求解器可替换且导出协商不变。 |
| AC-M3-04 | README：Camilla/UMIK/ASIO + **核心双低音说明在前**、后加轨在后。 |
| AC-M3-05 | **非回归：** 同 AC-M2-07。 |

---

## 10. Traceability Matrix (Summary)

| User Story | Primary REQs | Milestone |
|------------|--------------|-----------|
| US-CORE-01 | REQ-CORE-01..14, REQ-OPT-00 | M0 |
| US-CORE-02 | REQ-CORE-09 | M0 |
| US-00 | REQ-SYS-04, NFR-10, AC-M*-非回归 | M0–M3 |
| US-01 | REQ-IO-*, REQ-MEAS-*, REQ-UI-01..03 | M1 |
| US-02 | REQ-OPT-01..07, REQ-UI-04 | M2 |
| US-03 | REQ-EXP-*, REQ-UI-05 | M2 |
| US-04 | REQ-IO-02, REQ-IO-06 | M1–M3 |
| US-05 | REQ-MEAS-07, REQ-SYS-06 | optional |
| US-06 | REQ-PHASE-01..07, REQ-OPT-05, REQ-EXP-01 | M2 |

---

## 11. Glossary

| Term | Meaning |
|------|---------|
| RoomForge | 产品/仓库名；solution `AudioOptimizer.sln` |
| 核心产品 | 已交付双低音空间均匀性优化器（非「Product A」对等标签） |
| 后加功能轨 / Extension | ART-like primary+N supports、相位校准、Camilla 导出（非「Product B」对等标签） |
| ProductSafety | 有效 boost ≤ 3.0 dB 的用户面向模式 |
| Capability | 诊断模式；非单独产品推荐 |
| BoostRoles | theoretical / legal / final recommendation |
| L10 | 报告 boost 必须描述 final recommendation |
| MeasurementCoverage | 无外推；中文部分覆盖警告 |
| AbValidation §21 | 线性叠加 vs 实测 AB |
| FixedSafetyMarginDb | 0.5 dB 命名产品限制 |
| Primary / Support | **后加**角色；与双低音 A/B 并存 |
| ESS / Farina / IAudioBackend / Conv | 同既有术语 |
| GPL-3.0 | 许可证 |

---

*End of requirements.md · v2.1*
