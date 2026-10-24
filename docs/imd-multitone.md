# IMD + Multi-tone (offline slice)

Offline stimulus generation + analysis for intermodulation distortion. Bilingual / 双语.

This PR is the **Linux-testable library slice only**: pure-math generators and analyzers in
`AudioOptimizer.Dsp`, plus `tests/AudioOptimizer.Dsp.Tests` (net10.0 xUnit, no WPF/WASAPI).
**Hardware duplex UI wiring (WASAPI play + record through `IAudioBackend` / `MeasurementRunner`)
is NOT in this PR.**

## Quick use / 快速上手

```csharp
using AudioOptimizer.Dsp;

// Dual-tone stimulus (caller picks the pair; e.g. DIN-style 60 + 7000 Hz, or 250 + 8020 Hz).
double[] stimulus = DualToneGenerator.Generate(
    f1Hz: 60, f2Hz: 7000, amplitude1: 0.8, amplitude2: 0.2,
    sampleRate: 48000, durationSeconds: 2.0);

// ... play stimulus through IAudioBackend on Windows, record the response ...

// Offline analysis of the captured buffer:
ImdResult r = ImdAnalyzer.Analyze(capture, sampleRate: 48000, f1Hz: 60, f2Hz: 7000);
Console.WriteLine($"IMD {r.ImdRatioDb:F1} dB ({r.ImdPercent:F3} %)");
foreach (var p in r.SecondOrder.Concat(r.ThirdOrder))
    Console.WriteLine($"{p.Label} @ {p.FrequencyHz} Hz = {p.LevelDbFs:F1} dBFS ({p.LevelDbC:F1} dBc)");

// Multi-tone stimulus + analysis:
double[] multi = MultiToneGenerator.Generate([500.0, 1200.0, 2500.0, 6000.0], 0.25, 48000, 1.0);
MultitoneResult m = MultitoneAnalyzer.Analyze(capture, 48000, [500.0, 1200.0, 2500.0, 6000.0]);
Console.WriteLine($"worst clutter {m.WorstClutterDbC:F1} dBc");
```

## What the metrics mean / 指标含义

- **dBFS**: peak-amplitude decibels, 0 dBFS = peak 1.0 (a full-scale sine reads
  20·log10(A)). Fundamentals and products all use this scale.
  dBFS：峰值口径，0 dBFS = 峰值 1.0。
- **dBc**: product (or clutter) level relative to the **stronger fundamental**
  (dual-tone) or the **strongest tone** (multi-tone).
  dBc：相对最强基音/单音的相对电平。
- **IMD ratio (`ImdResult.ImdRatioDb` / `ImdPercent`)**: RSS ratio —
  20·log10( √(ΣP²) / √(ΣF²) ), P = linear peak amplitudes of all reported in-band
  2nd+3rd products, F = linear peaks of the two fundamentals. Identical to an RMS
  ratio (the 1/√2 factors cancel). −∞ (0 %) when no product is measurable.
  总 IMD 比率：全部带内 2+3 阶产物 RSS 相对双基音 RSS。
- **2nd order**: `|f1−f2|`, `f1+f2`. **3rd order**: `2f1−f2`, `2f2−f1`,
  `2f1+f2`, `2f2+f1`. Products at ≤ 0 Hz or ≥ Nyquist are skipped (absent from
  the result, not an error).
- **Multi-tone clutter** honestly means "strongest non-tone spectral peaks"
  (harmonics + IMD + noise peaks outside the exclusion bands) — not a total
  residual-energy integral. `exclusionWidthHz` (default 20 Hz) masks tone bins;
  `searchWidthHz` (default 10 Hz) is the per-tone peak-search half-width.

## Method notes / 方法说明

- One periodic-Hann FFT per capture; per-target parabolic peak interpolation with
  coherent-gain compensation, so coherent (bin-centred) and slightly off-bin tones
  read consistently. 一次 Hann FFT，多目标抛物线插值。
- Pure offline math: no `IAudioBackend`, no WPF. Later Windows work can play the
  generated buffers through the existing WASAPI backend and feed captures back here.
  纯离线数学；Windows 端后续可用现有 WASAPI 播放/录制后回送到这里分析。

## Verify on Linux

```
dotnet build src/AudioOptimizer.Dsp/AudioOptimizer.Dsp.csproj -c Release
dotnet test tests/AudioOptimizer.Dsp.Tests/AudioOptimizer.Dsp.Tests.csproj -c Release
```
