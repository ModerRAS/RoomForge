# Extension track on Linux (Art phase, support FIR, Camilla export)

RoomForge's shipped product is the dual-subwoofer spatial optimizer (`SubwooferOptimizer` and the product contract). This change does not replace that path, does not rewrite WPF or WASAPI, and does not change the dual-sub `session.json` schema (still version 3). Existing dual-sub projects stay readable.

## What this PR delivers

Portable `net10.0` libraries, tested on Linux:

- `AudioOptimizer.Art` — `SpeakerRole` (`Primary` / `Support` / `Unassigned`), a versioned art-session JSON document (version 1 files omit roles and load as `Unassigned`; a newer version is refused), real-cepstrum minimum-phase / excess split, spatially common excess across at least two positions, mixed-phase FIR under a modeling delay and pre-ring duration/level cap, and a regularized band-limited support FIR (`ISupportOptimizer`: P0 weighted least squares plus a stub solver). The export contract version stays `1` if the solver is swapped.
- `AudioOptimizer.Camilla` — CamillaDSP YAML (mixer, primary phase `Conv`, support `Conv`, biquad band-limit, gain, independent phase/support bypass), float32 FIR WAV, `manifest.json` (`export_version`, channel map, FIR length and sha256), and an optional Chinese README. If validation fails, the destination package is not written.
- `tests/AudioOptimizer.Art.Tests` — `net10.0` xUnit, no WPF.

Locked choices:

- Excess decomposition is the **real cepstrum** (`RealCepstrum`), not a Hilbert transformer and not a magnitude-only EQ.
- Support FIR length defaults to **2048** taps and is rounded up to a power of two. At 48 kHz, 2048 taps is about 42.7 ms. That length is the design DFT size, so the band limit is exact on those bins.
- A single listening position is rejected for phase calibration. The default path is not an unconstrained one-point inverse.

## Verified on Linux

```
dotnet build src/AudioOptimizer.Art/AudioOptimizer.Art.csproj -c Release
dotnet build src/AudioOptimizer.Camilla/AudioOptimizer.Camilla.csproj -c Release
dotnet test tests/AudioOptimizer.Art.Tests/AudioOptimizer.Art.Tests.csproj -c Release
```

`tests/AudioOptimizer.Tests` is `net10.0-windows` (WPF) and is not run on this agent.

## Not in this PR

| Issue | Topic | In this PR |
|-------|--------|------------|
| [#2](https://github.com/ModerRAS/RoomForge/issues/2) | Extension epic | Linux-doable library slice only |
| [#3](https://github.com/ModerRAS/RoomForge/issues/3) | ASIO backend | Not implemented |
| [#4](https://github.com/ModerRAS/RoomForge/issues/4) | Multi-measure + WPF | Not implemented |
| [#5](https://github.com/ModerRAS/RoomForge/issues/5) | ART UI | Not implemented |
| [#6](https://github.com/ModerRAS/RoomForge/issues/6) | Camilla on hardware | Files only; CamillaDSP is not launched |

Windows CI still builds `AudioOptimizer.sln` on `windows-latest`. The new test project is part of that solution and does not reference WPF.
