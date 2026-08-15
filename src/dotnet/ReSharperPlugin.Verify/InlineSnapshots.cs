using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffEngine;
using VerifyTests.ExceptionParsing;

/// <summary>
/// An inline snapshot keeps the expected text in the test source file, as a string literal beside
/// the code that produces it, instead of in a `.verified.` file. Verify reports one in the
/// `InlineNew:` and `InlineNotEqual:` sections of the exception message.
/// </summary>
/// <remarks>
/// A pending one lives in the inline queue, held by whichever process owns it: DiffEngineTray, or
/// the DiffEngineViewer a test run launched. This plugin is a third surface onto that same queue,
/// reached through <see cref="InlineQueueClient" />. Accepting asks the owner to splice the
/// snapshot into the source, which is what keeps one writer per file and leaves every display
/// agreeing about what is still pending.
/// <para>
/// Only when no owner answers does a test run stage the patch, and the received and expected text,
/// under the intermediate (obj) directory. That is the fallback these actions drop to, and the one
/// case where the source rewrite happens here.
/// </para>
/// </remarks>
public static class InlineSnapshots
{
    public static IEnumerable<InlineEntry> InlineEntries(this Result result) =>
        result.InlineNew.Concat(result.InlineNotEqual);

    /// <summary>
    /// How the queue addresses this call site.
    /// </summary>
    public static string Key(this InlineEntry entry) =>
        InlineKey.For(entry.SourceFile, entry.Line);

    public static bool CanAccept(this InlineEntry entry, InlineLookup lookup) =>
        lookup.IsQueued(entry) ||
        entry.HasStagedPatch();

    public static bool CanCompare(this InlineEntry entry, InlineLookup lookup) =>
        lookup.IsQueued(entry) ||
        entry.HasStagedText();

    /// <summary>
    /// Puts the snapshot in the source file. Returns true when it is there afterwards, whether this
    /// call put it there or an earlier one did.
    /// </summary>
    public static bool TryAccept(InlineEntry entry, InlineLookup lookup, ICollection<string> failures)
    {
        if (lookup.IsQueued(entry))
        {
            var outcome = InlineQueueClient.Accept(entry.Key(), out var message);
            if (outcome == InlineAcceptOutcome.Accepted)
            {
                // The owner applied it and dropped the entry. Nothing to settle, and nothing staged
                // to clean up: a run whose patch the owner took writes no files.
                return true;
            }

            if (outcome == InlineAcceptOutcome.Failed)
            {
                failures.Add($"{Describe(entry)}: {message}");
                return false;
            }

            // Unknown: the owner went away between the listing and the accept. Whatever the run
            // staged, if anything, is all that is left.
        }

        return TryAcceptStaged(entry, failures);
    }

    /// <summary>
    /// The two texts to show: what the test produced against the snapshot the source file holds.
    /// From the queue when an owner has it, and from the staged files otherwise.
    /// </summary>
    public static bool TryGetTexts(InlineEntry entry, InlineLookup lookup, out string received, out string expected)
    {
        if (lookup.Queued(entry) is { } pending)
        {
            return TryWriteTexts(entry, pending, out received, out expected);
        }

        received = entry.ReceivedPath;
        expected = entry.ExpectedPath;
        return entry.HasStagedText();
    }

    /// <summary>
    /// The patch a run stages when no owner answered, which this plugin then applies itself.
    /// </summary>
    private static bool HasStagedPatch(this InlineEntry entry) =>
        entry.PatchPath != null &&
        File.Exists(entry.PatchPath);

    private static bool HasStagedText(this InlineEntry entry) =>
        entry.ReceivedPath != null &&
        entry.ExpectedPath != null &&
        File.Exists(entry.ReceivedPath) &&
        File.Exists(entry.ExpectedPath);

    private static bool TryAcceptStaged(InlineEntry entry, ICollection<string> failures)
    {
        if (!entry.HasStagedPatch())
        {
            return false;
        }

        if (!InlinePatchFile.TryRead(entry.PatchPath, out var patch))
        {
            failures.Add($"{Describe(entry)}: the staged patch could not be read: {entry.PatchPath}");
            return false;
        }

        // InlineApplier owns all locking, in process and cross process, so applying beside a tray or
        // viewer doing the same is safe. No locking is added here.
        var result = InlineApplier.Apply(patch);
        switch (result.Status)
        {
            case InlineApplyStatus.Applied:
            case InlineApplyStatus.AlreadyApplied:
                // The run that staged these files may still have queued the patch with an owner
                // that arrived later, and that queue outlives the run. Without this a tray keeps
                // offering a snapshot that is already in the source.
                DiffRunner.SettleInline(patch.SourceFile, patch.LineHint);
                DeleteStaged(entry);
                return true;

            case InlineApplyStatus.NotFound:
                failures.Add($"{Describe(entry)}: the call site could not be found, so the source has changed since the test ran. Re-run the test and accept again.");
                return false;

            default:
                failures.Add($"{Describe(entry)}: {result.Message}");
                return false;
        }
    }

