# Implementation Tasks — RoomForge 核心保全 + 后加功能轨

**Document ID:** `SPEC-TASK-001`  
**Version:** 2.1  
**Date:** 2026-10-01 (CST)  
**Status:** Ready for execution（v2.1 framing：M0 核心保全；后加在非回归门后）  
**Based on:** [`requirements.md`](requirements.md), [`design.md`](design.md)  
**Codebase:** [ModerRAS/RoomForge](https://github.com/ModerRAS/RoomForge) — **在该仓库内实现**；本目录仅规格

---

## How to use

- 按编号顺序；`Depends:` 须满足。
- 完成后 `[ ]` → `[x]`。
- **Verify** 为最低验收；未通过不得勾选。
- 措辞必须是 **扩展项目 X / 新增项目 Y** 或 **BASELINE 保全**；禁止「创建 Cargo / 从零写 FFT / 替换 SubwooferOptimizer」。
- **硬门禁：** 任何后加里程碑勾选前，须满足「双低音产品契约非回归」（见 T011、各 AC-M*-非回归）。
- 相位 FIR 与 support FIR 是 **后加 M2** 用户里程碑；单通道幅值 EQ 仅 debug。

---

## M0 — 文档化 / 保全核心产品（先于后加编码）

> M0 的目标：**写清并锁住**原始双低音设计目标与契约，确认 upstream 已交付能力为 DONE/BASELINE。  
> **不得**在 M0 开始 ART/Camilla 功能编码（规格与门禁任务除外）。

### T000 — Spec / baseline framing v2.1
- [x] 规格 v2.1 + `research/roomforge-baseline.md` 以 **核心双低音使命** 开篇；ART 标为后加
- **Depends:** none
- **Req:** AC-M0-01, AC-M0-04, AC-M0-05
- **Verify:** 读者不会认为 RoomForge「等着被 ART 重新定义」；无 Product A/B 对等叙事

### T001 — ESS + Farina + FFT + deconv — BASELINE
- [x] **DONE/BASELINE** — `AudioOptimizer.Dsp`
- **Verify:** 勿新开 Rust FFT；勿重写 Farina

### T002 — Core IR/FR/Grid/MicCal / SubMode — BASELINE
- [x] **DONE/BASELINE** — `AudioOptimizer.Core`（含网格与 `SubMode` A/B/AB）

### T003 — IAudioBackend + WasapiAudioBackend — BASELINE
- [x] **DONE/BASELINE** — PlayAndRecord 已交付；ASIO 未交付（T020）

### T004 — SessionStore / Wav / CalibrationFileParser — BASELINE
- [x] **DONE/BASELINE** — `AudioOptimizer.IO`

### T005 — MeasurementRunner/Session/Slot — BASELINE
- [x] **DONE/BASELINE** — `AudioOptimizer.Measurement`

### T006 — SubwooferOptimizer + 正向模型 — BASELINE（核心心脏）
- [x] **DONE/BASELINE** — `SubwooferModel` / `SubwooferOptimizer` / `ObjectiveFunction` / `SpatialMetrics`
- **Req:** REQ-CORE-01..04, REQ-OPT-00
- **Verify:** 既有优化器测试保持；**禁止**后加任务「重实现双低音搜索」

### T007 — 产品契约层 — BASELINE
- [x] **DONE/BASELINE** — `BoostPolicy`（ProductSafety 3.0）、`BoostRoles`/L10、`MeasurementCoverage`（中文警告）、`AbValidation` §21、`RecommendationUncertaintyPolicy`（0.5 dB）
- **Req:** REQ-CORE-05..10, REQ-CORE-13
- **Verify:** `tests/.../ProductContract/*`、UI ProductSafety、AbValidation 测试存在且保持

### T008 — Visualization + WPF 双低音 UI — BASELINE
- [x] **DONE/BASELINE** — 测量向导 + 优化面板（含 {0,3,6} + ProductSafety 钳制）
- **Verify:** 后加 UI 不得拆除该入口

### T009 — Simulation lab + CI windows/dotnet10 — BASELINE
- [x] **DONE/BASELINE** — Simulation quick regression + CI

### T010 — UMIK calibration path — BASELINE
- [x] **DONE/BASELINE** — 仅允许 gap-fill bugfix

### T011 — 非回归门禁清单（文档任务）
- [x] 规格写明：后加 PR/里程碑须跑通 ProductContract + 双低音 UI 冒烟 + Simulation quick；失败则不得声称后加完成
- **Depends:** T000, T006, T007
- **Req:** NFR-10, AC-M0-05, US-00
- **Verify:** tasks/design/requirements 均出现门禁语句

### T012 — Cancelled greenfield / 对等產品叙事
- [x] **CANCELLED** — Cargo/WebUI/ALSA P0、`artcam`、用 ART 替换双低音、「Product A/B 对等」作为项目定义
- **Verify:** 规格无残留对等叙事硬性要求

---

## 后加启动条件（所有 M1+ 任务）

**GIVEN** M0（T000–T012）已勾选，**AND** upstream `dotnet test` + ProductContract + quick regression 绿，  
**THEN** 才允许开始下列后加任务。  
每个后加里程碑结束时重复非回归验证（见 T027 / T046 / T054）。

---

## M1 — 后加：多扬声器测量 + ASIO gap-fill

### T020 — Implement `AsioAudioBackend`
- [ ] **在 `AudioOptimizer.Audio` 新增** `AsioAudioBackend : IAudioBackend`
- **Depends:** T003, T011
- **Req:** REQ-IO-02, REQ-IO-09, AC-M1-04
- **Verify:** Windows lab 枚举 + 一次 sweep；失败路径风格一致；NuGet↔GPL 记录
- **Note:** ADR-Q2 可书面延期

### T021 — Extend measurement orchestration for N speakers
- [ ] **扩展** MeasurementRunner/Session：按通道串行；保留取消与部分结果；**不破坏** A/B/AB 双低音会话
- **Depends:** T005, T011
- **Req:** REQ-IO-03, REQ-MEAS-02, REQ-MEAS-05, REQ-MEAS-08, AC-M1-01
- **Verify:** Virtual/mock：N×M；取消保留；旧双低音会话仍可加载

### T022 — SpeakerRole model（后加）
- [ ] **扩展 Core 或新增 Art**：`SpeakerRole` + 持久化；缺省 Unassigned；版本号升级
- **Depends:** T002, T011
- **Req:** REQ-MEAS-09, REQ-PROJ-01
- **Verify:** round-trip；旧会话可读

### T023 — Dual-device / skew align gap-fill
- [ ] **扩展** `SweepAlignment` / 编排
- **Depends:** T001, T005
- **Req:** REQ-IO-05
- **Verify:** 合成已知 delay 回收；短文档

### T024 — WPF：多通道测量 + 角色（并列核心向导）
- [ ] **扩展 UI**；**保留**双低音测量/优化入口
- **Depends:** T021, T022
- **Req:** REQ-UI-01..03, AC-M1-03, AC-M1-06
- **Verify:** ≥2×≥3 保存；重测单点；双低音菜单仍在

### T025 — Safety limits on multi-out path
- [ ] 复核峰值上限/预检/急停
- **Depends:** T021
- **Req:** REQ-IO-07, NFR-02
- **Verify:** 超限缩放；取消停止

### T026 — ADR-Q2 / ADR-Q3
- [ ] 写入仓库 ADR 或 docs
- **Depends:** T020 进展
- **Req:** REQ-IO-08, AC-M1-04
- **Verify:** ADR 存在且 design §14 可引用

### T027 — M1 非回归 + CI
- [ ] 扩展 Tests；**强制** ProductContract + 双低音冒烟 + quick regression 绿
- **Depends:** T021, T022, T024
- **Req:** NFR-04, NFR-10, AC-M1-05, AC-M1-06
- **Verify:** 本地可复现 windows CI 语义

---

## M2 — 后加：相位 + Support FIR + Camilla

### T030 — Add project `AudioOptimizer.Art`（或 ADR-Q1 名）
- [ ] **新增** csproj；引用 Core、Dsp；**不**引用 UI/Audio；**不**删除 Optimization
- **Depends:** T022, T027
- **Req:** CO-02, NFR-06
- **Verify:** `dotnet build`；依赖图符合 design §2.4

### T031 — Min-phase / excess decomposition
- [ ] 在 Art 或 Dsp 实现并锁定算法
- **Depends:** T030
- **Req:** REQ-PHASE-01
- **Verify:** 合成 min-phase excess≈0；all-pass 可检出

### T032 — Spatially common excess estimator
- [ ] 多测点共有 excess；默认禁止单点无约束全逆
- **Depends:** T031
- **Req:** REQ-PHASE-02
- **Verify:** 三点夹具指标文档化

### T033 — Mixed-phase FIR + pre-ring constraints
- [ ] 设计相位 FIR
- **Depends:** T032
- **Req:** REQ-PHASE-03
- **Verify:** 预振铃低于上限；FIR 非空

### T034 — Phase preview DTO + Visualization hook
- [ ] 前后 IR / excess / 群延迟；WPF 可绑
- **Depends:** T033, T008
- **Req:** REQ-PHASE-06, REQ-UI-04
- **Verify:** 预览序列长度匹配

### T035 — REQ-PHASE-04 regression
- [ ] pure mag EQ 不降同一 excess；相位校准则降
- **Depends:** T033
- **Req:** REQ-PHASE-04, AC-M2-05
- **Verify:** 自动化对照

### T036 — Support FIR designer P0
- [ ] **在 Art 实现**；**不得**替换或调用为双低音实现的 `SubwooferOptimizer`
- **Depends:** T033
- **Req:** REQ-OPT-01..04, REQ-OPT-09..10, AC-M2-01
- **Verify:** 金样容差；非法参数拒绝

### T037 — Primary path policy（phase required）
- [ ] 默认含 primary 相位 FIR
- **Depends:** T033, T036
- **Req:** REQ-PHASE-05, REQ-OPT-05, AC-M2-06
- **Verify:** 非 bypass 时 Fir.Length > 0

### T038 — Independent bypass semantics
- [ ] phase-bypass ⊥ support-bypass
- **Depends:** T036, T037
- **Req:** REQ-PHASE-07, REQ-OPT-07, AC-M2-03
- **Verify:** 四种组合可表达

### T039 — Add project `AudioOptimizer.Camilla`（或 `.Export`）
- [ ] **新增**导出项目
- **Depends:** T030
- **Req:** REQ-EXP-*, ADR-Q1
- **Verify:** build；依赖规则

### T040 — Camilla YAML + FIR writer + validator
- [ ] Mixer、phase/support Conv、band-limit、Gain、bypass、manifest
- **Depends:** T039, T036, T037
- **Req:** REQ-EXP-01..05, REQ-EXP-07, AC-M2-02
- **Verify:** 目录结构；Camilla lab 加载；失败不写残缺包

### T041 — Channel map metadata
- [ ] 逻辑名 ↔ 索引
- **Depends:** T040
- **Req:** REQ-EXP-03
- **Verify:** manifest；错位失败

### T042 — WPF：相位 + ART + 导出（保留双低音）
- [ ] **扩展 UI**；双低音入口与 ProductSafety 路径 **必须仍在**
- **Depends:** T024, T034, T036, T040
- **Req:** REQ-UI-04..06, REQ-SYS-04, AC-M2-07
- **Verify:** 后加 E2E 一次；双低音菜单与契约冒烟

### T043 — Support optional phase FIR（P1 API）
- [ ] 默认 off
- **Depends:** T033
- **Req:** REQ-PHASE-08
- **Verify:** 默认无 support phase 文件

### T044 — Export README (zh) optional
- [ ] 中文加载说明
- **Depends:** T040
- **Req:** REQ-EXP-06
- **Verify:** 含 Camilla 步骤摘要

### T045 — End-to-end demo record
- [ ] 内部演示记录
- **Depends:** T042
- **Req:** AC-M2-04
- **Verify:** 检入 docs 或链接

### T046 — M2 CI / fixtures + **核心非回归**
- [ ] 后加金样入 Tests；**强制** ProductContract + 双低音路径绿
- **Depends:** T035, T036, T040, T042
- **Req:** NFR-04, NFR-10, AC-M2-01..07
- **Verify:** `dotnet test` 无硬件依赖通过

---

## M3 — 后加：duplex 打磨 + solver seam

### T050 — Shared-clock duplex recommended
- [ ] UI/文档推荐同设备 duplex
- **Depends:** T020, T042
- **Req:** REQ-IO-06, AC-M3-01, AC-M3-02
- **Verify:** 文案 + 至少一次同设备复现导出

### T051 — Replaceable ART solver interface
- [ ] `ISupportOptimizer` + P0 实现；导出消费稳定 DTO
- **Depends:** T036, T040
- **Req:** REQ-OPT-08, AC-M3-03
- **Verify:** stub 第二实现可切换；**不**触及 `SubwooferOptimizer` 替换

### T052 — README product docs（核心在前）
- [ ] RoomForge docs：**先**双低音使命与契约摘要；**后**后加 ART/Camilla/UMIK/ASIO/GPL
- **Depends:** T045
- **Req:** AC-M3-04, REQ-SYS-07, REQ-SYS-08
- **Verify:** 新用户能区分核心 vs 后加；能加载导出（若使用后加）

### T053 — P2 research spike only（optional）
- [ ] WebUI/Linux 一页笔记；Out of P0
- **Depends:** none
- **Req:** REQ-UI-09, REQ-IO-10
- **Verify:** 若执行，标明非承诺

### T054 — M3 非回归门禁
- [ ] 重复 T011 清单；AC-M3-05
- **Depends:** T050, T051, T052
- **Req:** NFR-10
- **Verify:** ProductContract + 双低音冒烟 + quick regression 绿

---

## Traceability (tasks → milestone)

| Milestone | Tasks | 性质 |
|-----------|-------|------|
| M0 核心保全 | T000–T012 | 文档/BASELINE；**无后加功能编码** |
| M1 后加测量 | T020–T027 | Extension；结束须非回归 |
| M2 后加相位+ART+Camilla | T030–T046 | Extension；结束须非回归 |
| M3 后加打磨 | T050–T054 | Extension；结束须非回归 |

## v2.0 → v2.1 task migration（摘要）

| 变化 | 说明 |
|------|------|
| 新增 T007 契约 BASELINE、T011/T027/T046/T054 非回归门 | 落实「先核心后后加」 |
| T006 明确「核心心脏」 | 禁止替换 |
| 原 Product A/B 措辞 | 改为核心 / 后加 |
| 相位与 Camilla 任务 | **保留实质**，叙事降为后加 |

---

*End of tasks.md · v2.1*
