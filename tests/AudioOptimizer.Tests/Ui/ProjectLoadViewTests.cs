namespace AudioOptimizer.Tests.Ui;

using System.Reflection;
using AudioOptimizer.IO;
using AudioOptimizer.UI.Controls;
using AudioOptimizer.UI.ViewModels;
using AudioOptimizer.UI.Windows;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// M1 — the shell and the project-load path. The IO layer's inability to open a device is already proven at the
/// assembly level in <c>ProjectPersistenceTests</c>; what these tests add is that the SHELL does not reach for
/// one. That distinction matters from M2 on, when the UI project gets the audio stack for the measurement flow
/// and the load path could suddenly reach a device if someone wrote "on load, show which saved devices are still
/// present". The guard is therefore placed on the view model rather than on the assembly reference.
/// <para>
/// Projects are written through <see cref="SessionStore"/> — the real producer path — so the view is exercised
/// against the file format instead of a hand-built object graph.
/// </para>
/// </summary>
public sealed class ProjectLoadViewTests(ITestOutputHelper output)
{
    private const double SampleRate = 48000;

    [Fact]
    public void A_saved_project_lists_every_slot_with_its_state_and_signals()
    {
        string directory = WriteProject();
        try
        {
            var viewModel = new ProjectLoadViewModel { ProjectDirectory = directory };
            viewModel.Load();

            Assert.True(viewModel.IsLoaded);
            Assert.Equal(ProjectLoadProblem.None, viewModel.Problem);
            // 3 modes × 2 grid points = 6 slots: one Done, one Invalid, one Skipped, three Pending.
            Assert.Equal(6, viewModel.SlotCount);
            Assert.Equal(1, viewModel.DoneCount);
            Assert.Equal(1, viewModel.InvalidCount);
            Assert.Equal(1, viewModel.SkippedCount);
            Assert.Equal(3, viewModel.PendingCount);
            Assert.Equal(0, viewModel.MissingSignalCount);
            Assert.Equal("6 slots: 1 done, 1 invalid, 1 skipped, 3 pending", viewModel.StatusLine);
            Assert.Equal("no missing or unreadable files", viewModel.AffectedPathSummary);
            Assert.Equal("schema 2", viewModel.Schema);
            Assert.Equal("20–150 Hz, 1 s @ 48000 Hz", viewModel.Sweep);
            // 1.8 × 1.0 × 0.6 m at 1 × 1 × 2 = 2 grid points, formatted with "0.##".
            Assert.Equal("1 × 1 × 2 = 2 points, 1.8 × 1 × 0.6 m", viewModel.Grid);
            Assert.Equal("demo-project", viewModel.ProjectId);
            Assert.Equal("umik-1 → dac", viewModel.Slots[0].Capture);
            Assert.Equal("gain 0.5, polarity +1, phase 0°", viewModel.Slots[0].Setting);
            Assert.Equal("1.2300E-2", viewModel.Slots[0].PeakMagnitude);
            Assert.Equal("InputClipping", viewModel.Slots[1].Reasons);
            Assert.Equal("—", viewModel.Slots[2].Reasons);

            // What the user actually sees: text presence from the visual tree, non-blankness from the bitmap.
            RenderedImage image = RenderShell(directory);
            output.WriteLine($"ink {image.InkPixels()} px of {image.Width * image.Height}, {image.Texts.Count} visible texts");

            Assert.True(image.InkPixels() > 1000, $"the shell rendered {image.InkPixels()} ink pixels: it is blank or unstyled");
            Assert.Contains(image.Texts, text => text.Contains(directory));                       // the loaded path
            Assert.Contains(image.Texts, text => text == "6 slots: 1 done, 1 invalid, 1 skipped, 3 pending");
            Assert.Contains(image.Texts, text => text == "grid 1 × 1 × 2 = 2 points, 1.8 × 1 × 0.6 m");
            Assert.Contains(image.Texts, text => text == "sweep 20–150 Hz, 1 s @ 48000 Hz");
            // Every mode × point row: the list is the whole project, not the first few.
            foreach (string id in new[] { "A/x0_y0_z-1", "A/x0_y0_z1", "B/x0_y0_z-1", "B/x0_y0_z1", "AB/x0_y0_z-1", "AB/x0_y0_z1" })
                Assert.Contains(image.Texts, text => text == id);
            Assert.Contains(image.Texts, text => text == "recording + IR");
            Assert.Contains(image.Texts, text => text == "Pending");
            Assert.Contains(image.Texts, text => text == "InputClipping");
            Assert.Contains(image.Texts, text => text == "1.2300E-2");
            // Negative twin: nothing on this path may claim a calibrated level. No calibration step exists in
            // the repo, so "dB SPL" would be a false label — the M0 guard, restated at the shell.
            Assert.DoesNotContain(image.Texts, text => text.Contains("SPL"));

            string png = image.WritePng("m1-project");
            Assert.True(File.Exists(png));
            Assert.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), png);
            Assert.Empty(Directory.GetFiles(TestPaths.RepoRoot, "*.png", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_project_missing_signal_files_lists_every_affected_path_not_just_the_first()
    {
        string directory = WriteProject();
        try
        {
            // Both files of the Done slot, and the impulse response of the Invalid slot: three files, two slots.
            File.Delete(Path.Combine(directory, "A", "x0_y0_z-1.wav"));
            File.Delete(Path.Combine(directory, "A", "x0_y0_z-1.ir.wav"));
            File.Delete(Path.Combine(directory, "A", "x0_y0_z1.ir.wav"));

            var viewModel = new ProjectLoadViewModel { ProjectDirectory = directory };
            viewModel.Load();

            Assert.True(viewModel.IsLoaded);                                  // a partial project is still a project
            // The metadata-only load does not read signals, so the store has nothing to report; checking the disk
            // per slot is the view model's own job, and it reports each file that is gone rather than one
            // category.
            Assert.Equal(ProjectLoadProblem.None, viewModel.Problem);
            Assert.Equal(3, viewModel.AffectedPaths.Count);
            Assert.Equal(2, viewModel.MissingSignalCount);
            Assert.Equal(
                new[] { "x0_y0_z-1.wav", "x0_y0_z-1.ir.wav", "x0_y0_z1.ir.wav" },
                viewModel.AffectedPaths.Select(Path.GetFileName));
            // The manifest stores portable forward-slash relative paths (ProjectPersistenceTests pins
            // "A/x0_y0_z0.wav"); Path.Combine here would silently demand the OS separator instead.
            Assert.EndsWith("A/x0_y0_z-1.wav", viewModel.AffectedPaths[0]);
            Assert.Equal("—", viewModel.Slots[0].Signals);                    // Done, both files gone
            Assert.Equal("recording only", viewModel.Slots[1].Signals);       // Invalid, IR gone, recording present
            Assert.Equal("—", viewModel.Slots[2].Signals);                    // Skipped, no signals by definition
            Assert.False(viewModel.Slots[3].IsMissingSignalFile);              // Pending claims nothing, so nothing is missing

            RenderedImage image = RenderShell(directory);
            Assert.Contains(image.Texts, text => text.StartsWith("3 file(s) affected:"));
            foreach (string name in new[] { "x0_y0_z-1.wav", "x0_y0_z-1.ir.wav", "x0_y0_z1.ir.wav" })
                Assert.Contains(image.Texts, text => text.Contains(name));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_corrupt_manifest_is_reported_with_its_path_instead_of_throwing()
    {
        string directory = Path.Combine(Path.GetTempPath(), "roomforge-m1-corrupt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        string manifestPath = SessionStore.ManifestPath(directory);
        File.WriteAllText(manifestPath, "{ this is not json");
        try
        {
            var viewModel = new ProjectLoadViewModel { ProjectDirectory = directory };
            viewModel.Load();

            Assert.False(viewModel.IsLoaded);
            Assert.Equal(ProjectLoadProblem.ManifestUnreadable, viewModel.Problem);
            Assert.Contains("is not readable JSON", viewModel.Message);
            Assert.Equal(new[] { manifestPath }, viewModel.AffectedPaths);
            Assert.Empty(viewModel.Slots);
            Assert.Equal("not loaded (ManifestUnreadable)", viewModel.StatusLine);
            Assert.Contains("session.json", viewModel.AffectedPathSummary);

            RenderedImage image = RenderShell(directory);
            Assert.Contains(image.Texts, text => text.Contains("is not readable JSON"));
            Assert.Contains(image.Texts, text => text.Contains("session.json"));
            Assert.Contains(image.Texts, text => text == "not loaded (ManifestUnreadable)");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_directory_without_a_project_reports_which_kind_of_absence_it_is()
    {
        string missing = Path.Combine(Path.GetTempPath(), "roomforge-m1-absent-" + Guid.NewGuid().ToString("N")[..8]);
        var viewModel = new ProjectLoadViewModel { ProjectDirectory = missing };
        viewModel.Load();

        Assert.False(viewModel.IsLoaded);
        Assert.Equal(ProjectLoadProblem.DirectoryMissing, viewModel.Problem);
        Assert.Equal(new[] { missing }, viewModel.AffectedPaths);

        // The other kind: the directory is there, the manifest is not.
        Directory.CreateDirectory(missing);
        try
        {
            viewModel.Load();
            Assert.Equal(ProjectLoadProblem.ManifestMissing, viewModel.Problem);
            Assert.Equal(new[] { SessionStore.ManifestPath(missing) }, viewModel.AffectedPaths);
        }
        finally
        {
            Directory.Delete(missing, recursive: true);
        }
    }

    [Fact]
    public void The_load_view_model_cannot_reach_a_device_or_do_plot_math()
    {
        Type viewModel = typeof(ProjectLoadViewModel);
        var audioAssembly = typeof(global::AudioOptimizer.Audio.IAudioBackend).Assembly;

        // (1) Surface: no constructor parameter and no property the caller has to supply or reads back comes from
        // the audio assembly, so a backend cannot even be handed to the load path. This is the view-model-sized
        // version of the assembly-level proof the IO format already has.
        IEnumerable<Type> surface = viewModel.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Concat(viewModel.GetProperties().Select(property => property.PropertyType));
        Assert.DoesNotContain(surface, type => type.Assembly == audioAssembly);

        // (2) The source of the load path names no audio type. Weaker than walking the IL, but it is the guard
        // that survives M2, when the UI project gains the audio reference for the measurement flow: opening a
        // device on load now requires writing one of these names into this file first.
        // ponytail: source scan, not IL walk. Walk method bodies if this code ever moves out of this file.
        string source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "AudioOptimizer.UI", "ViewModels", "ProjectLoadViewModel.cs"));
        foreach (string banned in new[] { "AudioOptimizer.Audio", "IAudioBackend", "WasapiAudioBackend", "AudioDeviceLists", "MeasurementRunner", "MeasurementSession", "PlayAndRecord", "EnumerateDevices" })
            Assert.DoesNotContain(banned, source);

        // (3) No plot or axis math either: that belongs in AudioOptimizer.Visualization, where each transform has
        // its own probe test. Nothing here may convert a level, so nothing here may label one "SPL".
        foreach (string banned in new[] { "AxisScale", "PlotArea", "ToPixel", "ToData", "LevelReference", "ColourScale", "RgbColour", "Math.Log", "Math.Sqrt", "Math.Pow", "SPL" })
            Assert.DoesNotContain(banned, source);

        // (4) Behavioural twin: constructing the view model needs nothing but the project directory, and a real
        // project opens with no backend in the process — this suite has no audio device, and there is no field or
        // argument through which one could have been created.
        string directory = WriteProject();
        try
        {
            ProjectLoadViewModel loaded = Activator.CreateInstance<ProjectLoadViewModel>();
            loaded.ProjectDirectory = directory;
            loaded.Load();

            Assert.True(loaded.IsLoaded);
            Assert.Equal(6, loaded.SlotCount);
            Assert.Equal(0, loaded.MissingSignalCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_window_hosts_the_shell_and_the_entry_point_is_wired()
    {
        // Nothing else in this suite touches MainWindow or App: a typo in StartupUri, in the xmlns prefix or in
        // the ShellView reference would otherwise only surface when the user launched the app. The window is
        // never SHOWN — that needs a desktop and a message pump, and these tests must not need either — so the
        // content is instantiated, detached from the unshown window (the M0 lesson: an unshown Window is not
        // measurable) and rendered on its own.
        string directory = WriteProject();
        try
        {
            RenderedImage image = RenderHarness.Render(
                () =>
                {
                    var window = new MainWindow();
                    Assert.Equal("RoomForge", window.Title);
                    var shell = Assert.IsType<ShellView>(window.Content);   // App.xaml/Window.xaml wiring, not a guess
                    window.Content = null;
                    shell.Project.ProjectDirectory = directory;
                    shell.Project.Load();
                    return shell;
                },
                width: 1200,
                height: 560);

            Assert.True(image.InkPixels() > 1000);
            Assert.Contains(image.Texts, text => text == "6 slots: 1 done, 1 invalid, 1 skipped, 3 pending");

            // The runnable entry point: the toolkit generates Main on App, and a WPF shell must start on the STA.
            MethodInfo? main = typeof(global::AudioOptimizer.UI.App).GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(main);
            Assert.True(main.GetCustomAttribute<STAThreadAttribute>() is not null, "the WPF entry point must be STA");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RenderedImage RenderShell(string directory)
        => RenderHarness.Render(
            () =>
            {
                var shell = new ShellView();
                shell.Project.ProjectDirectory = directory;
                shell.Project.Load();
                return shell;
            },
            width: 1200,
            height: 560);

    /// <summary>
    /// Writes a project through the real store: 3 modes × 2 points on a 1 × 1 × 2 grid = 6 slots, with the first
    /// Done, the second Invalid, the third Skipped and the rest Pending. The Done and Invalid slots get float32
    /// WAV signal files, exactly as a measurement session writes them.
    /// </summary>
    private static string WriteProject()
    {
        string directory = Path.Combine(Path.GetTempPath(), "roomforge-m1-" + Guid.NewGuid().ToString("N")[..8]);
        var slots = new List<SlotManifest>();
        foreach (string mode in new[] { "A", "B", "AB" })
            foreach (string point in new[] { "x0_y0_z-1", "x0_y0_z1" })
            {
                int index = slots.Count;
                slots.Add(new SlotManifest(
                    Mode: mode,
                    PointId: point,
                    State: index switch { 0 => "Done", 1 => "Invalid", 2 => "Skipped", _ => "Pending" },
                    Reasons: index == 1 ? ["InputClipping"] : [],
                    PeakMagnitude: index == 0 ? 0.0123 : null,
                    CompletedUtc: index <= 1 ? new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc) : null,
                    MeasurementId: $"{mode}/{point}",
                    RecordingFile: $"{mode}/{point}.wav",
                    ImpulseResponseFile: $"{mode}/{point}.ir.wav",
                    Capture: new MeasurementCapture("umik-1", "dac", 0.5, 1, 0.0, 0.0)));
            }

        SessionStore.Save(
            directory,
            new SessionManifest(
                SessionManifest.CurrentSchemaVersion,
                new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc),
                new SweepManifest(20, 150, 1.0, SampleRate),
                new GridManifest(1.8, 1.0, 0.6, 1, 1, 2),
                slots,
                "demo-project"));

        // 64 samples of a 100 Hz sine at 0.25 amplitude; the values are irrelevant to the view, their presence is not.
        double[] samples = [.. Enumerable.Range(0, 64).Select(n => Math.Sin(2 * Math.PI * 100 * n / SampleRate) * 0.25)];
        for (int index = 0; index <= 1; index++)
        {
            SessionStore.WriteRecording(directory, slots[index], samples, (int)SampleRate);
            SessionStore.WriteImpulseResponse(directory, slots[index], samples, (int)SampleRate);
        }

        return directory;
    }
}