    /// <summary>
    /// Writes the queued entry's two texts out, since a queued snapshot is held in the owner's
    /// memory and a diff view needs files. Named per call site rather than per invocation, so
    /// comparing the same snapshot twice overwrites rather than accumulating.
    /// </summary>
    private static bool TryWriteTexts(InlineEntry entry, PendingInline pending, out string received, out string expected)
    {
        received = null;
        expected = null;
        try
        {
            // The same directory name the staged files sit under, which is what the Rider diff view
            // reads to know it is looking at an inline snapshot rather than a verified file.
            var directory = Path.Combine(Path.GetTempPath(), "VerifyInline");
            Directory.CreateDirectory(directory);
            var name = $"{Path.GetFileNameWithoutExtension(entry.SourceFile)}.{entry.Line}.{Hash(entry.Key())}";
            received = Path.Combine(directory, $"{name}.received.txt");
            expected = Path.Combine(directory, $"{name}.expected.txt");
            File.WriteAllText(received, pending.Patch.NewContent);
            // Null for a snapshot that has no literal yet, which is an empty pane rather than no
            // comparison, exactly as a new file snapshot compares against an empty verified file.
            File.WriteAllText(expected, pending.Patch.OriginalValue ?? string.Empty);
            return true;
        }
        catch (Exception exception)
            when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The staged files are what both the accept and the compare read, so with the snapshot in the
    // source they are all that is left saying it is still pending.
    private static void DeleteStaged(InlineEntry entry)
    {
        Delete(entry.PatchPath);
        Delete(entry.ReceivedPath);
        Delete(entry.ExpectedPath);
    }

    private static void Delete(string file)
    {
        if (file == null ||
            !File.Exists(file))
        {
            return;
        }

        try
        {
            File.Delete(file);
        }
        catch (Exception exception)
            when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // The snapshot is in the source either way, so a file that cannot be removed is left
            // behind rather than failing an accept that has already happened.
        }
    }

    /// <summary>
    /// FNV-1a, for a file name that is the same every run without carrying a whole path in it.
    /// </summary>
    private static string Hash(string value)
    {
        var hash = 2166136261;
        foreach (var character in value)
        {
            hash = (hash ^ character) * 16777619;
        }

        return hash.ToString("x8");
    }

    private static string Describe(InlineEntry entry) =>
        $"{entry.SourceFile}({entry.Line})";
}

/// <summary>
/// The pending inline snapshots, listed at most once for the action being run.
/// <para>
/// Listing is a loopback round trip, and both the menu update and the execute walk every entry in
/// every selected test, so asking per entry would be a socket call per snapshot on a path that runs
/// whenever the menu opens. Nothing is asked at all unless a verification actually reported an
/// inline snapshot, which keeps a run of ordinary file snapshots off the socket entirely.
/// </para>
/// </summary>
public sealed class InlineLookup
{
    private bool listedKeys;
    private IReadOnlyList<string> keys;
    private bool listedPending;
    private IReadOnlyList<PendingInline> pending;

    /// <summary>
    /// Whether the owner holds this call site. Over the listing that carries no patches, since
    /// this decides whether to offer an action rather than rendering anything, and it is the menu
    /// update that asks it.
    /// </summary>
    public bool IsQueued(InlineEntry entry)
    {
        if (!listedKeys)
        {
            listedKeys = true;
            InlineQueueClient.TryListKeys(out keys);
        }

        return keys.Contains(entry.Key());
    }

    /// <summary>
    /// The entry with its patch, for when the snapshot itself is needed. A second round trip, paid
    /// only by an action that is about to show the text.
    /// </summary>
    public PendingInline Queued(InlineEntry entry)
    {
        if (!listedPending)
        {
            listedPending = true;
            InlineQueueClient.TryList(out pending);
        }

        var key = entry.Key();
        return pending.FirstOrDefault(_ => _.Key == key);
    }
}
