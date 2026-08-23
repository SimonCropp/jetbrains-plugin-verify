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
/// Otherwise the patch is on disk, under a <c>VerifyInline</c> directory in the intermediate (obj)
/// directory, and the source rewrite happens here. A test run stages one when nothing answered,
/// and names it in the exception message. A queue owner stages one for every entry it still holds
/// when it exits, and nothing names those, so they are found by scanning - see
/// <see cref="StagedInlines" />.
/// </para>
/// </remarks>
public static class InlineSnapshots
{
    /// <summary>
    /// The directory name an inline snapshot's two texts sit under.
    /// </summary>
    /// <remarks>
    /// Load bearing in three repos and enforced by none of them: Verify stages under a directory
    /// of this name, this plugin writes the queued texts to one so they look the same, and the
    /// Rider frontend decides a diff is an inline snapshot - read only right pane, titled
    /// "Expected" - by matching the parent directory name against it (CompareManager.kt). Renaming
    /// it anywhere silently downgrades the diff rather than breaking anything. A field on
    /// CompareData would carry it properly, which is a protocol change rather than a rename.
    /// <para>
    /// Taken from DiffEngine, where the processes that stage under it live, rather than spelled
    /// again here.
    /// </para>
    /// </remarks>
    internal const string InlineDirectoryName = InlineStaging.DirectoryName;

    public static IEnumerable<InlineEntry> InlineEntries(this Result result) =>
        result.InlineNew.Concat(result.InlineNotEqual);

    /// <summary>
    /// How the queue addresses this call site.
    /// </summary>
    public static string Key(this InlineEntry entry) =>
        InlineKey.For(entry.SourceFile, entry.Line);

    // Ordered by what each costs, since both of these run from the menu update: the files the
    // message named are a File.Exists, asking the queue is a loopback round trip on the thread the
    // menu is being built on, and the scan is a directory walk that only the owner-exited case
    // needs. Which of them ends up doing the accept is decided in TryAccept, where the queue comes
    // first regardless
    public static bool CanAccept(this InlineEntry entry, InlineLookup lookup) =>
        entry.HasStagedPatch() ||
        lookup.IsQueued(entry) ||
        lookup.Staged(entry).Count > 0;

