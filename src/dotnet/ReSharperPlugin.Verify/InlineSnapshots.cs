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
/// Accepting one rewrites the source file, which DiffEngine does through
/// <see cref="InlineApplier" />. The patch describing that rewrite, along with the received and the
/// expected text, is staged by the test run under the intermediate (obj) directory - but only when
/// no DiffEngineViewer could be resolved, since a viewer owns the review when there is one. Set the
/// `DiffEngine_InlineViewer` environment variable to `false` in the unit test runner options to
/// review inline snapshots here instead of in a viewer window.
/// </remarks>
public static class InlineSnapshots
{
    public static IEnumerable<InlineEntry> InlineEntries(this Result result) =>
        result.InlineNew.Concat(result.InlineNotEqual);

    // The staged patch is what an accept applies, so an entry without one is not pending here: it
    // was handed to a viewer, which owns it.
    public static bool CanAccept(this InlineEntry entry) =>
        entry.PatchPath != null &&
        File.Exists(entry.PatchPath);

    public static bool CanCompare(this InlineEntry entry) =>
        entry.ReceivedPath != null &&
        entry.ExpectedPath != null &&
        File.Exists(entry.ReceivedPath) &&
        File.Exists(entry.ExpectedPath);

    /// <summary>
    /// Splices the new snapshot into the source file. Returns true when the source holds it
    /// afterwards, whether this call put it there or an earlier one did.
    /// </summary>
    public static bool TryAccept(InlineEntry entry, ICollection<string> failures)
    {
        if (!entry.CanAccept())
        {
            return false;
        }

        if (!InlinePatchFile.TryRead(entry.PatchPath, out var patch))
        {
            failures.Add($"{Describe(entry)}: the staged patch could not be read: {entry.PatchPath}");
            return false;
        }

        // InlineApplier owns all locking, in process and cross process, so accepting alongside a
        // DiffEngineTray or viewer doing the same is safe. No locking is added here.
        var result = InlineApplier.Apply(patch);
        switch (result.Status)
        {
            case InlineApplyStatus.Applied:
            case InlineApplyStatus.AlreadyApplied:
                // The run that staged these files may also have queued the patch with whatever owns
                // the inline queue, and that queue outlives the run. Without this the tray keeps
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

    private static string Describe(InlineEntry entry) =>
        $"{entry.SourceFile}({entry.Line})";
}
