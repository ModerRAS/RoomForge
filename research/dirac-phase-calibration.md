# Dirac-class Residual / Excess Phase Calibration — Research Note

**文档用途：** 供父代理将「剩余相位 → 近最小相位」校准写入 ART-like CamillaDSP 工具规格。  
**日期：** 2026-09-30 (CST)  
**结论先行：** **是 — v0 规格原先遗漏了该基础阶段**；现已作为独立于 support MIMO 的 P0 流水线步骤补入规格。

> **v2.0 适配（2026-10-01）：** 实现落点改为 RoomForge / `AudioOptimizer.Art`（或 ADR-Q1 选定项目）+ WPF 预览；**不是** Rust crate。测量对齐对应既有 `SweepAlignment` / Measurement 编排；幅值目标可对照 `TargetCurve`（Product A 工具），仍 **不等于** excess-phase 校准。Camilla 导出落在 `AudioOptimizer.Camilla`（建议名）。详见 `../specs/` v2.0 与 `roomforge-baseline.md`。


---

## 1. Spec Gap Verdict / 规格缺口结论

| 问题 | 答案 |
|------|------|
| 规格是否遗漏「残差/过量相位 → 近最小相位」校准？ | **是（v1.0 遗漏）** |
| TOA / 双设备时钟对齐是否已有？ | 有：`REQ-IO-05`（v2.0）、RoomForge `SweepAlignment` / 测量对齐（测量/时钟层，**不是** excess-phase 校正） |
| 幅值 / 目标曲线是否提及？ | Product A `TargetCurve` / 幅值目标属 **magnitude**，**不等于** excess-phase 校正；ART 幅值阶段同样不得冒充相位校准 |
| Primary 路径 | 历史 v1.0 曾允许 primary 直通 → 与近 min-phase 冲突；v1.1/v2.0 已禁止作为 P0 完成态 |
| Support MIMO / ART | 已有 `REQ-OPT-*`；**不得**用 support 网络替代 per-loudspeaker phase calibration |

**检索证据（v1.0）：** 在 `requirements.md` / `design.md` / `tasks.md` / `PLAN.md` 中对  
`minimum phase|excess phase|mixed phase|Hilbert|cepstrum|residual phase|all-pass|group delay` 等关键词 **无功能需求命中**（仅有时钟对齐、幅值目标等邻近概念）。

---

## 2. Conceptual Distinctions / 概念区分（必须写进设计）

| # | 阶段 | 做什么 | 不做什么 |
|---|------|--------|----------|
| A | **TOA / bulk delay 对齐** | 估计各通道/点位到达时延、双设备 skew；对齐 IR 起点 | 不改正频率相关相位扭曲 |
| B | **幅值 EQ / 目标曲线（min-phase 一致）** | 按 \|H\| 与 target 设计最小相位均衡 |  alone 不消除 excess phase；IR 仍可有长拖尾/群延迟畸变 |
| C | **Excess-phase → near-min-phase（本注核心）** | 估计跨点位 **空间共有** 过量相位；在预振铃约束下做混合相位逆 | 不是线性相位清零；不是单点过拟合 |
| D | **ART / MIMO support** | 多扬声器联合，使 primary+supports ≈ target RTF | 建立在 **已相位预条件化** 的通道之上（公开文献结构） |

流水线顺序（产品锁定）：

```
测量 IR → TOA/delay 对齐 → 【相位校准 C】→ 幅值/目标曲线 B → Support MIMO D → Camilla 导出
```

C 与 D **可分离**：C 产出每扬声器（至少 primary）的 mixed-phase FIR；D 在相位校正后的通道上设计 support FIR。

---

## 3. How Dirac-class Systems Publicly Describe This / 公开描述摘要

### 3.1 Primary public technical sources（本地 PDF + 公开论文）