    public static bool CanCompare(this InlineEntry entry, InlineLookup lookup) =>
        entry.HasStagedText() ||
        lookup.IsQueued(entry) ||
        lookup.Staged(entry).Count > 0;

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
                // The owner applied it and dropped the entry. A run whose patch the owner took
                // stages nothing, so there is usually nothing left on disk - but an earlier run of
                // the same test may have staged before that owner arrived, and what it left is
                // still there saying the snapshot is pending.
                ClearStaged(entry, entry.SourceFile, entry.Line, null);
                return true;
            }

            if (outcome == InlineAcceptOutcome.Failed)
            {
                failures.Add($"{Describe(entry)}: {message}");
                return false;
            }

            // Unknown: the owner went away between the listing and the accept, or something else
            // took the entry first. Whatever is staged, if anything, is all that is left - and a
            // run whose patch the owner took stages nothing, so usually there is nothing. Said out
            // loud, because the click otherwise moved no file, changed no source and reported
            // nothing at all
            if (!entry.HasStagedPatch() &&
                lookup.Staged(entry).Count == 0)
            {
                failures.Add($"{Describe(entry)}: the queue owner did not apply it. It may have been accepted elsewhere, or the owner may have exited, and there is no staged patch to fall back on. Re-run the test if the snapshot is still pending.");
                return false;
            }
        }

        return TryAcceptStaged(entry, lookup, failures);
    }

    /// <summary>
    /// The two texts to show: what the test produced against the snapshot the source file holds.
    /// From the queue when an owner has it, and from what is staged otherwise.
    /// </summary>
    public static bool TryGetTexts(InlineEntry entry, InlineLookup lookup, ICollection<string> notes, out string received, out string expected)
    {
        received = null;
        expected = null;

        if (lookup.Queued(entry) is { } pending)
        {
            // A multi-targeted run that disagreed with itself holds a variant per framework, and
            // only the first is shown. Reviewing it and accepting was met with a refusal from the
            // owner naming frameworks the diff never mentioned, so it is said here instead
            if (pending.Conflicted)
            {
                notes?.Add($"{Describe(entry)}: conflicting snapshots ({pending.OriginsLabel}). The first is shown; resolve the conflict in the viewer before accepting.");
            }

            return TryWriteTexts(entry, pending.Patch, out received, out expected);
        }

        // The files the run itself staged, which are already in a VerifyInline directory and so
        // already read as an inline snapshot by the diff view.
        if (entry.HasStagedText())
        {
            received = entry.ReceivedPath;
            expected = entry.ExpectedPath;
            return true;
        }

        var staged = lookup.Staged(entry);
        if (staged.Count == 0)
        {
            return false;
        }

        if (IsConflicted(staged))
        {
            notes?.Add($"{Describe(entry)}: conflicting snapshots staged ({Origins(staged)}). The first is shown; re-run the tests so the frameworks agree before accepting.");
        }

        var first = staged[0];
        if (first.Received != null &&
            first.Expected != null)
        {
            received = first.Received;
            expected = first.Expected;
            return true;
        }

        // The patch carries both texts, so a trio missing one of them is still reviewable.
        return TryWriteTexts(entry, first.Patch, out received, out expected);
    }

    /// <summary>
    /// The patch the run itself staged, whose path the exception message carries.
    /// </summary>
    private static bool HasStagedPatch(this InlineEntry entry) =>
        entry.PatchPath != null &&
        File.Exists(entry.PatchPath);

    private static bool HasStagedText(this InlineEntry entry) =>
        entry.ReceivedPath != null &&
        entry.ExpectedPath != null &&
        File.Exists(entry.ReceivedPath) &&
        File.Exists(entry.ExpectedPath);

    private static bool TryAcceptStaged(InlineEntry entry, InlineLookup lookup, ICollection<string> failures)
    {
        if (!TryResolvePatch(entry, lookup, failures, out var patch))
        {
            return false;
        }

        // InlineApplier owns all locking, in process and cross process, so applying beside a tray or
        // viewer doing the same is safe. No locking is added here.
        var result = InlineApplier.Apply(patch);
        switch (result.Status)
        {
            case InlineApplyStatus.Applied:
            case InlineApplyStatus.AlreadyApplied:
                // The run that staged this may still have queued the patch with an owner that
                // arrived later, and that queue outlives the run. Without this a tray keeps
                // offering a snapshot that is already in the source.
                //
                // Retire rather than settle: SettleInline stamps the running process's own
                // framework as the origin, and that is this IDE backend rather than the test run,
                // so it names a framework no entry was ever labelled with and the owner strips
                // nothing. Retire carries no origin, which drops the whole entry - and that is what
                // is wanted anyway, since every variant of this call site was anchored to the
                // literal the source no longer holds.
                DiffRunner.RetireInline(patch.SourceFile, patch.LineHint, patch.MemberName);
                ClearStaged(entry, patch.SourceFile, patch.LineHint, patch.MemberName);
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
    /// The patch to apply for a call site nothing holds in a queue. The one the run named where
    /// there is one, since that is this run's own and cannot be mistaken for another framework's.
    /// </summary>
    private static bool TryResolvePatch(InlineEntry entry, InlineLookup lookup, ICollection<string> failures, out InlinePatch patch)
    {
        patch = null;

        if (entry.HasStagedPatch())
        {
            if (InlinePatchFile.TryRead(entry.PatchPath, out patch))
            {
                return true;
            }

            failures.Add($"{Describe(entry)}: the staged patch could not be read: {entry.PatchPath}");
            return false;
        }

        var staged = lookup.Staged(entry);
        if (staged.Count == 0)
        {
            return false;
        }

        // Only one of them can go into the source, and nothing here says which. Refused as the tray
        // refuses it, and for the same reason: accepting would be picking between snapshots that
        // were never shown.
        if (IsConflicted(staged))
        {
            failures.Add($"{Describe(entry)}: conflicting snapshots staged ({Origins(staged)}). Resolve them in DiffEngineViewer, or re-run the tests so the frameworks agree.");
            return false;
        }

        patch = staged[0].Patch;
        return true;
    }

    /// <summary>
    /// Whether a call site's staged patches say different things, which a multi targeted run
    /// produces when its frameworks disagree. Compared through the patch's own definition of
    /// sameness, which ignores which framework produced it.
    /// </summary>
    private static bool IsConflicted(IReadOnlyList<StagedInline> staged) =>
        staged.Any(_ => !_.Patch.Matches(staged[0].Patch));

    private static string Origins(IReadOnlyList<StagedInline> staged) =>
        string.Join(
            " / ",
            staged
                .Select(_ => _.Origin ?? "unknown")
                .Distinct(StringComparer.Ordinal)
                .ToArray());

    /// <summary>
    /// Writes a patch's two texts out, since a queued snapshot is held in the owner's memory and a
    /// diff view needs files. Named per call site rather than per invocation, so comparing the same
    /// snapshot twice overwrites rather than accumulating.
    /// </summary>
    private static bool TryWriteTexts(InlineEntry entry, InlinePatch patch, out string received, out string expected)
    {
        received = null;
        expected = null;
        try
        {
            // The same directory name the staged files sit under, which is what the Rider diff view
            // reads to know it is looking at an inline snapshot rather than a verified file.
            var directory = Path.Combine(Path.GetTempPath(), InlineDirectoryName);
            Directory.CreateDirectory(directory);
            var name = $"{Path.GetFileNameWithoutExtension(entry.SourceFile)}.{entry.Line}.{Hash(entry.Key())}";
            received = Path.Combine(directory, $"{name}.received.txt");
            expected = Path.Combine(directory, $"{name}.expected.txt");
            File.WriteAllText(received, patch.NewContent);
            // Null for a snapshot that has no literal yet, which is an empty pane rather than no
            // comparison, exactly as a new file snapshot compares against an empty verified file.
            File.WriteAllText(expected, patch.OriginalValue ?? string.Empty);
            return true;
        }
        catch (Exception exception)
            when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Drops everything staged for a call site, since with the snapshot in the source those files
    /// are all that is left saying it is still pending.
    /// </summary>
    /// <remarks>
    /// Through DiffEngine rather than by deleting the paths the message named, because those are
    /// only the trio the failing run wrote. A queue owner that exited staged its own, under its own
    /// name and possibly under a different <c>VerifyInline</c> directory, and leaving that behind
    /// leaves the snapshot pending for a source file that already holds it. Every framework's trio
    /// goes, not only the one applied: they were all anchored to the literal the source no longer
    /// holds, so none of them can apply again.
    /// </remarks>
    private static void ClearStaged(InlineEntry entry, string sourceFile, int line, string memberName)
    {
        // Verify stages under the intermediate directory, which is normally inside the project's
        // obj and walked to anyway, but does not have to be. Named explicitly where the message
        // gave a path to read it off.
        string intermediate = null;
        if (entry.PatchPath != null)
        {
            intermediate = Path.GetDirectoryName(Path.GetDirectoryName(entry.PatchPath));
        }

        try
        {
            InlineStaging.Clear(sourceFile, line, memberName, intermediate);
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
/// Listing is a loopback round trip and scanning is a directory walk, and both the menu update and
/// the execute walk every entry in every selected test, so asking per entry would pay one of those
/// per snapshot on a path that runs whenever the menu opens. Neither is done at all unless a
/// verification actually reported an inline snapshot, which keeps a run of ordinary file snapshots
/// off the socket and off the disk entirely.
/// </para>
/// </summary>
public sealed class InlineLookup
{
    private readonly IEnumerable<string> projectDirectories;
    private bool listedKeys;
    private IReadOnlyList<string> keys;
    private bool listedPending;
    private IReadOnlyList<PendingInline> pending;
    private StagedInlines staged;

    /// <param name="projectDirectories">
    /// The projects to scan for staged patches, which are the ones the selected tests live in.
    /// </param>
    public InlineLookup(IEnumerable<string> projectDirectories) =>
        this.projectDirectories = projectDirectories;

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

    /// <summary>
    /// What is staged on disk for this call site, which is where a snapshot ends up once the
    /// process that held it has gone. Empty when nothing is.
    /// </summary>
    public IReadOnlyList<StagedInline> Staged(InlineEntry entry)
    {
        if (staged == null)
        {
            staged = StagedInlines.Read(projectDirectories);
        }

        return staged.For(entry.Key());
    }
}
