namespace AudioOptimizer.Tests.Ui;

using System.Windows.Controls;
using System.Windows.Input;
using AudioOptimizer.Audio;
using AudioOptimizer.Core;
using AudioOptimizer.IO;
using AudioOptimizer.Measurement;
using AudioOptimizer.UI.Controls;
using AudioOptimizer.UI.ViewModels;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// M2 — the measurement flow. All of it runs without hardware through <see cref="FakeAudioBackend"/>: a perfect
/// rig (the sweep echoed back) reaches Done, digital silence reaches Invalid with named reasons, and a gate held
/// by the test keeps a capture in flight so "off-thread" and "the UI did not block" are structural facts.
/// <para>
/// The progress numbers are asserted as derivations over slot state after a skip, a retry and a resume — never as
/// the value of a maintained counter, and never against a literal denominator.
/// </para>
/// </summary>
[Collection(RenderCollection.Name)]
public sealed class MeasurementFlowViewTests(ITestOutputHelper output)
{
    [Fact]
    public void An_empty_device_list_is_a_list_and_a_message_never_an_exception()
    {
        var backend = new FakeAudioBackend { Devices = AudioDeviceLists.Empty };
        var flow = new MeasurementFlowViewModel(backend);
        flow.RefreshDevices();

        Assert.Empty(flow.InputDevices);
        Assert.Empty(flow.OutputDevices);
        Assert.Null(flow.SelectedInput);
        Assert.Null(flow.SelectedOutput);
        Assert.Equal(
            "No audio devices found: connect the measurement microphone and the output device, then refresh.",
            flow.DeviceMessage);

        // The other empty case: the device layer itself fails. Still a message, still no exception, still no
        // selection to measure with.
        backend.ThrowOnEnumerate = true;
        flow.RefreshDevices();
        Assert.Empty(flow.InputDevices);
        Assert.Contains("No device list available: the audio service is not running", flow.DeviceMessage);

        flow.ProjectDirectory = NewDirectory("nodevices");
        try
        {
            flow.StartSession();
            Assert.Contains("Select an input (microphone) and an output (playback) device first.", flow.Status);
            Assert.Null(flow.Session);
        }
        finally
        {
            Cleanup(flow.ProjectDirectory);
        }
    }