| 来源 | 路径 / URL | 相关内容 |
|------|------------|----------|
| Brännmark & Sternad, ISEAT 2015（AES c1506 英文稿） | `/workspace/dirac-art-papers/Brannmark_Sternad_AES_c1506.pdf` · text: `.../text/Brannmark_Sternad_AES_c1506.txt` | §3.1–3.4：min-phase vs mixed-phase；multipoint **constrained robust mixed-phase**（预振铃约束 + modeling delay）vs 纯 min-phase vs 无约束 Wiener |
| ICASSP 2012 MIMO support | `/workspace/dirac-art-papers/paper2_user.pdf` · `text/icassp2012_mimo.txt` · `text/paper2_user.txt` | 结构：`R = q^{-d} F^*(q) R_1(q^{-1})`；`F^*` = 共轭全通（由 **各扬声器跨测点共有 excess-phase 零点** 构造）；`R_1` = 因果稳定（含 supports） |
| JAES 2015 Focus Control | `/workspace/dirac-art-papers/paper1_user.pdf` · `text/jaes2015_focus.txt` | 重申 loudspeaker–room 几乎皆有 excess phase；须 **mixed-phase** 校正；min-phase 不足 |
| EP 2 692 155 B1 / US 9 781 510 | `EP2692155B1.pdf` / `US9781510.pdf` + text extracts | ART/MIMO：在 MIMO 前对每扬声器做 **all-pass 相位补偿 + 时延对齐**；`F_j` 来自该列（扬声器）在所有测点共有的 excess-phase 因子；目标含 bulk delay `d_0` 以避免预振铃 |
| US 8 194 885 B2（公开摘要） | Google Patents | Spatially robust audio precompensation：幅值（min-phase 逆）× 仅安全可逆的非最小相位零点之因果 FIR 近似 |

### 3.2 Algorithmic picture（可公开复述 / 可实现的推理）

公开文献给出的 **结构**（非 Dirac Live 闭源实现细节）：

1. **分解：** 对每条测得 RTF，概念上  
   `H(ω) = |H(ω)| · e^{j φ_min(|H|)} · e^{j φ_excess}`  
   其中 `φ_min` 由幅值经 **Hilbert / 实倒谱（cepstrum）最小相位重构** 得到（标准 DSP；Dirac 营销材料未强制点名，但是业界与公开论文隐含的「minimum phase representation」做法）。
2. **空间稳健 excess：** 仅校正对各测点 **共有 / 系统化** 的 excess-phase 成分（ICASSP：「common excess phase zeros」；EP：「share a common excess phase factor」）。位置相关的晚期反射等留给不确定度模型或留给后续 MIMO，**不过拟合单点**。
3. **混合相位补偿结构：**  
   - 非因果（共轭）**全通** `F^*`：抵消共有群延迟/过量相位扭曲；  
   - × 因果稳定滤波器 `R_1`（幅值 + 支撑/MIMO）；  
   - × modeling delay `q^{-d}`，使因果实现可行且控制预振铃窗口。
4. **预振铃约束：** 限制 modeling delay / 允许的预振铃时长与幅度（听觉对 pre-ring 极敏感）。目标是 **接近该幅值（或目标幅值）对应的最小相位脉冲**，而非线性相位冲激。
5. **与 ART 的关系：** EP/ICASSP 明确把 per-loudspeaker all-pass 相位预条件化当作 MIMO/`R_1` **之前** 的步骤。

### 3.3 Dirac Live product messaging（公开、非算法细节）

- Dirac 官方 / 员工公开表述：校正 **频率 + 时间（冲激响应）**；房间响应非最小相位 → 需 **mixed-phase**；在预振铃时长/电平约束内做 **broadband excess-phase correction**（含低频）。
- 实现介质：曾公开说明可用高阶 IIR 或 FIR 实现同一 mixed-phase 传递函数（全通是非最小相位 IIR 的例子）—— **与「是否 min-phase」无关，与硬件资源有关**。
- CamillaDSP 产品侧：本工具统一导出 **长 FIR `Conv`** 实现相位校准与 support，避免依赖闭源运行时。

