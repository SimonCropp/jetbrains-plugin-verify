using System;
using System.Collections.Generic;
using System.IO;
using DiffEngine;

/// <summary>
/// The inline patches a test run left on disk, found by scanning the projects in context.
/// </summary>
/// <remarks>
/// The exception message names a staged patch only when the run that threw it did the staging,
/// which is only when nothing owned the inline queue. A snapshot an owner took is named nowhere:
/// the queue holds it, and the message says only where the call site is. That is fine while the
/// owner is running, and stops being fine the moment it exits, because
/// <see cref="InlineStaging.Persist" /> writes every entry it still held out to disk on the way
/// out. The snapshot is then pending in a place no exception message mentions, so it has to be
/// looked for.
/// <para>
/// Scanned the way <c>ReceivedMaps</c> is, and for the same reason: the intermediate directory is
/// a convention rather than something the message carries, so the project directory is walked for
/// it rather than derived.
/// </para>
/// </remarks>
public sealed class StagedInlines
{
    private readonly Dictionary<string, List<StagedInline>> byKey;

    private StagedInlines(Dictionary<string, List<StagedInline>> byKey) =>
        this.byKey = byKey;

    /// <summary>
    /// Every readable patch under a <c>VerifyInline</c> directory below one of
    /// <paramref name="projectDirectories" />, grouped by the call site it belongs to.
    /// </summary>
    public static StagedInlines Read(IEnumerable<string> projectDirectories)
    {
        var byKey = new Dictionary<string, List<StagedInline>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in projectDirectories)
        {
            foreach (var staging in FindStagingDirectories(directory))
            {
                foreach (var file in EnumerateFiles(staging))
                {
                    // A project can be reached from more than one element in context, and two
                    // projects can share an intermediate directory, so the same patch can be found
                    // twice. Counting it twice would read as a conflict.
                    if (!seen.Add(file) ||
                        !InlinePatchFile.TryRead(file, out var patch) ||
                        // A Remove is applied by whoever produced it and is never reviewed, so one
                        // reaching here is not a pending snapshot. Checked rather than assumed,
                        // since this is read off disk.
                        patch.Mode == InlinePatchMode.Remove)
                    {
                        continue;
                    }

                    var key = InlineKey.For(patch.SourceFile, patch.LineHint);
                    if (!byKey.TryGetValue(key, out var group))
                    {
                        byKey[key] = group = new List<StagedInline>();
                    }

                    group.Add(new StagedInline(patch, file));
                }
            }
        }

        // Ordered, so a call site with more than one patch resolves to the same one on every run.
        foreach (var group in byKey.Values)
        {
            group.Sort((left, right) => string.CompareOrdinal(left.PatchPath, right.PatchPath));
        }

        return new StagedInlines(byKey);
    }

    /// <summary>
    /// What is staged for a call site. More than one when a multi targeted run staged a patch per
    /// framework; empty when nothing is staged for it.
    /// </summary>
    public IReadOnlyList<StagedInline> For(string key) =>
        byKey.TryGetValue(key, out var group) ? group : Array.Empty<StagedInline>();

    // A junction or symlink can make the tree cyclic, so the walk is bounded rather than open
    // ended. An intermediate directory sits only a handful of levels below its project.
    private const int maxDepth = 20;

    private static IEnumerable<string> FindStagingDirectories(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var pending = new Stack<KeyValuePair<string, int>>();
        pending.Push(new KeyValuePair<string, int>(directory, 0));

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            string[] children;
            try
            {
                children = Directory.GetDirectories(current.Key);
            }
            catch (Exception exception)
                when (exception is UnauthorizedAccessException || exception is IOException)
            {
                // Skip anything that cannot be walked, rather than failing the whole scan.
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (string.Equals(name, InlineStaging.DirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    // Staged files are flat inside, so there is no need to descend further.
                    yield return child;
                    continue;
                }

                if (current.Value + 1 < maxDepth &&
                    !IsSkipped(name))
                {
                    pending.Push(new KeyValuePair<string, int>(child, current.Value + 1));
                }
            }
        }
    }

    // Directories that can never hold an intermediate directory, and that are large enough to
    // dwarf the rest of the walk.
    private static bool IsSkipped(string name) =>
        string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*.inlinepatch");
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException || exception is IOException)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>
/// One staged inline snapshot: the patch, and the two texts staged beside it.
/// </summary>
public sealed class StagedInline
{
    public StagedInline(InlinePatch patch, string patchPath)
    {
        Patch = patch;
        PatchPath = patchPath;
        Received = Sibling(patchPath, "received.txt");
        Expected = Sibling(patchPath, "expected.txt");
        Origin = patch.Framework ?? FrameworkFromName(patchPath);
    }

    /// <summary>
    /// The edit the test run produced, carrying the anchors that say which call it came from.
    /// </summary>
    public InlinePatch Patch { get; }

    public string PatchPath { get; }

    /// <summary>
    /// The staged received text, or null when it is not on disk. Not needed to apply the patch,
    /// which carries the content itself, but it is what a diff view reads.
    /// </summary>
    public string Received { get; }

    /// <summary>
    /// The staged expected text, or null when it is not on disk.
    /// </summary>
    public string Expected { get; }

    /// <summary>
    /// The framework that produced this, or null when nothing says. Best effort: it only ever
    /// labels a conflict.
    /// </summary>
    public string Origin { get; }

    /// <summary>
    /// The framework read off the file name, for a patch that does not carry one.
    /// </summary>
    /// <remarks>
    /// A queue owner labels what it persists, since it knows which framework queued it. A test run
    /// staging its own does not: the label is stamped on the payload sent to an owner, and staging
    /// is what happens when there was no owner to send one to. Both name the files
    /// <c>{test}.{hash}.{runtime}</c> though, so the last segment says it either way.
    /// </remarks>
    private static string FrameworkFromName(string patchPath)
    {
        var stem = Path.GetFileNameWithoutExtension(patchPath);
        var index = stem.LastIndexOf('.');
        if (index < 0 ||
            index == stem.Length - 1)
        {
            return null;
        }

        return stem.Substring(index + 1);
    }

    private static string Sibling(string patchPath, string extension)
    {
        var directory = Path.GetDirectoryName(patchPath);
        if (directory == null)
        {
            return null;
        }

        var path = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(patchPath)}.{extension}");
        return File.Exists(path) ? path : null;
    }
}
