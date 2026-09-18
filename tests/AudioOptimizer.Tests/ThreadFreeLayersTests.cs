namespace AudioOptimizer.Tests;

using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.IO;
using AudioOptimizer.Optimization;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// §35: the DSP, core-model, IO and optimization layers are synchronous and free of threads — deliberately,
/// because that is what makes them testable headless and deterministic. The UI is the layer that owns asynchrony.
/// <para>
/// That was a fact and not a guarantee: the pressure that would dissolve it is a frozen UI, and the obvious wrong
/// fix is to make the DSP async. These two checks make it structural — a public surface that cannot return a
/// <c>Task</c>, and a source scan for the keywords that would have to appear first.
/// </para>
/// <para>
/// <see cref="CancellationToken"/> is deliberately NOT banned, for a reason that is about what it <i>is</i> rather
/// than who currently uses it: it is a cancellation signal a caller may observe, not a thread the layer owns. The
/// ban targets the two things that would break the guarantees above — returning a <c>Task</c> (work that escapes the
/// call) and owning a thread, a timer or a pool (work that runs behind it). A <c>CancellationToken</c> parameter
/// does neither. (The UI no longer passes one: a token that reaches no device call would only decide whether queued
/// work ran at all. That change did not touch this rule, which never depended on it.)
/// </para>
/// </summary>
public sealed class ThreadFreeLayersTests(ITestOutputHelper output)
{
    private static readonly Type[] BannedReturnTypes =
    [
        typeof(Task),
        typeof(Task<>),
        typeof(ValueTask),
        typeof(ValueTask<>),
        typeof(Thread),
        typeof(Timer),
        typeof(ThreadPool),
        typeof(CancellationTokenSource),
        typeof(IAsyncEnumerable<>),
        typeof(IAsyncEnumerator<>),
    ];

    public static TheoryData<string> LayerNames => new("Core", "Dsp", "IO", "Optimization");

    [Theory]
    [MemberData(nameof(LayerNames))]
    public void No_public_member_of_a_law_layer_exposes_a_thread_or_a_task(string layer)
    {
        Assembly assembly = layer switch
        {
            "Core" => typeof(SweepSettings).Assembly,
            "Dsp" => typeof(ComplexMath).Assembly,
            "IO" => typeof(SessionStore).Assembly,
            "Optimization" => typeof(SubwooferOptimizer).Assembly,
            _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown layer."),
        };

        var offenders = new List<string>();
        // Asserted, not printed: if GetExportedTypes() ever returned nothing, the loop below would check nothing
        // and the guard would pass vacuously. Same rule as the reference-set and platform-attribute proofs — the
        // positive companion is an assertion.
        Assert.NotEmpty(assembly.GetExportedTypes());
        foreach (Type type in assembly.GetExportedTypes())
            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (Type exposed in ExposedTypes(member))
                    if (IsBanned(exposed))
                        offenders.Add($"{type.Name}.{member.Name} → {exposed.Name}");
            }

        output.WriteLine($"{layer}: {assembly.GetExportedTypes().Length} public types checked");
        Assert.Empty(offenders);
    }

    [Theory]
    [MemberData(nameof(LayerNames))]
    public void No_law_layer_source_mentions_async_or_a_thread(string layer)
    {
        string project = Path.Combine(TestPaths.SourceRoot, $"AudioOptimizer.{layer}");
        Assert.True(Directory.Exists(project), $"{project} does not exist: the scan would have nothing to read.");
        var offenders = new List<string>();
        foreach (string file in Directory.GetFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            // Generated files only: every .NET class library gets `global using System.Threading.Tasks;` in its
            // obj/GlobalUsings.g.cs, which says nothing about the source. Scanning obj/ would make this check
            // report the toolchain, not the code — the same trap as the suppression grep.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            string source = File.ReadAllText(file);
            foreach (string banned in new[] { "async ", "await ", "Task.Run", "new Thread", "Thread.Sleep", "ThreadPool", "System.Threading.Tasks", "Parallel." })
                if (source.Contains(banned, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} contains '{banned}'");
        }

        Assert.Empty(offenders);
    }

    /// <summary>The contrasting half: the UI is where the asynchrony lives, so it must actually use it.</summary>
    [Fact]
    public void The_ui_layer_is_the_one_that_owns_the_off_thread_work()
    {
        Assembly ui = typeof(UI.ViewModels.MeasurementFlowViewModel).Assembly;
        Type flow = typeof(UI.ViewModels.MeasurementFlowViewModel);

        MethodInfo? measure = flow.GetMethod(nameof(UI.ViewModels.MeasurementFlowViewModel.MeasureAsync));
        Assert.NotNull(measure);
        Assert.Equal(typeof(Task), measure.ReturnType);                 // the law layers may not; this one must
        Assert.Contains(ui.GetExportedTypes(), type => type.Name == "MeasurementFlowViewModel");

        // The command that Space/Enter invoke is the same entry point: no second, synchronous path into a capture.
        // ponytail: this half is a source-text check and therefore formatting-sensitive — it is supplementary, not
        // the proof. The real proof is the behavioural gate test (the capture blocks on a TaskCompletionSource while
        // the invocation has already returned) plus the thread-id assertions in MeasurementFlowViewTests.
        string source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "AudioOptimizer.UI", "ViewModels", "MeasurementFlowViewModel.cs"));
        Assert.Contains("Task.Run(() => _runner.Run(slot))", source);
        Assert.Contains(".ConfigureAwait(true)", source);
    }

    private static IEnumerable<Type> ExposedTypes(MemberInfo member) => member switch
    {
        MethodBase method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(ReturnTypeOf(method)),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        EventInfo @event => [@event.EventHandlerType!],
        _ => [],
    };

    private static Type ReturnTypeOf(MethodBase method) => method switch
    {
        MethodInfo info => info.ReturnType,
        ConstructorInfo => typeof(void),
        _ => typeof(void),
    };

    private static bool IsBanned(Type type)
    {
        Type candidate = type.IsByRef || type.IsArray ? type.GetElementType() ?? type : type;
        if (candidate.IsGenericType) candidate = candidate.GetGenericTypeDefinition();
        return BannedReturnTypes.Contains(candidate);
    }
}