### 3.4 Proprietary vs inferable / 专有 vs 可推断

| 可公开依赖 / 可自研复现 | 专有 / 未知（不得声称克隆） |
|------------------------|------------------------------|
| min-phase vs excess-phase 分解概念；Hilbert/cepstral min-phase | Dirac Live 内部零点选取、正则、预振铃约束精确 psychoacoustic 曲线 |
| 「仅校正空间共有 excess」原则 | 专利中具体多项式阶次、数值求解器超参 |
| 全通 excess 逆 × 因果滤波器 × modeling delay 的 **结构** | 闭源滤波器系数、多速率分频实现细节 |
| 多点 MSE + 预振铃约束的 **设计哲学**（ISEAT） | Unison/ART 完整商业求解器 |

**本工具立场：** in-house 实现公开 DSP + 文献结构启发；**不**链接 Dirac SDK；**不**声称 bit-for-bit 专利复刻。

---

## 4. Local Excerpt Paths Relied On / 依赖的本地摘录

| 文件 | 用途 |
|------|------|
| `/workspace/dirac-art-papers/text/Brannmark_Sternad_AES_c1506.txt` | §3.1–3.4 min/mixed/constrained designs（约 L250–430） |
| `/workspace/dirac-art-papers/text/icassp2012_mimo.txt` | allpass `F^*` + causal `R_1`；common excess zeros（约 L68–155） |
| `/workspace/dirac-art-papers/text/jaes2015_focus.txt` | excess phase → mixed-phase necessity（约 L56–63） |
| `/workspace/dirac-art-papers/text/EP2692155B1.txt` | [0058]–[0060], [0084]–[0088] all-pass pre-conditioning before MIMO |
| `/workspace/dirac-art-papers/text/US9781510.txt` | 同族美专文本 |
| `/workspace/dirac-art-papers/text/paper1_user.txt` / `paper2_user.txt` | 用户提供 PDF 全文抽取 |
| 规格（修订前缺口）：`/workspace/art-camilladsp-tool/specs/requirements.md` 等 |

公开 Web（未用 Sci-Hub）：ISEAT PDF 镜像、`patents.google.com` US8194885 / US10284995、Dirac Live 产品页、Dirac 员工/社区对 excess-phase + pre-ringing 的公开说明。

---

## 5. Spec Patch Proposal Summary / 已落地补丁摘要

**v1.1：** 曾写入独立 Rust 规格（见 git 历史 / 本目录旧版）。  

**v2.0（现行）：** 同一需求实质已 **rebase 到 RoomForge**：

- **`REQ-PHASE-01`…`08`**：保留；模块指向 `AudioOptimizer.Art` / Dsp 扩展，预览走 WPF Visualization  
- **`REQ-OPT-05`**：primary 默认含相位校准 FIR  
- **Design v2.0：** RoomForge 流水线；取消 Rust/`PrimaryPathPolicy` crate 表述  
- **Tasks v2.0：** `T031`–`T038`、`T043`（取代旧 `T061`–`T068`）；旧 FFT/WebUI 重建任务标 BASELINE/CANCELLED  
- **Open decision：** P0 强制 **primary** 相位 FIR；**support** 独立相位 FIR 默认 P1；项目命名见 ADR-Q1

---

## 6. Suggested Acceptance Metrics（实现指引）

- 合成夹具：已知 mixed-phase IR → 校准后 excess group delay 在目标带内显著降低；主峰前能量低于配置的 pre-ring 上限。  
- 与 pure min-phase magnitude EQ 对照：后者 FR 可平但 excess 指标不变 → 满足 `REQ-PHASE-04`。  
- Camilla（经 `AudioOptimizer.Camilla`）：primary 路径存在 `fir/phase_<ch>.wav`（或合并 FIR）；phase-bypass 与 support-bypass 可独立切换。

---

*End of research note*
