# RoomForge 基线 — 原始设计目标与仓库清单

**文档用途：** 锁定 [ModerRAS/RoomForge](https://github.com/ModerRAS/RoomForge)（AudioOptimizer）的 **原始产品身份、已交付管线与契约**，供规格 v2.1 引用。  
**ART / 相位 / Camilla** 不在本基线「已交付」范围内，见文末「后加缺口」——它们是用户稍后提出的扩展，**不得**据此重写本节使命。  
**核查方式：** GitHub API（`get_repository` / `get_git_tree` / `get_file_contents`），**未** clone 到本机。  
**日期：** 2026-10-01 (CST)  
**基线分支：** `master`  
**规格版本引用：** v2.1 framing（核心优先）

---

## 0. 原始设计目标（核心产品 — 已交付）

> 用户更正（2026-10-01）：「你要先看好原本的项目设计目标是啥，后面我要的东西属于后加的功能了。」

**核心产品：** 双低音 **空间均匀性** 优化器——在听音区（测量网格）上，固定 Sub A，调节 Sub B，使低频空间散布最小，并遵守硬性 boost 安全契约。

这 **不是**「为 ART 准备的测量套件」。双低音优化器 **就是** 产品。

### 0.1 已交付管线（源码对应）

| # | 能力 | 主要落点 |
|---|------|----------|
| 1 | 网格三模式 **A / B / AB**（`SubMode`）；会话为 3× 网格 | Core + Measurement |
| 2 | ESS + Farina 逆滤波 + FFT/去卷积 | `Dsp`：`SweepGenerator`, `InverseFilter`, `Fft`, `Deconvolver`, … |
| 3 | WASAPI `IAudioBackend.PlayAndRecord`（ASIO **计划中**） | `Audio`：`WasapiAudioBackend` |
| 4 | 麦克风校准、会话存储、WPF 向导 | `IO` + `Measurement` + `UI` |
| 5 | 正向模型复数求和 \(H_{\mathrm{total}}=H_A+G\cdot\mathrm{polarity}\cdot e^{j\phi}\cdot\mathrm{delay}(H_B)\) | `SubwooferModel` |
| 6 | 仅搜索 **B** 的 gain/phase/polarity/（可选）delay；A 固定 0 dB | `SubwooferOptimizer` |
| 7 | 目标：最小化空间散布（mean σ、P90−P10）+ boost/null 惩罚；**level-invariant**；`LevelChangeDb` 另报 | `ObjectiveFunction`, `SpatialMetrics`, `OptimizerResult` |
| 8 | **硬** max-boost 拒绝（不可被软惩罚否决） | `ObjectiveFunction.WithinBoostLimit` |
| 9 | **ProductSafety**：有效 boost ≤ **3.0 dB**；Capability 仅诊断 | `BoostPolicy`, `OptimizerOperatingMode` |
| 10 | UI 选项 {0, 3, 6} dB；ProductSafety 钳制 ≤3 | `OptimizerPanelViewModel` + `OptimizerPanelProductSafetyTests` |
| 11 | 三角色 theoretical / legal / final（report-grid）；L10：报告 boost = final recommendation | `BoostRoles`, `BoostContractReport` |
| 12 | **MeasurementCoverage**：无外推；警告原文「本次结果只对已测位置提供空间均匀性保证。」 | `MeasurementCoverage.MeasuredRegionOnlyWarning` |
| 13 | **AbValidation §21**：线性叠加预测 vs 实测 AB | `AbValidation` |
| 14 | **RecommendationUncertaintyPolicy**：固定 **0.5 dB** 裕量（命名产品限制） | `FixedSafetyMarginDb` |
| 15 | Simulation lab + ProductContract / adversarial 测试 | `Simulation`, `tests/.../ProductContract` |
| 16 | C# net10.0 / net10.0-windows、WPF、**GPL-3.0** | solution + LICENSE |

### 0.2 产品契约摘要（不可被后加静默削弱）

- ProductSafety 天花板常量：`BoostPolicy.ProductSafetyMaxBoostDb = 3.0`
- 硬拒绝 vs 软惩罚：搜索外拒绝，限内仍可有 PeakPenalty
- Coverage：未测位置 = 未知；部分覆盖必须中文警告常量
- §21：与优化器 **同一** `SubwooferModel` 做预测，避免双实现漂移
- L10：用户可读的 achieved boost 描述 **Recommended**，不是「搜索曾见过的最佳合法点」
- 不确定度策略：固定裕量是 **产品限制**，不是「测量不确定度已修复」

---

## 1. 仓库元数据

| 项 | 值 |
|----|-----|
| 全名 | `ModerRAS/RoomForge` |
| 可见性 | public |
| 默认分支 | `master` |
| 语言 | C# |
| 许可证 | **GPL-3.0** |
| Solution | `AudioOptimizer.sln` |
| README 自述 | Phase 1：指数扫频、FFT/卷积与复数求和 DSP 原语（偏基础设施表述；**产品行为以上表 §0 为准**） |

---

## 2. 目标框架与平台

| 项目 | TFM | 备注 |
|------|-----|------|
| Core / Dsp / IO / Optimization 等 | `net10.0` | 纯逻辑 |
| `AudioOptimizer.Audio` | **`net10.0-windows`** | WASAPI；ASIO 同目标规划 |
| `AudioOptimizer.UI` | **`net10.0-windows`** + WPF | |
| CI | `windows-latest` + `dotnet 10.0.x` | build、full test、Simulation `--regression quick` |

---

## 3. 现有项目层（Solution 已含）

| 项目 | 职责（已实现） |
|------|----------------|
| `AudioOptimizer.Core` | IR/FR、`SweepSettings`、`MeasurementGrid`/`Point`、`MicrophoneCalibration`、`SubMode`、… |
| `AudioOptimizer.Dsp` | ESS、Farina、`Fft`、`Deconvolver`、FR、`SweepAlignment`、`QualityChecks`、… |
| `AudioOptimizer.Audio` | `IAudioBackend.PlayAndRecord`、`WasapiAudioBackend`；ASIO 计划 |
| `AudioOptimizer.IO` | `WavFile`、`CalibrationFileParser`、`SessionStore` |
| `AudioOptimizer.Measurement` | `MeasurementRunner` / `Session` / `Slot` / `PointMeasurement` |
| `AudioOptimizer.Optimization` | **核心产品层：** `SubwooferModel`、`SubwooferOptimizer`、`ObjectiveFunction`、`SpatialMetrics`、`BoostPolicy`、`AbValidation`、`MeasurementCoverage`、`RecommendationUncertaintyPolicy`、`TargetCurve`、… |
| `AudioOptimizer.Simulation` | shoebox / image-source、`VirtualAudioBackend`、regression |
| `AudioOptimizer.Visualization` | 曲线/heatmap（无 WPF 依赖） |
| `AudioOptimizer.UI` | WPF 向导 / 优化面板（ProductSafety 路径） |
| `tests/AudioOptimizer.Tests` | 含 `ProductContract/`、Adversarial、UI 契约等 |

---

## 4. 关键源码摘录（可引用）

### 4.1 `IAudioBackend`

> WASAPI is the shipped implementation; ASIO is the planned second one…

路径：`src/AudioOptimizer.Audio/IAudioBackend.cs`

### 4.2 `InverseFilter`（Farina）

> Farina exponential-sweep (ESS) inverse filter…

路径：`src/AudioOptimizer.Dsp/InverseFilter.cs`

### 4.3 `SubwooferModel`（正向模型）

> H_total(f,i) = H_A(f,i) + H_B'(f,i) … complex sum — magnitudes are added as vectors, never in dB.

路径：`src/AudioOptimizer.Optimization/SubwooferModel.cs`

### 4.4 `SubwooferOptimizer`

> The search proper is the last stage: a **joint 2-D** coarse sweep of gain × phase … A is fixed at 0 dB; only B is searched.

路径：`src/AudioOptimizer.Optimization/SubwooferOptimizer.cs`

### 4.5 `BoostPolicy` / ProductSafety

> ProductSafety — the effective limit is never larger than … (3.0 dB) … Capability — DIAGNOSTIC mode…

路径：`src/AudioOptimizer.Optimization/BoostPolicy.cs`

### 4.6 `MeasurementCoverage`

> MeasuredRegionOnlyWarning = "本次结果只对已测位置提供空间均匀性保证。"

路径：`src/AudioOptimizer.Optimization/MeasurementCoverage.cs`

### 4.7 `AbValidation` §21

> §21: does the linear-superposition model describe the rig? … SAME model the optimizer searches with…

路径：`src/AudioOptimizer.Optimization/AbValidation.cs`

### 4.8 `RecommendationUncertaintyPolicy`

> FixedSafetyMarginDb = 0.5 … NAMED PRODUCT LIMITATION — fixed safety margin.

路径：`src/AudioOptimizer.Optimization/RecommendationUncertaintyPolicy.cs`

---

## 5. 核心 vs 后加（v2.1 用语 — 取代「Product A/B」）

| 层级 | 名称 | 状态 |
|------|------|------|
| **核心产品（已交付）** | 双低音空间均匀性优化（§0 全表） | **已交付**；持续维护；后加不得替换 |
| **后加功能轨（规划）** | primary + N supports、excess→near-min-phase、Camilla 导出、多通道编排/ASIO | **未交付**；加性演进；须契约非回归 |

---

## 6. 复用清单 vs 禁止重写

| 必须复用 | 不得从零重建（除非 ADR） |
|----------|---------------------------|
| ESS / Farina / FFT / deconv | 新语言栈 FFT/ESS |
| `IAudioBackend` + WASAPI；扩展 ASIO | P0 平行 WebUI 音频栈 |
| UMIK 校准解析 | 另写解析器 |
| Measurement 会话栈 | 新测量引擎 |
| SessionStore / WAV / Visualization / Simulation | 无故新会话格式 |
| **整层 Optimization 契约** | 「用 ART 重写双低音」 |
| WPF 双低音 UI | P0 拆掉换 WebUI |
| ProductContract + CI | 另起无关 CI 栈 |

---

## 7. 后加缺口（Extension track — 非核心定义）

以下由用户稍后提出，规格标为 **后加**；实现前须通过核心非回归门禁：

1. 扬声器角色：**primary + N supports**（与 dual-sub A/B **并存**）
2. Excess-phase → near-min-phase 校准器
3. Support / MIMO FIR 设计器
4. CamillaDSP YAML + FIR 导出
5. 多通道同设备 duplex（ASIO 后端很可能利于后加演示 —— ADR，未锁死）

---

*End of roomforge-baseline.md · v2.1*
