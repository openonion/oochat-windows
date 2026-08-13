namespace ConnectOnion.TrimSmoke;

/// <summary>
/// The world's smallest test runner. xunit is not an option here — a trimmed, self-contained
/// publish has no VSTest host to load into, and the whole question this project answers is what
/// happens inside that binary.
///
/// <para>Failures are collected rather than thrown, so one broken path does not hide the eleven
/// behind it. That matters more than usual for trim regressions, which tend to arrive in
/// clusters: one reintroduced reflection call site can take out every check downstream of it.</para>
/// </summary>
internal sealed class Harness
{
    private readonly List<string> _failures = [];
    private int _passed;

    public bool AnyFailed => _failures.Count > 0;

    public void Check(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (Exception ex)
        {
            Record(name, ex);
        }
    }

    public async Task CheckAsync(string name, Func<Task> body)
    {
        try
        {
            await body();
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (Exception ex)
        {
            Record(name, ex);
        }
    }

    private void Record(string name, Exception ex)
    {
        // "Reflection-based serialization has been disabled" is the signature failure of this
        // whole exercise, so it gets named rather than buried in a stack trace. Note the type is
        // InvalidOperationException, not the NotSupportedException IL2026's wording suggests.
        var hint = ex.Message.Contains("Reflection-based serialization", StringComparison.Ordinal)
            ? "  <-- reflection-based JsonSerializer on a trimmed path; use a source-generated context or WireJson"
            : "";
        _failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}{hint}");
        Console.WriteLine($"  FAIL  {name}");
        Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}{hint}");
    }

    public void Section(string title) => Console.WriteLine($"\n{title}");

    public int Report()
    {
        Console.WriteLine();
        if (_failures.Count == 0)
        {
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }

        Console.WriteLine($"{_failures.Count} of {_passed + _failures.Count} checks FAILED:");
        foreach (var failure in _failures) Console.WriteLine($"  - {failure}");
        return 1;
    }

    // --- assertions ---

    public static void True(bool condition, string because)
    {
        if (!condition) throw new InvalidOperationException(because);
    }

    public static void Equal<T>(T expected, T actual, string because)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{because} (expected '{expected}', got '{actual}')");
    }

    public static void NotNull(object? value, string because)
    {
        if (value is null) throw new InvalidOperationException(because);
    }

    public static void Contains(string expected, string actual, string because)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{because} (expected to find '{expected}' in '{actual}')");
    }
}
