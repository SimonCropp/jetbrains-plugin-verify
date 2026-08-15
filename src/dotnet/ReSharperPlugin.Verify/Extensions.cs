using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.DataContext;
using JetBrains.Application.UI.ActionSystem.ActionsRevised.Menu;
using JetBrains.DocumentModel.DataContext;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Actions;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.UnitTestFramework.Actions;
using JetBrains.ReSharper.UnitTestFramework.Criteria;
using JetBrains.ReSharper.UnitTestFramework.Elements;
using JetBrains.ReSharper.UnitTestFramework.Execution;
using JetBrains.Util;
using VerifyTests.ExceptionParsing;

public static class Extensions
{
    public static IActionRequirement GetRequirement(this IDataContext dataContext)
    {
        if (dataContext.GetData(DocumentModelDataConstants.DOCUMENT) == null)
        {
            return CommitAllDocumentsRequirement.TryGetInstance(dataContext);
        }

        return CurrentPsiFileRequirement.FromDataContext(dataContext);
    }

    public static bool HasPendingCompare(this IDataContext context)
    {
        var lookup = new InlineLookup();
        foreach (var (result, _) in context.GetVerifyResults())
        {
            foreach (var file in result.New.Concat(result.NotEqual))
            {
                if (File.Exists(file.Received))
                {
                    return true;
                }
            }

            // An inline snapshot has no received file. The two texts are in the inline queue, or
            // staged under the intermediate (obj) directory when nothing owns one.
            foreach (var entry in result.InlineEntries())
            {
                if (entry.CanCompare(lookup))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool HasPendingAccept(this IDataContext context)
    {
        var lookup = new InlineLookup();
        foreach (var (result, _) in context.GetVerifyResults())
        {
            foreach (var file in result.New.Concat(result.NotEqual))
            {
                if (File.Exists(file.Received))
                {
                    return true;
                }
            }

            foreach (var file in result.Delete)
            {
                if (File.Exists(file))
                {
                    return true;
                }
            }

            // An inline snapshot is accepted by rewriting the test source file, which the queue
            // owner does on request, or which this plugin does itself from a staged patch.
            foreach (var entry in result.InlineEntries())
            {
                if (entry.CanAccept(lookup))
                {
                    return true;
                }
            }
        }

        // Fall back to the received maps Verify writes to disk. A single test can leave many received
        // files (for example one Verify call per endpoint via Task.WhenAll, or a loop that aggregates
        // the failures), but the exception only carries the first pair, or is not a VerifyException at
        // all. The maps record every received file with its verified target, independent of exceptions.
        foreach (var file in context.GetReceivedMaps())
        {
            if (File.Exists(file.Received))
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<VirtualFileSystemPath> GetVerifiedFiles(this IDataContext context)
    {
        var session = context.GetData(UnitTestDataConstants.Session.CURRENT);
        if (session == null)
        {
            yield break;
        }

        var elements = context.GetData(UnitTestDataConstants.Elements.IN_CONTEXT)?.Criterion.Evaluate();
        if (elements == null)
        {
            yield break;
        }

        foreach (var element in elements)
        {
            var directory = element.GetProjectFiles()?.FirstOrDefault()?.Location.Parent;
            if (directory == null)
                continue;

            var parent = element.TraverseAcross(x => x.Parent).Last().ShortName;
            var name = element.ShortName
                .Replace("(", "_")
                .Replace(": ", "=")
                .Replace(", ", "_")
                .Replace("\"", string.Empty)
                .Replace(")", string.Empty);
            var verifiedFileName = $"{parent}.{name}.verified";

            foreach (var file in directory.GetChildFiles(verifiedFileName + "*"))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Every parsed Verify failure in context.
    /// </summary>
    /// <param name="parseFailures">
    /// Where a message that could not be parsed is reported, or null to say nothing about it.
    /// <para>
    /// Null from the menu update, which runs every time the menu opens: a parse failure used to
    /// raise a modal dialog from there, so a single unreadable message put one in front of the
    /// user on every right click, over and over, with no way to act on it. An execute passes a
    /// collection, since that is a click that is about to do nothing and the reason is worth
    /// having.
    /// </para>
    /// </param>
    public static IEnumerable<(Result, IUnitTestElement)> GetVerifyResults(this IDataContext context, ICollection<string> parseFailures = null)
    {
        var session = context.GetData(UnitTestDataConstants.Session.CURRENT);
        if (session == null)
        {
            yield break;
        }

        var elements = context.GetData(UnitTestDataConstants.Elements.IN_CONTEXT)?.Criterion.Evaluate();
        if (elements == null)
        {
            yield break;
        }

        var resultManager = context.GetComponent<IUnitTestResultManager>();

        foreach (var element in elements)
        {
            var result = resultManager.GetResultData(element, session);
            if (!result.HasVerifyException())
            {
                continue;
            }

            var parsed = result.GetParseResult(parseFailures);
            if (!parsed.Equals(default(Result)))
            {
                yield return (parsed, element);
            }
        }
    }

    private static bool HasVerifyException(this UnitTestResultData result)
    {
        if (result.ExceptionCount == 0)
            return false;

        var info = result.GetExceptionInfo(0);

        // Most adapters (xUnit, NUnit, MSTest) surface the real exception type.
        if (info.Type == "VerifyException")
            return true;

        // Frameworks running on Microsoft.Testing.Platform without a dedicated Rider
        // adapter (e.g. TUnit) come through the generic MTP provider, which reports the
        // failure with a null exception type and only the message text. Fall back to
        // recognising the VerifyException payload from the message itself.
        return TryGetVerifyMessage(info.Message) != null;
    }

    private static Result GetParseResult(this UnitTestResultData result, ICollection<string> parseFailures)
    {
        var rawMessage = result.GetExceptionInfo(0).Message!;
        var message = TryGetVerifyMessage(rawMessage) ?? rawMessage;
        try
        {
            return Parser.Parse(message);
        }
        catch (Exception exception)
        {
            parseFailures?.Add(
                exception.Message +
                "\n\nNote that you might need to rerun tests before your changes take effect.");
            return default;
        }
    }

    // The inline headers are listed in their own right, since an inline only failure carries no
    // file sections at all. They matched anyway - "InlineNew:" contains "New:" - but by accident
    // of how they happen to be spelled, which is not something to rest a payload check on
    private static readonly string[] sectionMarkers =
    {
        "New:",
        "NotEqual:",
        "Equal:",
        "Delete:",
        "InlineNew:",
        "InlineNotEqual:"
    };

    /// <summary>
    /// Normalises a test failure message down to the raw <c>VerifyException</c> payload that
    /// <see cref="Parser" /> understands, or returns <c>null</c> when the message is not a
    /// Verify failure.
    /// </summary>
    /// <remarks>
    /// This handles the cases where the exception type is unavailable, which happens for test
    /// frameworks that run on Microsoft.Testing.Platform without a dedicated Rider adapter.
    /// TUnit is the notable example: Rider receives the failure through the generic MTP provider,
    /// which loses the exception type (it is reported as <c>null</c>) and may prefix the message
    /// with a category label such as <c>"[Test Failure] "</c>. The remaining text is the raw
    /// <c>VerifyException.Message</c>, which always begins with <c>"Directory:"</c> followed by one
    /// of the section headers.
    /// </remarks>
    private static string TryGetVerifyMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return null;

        var candidate = message.TrimStart();

        // Strip a leading "[label] " prefix added by TUnit on Microsoft.Testing.Platform.
        if (candidate.StartsWith("["))
        {
            var closing = candidate.IndexOf(']');
            if (closing > 0)
            {
                candidate = candidate.Substring(closing + 1).TrimStart();
            }
        }

        if (LooksLikeVerifyPayload(candidate))
            return candidate;

        // MSTest prepends "Test method ..." on the first line, with "VerifyException" and the
        // payload starting on a later line.
        var index = message.IndexOf("VerifyException", StringComparison.Ordinal);
        return index >= 0 ? message.Substring(index) : null;
    }

    private static bool LooksLikeVerifyPayload(string candidate)
    {
        if (candidate.StartsWith("VerifyException"))
            return true;

        if (!candidate.StartsWith("Directory:"))
            return false;

        // Guard against unrelated failures that merely start with "Directory:" by requiring at
        // least one of the section headers a VerifyException always contains.
        foreach (var marker in sectionMarkers)
        {
            if (candidate.Contains(marker))
                return true;
        }

        return false;
    }

    public static IReadOnlyList<IUnitTestElement> GetContextElements(this IDataContext context)
    {
        var session = context.GetData(UnitTestDataConstants.Session.CURRENT);
        if (session == null)
        {
            return Array.Empty<IUnitTestElement>();
        }

        var elements = context.GetData(UnitTestDataConstants.Elements.IN_CONTEXT)?.Criterion.Evaluate();
        if (elements == null)
        {
            return Array.Empty<IUnitTestElement>();
        }

        return elements.ToList();
    }

    // Reads the received maps Verify writes to the intermediate (obj) directory of each project in
    // context. Unlike the exception message, these cover every received file a test left on disk, so
    // a test that produces many snapshots can be accepted as a whole. Each returned pair is one whose
    // received file still exists (ReceivedMaps drops stale records), deduplicated by received path.
    public static IReadOnlyList<FilePair> GetReceivedMaps(this IDataContext context)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in context.GetContextElements())
        {
            var directory = element.GetProjectDirectory();
            if (directory != null)
            {
                directories.Add(directory);
            }
        }

        if (directories.Count == 0)
        {
            return Array.Empty<FilePair>();
        }

        var pairs = new List<FilePair>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var pair in ReceivedMaps.Read(directory).Pairs)
            {
                if (seen.Add(pair.Received))
                {
                    pairs.Add(pair);
                }
            }
        }

        return pairs;
    }

    // The project directory holds the obj directory the maps are written under. ReceivedMaps.Read
    // scans it recursively, so the exact intermediate path does not need to be known here.
    private static string GetProjectDirectory(this IUnitTestElement element)
    {
        var project = element.GetProjectFiles()?.FirstOrDefault()?.GetProject();
        if (project == null)
        {
            return null;
        }

        var location = project.Location;
        if (location.IsEmpty)
        {
            return null;
        }

        return location.FullPath;
    }
}