    [Fact]
    public void The_prompt_states_the_grid_indices_and_the_metres_the_grid_itself_derived()
    {
        string directory = NewDirectory("prompt");
        try
        {
            var flow = Open(new FakeAudioBackend(), directory);

            // 1 × 1 × 2 grid, 0.6 m high: the two z levels are −1 and +1, whose coordinates are 0 m and 0.6 m.
            Assert.Equal(2, flow.Grid.PointCount);
            Assert.Equal("1/2", flow.PositionText);
            Assert.Contains("Mode A", flow.PromptText);
            Assert.Contains("Grid X 0 / Grid Y 0 / Grid Z -1", flow.PromptText);
            // X and Y are the centres (a 1-point axis has level 0 → 0.9 m and 0.5 m of 1.8 × 1.0 m); Z is the
            // low end of a 2-point axis: 0 m of 0.6 m.
            Assert.Contains("X 0.90 m / Y 0.50 m / Z 0.00 m", flow.PromptText);

            RenderHarness.RunPumped(async () =>
            {
                await flow.MeasureAsync();
                Assert.Equal(MeasurementSlotState.Done, flow.Session!.Slots[0].State);
            });

            // Second point: z = +1 → Level(+1, 0.6) = 0.6 m. A re-derived guess (0.3 m) would fail here.
            Assert.Equal("2/2", flow.PositionText);
            Assert.Contains("Grid Z 1", flow.PromptText);
            Assert.Contains("Z 0.60 m", flow.PromptText);
            output.WriteLine(flow.PromptText);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void The_position_denominator_is_the_grid_point_count_never_a_literal()
    {
        string small = NewDirectory("small");
        string large = NewDirectory("large");
        try
        {
            var two = Open(new FakeAudioBackend(), small, countZ: 2);
            Assert.Equal("1/2", two.PositionText);
            Assert.Equal("1/6", two.CursorText);                    // 3 modes × 2 points = 6 slots
            Assert.DoesNotContain("27", two.PositionText + two.CursorText);

            var many = Open(new FakeAudioBackend(), large, countX: 3, countY: 3, countZ: 3);
            Assert.Equal("1/27", many.PositionText);                // 27 because 3·3·3 is 27, not because it is typed
            Assert.Equal("1/81", many.CursorText);
        }
        finally
        {
            Cleanup(small);
            Cleanup(large);
        }
    }

    [Fact]
    public void Progress_after_a_skip_keeps_both_denominators_and_advances_only_the_cursor()
    {
        string directory = NewDirectory("skip");
        try
        {
            var backend = new FakeAudioBackend();
            var flow = Open(backend, directory);

            RenderHarness.RunPumped(async () =>
            {
                await flow.MeasureAsync();                          // slot 1: clean → Done
                Assert.Equal("1 of 6 measured", flow.MeasuredText);
                Assert.Equal("2/6", flow.CursorText);

                backend.ReturnSilence = true;
                await flow.MeasureAsync();                          // slot 2: rejected with named reasons
                Assert.True(flow.HasFeedback);
                IReadOnlyList<QualityIssue> reasons = flow.Session!.Slots[1].Reasons;
                Assert.NotEmpty(reasons);
                Assert.Contains(reasons[0].ToString(), flow.FeedbackText);
                Assert.Equal("1 of 6 measured", flow.MeasuredText);  // an Invalid slot is not measured-and-usable

                flow.Skip();
            });

            Assert.Equal(MeasurementSlotState.Skipped, flow.Session!.Slots[1].State);
            Assert.Equal("1 of 6 measured", flow.MeasuredText);      // a skip is not a measurement
            Assert.Equal("3/6", flow.CursorText);                    // denominator still the 6 total slots
            Assert.Equal("1/2", flow.PositionText);                  // denominator still the 2 grid points
            Assert.False(flow.HasFeedback);
            Assert.Contains("Skipped grid position 2/2", flow.Status);
            Assert.Contains("next slot 3/6", flow.Status);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void A_rejected_point_holds_the_cursor_until_it_is_retried_or_skipped()
    {
        string directory = NewDirectory("retry");
        try
        {
            var backend = new FakeAudioBackend { ReturnSilence = true };
            var flow = Open(backend, directory);

            RenderHarness.RunPumped(async () =>
            {
                await flow.MeasureAsync();                          // slot 1 rejected
                Assert.Equal("0 of 6 measured", flow.MeasuredText);  // Done only, so a rejected point is not "measured"
                Assert.Equal("1/6", flow.CursorText);                // Invalid is retryable, so it holds the cursor
                Assert.Equal("1/2", flow.PositionText);
                Assert.True(flow.CanSkip);

                backend.ReturnSilence = false;
                await flow.MeasureAsync();                          // retry the same slot
                Assert.Equal("1 of 6 measured", flow.MeasuredText);
                Assert.Equal("2/6", flow.CursorText);
                Assert.Equal(MeasurementSlotState.Done, flow.Session!.Slots[0].State);
                Assert.False(flow.HasFeedback);
            });
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void A_measurement_runs_off_the_ui_thread_and_its_progress_arrives_on_the_dispatcher()
    {
        string directory = NewDirectory("threads");
        try
        {
            var backend = new FakeAudioBackend { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            backend.ArmCaptureStart();
            var flow = Open(backend, directory);

            int dispatcherThread = 0;
            RenderHarness.RunPumped(async () =>
            {
                dispatcherThread = Environment.CurrentManagedThreadId;
                int caller = dispatcherThread;
                var notifiedOn = new List<int>();
                flow.PropertyChanged += (_, _) => notifiedOn.Add(Environment.CurrentManagedThreadId);

                Task task = flow.MeasureAsync();
                await backend.Started.Task;                          // the capture has started and is blocked

                Assert.True(backend.CaptureOpen);
                Assert.False(task.IsCompleted);                       // the call returned while the work is blocked
                Assert.NotEqual(caller, backend.WorkerThreadId);      // and the work is on another thread

                backend.Gate!.SetResult();
                await task;

                Assert.Equal(caller, Environment.CurrentManagedThreadId);   // the continuation resumed on the dispatcher
                Assert.NotEmpty(notifiedOn);
                Assert.All(notifiedOn, id => Assert.Equal(caller, id));     // every notification was marshalled
            });

            Assert.NotEqual(0, dispatcherThread);
            Assert.Equal(MeasurementSlotState.Done, flow.Session!.Slots[0].State);
            output.WriteLine($"capture ran on thread {backend.WorkerThreadId}, dispatcher thread {dispatcherThread}");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>
    /// The regression guard for the outcome a cancellation token used to make reachable: aborting immediately after
    /// invoking a measurement — racing the scheduler, with no await in between — must end in a <b>recorded</b> state,
    /// never <c>Pending</c>. It fails in two ways if a token is ever put back on the <c>Task.Run</c> here: the queued
    /// delegate can lose the race and never run (leaving the slot Pending, which is what this asserts against), and the
    /// source scan in <see cref="ThreadFreeLayersTests"/> pins the tokenless call shape exactly. Deterministic today:
    /// with no token the delegate always runs and <c>IsAborted</c> is already set when its verdict is read.
    /// </summary>
    [Fact]
    public void An_abort_raced_against_the_scheduler_still_records_a_state_and_never_leaves_the_slot_pending()
    {
        string directory = NewDirectory("abortrace");
        try
        {
            var backend = new FakeAudioBackend();
            var flow = Open(backend, directory);

            RenderHarness.RunPumped(async () =>
            {
                Task task = flow.MeasureAsync();   // starts synchronously up to its first await
                flow.Abort();                      // raced on purpose: no wait for the capture to start
                await task;

                MeasurementSlot slot = flow.Session!.Slots[0];
                Assert.Equal(MeasurementSlotState.Invalid, slot.State);       // never Pending
                Assert.Contains(QualityIssue.AbortedInFlight, slot.Reasons);
                Assert.NotNull(slot.PeakMagnitude);                          // and the capture is kept
                Assert.Equal("0 of 6 measured", flow.MeasuredText);
                Assert.Equal("1/6", flow.CursorText);
                Assert.False(backend.CaptureOpen);
            });
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Abort_from_the_decision_panel_records_the_slot_and_moves_neither_number()
    {
        string directory = NewDirectory("abort");
        try
        {
            var backend = new FakeAudioBackend { ReturnSilence = true };
            var flow = Open(backend, directory);

            RenderHarness.RunPumped(async () =>
            {
                await flow.MeasureAsync();
                MeasurementSlotState recorded = flow.Session!.Slots[0].State;   // Invalid, with named reasons
                Assert.Equal(MeasurementSlotState.Invalid, recorded);
                string cursor = flow.CursorText;
                string tally = flow.MeasuredText;

                flow.Abort();                                                // must not throw out of the flow

                Assert.True(flow.IsAborted);
                Assert.False(flow.HasFeedback);
                Assert.Equal(recorded, flow.Session!.Slots[0].State);         // the verdict is kept, not discarded
                Assert.Equal(cursor, flow.CursorText);                       // the abort moved nothing
                Assert.Equal(tally, flow.MeasuredText);
                Assert.Contains("keeps its recorded Invalid verdict", flow.Status);
                Assert.False(backend.CaptureOpen);                           // no capture left open
                Assert.False(backend.Disposed);                              // and the layer was not torn down

                backend.ReturnSilence = false;
                await flow.MeasureAsync();                                    // the next run opens cleanly
                Assert.Equal(2, backend.Opens);
                Assert.Equal(MeasurementSlotState.Done, flow.Session!.Slots[0].State);
                Assert.False(flow.IsAborted);
            });
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Abort_while_a_capture_is_in_flight_records_Invalid_and_keeps_the_capture()
    {
        string directory = NewDirectory("abortflight");
        try
        {
            var backend = new FakeAudioBackend { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            backend.ArmCaptureStart();
            var flow = Open(backend, directory);

            RenderHarness.RunPumped(async () =>
            {
                Task task = flow.MeasureAsync();
                await backend.Started.Task;
                Assert.True(backend.CaptureOpen);

                flow.Abort();                                                 // returns while the capture is still running

                Assert.False(task.IsCompleted);
                Assert.True(flow.IsAborted);
                Assert.Contains("Run aborted", flow.Status);

                backend.Gate!.SetResult();
                await task;                                                   // the capture ends; nothing escapes

                // boss-1's ruling: the completed capture is KEPT as data but recorded Invalid with a reason that no
                // quality check can produce, so the Done-only tally does not move and the Invalid point holds the cursor.
                MeasurementSlot slot = flow.Session!.Slots[0];
                Assert.False(backend.CaptureOpen);                            // the device is closed
                Assert.Equal(MeasurementSlotState.Invalid, slot.State);
                Assert.Equal([QualityIssue.AbortedInFlight], slot.Reasons);
                Assert.NotNull(slot.PeakMagnitude);                           // the data is kept, not discarded
                Assert.NotNull(flow.Session.StoredImpulseResponse(slot));
                Assert.NotNull(flow.Session.ReadRecording(slot));
                Assert.Equal("0 of 6 measured", flow.MeasuredText);           // Done-only tally unchanged
                Assert.Equal("1/6", flow.CursorText);                        // the cursor is back at this point
                Assert.True(flow.CanSkip);
                Assert.Contains("aborted by you", flow.FeedbackText, StringComparison.OrdinalIgnoreCase);

                // The next measure re-measures THIS point rather than advancing past it.
                backend.Gate = null;
                await flow.MeasureAsync();
                Assert.Equal(2, backend.Opens);
                Assert.Equal(MeasurementSlotState.Done, flow.Session.Slots[0].State);
                Assert.Equal("1 of 6 measured", flow.MeasuredText);
                Assert.Equal("2/6", flow.CursorText);
                Assert.False(flow.IsAborted);
            });
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void The_abort_reason_is_distinguishable_from_a_quality_rejection_in_both_directions()
    {
        string directory = NewDirectory("abortword");
        try
        {
            var backend = new FakeAudioBackend { ReturnSilence = true };
            var flow = Open(backend, directory);

            RenderHarness.RunPumped(async () =>
            {
                await flow.MeasureAsync();                                    // silence → a real quality rejection
                Assert.Equal(MeasurementSlotState.Invalid, flow.Session!.Slots[0].State);
                Assert.DoesNotContain(QualityIssue.AbortedInFlight, flow.Session.Slots[0].Reasons);
                Assert.DoesNotContain("aborted by you", flow.FeedbackText, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith("Rejected at grid position", flow.FeedbackText);

                flow.Skip();                                                  // move to the next point
                backend.ReturnSilence = false;
                backend.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                backend.ArmCaptureStart();                                    // wait for THIS capture, not the first
                Task task = flow.MeasureAsync();
                await backend.Started.Task;
                flow.Abort();
                backend.Gate.SetResult();
                await task;

                MeasurementSlot aborted = flow.Session.Slots[1];
                Assert.Equal(MeasurementSlotState.Invalid, aborted.State);
                Assert.Contains(QualityIssue.AbortedInFlight, aborted.Reasons);
                Assert.Contains("aborted by you", flow.FeedbackText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Rejected at grid position", flow.FeedbackText);
                // The quality verdict for the skipped point is untouched: the abort reason did not leak backwards.
                Assert.DoesNotContain(QualityIssue.AbortedInFlight, flow.Session.Slots[0].Reasons);
            });
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void A_device_failure_is_a_message_and_the_point_stays_pending()
    {
        string directory = NewDirectory("devicefail");
        try
        {
            var backend = new FakeAudioBackend { FailWith = AudioOpenFailureKind.DeviceInUse };
            var flow = Open(backend, directory);

            RenderHarness.RunPumped(async () =>
            {
                await flow.MeasureAsync();                                    // must not throw
                Assert.Equal(MeasurementSlotState.Pending, flow.Session!.Slots[0].State);
                Assert.Contains("DeviceInUse", flow.Status);
                Assert.Contains("still Pending", flow.Status);
                Assert.Equal("1/6", flow.CursorText);                         // the cursor did not move
                Assert.False(backend.CaptureOpen);
            });
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Resuming_a_project_keeps_its_grid_and_continues_where_it_stopped()
    {
        string directory = NewDirectory("resume");
        try
        {
            SessionManifest manifest = SeedProjectWithOneDoneSlot(directory);
            Assert.Equal(1, manifest.Slots.Count(slot => slot.State == "Done"));

            var backend = new FakeAudioBackend();
            var flow = new MeasurementFlowViewModel(backend);
            flow.UseProject(directory, manifest);                             // the handoff the shell performs
            Assert.Equal(2, flow.CountZ);
            Assert.Equal(1.8, flow.WidthMetres);
            Assert.Equal(0.6, flow.HeightMetres);
            Assert.Equal(1.0, flow.DurationSeconds);
            Assert.Equal(150.0, flow.EndHz);

            flow.RefreshDevices();
            flow.StartSession();                                              // resumes: grid and sweep match
            Assert.Contains("Session open", flow.Status);
            Assert.Equal("1 of 6 measured", flow.MeasuredText);               // the tally survives the resume
            Assert.Equal("2/6", flow.CursorText);                             // and the cursor points at the next Pending slot
            Assert.Equal("2/2", flow.PositionText);
            Assert.Contains("Grid Z 1", flow.PromptText);
            Assert.Contains("Z 0.60 m", flow.PromptText);

            RenderHarness.RunPumped(async () =>
            {
                await flow.MeasureAsync();
                Assert.Equal("2 of 6 measured", flow.MeasuredText);
                Assert.Equal("3/6", flow.CursorText);
            });
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void A_project_with_a_different_grid_is_refused_rather_than_merged()
    {
        string directory = NewDirectory("mismatch");
        try
        {
            SessionManifest manifest = SeedProjectWithOneDoneSlot(directory);
            var flow = new MeasurementFlowViewModel(new FakeAudioBackend());
            flow.UseProject(directory, manifest);
            flow.CountZ = 3;                                                  // the user changed the grid, not the file
            flow.RefreshDevices();
            flow.StartSession();

            Assert.Null(flow.Session);
            Assert.Contains("Could not open a session", flow.Status);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Space_and_enter_carry_the_same_measure_command_as_the_button()
    {
        string directory = NewDirectory("keys");
        try
        {
            var backend = new FakeAudioBackend();
            RenderedImage image = RenderHarness.Render(
                () =>
                {
                    var shell = new ShellView(backend);
                    shell.TabStrip.SelectedIndex = 1;               // the Measure tab: unselected tab content is not loaded
                    // A Button's Command binding is evaluated by the layout pass, not by the constructor:
                    // measured, Command read null before this. The harness lays out again; this is only here so
                    // the assertions below look at what the user's screen has.
                    shell.Width = 1200;
                    shell.Height = 560;
                    shell.Measure(new System.Windows.Size(1200, 560));
                    shell.Arrange(new System.Windows.Rect(0, 0, 1200, 560));
                    shell.UpdateLayout();
                    List<KeyBinding> bindings = [.. shell.InputBindings.OfType<KeyBinding>()];

                    // §25: Space and Enter are how a run is driven. Both must take the button's own command, so a
                    // binding wired to something else fails here rather than at the user's desk.
                    Assert.Contains(bindings, binding => binding.Key == Key.Space && ReferenceEquals(binding.Command, shell.Flow.MeasureCommand));
                    Assert.Contains(bindings, binding => binding.Key == Key.Enter && ReferenceEquals(binding.Command, shell.Flow.MeasureCommand));
                    var button = Assert.IsType<Button>(shell.FindName("MeasureButton"));
                    Assert.Same(shell.Flow.MeasureCommand, button.Command);

                    // Negative twin: with no session there is nothing to measure, and the panel says so instead of
                    // enumerating devices behind the user's back.
                    Assert.False(shell.Flow.MeasureCommand.CanExecute(null));
                    Assert.Contains("No device list read yet", shell.Flow.DeviceMessage);

                    shell.Flow.ProjectDirectory = directory;
                    shell.Flow.CountX = 1;
                    shell.Flow.CountY = 1;
                    shell.Flow.CountZ = 2;
                    shell.Flow.RefreshDevices();
                    shell.Flow.StartSession();
                    Assert.True(shell.Flow.MeasureCommand.CanExecute(null));
                    return shell;
                },
                width: 1200,
                height: 560);

            Assert.True(image.InkPixels() > 1000, $"the shell rendered {image.InkPixels()} ink pixels");
            output.WriteLine($"{image.InkPixels()} ink px of {image.Width * image.Height}: " + string.Join(" | ", image.Texts));
            Assert.Contains(image.Texts, text => text.Contains("Grid X 0 / Grid Y 0 / Grid Z -1"));
            Assert.Contains(image.Texts, text => text.Contains("next slot 1/6 · grid position 1/2 · 0 of 6 measured"));
            Assert.Contains(image.Texts, text => text == "1 input(s), 1 output(s).");
            Assert.Contains(image.Texts, text => text.Contains("measure each position with Space or Enter"));
            string png = image.WritePng("m2-flow");
            Assert.True(File.Exists(png));
            Assert.Empty(Directory.GetFiles(TestPaths.RepoRoot, "*.png", SearchOption.AllDirectories));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void A_device_layer_torn_down_mid_capture_is_recorded_not_thrown()
    {
        // Abort does NOT dispose the backend (the design keeps IAudioBackend unchanged: WasapiAudioBackend.Dispose
        // is a documented no-op because each call opens and closes its own endpoints). This test covers the case
        // the lead flagged anyway: the device layer failing underneath an in-flight capture — ObjectDisposedException
        // arriving from inside the runner rather than from the abort path. It must become a message and a state,
        // never an exception out of the UI.
        string directory = NewDirectory("disposed");
        try
        {
            var backend = new FakeAudioBackend
            {
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                ThrowObjectDisposedOnRelease = true,
            };
            var flow = Open(backend, directory);

            RenderHarness.RunPumped(async () =>
            {
                Task task = flow.MeasureAsync();
                await backend.Started.Task;

                flow.Abort();                                  // abort returns while the capture is still in flight
                Assert.False(task.IsCompleted);

                backend.Gate!.SetResult();                     // the capture now unwinds with ObjectDisposedException
                await task;                                     // and nothing escapes

                Assert.False(backend.CaptureOpen);
                Assert.True(flow.IsAborted);
                Assert.Contains("ObjectDisposedException", flow.Status);
                Assert.Equal(MeasurementSlotState.Pending, flow.Session!.Slots[0].State);   // nothing was measured
                Assert.Equal("0 of 6 measured", flow.MeasuredText);
                Assert.Equal("1/6", flow.CursorText);

                backend.Gate = null;
                backend.ThrowObjectDisposedOnRelease = false;
                await flow.MeasureAsync();                     // the next run opens cleanly
                Assert.Equal(2, backend.Opens);
                Assert.Equal(MeasurementSlotState.Done, flow.Session!.Slots[0].State);
            });
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static MeasurementFlowViewModel Open(FakeAudioBackend backend, string directory, int countX = 1, int countY = 1, int countZ = 2)
    {
        var flow = new MeasurementFlowViewModel(backend)
        {
            ProjectDirectory = directory,
            CountX = countX,
            CountY = countY,
            CountZ = countZ,
        };
        flow.RefreshDevices();
        Assert.Single(flow.InputDevices);       // the fake rig: one input and one output
        flow.StartSession();
        Assert.NotNull(flow.Session);
        return flow;
    }

    /// <summary>Writes a real project through <see cref="MeasurementSession"/>: 3 modes × 2 points, slot 0 Done.</summary>
    private static SessionManifest SeedProjectWithOneDoneSlot(string directory)
    {
        var sweep = new SweepSettings(20, 150, 1.0, 48000);
        MeasurementSession session = MeasurementSession.Start(directory, MeasurementGrid.Create(1.8, 1.0, 0.6, 1, 1, 2), sweep);
        var backend = new FakeAudioBackend();
        var runner = new MeasurementRunner(
            backend,
            session,
            backend.Devices.Outputs[0],
            backend.Devices.Inputs[0],
            new AudioBackendSettings(48000, AudioShareMode.Shared, 100),
            TimeSpan.FromSeconds(0.5),
            TimeSpan.FromSeconds(0.5));
        // Measured through the real runner, so the stored peak is a real one: an invented peak would poison the
        // session median and make the next point fail the outlier check (measured while writing this test).
        MeasurementRunOutcome outcome = runner.Run(session.Slots[0])!;
        Assert.Equal(MeasurementSlotState.Done, outcome.Slot.State);
        return SessionStore.Load(directory).Require();
    }

    private static string NewDirectory(string tag)
        => Path.Combine(Path.GetTempPath(), $"roomforge-m2-{tag}-{Guid.NewGuid().ToString("N")[..8]}");

    private static void Cleanup(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
